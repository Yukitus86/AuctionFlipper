using System.IO;
using AuctionFlipper.Api;
using AuctionFlipper.Core;
using AuctionFlipper.Services;
using AuctionFlipper.Ui;

namespace AuctionFlipper;

/// <summary>
/// Offline checks of the parts that decide whether money is made or lost, run with
/// <c>--logictest</c> and also as the first half of <c>--selftest</c>. They spend no requests.
///
/// The cases here are the ones that actually went wrong during development, and the ones that
/// would be expensive to get wrong in use: the rate limiter must never exceed the API's cap, and a
/// valuation must never be fabricated out of a troll listing.
/// </summary>
public static class LogicTests
{
    public static int Run()
    {
        int failures = 0;
        Console.WriteLine("Offline logic checks");
        Console.WriteLine(new string('-', 66));

        failures += Check("rate limiter never exceeds its window", RateLimiterRespectsCap);
        failures += Check("idle lanes do not hoard the request budget", RateLimiterAbsorbsSlack);
        failures += Check("background scan yields to a busy sniper", RateLimiterYieldsToDemand);
        failures += Check("an active sniper always reaches its guarantee", ActiveSniperKeepsItsGuarantee);
        failures += Check("listing identity is stable across polls", FingerprintStability);
        failures += Check("sale median ignores a single absurd print", TapeMedianIsRobust);
        failures += Check("order book ranks asks by unit price", BookRanksByUnitPrice);
        failures += Check("a sold listing leaves the book", BookRemovesSold);
        failures += Check("a listing that stops appearing is dropped", BookEvictsUnseen);
        failures += Check("book scan recovers a badly wrong page estimate", ScanEdgeRecovers);
        failures += Check("a shallow book yields no valuation", ShallowBookIsNotPriced);
        failures += Check("a deep book yields a sane valuation", DeepBookIsPriced);
        failures += Check("a normal discount scores as a flip", NormalFlipScores);
        failures += Check("an impossible discount is flagged, not celebrated", AbsurdFlipIsFlagged);
        failures += Check("unpriced container contents count as worthless", ContainerIgnoresUnknownContents);
        failures += Check("a malformed page does not throw", IngestSurvivesNullRows);
        failures += Check("both languages carry the same strings", TranslationsAreComplete);
        failures += Check("a pinned item alerts below the threshold", PinnedItemsAlwaysAlert);
        failures += Check("row badges follow the chosen language", BadgesAreTranslated);
        failures += Check("settings survive a write and a reload", ConfigRoundTrips);
        failures += Check("the saved book comes back without its dead listings", BookSnapshotRoundTrips);
        failures += Check("the icon sheet covers what the market trades", IconSheetCoversTheMarket);
        failures += Check("an item's own sale history reads newest first", ItemSaleHistoryReadsNewestFirst);
        failures += Check("the scan countdown measures the rate it sees", SweepEtaTracksTheMeasuredRate);
        failures += Check("card money is grouped and free of dead cents", CardMoneyReadsAsCoins);

        Console.WriteLine(new string('-', 66));
        Console.WriteLine(failures == 0 ? "ALL LOGIC CHECKS PASSED" : $"{failures} LOGIC CHECK(S) FAILED");
        return failures;
    }

    // ------------------------------------------------------------------ rate limiter

    private static (bool, string) RateLimiterRespectsCap()
    {
        var limiter = new RateLimiter(50, [10, 10, 10, 0]);

        int admitted = 0;
        for (int i = 0; i < 400; i++)
            if (limiter.TryAcquire(Lane.Sweeper)) admitted++;

        RateSnapshot snap = limiter.Snapshot();
        return (admitted <= 50 && snap.Used <= 50,
            $"{admitted} admitted against a ceiling of 50, window reports {snap.Used}");
    }

    /// <summary>
    /// The budget must be spent, not hoarded: when nothing else is asking, the background scan
    /// should get almost all of it, while still leaving the sniper room to start immediately.
    /// </summary>
    private static (bool, string) RateLimiterAbsorbsSlack()
    {
        var limiter = new RateLimiter(100, [60, 10, 10, 0]);

        int sweeperGot = 0;
        while (limiter.TryAcquire(Lane.Sweeper)) sweeperGot++;

        int sniperGot = 0;
        while (limiter.TryAcquire(Lane.Sniper)) sniperGot++;

        bool absorbsSlack = sweeperGot >= 75;
        bool keepsHeadroom = sniperGot >= 15;
        bool respectsCap = sweeperGot + sniperGot <= 100;

        return (absorbsSlack && keepsHeadroom && respectsCap,
            $"with every other lane idle the scan took {sweeperGot} of 100 and the sniper still found {sniperGot}");
    }

