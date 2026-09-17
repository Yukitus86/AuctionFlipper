using AuctionFlipper.Api;
using AuctionFlipper.Core;
using AuctionFlipper.Services;

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
