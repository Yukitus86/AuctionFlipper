namespace AuctionFlipper.Core;

/// <summary>The shape of one item's sell side, as far as the tool has observed it.</summary>
/// <param name="AskCount">Distinct listings known for this item.</param>
/// <param name="LowestUnit">Cheapest unit price on offer.</param>
/// <param name="SecondUnit">Next cheapest unit price — the price a reseller must undercut.</param>
/// <param name="MedianUnit">Median ask, used as a fallback value when no sales are known.</param>
/// <param name="UnitsAvailable">Total items offered, not listings, so stacks count properly.</param>
public readonly record struct BookStats(
    int AskCount,
    double LowestUnit,
    double SecondUnit,
    double TenthUnit,
    double MedianUnit,
    double TrimmedMeanUnit,
    long UnitsAvailable);

/// <summary>
/// The live sell side of the market, reconstructed locally.
///
/// Re-reading all ~3200 pages just to know current prices would cost the entire request budget
/// every quarter hour. Instead the book is maintained from the event streams the collectors
/// already pull: the sniper adds every new listing, the tape removes every listing that sells, and
/// anything untouched expires on the server's 24 h clock. The sweeper only has to seed it and
/// periodically re-verify, which leaves most of the budget free.
/// </summary>
public sealed class OrderBook
{
    private sealed class ItemAsks
    {
        public readonly List<Listing> Asks = new(8);
        public bool Sorted = true;
    }

    private readonly Dictionary<ulong, Listing> _byFingerprint = new(200_000);
    private readonly Dictionary<int, ItemAsks> _byItem = new(4096);
    private readonly object _gate = new();

    public int ListingCount { get { lock (_gate) return _byFingerprint.Count; } }
    public int ItemCount { get { lock (_gate) return _byItem.Count; } }

    /// <summary>
    /// Records a listing. Returns true only the first time it is seen, which is what makes a
    /// listing "new" for the sniper and for alerts.
    /// </summary>
    public bool Add(Listing listing, long nowUnixMs)
    {
        lock (_gate)
        {
            if (_byFingerprint.TryGetValue(listing.Fingerprint, out Listing? existing))
            {
                existing.LastSeenUnixMs = nowUnixMs;

                // Seeing it live is the confirmation a restored listing was waiting for.
                existing.Restored = false;
                return false;
            }

            _byFingerprint[listing.Fingerprint] = listing;
            listing.LastSeenUnixMs = nowUnixMs;

            if (!_byItem.TryGetValue(listing.ItemIndex, out ItemAsks? asks))
            {
                asks = new ItemAsks();
                _byItem[listing.ItemIndex] = asks;
            }

            asks.Asks.Add(listing);
            asks.Sorted = false;
            return true;
        }
    }

    /// <summary>
    /// Removes the listing a sale corresponds to. The tape reports the same seller, item, count
    /// and price the listing carried, so the fingerprint matches directly.
    /// </summary>
    public bool RemoveSold(int sellerIndex, int itemIndex, int count, double price)
    {
        ulong fp = Fingerprint.For(sellerIndex, itemIndex, count, price);
        lock (_gate)
        {
            if (!_byFingerprint.TryGetValue(fp, out Listing? listing))
                return false;

            // Identical stacks from one seller collapse into a single record; a sale consumes one.
            if (listing.Multiplicity > 1)
            {
                listing.Multiplicity--;
                return true;
            }

            _byFingerprint.Remove(fp);
            if (_byItem.TryGetValue(itemIndex, out ItemAsks? asks))
            {
                asks.Asks.Remove(listing);
                if (asks.Asks.Count == 0) _byItem.Remove(itemIndex);
            }

            return true;
        }
    }

    /// <summary>Drops listings past the server's 24 h expiry. Cheap enough to run every 30 s.</summary>
    public int EvictExpired(long nowUnixMs)
    {
        lock (_gate)
        {
            List<ulong>? dead = null;
            foreach ((ulong fp, Listing listing) in _byFingerprint)
            {
                if (listing.ExpiresAtUnixMs <= nowUnixMs)
                    (dead ??= new List<ulong>()).Add(fp);
            }

            if (dead is null) return 0;

            foreach (ulong fp in dead)
            {
                Listing listing = _byFingerprint[fp];
                _byFingerprint.Remove(fp);
                if (_byItem.TryGetValue(listing.ItemIndex, out ItemAsks? asks))
                {
                    asks.Asks.Remove(listing);
                    if (asks.Asks.Count == 0) _byItem.Remove(listing.ItemIndex);
                }
            }

            return dead.Count;
        }
    }

    /// <summary>
    /// Drops listings the tool has not seen for a long time.
    ///
    /// The server rebuilds its listing view in bulk every few minutes, and a listing that was
    /// cancelled or bought outside the transaction feed simply stops appearing - there is no
    /// deletion to observe. Since the book scan re-reads every page well inside this window, a
    /// listing that has gone unseen for that long is almost certainly no longer for sale, and
    /// keeping it would mean offering the user a flip that cannot be bought.
    /// </summary>
    public int EvictUnseen(long lastSeenBeforeUnixMs)
    {
        lock (_gate)
        {
            List<ulong>? dead = null;
            foreach ((ulong fp, Listing listing) in _byFingerprint)
            {
                if (listing.LastSeenUnixMs < lastSeenBeforeUnixMs)
                    (dead ??= new List<ulong>()).Add(fp);
            }

            if (dead is null) return 0;

            foreach (ulong fp in dead)
            {
                Listing listing = _byFingerprint[fp];
                _byFingerprint.Remove(fp);
                if (_byItem.TryGetValue(listing.ItemIndex, out ItemAsks? asks))
                {
                    asks.Asks.Remove(listing);
                    if (asks.Asks.Count == 0) _byItem.Remove(listing.ItemIndex);
                }
            }

            return dead.Count;
        }
    }