    /// <summary>
    /// The reservation has to track what a lane is really using. A busy sniper should push the
    /// background scan back automatically, without anyone changing a setting.
    /// </summary>
    private static (bool, string) RateLimiterYieldsToDemand()
    {
        var limiter = new RateLimiter(100, [60, 10, 10, 0]);

        // The sniper has been polling steadily.
        int sniperBefore = 0;
        for (int i = 0; i < 20; i++)
            if (limiter.TryAcquire(Lane.Sniper)) sniperBefore++;

        int sweeperGot = 0;
        while (limiter.TryAcquire(Lane.Sweeper)) sweeperGot++;

        // It must still be able to carry on at its own rate.
        int sniperAfter = 0;
        while (limiter.TryAcquire(Lane.Sniper)) sniperAfter++;

        return (sniperAfter >= 15 && sweeperGot + sniperBefore + sniperAfter <= 100,
            $"after {sniperBefore} sniper requests the scan took {sweeperGot}, "
            + $"leaving the sniper {sniperAfter} more - more headroom than when it was idle");
    }

    /// <summary>
    /// The regression that mattered most: once the sniper is polling, the background scan must
    /// never eat the capacity it still needs to reach its guaranteed rate.
    ///
    /// Sizing reservations from measured volume made this fail in a way that hid itself - throttling
    /// the sniper lowered its measured demand, which lowered its reservation, which throttled it
    /// harder, and it settled at under a fiftieth of the listing rate while every number on screen
    /// looked healthy.
    /// </summary>
    private static (bool, string) ActiveSniperKeepsItsGuarantee()
    {
        var limiter = new RateLimiter(235, [120, 12, 20, 0]);

        int sniperUsed = 0;
        for (int i = 0; i < 5; i++)
            if (limiter.TryAcquire(Lane.Sniper)) sniperUsed++;

        int sweeperGot = 0;
        while (limiter.TryAcquire(Lane.Sweeper)) sweeperGot++;

        RateSnapshot snap = limiter.Snapshot();
        int roomLeft = 235 - snap.Used;
        int stillOwed = 120 - sniperUsed;

        return (roomLeft >= stillOwed,
            $"scan took {sweeperGot}; {roomLeft} slots still free against the {stillOwed} the sniper is owed");
    }

    // ------------------------------------------------------------------ identity

    private static (bool, string) FingerprintStability()
    {
        ulong a = Fingerprint.For(7, 3, 64, 1_250_000.00);
        ulong b = Fingerprint.For(7, 3, 64, 1_250_000.004);   // float round-trip noise
        ulong c = Fingerprint.For(7, 3, 64, 1_250_001.00);
        ulong d = Fingerprint.For(8, 3, 64, 1_250_000.00);

        return (a == b && a != c && a != d,
            "same listing matches across polls, a different price or seller does not");
    }

    // ------------------------------------------------------------------ tape

    private static (bool, string) TapeMedianIsRobust()
    {
        var tape = new SaleTape();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        for (int i = 0; i < 20; i++)
            tape.Add(new Sale(1, 1, 100 + i % 3, 0, now - i * 1000));

        // One of the trillion-coin listings the auction house is full of.
        tape.Add(new Sale(1, 1, 1_000_000_000_000, 0, now));

        ItemSaleStats stats = tape.GetStats(1, now);
        return (stats.MedianUnit is > 95 and < 110,
            $"median {stats.MedianUnit:N2} from {stats.SampleCount} sales including one at 1e12");
    }

    // ------------------------------------------------------------------ book

    private static (bool, string) BookRanksByUnitPrice()
    {
        var book = new OrderBook();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // A stack of 64 at 640k is cheaper per item than a single at 20k, which is the whole point
        // of ranking on unit price rather than listing price.
        book.Add(MakeListing(1, count: 64, price: 640_000, seller: 1, now), now);
        book.Add(MakeListing(1, count: 1, price: 20_000, seller: 2, now), now);
        book.Add(MakeListing(1, count: 1, price: 12_000, seller: 3, now), now);

        BookStats stats = book.GetStats(1);
        return (Math.Abs(stats.LowestUnit - 10_000) < 0.01 && Math.Abs(stats.SecondUnit - 12_000) < 0.01,
            $"lowest {stats.LowestUnit:N0}/unit, second {stats.SecondUnit:N0}/unit, {stats.UnitsAvailable} units offered");
    }

