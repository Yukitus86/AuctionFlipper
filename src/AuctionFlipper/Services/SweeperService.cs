using AuctionFlipper.Api;
using AuctionFlipper.Core;

namespace AuctionFlipper.Services;

public sealed record SweeperStatus(
    int PagesVisited,
    int EstimatedTotalPages,
    double CoverageFraction,
    long ListingsSeen,
    TimeSpan LastCycleDuration,
    bool Running);

/// <summary>
/// Fills in the standing order book using whatever request budget the live feeds leave behind.
///
/// The listing watcher only sees the head of the market. Everything else - and the cheapest listing
/// for an item, the only one that can ever be a flip, is frequently an old one - is invisible until
/// somebody goes and looks. This is what goes and looks.
///
/// Pages are visited on a golden-ratio stride rather than in order. A sweep of ~3,200 pages takes
/// the better part of twenty minutes and can be interrupted at any point, so walking 1, 2, 3...
/// would mean a partial sweep knows the front of the book thoroughly and the rest not at all.
/// Striding spreads every partial sweep evenly across the whole market, which makes the depth and
/// price statistics it feeds unbiased from the first minute.
/// </summary>
public sealed class SweeperService
{
    /// <summary>Requests an edge probe may spend. A handful, once per cycle, is nothing.</summary>
    private const int MaxProbeRequests = 18;

    private readonly DonutClient _client;
    private readonly MarketState _market;

    private int _estimatedTotalPages = MarketConstants.KnownLastPage;
    private int _pagesVisited;
    private long _listingsSeen;
    private TimeSpan _lastCycle;
    private bool _running;
    private int _probeRequests;

    public SweeperService(DonutClient client, MarketState market)
    {
        _client = client;
        _market = market;
    }

    public bool Enabled { get; set; } = true;

    public SweeperStatus Status => new(
        _pagesVisited,
        _estimatedTotalPages,
        _estimatedTotalPages > 0 ? Math.Min(1.0, (double)_pagesVisited / _estimatedTotalPages) : 0,
        Interlocked.Read(ref _listingsSeen),
        _lastCycle,
        _running);