    public BookStats GetStats(int itemIndex)
    {
        lock (_gate)
        {
            if (!_byItem.TryGetValue(itemIndex, out ItemAsks? asks) || asks.Asks.Count == 0)
                return default;

            EnsureSorted(asks);
            List<Listing> a = asks.Asks;

            long units = 0;
            foreach (Listing l in a) units += (long)l.Count * l.Multiplicity;

            double lowest = a[0].UnitPrice;
            double second = a.Count > 1 ? a[1].UnitPrice : lowest;
            double tenth = a[Math.Min(a.Count - 1, 9)].UnitPrice;
            double median = a[a.Count / 2].UnitPrice;

            return new BookStats(a.Count, lowest, second, tenth, median, TrimmedMean(a), units);
        }
    }

    /// <summary>
    /// The unit price a reseller should expect to beat, ignoring one specific listing.
    ///
    /// When evaluating a flip, the listing being bought is about to leave the book, so the price to
    /// undercut is the next one up — not the one being bought.
    /// </summary>
    public double NextAskExcluding(int itemIndex, ulong excludeFingerprint)
    {
        lock (_gate)
        {
            if (!_byItem.TryGetValue(itemIndex, out ItemAsks? asks) || asks.Asks.Count == 0)
                return 0;

            EnsureSorted(asks);
            foreach (Listing l in asks.Asks)
            {
                if (l.Fingerprint == excludeFingerprint)
                {
                    // A collapsed duplicate means another identical stack is still on sale at the
                    // same price, so the effective next ask is that price.
                    if (l.Multiplicity > 1) return l.UnitPrice;
                    continue;
                }
                return l.UnitPrice;
            }
            return 0;
        }
    }

    /// <summary>How many listings sit within a tolerance of a price. Thin books are unreliable.</summary>
    public int DepthNear(int itemIndex, double unitPrice, double tolerance)
    {
        lock (_gate)
        {
            if (!_byItem.TryGetValue(itemIndex, out ItemAsks? asks)) return 0;

            double lo = unitPrice * (1 - tolerance);
            double hi = unitPrice * (1 + tolerance);
            int n = 0;
            foreach (Listing l in asks.Asks)
            {
                double u = l.UnitPrice;
                if (u >= lo && u <= hi) n += l.Multiplicity;
            }
            return n;
        }
    }

    /// <summary>How many of the cheapest listings for an item come from one seller.</summary>
    public int SellerConcentration(int itemIndex, int sellerIndex, int topN = 10)
    {
        lock (_gate)
        {
            if (!_byItem.TryGetValue(itemIndex, out ItemAsks? asks) || asks.Asks.Count == 0)
                return 0;

            EnsureSorted(asks);
            int n = 0;
            int limit = Math.Min(topN, asks.Asks.Count);
            for (int i = 0; i < limit; i++)
                if (asks.Asks[i].SellerIndex == sellerIndex) n += asks.Asks[i].Multiplicity;
            return n;
        }
    }

    /// <summary>The cheapest listings for an item, for the detail view ladder.</summary>
    public Listing[] Ladder(int itemIndex, int take)
    {
        lock (_gate)
        {
            if (!_byItem.TryGetValue(itemIndex, out ItemAsks? asks) || asks.Asks.Count == 0)
                return [];

            EnsureSorted(asks);
            return asks.Asks.Take(take).ToArray();
        }
    }

    public int[] TrackedItemIndexes()
    {
        lock (_gate) return _byItem.Keys.ToArray();
    }

    /// <summary>
    /// Every listing currently held, for writing the book to disk.
    ///
    /// Copied out under the lock rather than handed back as a live view: the collectors are adding
    /// and removing listings continuously, and the snapshot is written to a file over several
    /// hundred milliseconds.
    /// </summary>
    public Listing[] AllListings()
    {
        lock (_gate) return _byFingerprint.Values.ToArray();
    }

    private static void EnsureSorted(ItemAsks asks)
    {
        if (asks.Sorted) return;
        asks.Asks.Sort(static (x, y) => x.UnitPrice.CompareTo(y.UnitPrice));
        asks.Sorted = true;
    }

    /// <summary>
    /// Mean of the asks between the 10th and 40th percentile.
    ///
    /// Used as a fallback value when an item has no sale history. The bottom decile is skipped
    /// because that is exactly where mispriced and trap listings live, and everything above the
    /// 40th is skipped because the top of an auction book is mostly wishful pricing.
    /// </summary>
    private static double TrimmedMean(List<Listing> sorted)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count < 4) return sorted[sorted.Count / 2].UnitPrice;

        int lo = (int)(sorted.Count * 0.10);
        int hi = Math.Max(lo + 1, (int)(sorted.Count * 0.40));

        double sum = 0;
        int n = 0;
        for (int i = lo; i < hi && i < sorted.Count; i++)
        {
            sum += sorted[i].UnitPrice;
            n++;
        }
        return n > 0 ? sum / n : sorted[sorted.Count / 2].UnitPrice;
    }
}