    private static (bool, string) BookRemovesSold()
    {
        var book = new OrderBook();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        book.Add(MakeListing(1, 64, 640_000, seller: 1, now), now);
        book.Add(MakeListing(1, 1, 20_000, seller: 2, now), now);

        bool removed = book.RemoveSold(1, 1, 64, 640_000);
        return (removed && book.ListingCount == 1,
            $"removed={removed}, {book.ListingCount} listing(s) left");
    }

    /// <summary>
    /// Nothing announces a cancelled listing, so the only evidence it is gone is that the book scan
    /// stopped finding it. A listing still being seen must survive; one that has not been seen since
    /// before the cutoff must not.
    /// </summary>
    private static (bool, string) BookEvictsUnseen()
    {
        var book = new OrderBook();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long twoHoursAgo = now - 2 * 60 * 60 * 1000;

        // Seen two hours ago and never again: presumed sold or cancelled elsewhere.
        book.Add(MakeListing(1, 1, 500, seller: 1, now), twoHoursAgo);
        // Seen on the most recent pass: still for sale.
        book.Add(MakeListing(1, 1, 700, seller: 2, now), now);

        int before = book.ListingCount;
        int evicted = book.EvictUnseen(now - 90 * 60 * 1000);
        BookStats stats = book.GetStats(1);

        bool ok = before == 2
                  && evicted == 1
                  && book.ListingCount == 1
                  && Math.Abs(stats.LowestUnit - 700) < 0.01;

        return (ok, $"{evicted} of {before} dropped; the surviving ask is {stats.LowestUnit:N0}");
    }

    /// <summary>
    /// The scan has to be able to climb back out of a wrong estimate.
    ///
    /// A transient API error once convinced it the market had shrunk from 3,155 pages to 721, and
    /// the old design had no way of ever raising that figure sharply again: three quarters of the
    /// auction house went unscanned while the progress bar read 72%.
    /// </summary>
    private static (bool, string) ScanEdgeRecovers()
    {
        const int trueEdge = 3242;
        int probes = 0;

        Task<bool> Exists(int page)
        {
            probes++;
            return Task.FromResult(page <= trueEdge);
        }

        // Recovering upwards from the bad estimate the bug produced.
        int fromTooLow = SweeperService.FindEdgeAsync(721, Exists, () => probes < 18)
            .GetAwaiter().GetResult();

        int probesLow = probes;
        probes = 0;

        // And settling back down from an estimate that overshoots.
        int fromTooHigh = SweeperService.FindEdgeAsync(9000, Exists, () => probes < 18)
            .GetAwaiter().GetResult();

        bool up = fromTooLow > trueEdge * 0.93 && fromTooLow <= trueEdge;
        bool down = fromTooHigh > trueEdge * 0.93 && fromTooHigh <= trueEdge;

        return (up && down,
            $"721 -> {fromTooLow} in {probesLow} probes, 9000 -> {fromTooHigh} in {probes} (true edge {trueEdge})");
    }

    // ------------------------------------------------------------------ valuation

    private static (bool, string) ShallowBookIsNotPriced()
    {
        var book = new OrderBook();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        book.Add(MakeListing(1, 1, 500, seller: 1, now), now);
        book.Add(MakeListing(1, 1, 1_000_000_000_000, seller: 2, now), now);

        ItemValue value = Valuation.Compute(ItemSaleStats.Empty, book.GetStats(1));
        return (value.Source == ValueSource.None,
            $"two asks, one of them absurd - value source is {value.Source}");
    }

    private static (bool, string) DeepBookIsPriced()
    {
        var book = new OrderBook();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        for (int i = 0; i < 10; i++)
            book.Add(MakeListing(1, 1, 1_000 + i * 50, seller: i + 1, now), now);
        book.Add(MakeListing(1, 1, 1_000_000_000_000, seller: 99, now), now);

        ItemValue value = Valuation.Compute(ItemSaleStats.Empty, book.GetStats(1));
        return (value.Source == ValueSource.Book && value.Unit is > 900 and < 1_600,
            $"value {value.Unit:N0} from the ask ladder, unaffected by the 1e12 listing");
    }

    // ------------------------------------------------------------------ scoring

