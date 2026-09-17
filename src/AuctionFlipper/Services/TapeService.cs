using AuctionFlipper.Api;
using AuctionFlipper.Core;

namespace AuctionFlipper.Services;

public sealed record TapeStatus(
    double SalesPerSecond,
    int LastPageDepth,
    int PollIntervalMs,
    long SalesCaptured,
    bool KeepingUp);

/// <summary>
/// Polls the transaction feed and turns it into permanent price history.
///
/// The API keeps only the last ~1000 sales, which at the observed rate is one to two minutes of
/// market. Anything not collected in that window is gone for good, so this service is the only
/// reason the tool can ever say what an item is really worth rather than what someone is asking.
///
/// It costs almost nothing. One request returns 100 sales, so even a busy market is covered by a
/// request every few seconds - about 5% of the budget. Depth adapts the same way the sniper's
/// does: if every sale on page one was new, the poll arrived too late and the pages behind it are
/// fetched to close the gap.
/// </summary>
public sealed class TapeService
{
    private const int MinIntervalMs = 4_000;
    private const int MaxIntervalMs = 20_000;

    private readonly DonutClient _client;
    private readonly MarketState _market;
    private readonly Persistence _persistence;

    private int _intervalMs = 8_000;
    private double _salesPerSecond = MarketConstants.InitialSalesPerSecond;
    private int _lastDepth = 1;
    private long _captured;
    private bool _keepingUp = true;

    public TapeService(DonutClient client, MarketState market, Persistence persistence)
    {
        _client = client;
        _market = market;
        _persistence = persistence;
    }

    public TapeStatus Status => new(_salesPerSecond, _lastDepth, _intervalMs, Interlocked.Read(ref _captured), _keepingUp);

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
                await Task.Delay(3_000, ct).ConfigureAwait(false);
            }

            await Task.Delay(_intervalMs, ct).ConfigureAwait(false);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        int depth = 0;
        bool sawOverlapOnFirstPage = false;

        for (int page = 1; page <= DonutClient.MaxTransactionPage; page++)
        {
            TransactionResponse response = await _client
                .GetTransactionsAsync(page, Lane.Tape, ct)
                .ConfigureAwait(false);

            TransactionEntry[] sales = response.Result ?? [];
            if (sales.Length == 0) break;

            depth = page;
            if (page == 1) MeasureRate(sales);

            int fresh = _market.IngestSales(sales, _persistence.RecordSale);
            Interlocked.Add(ref _captured, fresh);

            bool overlapped = fresh < sales.Length;
            if (page == 1) sawOverlapOnFirstPage = overlapped;

            // Overlap means this page reaches back past the last poll, so nothing was missed.
            if (overlapped) break;
        }

        _lastDepth = depth;
        Adapt(sawOverlapOnFirstPage, depth);
    }

    private void MeasureRate(TransactionEntry[] sales)
    {
        long newest = long.MinValue, oldest = long.MaxValue;
        foreach (TransactionEntry s in sales)
        {
            if (s.SoldAtUnixMs > newest) newest = s.SoldAtUnixMs;
            if (s.SoldAtUnixMs < oldest) oldest = s.SoldAtUnixMs;
        }

        double spanSeconds = (newest - oldest) / 1000.0;
        if (spanSeconds <= 0.05) return;

        double observed = sales.Length / spanSeconds;
        _salesPerSecond = _salesPerSecond * 0.7 + observed * 0.3;

        // Aim to poll well inside the window one page covers, so a single page is normally enough.
        double secondsPerPage = DonutClient.TransactionPageSize / Math.Max(0.5, _salesPerSecond);
        _intervalMs = (int)Math.Clamp(secondsPerPage * 600, MinIntervalMs, MaxIntervalMs);
    }

    private void Adapt(bool overlappedOnFirstPage, int depth)
    {
        if (!overlappedOnFirstPage)
        {
            // Page one was entirely new, so sales were missed between polls.
            _intervalMs = Math.Max(MinIntervalMs, (int)(_intervalMs * 0.7));
            _keepingUp = depth > 1;
        }
        else
        {
            _keepingUp = true;
        }
    }
}
