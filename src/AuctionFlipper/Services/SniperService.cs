using AuctionFlipper.Api;
using AuctionFlipper.Core;

namespace AuctionFlipper.Services;

public sealed record SniperStatus(
    double NewListingsPerMinute,
    int PollIntervalMs,
    long ListingsCaptured,
    bool KeepingUp);

/// <summary>
/// Watches the head of the auction house for listings the tool has not seen before.
///
/// This service was originally built to poll several times a second, on the assumption that the
/// listing feed was live and the job was to beat other players to a fresh mispricing. Measurement
/// said otherwise. Polling <c>recently_listed</c> twice a second returns a byte-identical set of
/// listings; over a minute the first page turns over by one or two entries out of forty-four, and
/// its newest entry is already several minutes old when it arrives. The server is serving a view
/// that moves far more slowly than the market underneath it.
///
/// So the cadence is driven by observed change and nothing else. Every poll that finds something
/// new pulls the interval in; every poll that finds nothing pushes it out, up to a couple of
/// minutes. On the live API it settles around one poll every twenty to sixty seconds, which costs
/// a handful of requests a minute instead of a hundred and twenty, and misses nothing - the
/// listings simply are not there any sooner.
///
/// The budget that frees goes to the book scan, which is where the remaining unseen listings
/// actually are.
/// </summary>
public sealed class SniperService
{
    private const int MaxDepth = 6;
    private const int MinDelayMs = 5_000;
    private const int MaxDelayMs = 120_000;

    private readonly DonutClient _client;
    private readonly MarketState _market;

    private int _delayMs = 15_000;
    private long _captured;
    private bool _keepingUp = true;

    // Captures per minute, measured rather than inferred from the listing feed's own timestamps.
    private long _rateWindowStartTicks;
    private long _capturedAtWindowStart;
    private double _newPerMinute;

    public SniperService(DonutClient client, MarketState market)
    {
        _client = client;
        _market = market;
        _rateWindowStartTicks = Environment.TickCount64;
    }

    public SniperStatus Status => new(
        _newPerMinute,
        _delayMs,
        Interlocked.Read(ref _captured),
        _keepingUp);

    public event Action<Exception>? Faulted;

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollAsync(ct).ConfigureAwait(false);
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

            await Task.Delay(_delayMs, ct).ConfigureAwait(false);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        int depth = 0;
        int addedTotal = 0;
        double firstPageOverlap = 1;

        for (int page = 1; page <= MaxDepth; page++)
        {
            AhResponse response = await _client
                .GetListingsAsync(page, Lane.Sniper, AuctionSort.RecentlyListed, ct: ct)
                .ConfigureAwait(false);

            AhEntry[] entries = response.Result ?? [];
            if (entries.Length == 0) break;

            depth = page;

            int added = _market.IngestListings(entries, fromSniper: true);
            addedTotal += added;
            Interlocked.Add(ref _captured, added);

            double overlap = 1.0 - (double)added / entries.Length;
            if (page == 1) firstPageOverlap = overlap;

            // Something already on file means this page reaches back past the last poll, so
            // everything newer has been captured and there is no need to go deeper.
            if (overlap > 0) break;
        }

        UpdateRate();
        Adapt(addedTotal, firstPageOverlap, depth);
    }

    private void UpdateRate()
    {
        long now = Environment.TickCount64;
        long elapsed = now - _rateWindowStartTicks;
        if (elapsed < 30_000) return;

        long captured = Interlocked.Read(ref _captured);
        double perMinute = (captured - _capturedAtWindowStart) * 60_000.0 / elapsed;

        _newPerMinute = _newPerMinute <= 0 ? perMinute : _newPerMinute * 0.6 + perMinute * 0.4;
        _rateWindowStartTicks = now;
        _capturedAtWindowStart = captured;
    }

    /// <summary>
    /// Tunes the interval purely on whether polling found anything.
    ///
    /// No assumption is made about how often the server refreshes its view, because that is the
    /// server's business and it can change. Finding new listings is evidence the feed is moving and
    /// worth watching closely; finding none, repeatedly, is evidence that asking again sooner would
    /// spend requests to be told the same thing.
    /// </summary>
    private void Adapt(int added, double overlapFraction, int depth)
    {
        if (added == 0)
        {
            _delayMs = Math.Min(MaxDelayMs, (int)(_delayMs * 1.5) + 1_000);
            _keepingUp = true;
            return;
        }

        if (overlapFraction <= 0.001)
        {
            // The whole first page was unfamiliar, so listings appeared and scrolled past between
            // polls. The deeper pages backfilled them, but poll sooner.
            _delayMs = Math.Max(MinDelayMs, (int)(_delayMs * 0.4));
            _keepingUp = depth > 1;
            return;
        }

        _keepingUp = true;

        // Some movement: converge on an interval that keeps finding a little each time.
        double changedFraction = 1.0 - overlapFraction;
        _delayMs = changedFraction > 0.25
            ? Math.Max(MinDelayMs, (int)(_delayMs * 0.7))
            : Math.Max(MinDelayMs, (int)(_delayMs * 0.95));
    }
}
