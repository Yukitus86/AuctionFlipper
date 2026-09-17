using AuctionFlipper.Api;
using AuctionFlipper.Services;

namespace AuctionFlipper.Core;

/// <summary>Everything the detail panel needs about one item.</summary>
public sealed record ItemDetail(
    string ItemId,
    ItemInfo Info,
    ItemValue Value,
    Listing[] Ladder,
    (long Time, double UnitPrice)[] History);

/// <summary>
/// The tool's picture of the market: what is for sale, what has sold, what each item is worth, and
/// which listings are worth buying.
///
/// Scoring deliberately walks items rather than listings. Only the cheapest listing of an item can
/// ever be a flip - anything above it would have to be relisted below what it cost - so a full
/// re-score touches a few thousand items rather than the ~143,000 listings in the book, and can
/// run every couple of seconds instead of being amortised.
/// </summary>
public sealed class MarketState
{
    public ItemRegistry Items { get; } = new();
    public ItemRegistry Sellers { get; } = new();
    public SaleTape Tape { get; } = new();
    public OrderBook Book { get; } = new();

    /// <summary>
    /// How long a listing may go unseen before it is presumed gone. Comfortably longer than a full
    /// pass of the book scan, so a listing that is still for sale is always re-observed first.
    /// </summary>
    private const long UnseenEvictionMs = 90 * 60 * 1000;

    private readonly Dictionary<ulong, FlipOpportunity> _flips = new(4096);
    private readonly object _flipGate = new();

    // The tape pages overlap heavily between polls, so sales are de-duplicated on the way in.
    private readonly HashSet<ulong> _seenSales = new(8192);
    private readonly Queue<ulong> _seenSaleOrder = new(8192);
    private const int SeenSaleCapacity = 20_000;

    private AppConfig _config;
    private FlipScorer _scorer;

    public MarketState(AppConfig config)
    {
        _config = config;
        _scorer = new FlipScorer(config.TaxRate, config.Capital, config.ListingSlots);
    }

    /// <summary>Applies edited settings without restarting the collectors.</summary>
    public void UpdateConfig(AppConfig config)
    {
        _config = config;
        _scorer = new FlipScorer(config.TaxRate, config.Capital, config.ListingSlots);
    }

    public event Action<FlipOpportunity>? FlipDiscovered;

    public long LastSaleSeenUnixMs { get; private set; }

    // ------------------------------------------------------------------ ingest

    /// <summary>
    /// Folds a page of listings into the book. Returns how many were new, so the sniper can tell
    /// whether it is keeping up with the listing rate or falling behind.
    /// </summary>
    public int IngestListings(AhEntry[] entries, bool fromSniper)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int added = 0;

        // A JSON array can carry null elements, and this one occasionally does. An unguarded
        // dereference here took down whole sweep cycles: the exception escaped the per-page handler
        // and restarted the scan from zero coverage, which is far more damage than one bad row.
        foreach (AhEntry? entry in entries)
        {
            if (entry?.Item?.Id is not { Length: > 0 } id) continue;
            if (entry.Item.Count <= 0 || entry.Price <= 0) continue;

            int itemIndex = Items.GetOrAdd(id);
            int sellerIndex = Sellers.GetOrAdd(entry.Seller?.Name ?? "?");

            ContainerContents? contents = null;
            if (entry.Item.Contents is { Length: > 0 } raw)
            {
                contents = ContainerValuer.Build(raw
                    .Where(c => c?.Id is { Length: > 0 })
                    .Select(c => (Items.GetOrAdd(c!.Id!), c.Count)));
            }

            // time_left counts down from a 24 h listing, which makes it a clock: subtracting it
            // from the lifetime recovers when the listing went up, and therefore how long the
            // market has had to look at it.
            long listedAt = now - (MarketConstants.ListingLifetimeMs - entry.TimeLeft);

            var listing = new Listing
            {
                Fingerprint = Fingerprint.For(sellerIndex, itemIndex, entry.Item.Count, entry.Price),
                ItemIndex = itemIndex,
                Count = entry.Item.Count,
                Price = entry.Price,
                SellerIndex = sellerIndex,
                ListedAtUnixMs = listedAt,
                ExpiresAtUnixMs = now + entry.TimeLeft,
                Contents = contents,
                FromSniper = fromSniper,
            };

            if (Book.Add(listing, now))
            {
                added++;
                // A brand new listing is where the money is, so it is priced the moment it lands
                // rather than waiting for the next full re-score.
                if (fromSniper) ScoreItemAndPublish(itemIndex, now, announce: true);
            }
        }