    private static (bool, string) NormalFlipScores()
    {
        var scorer = new FlipScorer(taxRate: 0, capital: 0);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Listing listing = MakeListing(1, count: 64, price: 640_000, seller: 1, now);
        var sales = new ItemSaleStats(40, 14_000, 13_000, 15_000, 0.04, 12, 400, 0, now);
        var bookStats = new BookStats(30, 10_000, 13_500, 14_500, 14_000, 13_800, 4_000);
        var value = new ItemValue(14_000, ValueSource.Sales, sales, bookStats);

        FlipOpportunity? flip = scorer.Score(
            listing, "minecraft:diamond", "someone", value,
            nextAskUnit: 13_500, depthNearValue: 12, sellerConcentration: 1, now);

        if (flip is null) return (false, "a clearly profitable flip was rejected");

        bool ok = flip.NetProfit > 0
                  && flip.Roi is > 0.2 and < 1.0
                  && flip.Confidence > 60
                  && !flip.Flags.HasFlag(FlipFlags.TooGoodToBeTrue);

        return (ok, $"net {flip.NetProfit:N0}, ROI {flip.Roi:P0}, confidence {flip.Confidence:N0}, grade {flip.Grade}");
    }

    private static (bool, string) AbsurdFlipIsFlagged()
    {
        var scorer = new FlipScorer(taxRate: 0, capital: 0);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Priced at a thousandth of its supposed value: a broken reference, not a bargain.
        Listing listing = MakeListing(1, count: 1, price: 1_000, seller: 1, now);
        var sales = new ItemSaleStats(40, 1_000_000, 900_000, 1_100_000, 0.05, 10, 10, 0, now);
        var bookStats = new BookStats(30, 1_000, 990_000, 1_000_000, 1_000_000, 990_000, 30);
        var value = new ItemValue(1_000_000, ValueSource.Sales, sales, bookStats);

        FlipOpportunity? flip = scorer.Score(
            listing, "minecraft:diamond", "someone", value,
            nextAskUnit: 990_000, depthNearValue: 12, sellerConcentration: 1, now);

        if (flip is null) return (true, "rejected outright");

        bool ok = flip.Flags.HasFlag(FlipFlags.TooGoodToBeTrue)
                  && flip.Confidence < 25
                  && flip.Grade == FlipGrade.C;

        return (ok, $"flagged={flip.Flags.HasFlag(FlipFlags.TooGoodToBeTrue)}, "
                    + $"confidence {flip.Confidence:N0}, grade {flip.Grade}");
    }

    private static (bool, string) ContainerIgnoresUnknownContents()
    {
        var known = new ItemSaleStats(40, 5_000, 4_800, 5_200, 0.03, 10, 640, 0, 0);
        var contents = ContainerValuer.Build([(1, 64), (2, 64)]);

        ContainerValuation valuation = ContainerValuer.Value(
            contents,
            // Item 1 has real sale history; item 2 has only a wild ask price behind it.
            index => index == 1
                ? new ItemValue(5_000, ValueSource.Sales, known, default)
                : new ItemValue(9_000_000, ValueSource.Book, ItemSaleStats.Empty, default),
            index => index == 1 ? "minecraft:diamond" : "minecraft:dirt",
            haircut: 0.9);

        bool ok = valuation.ValuedItems == 64
                  && valuation.UnvaluedItems == 64
                  && Math.Abs(valuation.GrossValue - 320_000) < 1
                  // The only priced line is the whole of the value, so it is a single-item bet.
                  && valuation.DominantShare > 0.99;

        return (ok, $"gross {valuation.GrossValue:N0} from {valuation.ValuedItems} priced items, "
                    + $"{valuation.UnvaluedItems} ignored");
    }

    /// <summary>
    /// A page with null rows must be ingested, not thrown over.
    ///
    /// This is the defect it was written for: a null element in the listings array raised a null
    /// reference deep inside the ingest loop, the exception escaped the sweeper's per-page handler,
    /// and the whole scan cycle restarted from zero coverage. One unusable row cost three thousand
    /// pages of progress.
    /// </summary>
    private static (bool, string) IngestSurvivesNullRows()
    {
        var market = new MarketState(new AppConfig());

        AhEntry[] page =
        [
            null!,
            new AhEntry { Item = null, Price = 100, TimeLeft = 3_600_000 },
            new AhEntry
            {
                Item = new ApiItem { Id = "minecraft:diamond", Count = 64, Contents = [null!] },
                Price = 640_000,
                TimeLeft = 3_600_000,
            },
        ];

        int added;
        try
        {
            added = market.IngestListings(page, fromSniper: false);
        }
        catch (Exception ex)
        {
            return (false, $"threw {ex.GetType().Name}: {ex.Message}");
        }

        return (added == 1, $"{added} of 3 rows ingested, the other two skipped without throwing");
    }