    public event Action<Exception>? Faulted;

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!Enabled)
            {
                _running = false;
                await Task.Delay(1_000, ct).ConfigureAwait(false);
                continue;
            }

            _running = true;
            DateTime cycleStart = DateTime.UtcNow;

            try
            {
                _estimatedTotalPages = await ProbeEdgeAsync(ct).ConfigureAwait(false);
                await SweepCycleAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Faulted?.Invoke(ex);
                await Task.Delay(5_000, ct).ConfigureAwait(false);
            }

            _lastCycle = DateTime.UtcNow - cycleStart;
        }
    }

    /// <summary>
    /// Finds how deep the book currently runs, once per cycle, by expanding from the last estimate
    /// and then bisecting.
    ///
    /// An earlier version inferred this during the sweep instead: a page that failed was taken as
    /// the edge, and the estimate was pulled in to match. That turned out badly. A single transient
    /// error in the middle of the book convinced it the market had shrunk from 3,155 pages to 721,
    /// and because nothing ever raised the figure sharply again, three quarters of the auction house
    /// stopped being scanned at all - silently, with the progress bar reporting a healthy 72%.
    ///
    /// So the edge is now established deliberately, with a probe that costs a dozen requests every
    /// twenty minutes, and a page is only believed missing after it has failed twice.
    /// </summary>
    private async Task<int> ProbeEdgeAsync(CancellationToken ct)
    {
        _probeRequests = 0;

        // The search itself is pure and lives in FindEdge so it can be tested directly; this only
        // supplies the "does this page answer" predicate.
        return await FindEdgeAsync(
            _estimatedTotalPages,
            page => PageExistsAsync(page, ct),
            () => _probeRequests < MaxProbeRequests).ConfigureAwait(false);
    }

    /// <summary>
    /// Expands outwards from a starting guess and then bisects to locate the last page that answers.
    ///
    /// Starting from the previous estimate rather than from scratch keeps the usual cost to a couple
    /// of requests, while the outward expansion is what lets a badly wrong estimate recover - which
    /// is exactly what the old design could not do.
    /// </summary>
    internal static async Task<int> FindEdgeAsync(
        int startEstimate,
        Func<int, Task<bool>> pageExists,
        Func<bool> hasBudget)
    {
        int start = Math.Max(1, startEstimate);
        int lo, hi;

        if (await pageExists(start).ConfigureAwait(false))
        {
            lo = start;
            hi = Math.Max(lo + 1, lo * 2);

            while (hasBudget() && await pageExists(hi).ConfigureAwait(false))
            {
                lo = hi;
                hi *= 2;
            }
        }
        else
        {
            // It may genuinely be shallower - or that page may simply have failed. Bisect downwards
            // rather than trusting one failure as the edge.
            hi = start;
            lo = 1;
        }

        // Bisect until the bracket is within a couple of percent; more precision buys nothing.
        while (hi - lo > Math.Max(8, lo / 50) && hasBudget())
        {
            int mid = lo + (hi - lo) / 2;
            if (await pageExists(mid).ConfigureAwait(false)) lo = mid;
            else hi = mid;
        }

        return Math.Max(64, lo);
    }

    /// <summary>
    /// Whether a page answers. Requires two failures before reporting absence, because the API
    /// returns the occasional transient error on pages that plainly do exist.
    /// </summary>
    private async Task<bool> PageExistsAsync(int page, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!await WaitForSweeperSlotAsync(ct).ConfigureAwait(false))
                return true;   // could not check; assume unchanged rather than shrink on no evidence

            _probeRequests++;

            try
            {
                AhResponse response = await _client.GetListingsAsyncPreAuthorized(page, ct).ConfigureAwait(false);
                AhEntry[] entries = response.Result ?? [];
                if (entries.Length > 0)
                {
                    // A probe still returns real listings, so keep them.
                    _market.IngestListings(entries, fromSniper: false);
                    Interlocked.Add(ref _listingsSeen, entries.Length);
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Fall through and try once more before believing it. A parse failure is treated
                // the same as an API error: either way this attempt proved nothing about whether
                // the page exists, and the edge probe must not abort the cycle over it.
            }
        }

        return false;
    }

    private async Task<bool> WaitForSweeperSlotAsync(CancellationToken ct)
    {
        for (int i = 0; i < 25; i++)
        {
            if (_client.Limiter.TryAcquire(Lane.Sweeper)) return true;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return false;
    }

    private async Task SweepCycleAsync(CancellationToken ct)
    {
        int total = _estimatedTotalPages;
        int stride = CoprimeStride(total);
        int cursor = 0;
        _pagesVisited = 0;

        for (int step = 0; step < total && !ct.IsCancellationRequested; step++)
        {
            if (!Enabled) return;

            cursor = (cursor + stride) % total;
            int page = cursor + 1;

            // Best-effort only: the scan never queues behind the live feeds, it just skips a beat
            // whenever the budget is committed elsewhere.
            if (!_client.Limiter.TryAcquire(Lane.Sweeper))
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
                step--;
                continue;
            }

            try
            {
                AhResponse response = await _client
                    .GetListingsAsyncPreAuthorized(page, ct)
                    .ConfigureAwait(false);

                AhEntry[] entries = response.Result ?? [];
                _market.IngestListings(entries, fromSniper: false);
                Interlocked.Add(ref _listingsSeen, entries.Length);
                _pagesVisited++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One page failed. That is all it means - the edge is established by the probe at
                // the start of each cycle, never inferred from a failure here.
                //
                // Every failure is caught, not just DonutApiException: a malformed page that threw
                // while being parsed used to escape to RunAsync, which abandoned the cycle and
                // restarted the scan at zero coverage. A single bad row is not worth re-reading
                // three thousand pages for.
                _pagesVisited++;
            }
        }
    }

    /// <summary>
    /// A stride near the golden ratio of the page count, adjusted to be coprime with it so the
    /// walk visits every page exactly once before repeating.
    /// </summary>
    private static int CoprimeStride(int total)
    {
        if (total <= 2) return 1;

        int stride = (int)(total * 0.6180339887);
        if (stride < 1) stride = 1;

        for (int i = 0; i < 64; i++)
        {
            if (Gcd(stride, total) == 1) return stride;
            stride++;
            if (stride >= total) stride = 1;
        }
        return 1;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