        return added;
    }

    /// <summary>
    /// Folds a page of sales into the tape and clears the matching listings out of the book.
    /// Returns how many sales had not been seen before.
    /// </summary>
    public int IngestSales(TransactionEntry[] entries, Action<Sale>? persist = null)
    {
        int fresh = 0;

        foreach (TransactionEntry? entry in entries)
        {
            if (entry?.Item?.Id is not { Length: > 0 } id) continue;
            if (entry.Item.Count <= 0 || entry.Price <= 0) continue;

            int itemIndex = Items.GetOrAdd(id);
            int sellerIndex = Sellers.GetOrAdd(entry.Seller?.Name ?? "?");

            ulong key = SaleKey(entry.SoldAtUnixMs, sellerIndex, itemIndex, entry.Item.Count, entry.Price);
            if (!MarkSeen(key)) continue;

            var sale = new Sale(itemIndex, entry.Item.Count, entry.Price, sellerIndex, entry.SoldAtUnixMs);
            Tape.Add(sale);
            persist?.Invoke(sale);

            // The sold listing is gone from the server, so take it out of the local book too.
            Book.RemoveSold(sellerIndex, itemIndex, entry.Item.Count, entry.Price);

            if (entry.SoldAtUnixMs > LastSaleSeenUnixMs)
                LastSaleSeenUnixMs = entry.SoldAtUnixMs;

            fresh++;
        }

        return fresh;
    }

    private bool MarkSeen(ulong key)
    {
        lock (_seenSales)
        {
            if (!_seenSales.Add(key)) return false;

            _seenSaleOrder.Enqueue(key);
            while (_seenSaleOrder.Count > SeenSaleCapacity)
                _seenSales.Remove(_seenSaleOrder.Dequeue());

            return true;
        }
    }

    private static ulong SaleKey(long soldAt, int seller, int item, int count, double price)
        => Fingerprint.For(seller, item, count, price) ^ (ulong)soldAt * 1099511628211UL;

    // ------------------------------------------------------------------ valuation

    public ItemValue ValueOf(int itemIndex)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return Valuation.Compute(Tape.GetStats(itemIndex, now), Book.GetStats(itemIndex));
    }

    // ------------------------------------------------------------------ scoring

    /// <summary>
    /// Re-scores every item in the book. Also drops opportunities whose listing has since sold,
    /// expired, or stopped being the cheapest.
    /// </summary>
    public void RescanAll()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int[] itemIndexes = Book.TrackedItemIndexes();

        var alive = new HashSet<ulong>(itemIndexes.Length);
        foreach (int itemIndex in itemIndexes)
        {
            FlipOpportunity? flip = ScoreItemAndPublish(itemIndex, now, announce: false);
            if (flip is not null) alive.Add(flip.Listing.Fingerprint);
        }

        lock (_flipGate)
        {
            List<ulong>? stale = null;
            foreach (ulong fp in _flips.Keys)
                if (!alive.Contains(fp)) (stale ??= new List<ulong>()).Add(fp);

            if (stale is not null)
                foreach (ulong fp in stale) _flips.Remove(fp);
        }
    }

    private FlipOpportunity? ScoreItemAndPublish(int itemIndex, long nowUnixMs, bool announce)
    {
        FlipOpportunity? flip = ScoreItem(itemIndex, nowUnixMs);
        if (flip is null) return null;

        bool isNew;
        lock (_flipGate)
        {
            isNew = !_flips.ContainsKey(flip.Listing.Fingerprint);
            _flips[flip.Listing.Fingerprint] = flip;
        }

        if (isNew && announce)
            FlipDiscovered?.Invoke(flip);

        return flip;
    }

    /// <summary>Scores the cheapest listing of one item, which is the only one that can be a flip.</summary>
    public FlipOpportunity? ScoreItem(int itemIndex, long nowUnixMs)
    {
        Listing[] ladder = Book.Ladder(itemIndex, 2);
        if (ladder.Length == 0) return null;

        Listing cheapest = ladder[0];
        string itemId = Items.GetName(itemIndex);
        string seller = Sellers.GetName(cheapest.SellerIndex);

        // Containers are a different trade: the exit is unpacking the box, not relisting it.
        if (cheapest.Contents is { Slots.Length: > 0 } contents)
        {
            if (!_config.IncludeContainers) return null;

            ContainerValuation cv = ContainerValuer.Value(
                contents, ValueOf, Items.GetName, _config.ContainerHaircut);
            return _scorer.ScoreContainer(cheapest, itemId, seller, cv, nowUnixMs);
        }

        ItemSaleStats sales = Tape.GetStats(itemIndex, nowUnixMs);
        BookStats book = Book.GetStats(itemIndex);
        ItemValue value = Valuation.Compute(sales, book);
        if (!value.IsKnown) return null;

        double nextAsk = Book.NextAskExcluding(itemIndex, cheapest.Fingerprint);
        int depth = Book.DepthNear(itemIndex, value.Unit, 0.20);
        int concentration = Book.SellerConcentration(itemIndex, cheapest.SellerIndex);

        return _scorer.Score(cheapest, itemId, seller, value, nextAsk, depth, concentration, nowUnixMs);
    }

    // ------------------------------------------------------------------ queries

    /// <summary>Every live opportunity, unfiltered. The UI applies the user's thresholds.</summary>
    public FlipOpportunity[] CurrentFlips()
    {
        lock (_flipGate) return _flips.Values.ToArray();
    }

    public ItemDetail? GetDetail(string itemId)
    {
        if (!Items.TryGetIndex(itemId, out int itemIndex)) return null;

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Listing[] ladder = Book.Ladder(itemIndex, 40);

        return new ItemDetail(
            itemId,
            ItemCatalog.Get(itemId),
            ValueOf(itemIndex),
            ladder,
            Tape.History(itemIndex, now));
    }

    /// <summary>
    /// Removes listings that are gone: expired on the server's clock, or simply not seen for long
    /// enough that they must have been bought or cancelled. Cheap; runs on a slow timer.
    /// </summary>
    public int EvictExpired(bool bookScanRunning)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int evicted = Book.EvictExpired(now);

        // Only safe while the scan is re-reading the whole book; without it nothing is re-observed
        // and this would empty the board rather than prune it.
        if (bookScanRunning)
            evicted += Book.EvictUnseen(now - UnseenEvictionMs);

        if (evicted > 0)
        {
            lock (_flipGate)
            {
                List<ulong>? dead = null;
                foreach ((ulong fp, FlipOpportunity flip) in _flips)
                    if (flip.ExpiresAtUnixMs <= now) (dead ??= new List<ulong>()).Add(fp);

                if (dead is not null)
                    foreach (ulong fp in dead) _flips.Remove(fp);
            }
        }

        return evicted;
    }
}