    // ------------------------------------------------------------------ localisation

    /// <summary>
    /// The two string tables must match key for key and placeholder for placeholder.
    ///
    /// A missing key is invisible at runtime - it falls back to English, which looks like an
    /// oversight rather than a bug - and a placeholder that disagrees between the two is worse than
    /// invisible: string.Format throws when it is finally shown, which for a status message means
    /// an exception raised in front of the user rather than at build time.
    /// </summary>
    private static (bool, string) TranslationsAreComplete()
    {
        string[] untranslated = Loc.UntranslatedKeys();
        string[] orphans = Loc.OrphanKeys();
        string[] mismatched = Loc.PlaceholderMismatches();

        bool ok = untranslated.Length == 0 && orphans.Length == 0 && mismatched.Length == 0;

        string detail = ok
            ? $"{Loc.KeyCount} keys in both English and German, placeholders agree"
            : $"untranslated [{string.Join(", ", untranslated)}] "
              + $"orphaned [{string.Join(", ", orphans)}] "
              + $"placeholder mismatch [{string.Join(", ", mismatched)}]";

        return (ok, detail);
    }

    /// <summary>
    /// The badges are the most-read text on the board and were the last place English survived a
    /// switch to German. Reading them in both languages is the only way to catch a literal that
    /// crept back in, since a hard-coded string passes every completeness check in the table.
    /// </summary>
    private static (bool, string) BadgesAreTranslated()
    {
        const FlipFlags sample = FlipFlags.ThinBook | FlipFlags.Volatile | FlipFlags.TrendingDown
                                 | FlipFlags.TooGoodToBeTrue | FlipFlags.Unverified;

        string original = Loc.Current.Language;
        try
        {
            Loc.Current.SetLanguage("en");
            string[] english = Format.Badges(sample).ToArray();

            Loc.Current.SetLanguage("de");
            string[] german = Format.Badges(sample).ToArray();

            // Every badge in this sample is prose, so none of them may survive the switch unchanged.
            int shared = english.Intersect(german, StringComparer.Ordinal).Count();

            return (english.Length == 5 && german.Length == 5 && shared == 0,
                $"english [{string.Join(" ", english)}] german [{string.Join(" ", german)}]");
        }
        finally
        {
            Loc.Current.SetLanguage(original);
        }
    }

    /// <summary>
    /// The icon sheet is built ahead of time from a fixed list, so the failure it can actually
    /// have is silence: a resource that did not get embedded, or an index that no longer lines up
    /// with the picture. Both show up as items losing their icon, which is easy to miss on a board
    /// that still renders. This checks the sheet is present, that a handful of ids every DonutSMP
    /// session sees resolve, and that no cell points off the end of the sheet.
    /// </summary>
    /// <summary>
    /// The per-item read behind the sale popup: newest sale first, stacks intact, and no bleed
    /// from a neighbouring item. The series it reads is a ring buffer, so an off-by-one in the
    /// wrap would quietly hand back the oldest sale as if it were the newest.
    /// </summary>
    private static (bool, string) ItemSaleHistoryReadsNewestFirst()
    {
        var tape = new SaleTape();
        const long t0 = 1_700_000_000_000;

        for (int i = 0; i < 5; i++)
            tape.Add(new Sale(7, i + 1, 100 * (i + 1), 1, t0 + i * 1_000));

        tape.Add(new Sale(9, 64, 64_000, 1, t0 + 500));

        (long Time, double UnitPrice, int Count)[] history = tape.RecentSalesFor(7, 10);

        if (history.Length != 5)
            return (false, $"{history.Length} sales read back, expected 5");

        for (int i = 1; i < history.Length; i++)
        {
            if (history[i].Time > history[i - 1].Time)
                return (false, "the history came back oldest first");
        }

        if (history[0].Count != 5 || Math.Abs(history[0].UnitPrice - 100) > 0.001)
            return (false, $"newest sale is x{history[0].Count} at {history[0].UnitPrice}");

        if (tape.RecentSalesFor(9, 10).Length != 1)
            return (false, "one item's history leaked into another's");

        if (tape.RecentSalesFor(1234, 10).Length != 0)
            return (false, "an item that never sold reported a history");

        return (true, $"{history.Length} sales newest first, newest x{history[0].Count}");
    }

