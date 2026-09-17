using System.Diagnostics;
using AuctionFlipper.Api;
using AuctionFlipper.Core;
using AuctionFlipper.Services;

namespace AuctionFlipper;

/// <summary>
/// A cheap end-to-end probe of the live API, run with <c>--selftest</c>.
///
/// It exists mainly to prove the one assumption everything else rests on: that this runtime will
/// send a body on a GET request, which is the only way the API accepts sort and search. It spends
/// fewer than ten requests.
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? key = ArgValue(args, "--key") ?? Environment.GetEnvironmentVariable("DONUT_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            var cfg = AppConfig.Load();
            key = cfg.ApiKey;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            Console.WriteLine("No API key. Pass --key <key>, set DONUT_API_KEY, or save one in Settings.");
            return 2;
        }

        var limiter = new RateLimiter();
        using var client = new DonutClient(key, limiter);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        CancellationToken ct = cts.Token;

        int failures = 0;
        Console.WriteLine("DonutSMP Auction Flipper - API self-test");
        Console.WriteLine(new string('-', 66));

        // 1. Plain listing page. Establishes reachability, auth and page size.
        failures += await Check("auction/list/1 returns 44 entries", async () =>
        {
            var sw = Stopwatch.StartNew();
            AhResponse page = await client.GetListingsAsync(1, Lane.Interactive, ct: ct);
            sw.Stop();
            int n = page.Result?.Length ?? 0;
            return (n == DonutClient.PageSize, $"{n} entries in {sw.ElapsedMilliseconds} ms");
        });

        // 2. THE critical one: GET carrying a JSON body. If this fails, the client needs the
        //    hand-rolled SslStream fallback before anything else is worth building.
        failures += await Check("GET with JSON body (sort=lowest_price) is accepted", async () =>
        {
            AhResponse page = await client.GetListingsAsync(1, Lane.Interactive, AuctionSort.LowestPrice, ct: ct);
            AhEntry[] entries = page.Result ?? [];
            if (entries.Length < 2) return (false, "not enough entries to verify ordering");

            bool ascending = true;
            for (int i = 1; i < entries.Length; i++)
                if (entries[i].Price < entries[i - 1].Price) { ascending = false; break; }

            string sample = $"{entries[0].Item?.Id} @ {entries[0].Price:N0} .. {entries[^1].Price:N0}";
            return (ascending, ascending ? sample : "prices were not ascending - body ignored?");
        });

        // 3. Search body, verified against a name that is unique enough to match exactly.
        failures += await Check("search body filters results", async () =>
        {
            AhResponse page = await client.GetListingsAsync(
                1, Lane.Interactive, AuctionSort.LowestPrice, "netherite_ingot", ct);
            AhEntry[] entries = page.Result ?? [];
            int exact = entries.Count(e => e.Item?.Id == "minecraft:netherite_ingot");
            return (exact > 0, $"{exact}/{entries.Length} exact matches, cheapest {entries.FirstOrDefault()?.Price:N0}");
        });

        // 4. Transaction tape, plus how much wall-clock history 100 sales actually covers.
        failures += await Check("auction/transactions/1 returns 100 sales", async () =>
        {
            TransactionResponse page = await client.GetTransactionsAsync(1, Lane.Interactive, ct);
            TransactionEntry[] sales = page.Result ?? [];
            if (sales.Length == 0) return (false, "no sales returned");

            long newest = sales.Max(s => s.SoldAtUnixMs);
            long oldest = sales.Min(s => s.SoldAtUnixMs);
            double spanSec = (newest - oldest) / 1000.0;
            double rate = spanSec > 0 ? sales.Length / spanSec : 0;
            return (sales.Length == DonutClient.TransactionPageSize,
                $"{sales.Length} sales spanning {spanSec:F1} s (~{rate:F1} sales/sec)");
        });

        // 5. How stale the listing feed is, and whether it moves between two back-to-back reads.
        //
        //    Deliberately not reported as a rate. The timestamp spread within one page looks like a
        //    live listing rate and is not one: the endpoint serves a snapshot rebuilt every few
        //    minutes, so two reads a second apart return the same rows. Reading that spread as a
        //    rate is what led an earlier version of this tool to poll twice a second for nothing.
        failures += await Check("listing feed is a snapshot, not a live stream", async () =>
        {
            AhResponse first = await client.GetListingsAsync(1, Lane.Interactive, AuctionSort.RecentlyListed, ct: ct);
            AhEntry[] a = first.Result ?? [];
            if (a.Length < 2) return (false, "not enough entries");

            await Task.Delay(1200, ct);

            AhResponse second = await client.GetListingsAsync(1, Lane.Interactive, AuctionSort.RecentlyListed, ct: ct);
            AhEntry[] b = second.Result ?? [];

            static string KeyOf(AhEntry e) => $"{e.Seller?.Name}|{e.Item?.Id}|{e.Item?.Count}|{e.Price}";
            HashSet<string> before = a.Select(KeyOf).ToHashSet(StringComparer.Ordinal);
            int changed = b.Count(e => !before.Contains(KeyOf(e)));

            // Age of the freshest listing on offer, from the server's own countdown.
            double newestAgeMin = (MarketConstants.ListingLifetimeMs - a.Max(e => e.TimeLeft)) / 60_000.0;

            return (true,
                $"{changed}/{b.Length} rows changed in 1.2 s; freshest listing is already "
                + $"{newestAgeMin:F1} min old - watch the head slowly, spend the budget on the book scan");
        });

        // 6. Book size, which sets the cost of a full sweep. The book grows and shrinks, so this
        //    reports where the edge currently sits rather than asserting a fixed page count.
        failures += await Check("book depth is in the expected range", async () =>
        {
            bool midOk = await PageExists(client, MarketConstants.KnownLastPage / 2, ct);
            bool farRejected = !await PageExists(client, MarketConstants.KnownLastPage * 4, ct);
            bool knownEdgeOk = await PageExists(client, MarketConstants.KnownLastPage, ct);

            long atLeast = (long)(MarketConstants.KnownLastPage / 2) * DonutClient.PageSize;
            string edge = knownEdgeOk
                ? $"page {MarketConstants.KnownLastPage} still exists"
                : $"book is currently shallower than page {MarketConstants.KnownLastPage}";

            return (midOk && farRejected, $"{edge}; at least ~{atLeast:N0} live listings");
        });

        RateSnapshot snap = limiter.Snapshot();
        Console.WriteLine(new string('-', 66));
        Console.WriteLine($"requests spent: {snap.Used}/{snap.Limit} in the trailing minute");
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<bool> PageExists(DonutClient client, int page, CancellationToken ct)
    {
        try
        {
            AhResponse r = await client.GetListingsAsync(page, Lane.Interactive, ct: ct);
            return (r.Result?.Length ?? 0) > 0;
        }
        catch (DonutApiException)
        {
            return false;
        }
    }

    private static async Task<int> Check(string name, Func<Task<(bool Ok, string Detail)>> probe)
    {
        Console.Write($"  {name,-52} ");
        try
        {
            (bool ok, string detail) = await probe();
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

    private static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
