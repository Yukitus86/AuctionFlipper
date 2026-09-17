namespace AuctionFlipper.Core;

/// <summary>
/// How long until the standing book has been read once end to end.
///
/// The figure is measured rather than assumed. The sweeper spends only the request budget the live
/// feeds leave behind, and that share changes minute to minute with how busy the auction house is,
/// so a fixed "about a quarter of an hour" would be wrong exactly when it mattered - a busy market
/// both slows the scan down and is the market you most want scanned.
///
/// Pages visited are sampled over a rolling window and the rate that comes out drives the estimate.
/// A window rather than an average since launch, because the first seconds of a run are spent on
/// the edge probe and would otherwise drag the figure for minutes afterwards.
/// </summary>
public sealed class SweepEta
{
    /// <summary>How far back the rate is measured. Long enough to ride out a stall, short enough
    /// to notice the budget freeing up.</summary>
    private const long WindowMs = 60_000;

    /// <summary>Below this the sample is too short to divide by.</summary>
    private const long MinSpanMs = 8_000;

    private readonly Queue<(long TimeMs, int Pages)> _samples = new();
    private int _lastPages = -1;

    /// <summary>Forgets the measured rate; the next estimate starts from scratch.</summary>
    public void Reset()
    {
        _samples.Clear();
        _lastPages = -1;
    }

    /// <summary>
    /// Time left in the current sweep, or null while the rate is still unknown - too few samples,
    /// or a scan that is not moving at all.
    /// </summary>
    public TimeSpan? Estimate(long nowMs, int pagesVisited, int totalPages)
    {
        if (totalPages <= 0 || pagesVisited < 0) return null;

        // Each cycle starts counting at zero again. Samples from the cycle before describe a sweep
        // that has already finished, and against the new count they read as a scan running
        // backwards, so they go.
        if (pagesVisited < _lastPages) _samples.Clear();
        _lastPages = pagesVisited;

        _samples.Enqueue((nowMs, pagesVisited));
        while (_samples.Count > 2 && nowMs - _samples.Peek().TimeMs > WindowMs)
            _samples.Dequeue();

        int remaining = Math.Max(0, totalPages - pagesVisited);
        if (remaining == 0) return TimeSpan.Zero;

        (long firstMs, int firstPages) = _samples.Peek();
        long spanMs = nowMs - firstMs;
        int pages = pagesVisited - firstPages;
        if (spanMs < MinSpanMs || pages <= 0) return null;

        double pagesPerMs = (double)pages / spanMs;
        return TimeSpan.FromMilliseconds(remaining / pagesPerMs);
    }
}