    /// <summary>
    /// The countdown in the header. It divides by a measured rate, so the cases that matter are the
    /// ones where there is no rate to measure: a scan that has just started, one that is not
    /// moving, and the moment a finished cycle starts counting from zero again.
    /// </summary>
    private static (bool, string) SweepEtaTracksTheMeasuredRate()
    {
        var eta = new SweepEta();
        const long t0 = 1_700_000_000_000;
        const int total = 3_000;

        // Too short a sample to divide by.
        if (eta.Estimate(t0, 0, total) is not null) return (false, "estimated from a single sample");
        if (eta.Estimate(t0 + 3_000, 20, total) is not null) return (false, "estimated from three seconds");

        // Four pages a second for a minute: 2,760 pages left, so about eleven and a half minutes.
        for (int second = 4; second <= 60; second++)
            eta.Estimate(t0 + second * 1_000, second * 4, total);

        TimeSpan? measured = eta.Estimate(t0 + 60_000, 240, total);
        if (measured is not { } left) return (false, "no estimate after a minute of scanning");
        if (Math.Abs(left.TotalSeconds - 690) > 30)
            return (false, $"estimated {left.TotalSeconds:0}s, expected about 690s");

        // A scan that stops moving reports no estimate rather than a stale one.
        SweepEta stalled = new();
        for (int second = 0; second <= 90; second++)
            stalled.Estimate(t0 + second * 1_000, 500, total);
        if (stalled.Estimate(t0 + 90_000, 500, total) is not null)
            return (false, "a stalled scan still reported an estimate");

        // A new cycle counts from zero; the previous cycle's samples must not survive it.
        if (eta.Estimate(t0 + 61_000, 4, total) is not null)
            return (false, "the estimate carried over into the next cycle");

        // A finished sweep is zero, not a rate problem.
        var done = new SweepEta();
        if (done.Estimate(t0, total, total) != TimeSpan.Zero)
            return (false, "a completed sweep did not read as finished");

        return (true, $"{left.TotalSeconds:0}s left at 4 pages/s with {total - 240:N0} to go");
    }

    private static (bool, string) IconSheetCoversTheMarket()
    {
        string[] staples =
        [
            "minecraft:diamond", "minecraft:netherite_ingot", "minecraft:gold_block",
            "minecraft:enchanted_book", "minecraft:shulker_box", "minecraft:wind_charge",
            "minecraft:cooked_beef", "minecraft:waxed_copper_block", "minecraft:music_disc_cat",
        ];

        var unresolved = staples.Where(id => !ItemIconAtlas.TryGetCell(id, out _)).ToArray();
        if (unresolved.Length > 0)
            return (false, $"no icon for {string.Join(", ", unresolved)}");

        if (ItemIconAtlas.MappedIds < 1_000)
            return (false, $"sheet index holds only {ItemIconAtlas.MappedIds} ids");

        // Every mapped cell must sit inside the sheet the index claims.
        foreach (string id in staples)
        {
            ItemIconAtlas.TryGetCell(id, out int cell);
            if (cell < 0 || cell >= ItemIconAtlas.Count)
                return (false, $"{id} points at cell {cell} of {ItemIconAtlas.Count}");
        }

        byte[]? sheet = ItemIconAtlas.ReadSheetBytes();
        if (sheet is null || sheet.Length < 1024)
            return (false, "the sheet image is missing from the build");

        return (true, $"{ItemIconAtlas.MappedIds} ids over {ItemIconAtlas.Count} icons");
    }

    // ------------------------------------------------------------------ persistence

    /// <summary>
    /// The settings file is the only memory the tool has of what the user chose, and a pin that
    /// does not come back is indistinguishable from a pin that was never made. This writes a config
    /// with every awkward field set - a list, a language, a window rectangle - reads it back, and
    /// also checks the dirty flag, since the timed autosave only writes when that flag is set.
    /// </summary>
    private static (bool, string) ConfigRoundTrips()
    {
        string path = Path.Combine(Path.GetTempPath(), $"auctionflipper-config-{Guid.NewGuid():N}.json");

        try
        {
            var written = new AppConfig
            {
                Language = "de",
                PinnedItems = ["minecraft:netherite_ingot", "minecraft:wind_charge"],
                BoardSort = "Roi",
                LastSection = "Pinned",
                ShowPerUnit = true,
                DetailPanelWidth = 455,
                WindowLeft = -1280,
                WindowTop = 120,
                WindowWidth = 1600,
                WindowHeight = 980,
                WindowMaximized = true,
                MinNetProfit = 77_000,
                WarmStart = false,
            };

            bool dirtyBeforeSave = written.IsDirty;
            written.SaveTo(path);
            bool cleanAfterSave = !written.IsDirty;

            AppConfig read = AppConfig.LoadFrom(path);
            bool cleanAfterLoad = !read.IsDirty;

            // Assigning a property the value it already holds must not dirty the file, or the
            // autosave would rewrite it every few seconds for as long as the app is open.
            read.Language = "de";
            bool noSpuriousDirty = !read.IsDirty;

            read.Language = "en";
            bool dirtyOnRealChange = read.IsDirty;

            bool restored =
                read.PinnedItems.Count == 2
                && read.PinnedItems[0] == "minecraft:netherite_ingot"
                && read.BoardSort == "Roi"
                && read.LastSection == "Pinned"
                && read.ShowPerUnit
                && Math.Abs(read.DetailPanelWidth - 455) < 0.001
                && Math.Abs(read.WindowLeft + 1280) < 0.001
                && Math.Abs(read.WindowWidth - 1600) < 0.001
                && read.WindowMaximized
                && Math.Abs(read.MinNetProfit - 77_000) < 0.001
                && !read.WarmStart;

            bool ok = dirtyBeforeSave && cleanAfterSave && cleanAfterLoad
                      && noSpuriousDirty && dirtyOnRealChange && restored;

            return (ok,
                $"{read.PinnedItems.Count} pin(s), section {read.LastSection}, "
                + $"window {read.WindowWidth}x{read.WindowHeight}, dirty tracking "
                + (noSpuriousDirty && dirtyOnRealChange ? "correct" : "wrong"));
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The warm start is only worth having if what comes back is true. A listing that has expired
    /// since the file was written must not reappear, what does come back must be marked unverified
    /// so the board can say so, and the ids must survive the trip - the snapshot carries its own
    /// name table precisely so a restored price cannot end up attached to the wrong item.
    /// </summary>
    private static (bool, string) BookSnapshotRoundTrips()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"auctionflipper-book-{Guid.NewGuid():N}");

        try
        {
            var saved = new MarketState(new AppConfig());
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            int ingot = saved.Items.GetOrAdd("minecraft:netherite_ingot");
            int charge = saved.Items.GetOrAdd("minecraft:wind_charge");
            int seller = saved.Sellers.GetOrAdd("Yukitus");

            saved.Book.Add(MakeListing(ingot, 4, 6_000_000, seller, now), now);

            // Already past its expiry by the time it is read back.
            saved.Book.Add(new Listing
            {
                Fingerprint = Fingerprint.For(seller, charge, 16, 48_000),
                ItemIndex = charge,
                Count = 16,
                Price = 48_000,
                SellerIndex = seller,
                ListedAtUnixMs = now - MarketConstants.ListingLifetimeMs + 60_000,
                ExpiresAtUnixMs = now - 1,
            }, now);

            using (var writer = new Persistence(directory))
                writer.SaveBook(saved);

            // A second, independent market, with its registries filled in a different order on
            // purpose: if the snapshot leaned on positional indexes, this is where the prices would
            // cross over onto the wrong items.
            var loaded = new MarketState(new AppConfig());
            loaded.Items.GetOrAdd("minecraft:dirt");
            loaded.Sellers.GetOrAdd("SomebodyElse");

            int count;
            using (var reader = new Persistence(directory))
                count = reader.LoadBook(loaded, TimeSpan.FromHours(6));

            int loadedIngot = loaded.Items.GetOrAdd("minecraft:netherite_ingot");
            Listing[] ladder = loaded.Book.Ladder(loadedIngot, 4);

            bool onlyLiveOne = count == 1 && loaded.Book.ListingCount == 1 && ladder.Length == 1;
            bool samePrice = onlyLiveOne && Math.Abs(ladder[0].Price - 6_000_000) < 0.001
                             && ladder[0].Count == 4;
            bool marked = onlyLiveOne && ladder[0].Restored;
            bool sellerKept = onlyLiveOne && loaded.Sellers.GetName(ladder[0].SellerIndex) == "Yukitus";

            // An old file is worse than no file: the board would spend the session advertising
            // listings that sold hours ago.
            var stale = new MarketState(new AppConfig());
            int refused;
            using (var reader = new Persistence(directory))
                refused = reader.LoadBook(stale, TimeSpan.Zero);

            bool ok = onlyLiveOne && samePrice && marked && sellerKept && refused == 0;

            return (ok,
                $"{count} of 2 listings restored (expired one dropped), unverified={marked}, "
                + $"seller={(sellerKept ? "kept" : "lost")}, stale file restored {refused}");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // ------------------------------------------------------------------ alerts

    /// <summary>
    /// Pinning an item is a standing request to be told about it, so the thresholds that keep the
    /// general feed quiet must not apply to it - while an unpinned flip of the same size stays
    /// silent, which is the half of this that actually proves the threshold still works.
    /// </summary>
    private static (bool, string) PinnedItemsAlwaysAlert()
    {
        var config = new AppConfig
        {
            AlertPinnedAlways = true,
            AlertMinGrade = 0,             // S only
            AlertMinNetProfit = 5_000_000, // far above the test flip
            AlertCooldownSeconds = 0,
            AlertSoundEnabled = false,
        };

        var alerts = new AlertService(config) { IsPinned = id => id == "minecraft:diamond" };

        var raised = new List<string>();
        alerts.AlertRaised += flip => raised.Add(flip.ItemId);

        var scorer = new FlipScorer(taxRate: 0, capital: 0);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sales = new ItemSaleStats(40, 14_000, 13_000, 15_000, 0.04, 10, 640, 0, now);
        var bookStats = new BookStats(30, 10_000, 13_600, 14_200, 14_000, 13_600, 30);
        var value = new ItemValue(14_000, ValueSource.Sales, sales, bookStats);

        FlipOpportunity? pinned = scorer.Score(
            MakeListing(1, count: 64, price: 640_000, seller: 1, now),
            "minecraft:diamond", "someone", value,
            nextAskUnit: 13_500, depthNearValue: 12, sellerConcentration: 1, now);

        FlipOpportunity? unpinned = scorer.Score(
            MakeListing(1, count: 64, price: 641_000, seller: 2, now),
            "minecraft:emerald", "someone", value,
            nextAskUnit: 13_500, depthNearValue: 12, sellerConcentration: 1, now);

        if (pinned is null || unpinned is null) return (false, "the test flips did not score");

        alerts.Consider(pinned);
        alerts.Consider(unpinned);

        bool ok = raised.Contains("minecraft:diamond") && !raised.Contains("minecraft:emerald");
        return (ok, raised.Count == 0 ? "nothing alerted at all" : $"alerted on [{string.Join(", ", raised)}]");
    }

    /// <summary>
    /// The hover card prints the full figure rather than the board's three significant digits, and
    /// it is the one place in the tool where a number is read digit by digit. Two decimal places on
    /// a six-figure price are two characters of noise in front of the digits that matter, so they
    /// are dropped above a thousand coins and kept below it, where they are the whole price.
    /// </summary>
    private static (bool, string) CardMoneyReadsAsCoins()
    {
        string lot = Format.Grouped(200_000);
        string unit = Format.Grouped(7_796.88);
        string small = Format.Grouped(3.5);
        string net = Format.GroupedSigned(299_000);
        string loss = Format.GroupedSigned(-1_250.5);

        bool ok = lot == "200,000" && unit == "7,797" && small == "3.5"
                  && net == "+299,000" && loss == "-1,251";

        return (ok, $"{lot} / {unit} / {small} / {net} / {loss}");
    }

    // ------------------------------------------------------------------ helpers

    private static Listing MakeListing(int itemIndex, int count, double price, int seller, long now) => new()
    {
        Fingerprint = Fingerprint.For(seller, itemIndex, count, price),
        ItemIndex = itemIndex,
        Count = count,
        Price = price,
        SellerIndex = seller,
        ListedAtUnixMs = now,
        ExpiresAtUnixMs = now + MarketConstants.ListingLifetimeMs,
    };

    private static int Check(string name, Func<(bool Ok, string Detail)> probe)
    {
        Console.Write($"  {name,-52} ");
        try
        {
            (bool ok, string detail) = probe();
            Console.WriteLine(ok ? "PASS" : "FAIL");
            if (detail.Length > 0) Console.WriteLine($"      {detail}");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL");
            Console.WriteLine($"      {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
