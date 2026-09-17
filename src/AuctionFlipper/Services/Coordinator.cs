using AuctionFlipper.Api;
using AuctionFlipper.Core;

namespace AuctionFlipper.Services;

public sealed record MarketStatus(
    RateSnapshot Budget,
    SniperStatus Sniper,
    TapeStatus Tape,
    SweeperStatus Sweeper,
    int BookListings,
    int BookItems,
    long TapeSales,
    int TapeItems,
    TimeSpan TapeSpan,
    TimeSpan Uptime,
    double AverageLatencyMs,
    int RecentFailures,
    string? LastError);

/// <summary>
/// Owns the collectors, the market model and the shared request budget, and keeps them running.
///
/// The division of labour is the point of the whole design. The sniper takes the largest share of
/// the budget because a listing that is mispriced is only mispriced for a few seconds. The tape
/// takes a sliver, but is never allowed to starve, because without it there is no such thing as a
/// correct price. The sweeper takes only what is left over, because the standing book changes
/// slowly and can be learned at leisure.
/// </summary>
public sealed class Coordinator : IDisposable
{
    private readonly RateLimiter _limiter;
    private readonly DonutClient _client;
    private readonly Persistence _persistence;

    private readonly SniperService _sniper;
    private readonly TapeService _tape;
    private readonly SweeperService _sweeper;

    private CancellationTokenSource? _cts;
    private readonly List<Task> _tasks = new();
    private DateTime _startedAt = DateTime.UtcNow;

    private bool _historyRestored;
    private double _latencyEma;
    private int _recentFailures;
    private string? _lastError;
    private long _lastErrorAtTicks;

    private AppConfig _config;

    public Coordinator(AppConfig config)
    {
        _config = config;

        _limiter = new RateLimiter(config.RequestsPerMinute, BuildGuarantees(config));
        _client = new DonutClient(config.ApiKey, _limiter);
        _persistence = new Persistence(AppConfig.DataDirectory);

        Market = new MarketState(config);
        Alerts = new AlertService(config);

        _sniper = new SniperService(_client, Market);
        _tape = new TapeService(_client, Market, _persistence);
        _sweeper = new SweeperService(_client, Market) { Enabled = config.SweeperEnabled };

        _client.RequestCompleted += OnRequestCompleted;
        Market.FlipDiscovered += flip => Alerts.Consider(flip);

        _sniper.Faulted += ex => Note("Sniper", ex);
        _tape.Faulted += ex => Note("Tape", ex);
        _sweeper.Faulted += ex => Note("Sweeper", ex);
    }

    public MarketState Market { get; }
    public AlertService Alerts { get; }
    public Persistence Storage => _persistence;
    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public event Action<string>? LogMessage;

    /// <summary>Loads persisted history, then starts the collectors.</summary>
    public void Start()
    {
        if (IsRunning) return;

        _startedAt = DateTime.UtcNow;
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        // Restore persisted history once per session. Replaying it on every resume would count
        // the same sales twice and quietly skew every valuation derived from them.
        if (!_historyRestored)
        {
            // Item indexes in the sale log are positional, so the name table has to be restored
            // before any sale is replayed or history would attach to the wrong items.
            _persistence.LoadItemTable(Market.Items);
            int restored = _persistence.LoadRecentSales(Market.Tape, MarketConstants.ValuationWindow);

            // The standing book: what was for sale last time the tool ran. Without it the board is
            // empty until the scan has walked enough pages to price something, which is a quarter
            // of an hour of watching nothing on a tool whose whole job is to be watched.
            int listings = _config.WarmStart
                ? _persistence.LoadBook(Market, MarketConstants.BookSnapshotMaxAge)
                : 0;

            _historyRestored = true;

            if (restored > 0 || listings > 0)
                LogMessage?.Invoke(Loc.T("StatusRestored",
                    restored.ToString("N0"), listings.ToString("N0")));
        }

        _tasks.Add(Task.Run(() => _sniper.RunAsync(ct), ct));
        _tasks.Add(Task.Run(() => _tape.RunAsync(ct), ct));
        _tasks.Add(Task.Run(() => _sweeper.RunAsync(ct), ct));
        _tasks.Add(Task.Run(() => MaintenanceAsync(ct), ct));
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;

        _cts.Cancel();
        try
        {
            await Task.WhenAll(_tasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
        }

        _tasks.Clear();
        _cts.Dispose();
        _cts = null;

        _persistence.SaveItemTable(Market.Items);
        if (_config.WarmStart) _persistence.SaveBook(Market);
        _persistence.Flush();
    }

    public void ApplyConfig(AppConfig config)
    {
        _config = config;
        _client.SetApiKey(config.ApiKey);
        _limiter.Reconfigure(config.RequestsPerMinute, BuildGuarantees(config));
        Market.UpdateConfig(config);
        Alerts.UpdateConfig(config);
        _sweeper.Enabled = config.SweeperEnabled;
    }

    private static int[] BuildGuarantees(AppConfig config) =>
        [config.SniperGuarantee, config.TapeGuarantee, config.InteractiveGuarantee, 0];

    /// <summary>
    /// Housekeeping on one loop rather than four timers: re-score, expire, persist, prune.
    /// </summary>
    private async Task MaintenanceAsync(CancellationToken ct)
    {
        long lastEvict = 0, lastSave = 0, lastPrune = 0, lastBook = Environment.TickCount64;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                long now = Environment.TickCount64;

                // Re-scoring walks items, not listings, so a full pass is a few thousand cheap
                // evaluations and can run on a short cycle.
                Market.RescanAll();

                if (now - lastEvict > 30_000)
                {
                    int removed = Market.EvictExpired(_sweeper.Enabled);
                    if (removed > 0)
                        LogMessage?.Invoke(Loc.T("StatusDropped", removed.ToString("N0")));
                    lastEvict = now;
                }

                if (now - lastSave > 60_000)
                {
                    _persistence.SaveItemTable(Market.Items);
                    _persistence.Flush();
                    lastSave = now;
                }

                // The book is written on a slow cycle as well as at shutdown, because the case the
                // warm start is really for is the one where there was no shutdown to speak of.
                if (_config.WarmStart && now - lastBook > (long)MarketConstants.BookSnapshotInterval.TotalMilliseconds)
                {
                    _persistence.SaveBook(Market);
                    lastBook = now;
                }

                if (now - lastPrune > 6 * 3_600_000)
                {
                    _persistence.PruneOldLogs(_config.SaleRetentionDays);
                    lastPrune = now;
                }
            }
            catch (Exception ex)
            {
                Note("Maintenance", ex);
            }

            await Task.Delay(2_000, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Error messages older than this are cleared once requests are succeeding again.</summary>
    private const long ErrorExpiryMs = 60_000;

    private void OnRequestCompleted(RequestTelemetry telemetry)
    {
        _latencyEma = _latencyEma <= 0
            ? telemetry.ElapsedMs
            : _latencyEma * 0.9 + telemetry.ElapsedMs * 0.1;

        if (telemetry.Failed)
        {
            Interlocked.Increment(ref _recentFailures);
            if (telemetry.RateLimited)
                SetError(Loc.T("StatusRateLimited"));
            return;
        }

        // A transient fault shown permanently is worse than not showing it: the status bar is the
        // only place problems surface, and one back-off at startup used to leave it reading
        // "rate limited" for the rest of the session while every request was in fact succeeding.
        if (_lastError is not null && Environment.TickCount64 - _lastErrorAtTicks > ErrorExpiryMs)
            _lastError = null;
    }

    private void SetError(string message)
    {
        _lastError = message;
        _lastErrorAtTicks = Environment.TickCount64;
    }

    /// <summary>
    /// Surfaces a collector fault on the status bar.
    ///
    /// The originating method is included because the message alone is often useless - "Object
    /// reference not set to an instance of an object" says nothing about which collector step
    /// broke, and that is precisely the message a fault like this produces.
    /// </summary>
    private void Note(string source, Exception ex)
    {
        string where = ex.TargetSite?.Name is { Length: > 0 } method ? $" (in {method})" : "";
        SetError($"{source}: {ex.Message}{where}");
        LogMessage?.Invoke(_lastError!);
    }

    public MarketStatus GetStatus() => new(
        _limiter.Snapshot(),
        _sniper.Status,
        _tape.Status,
        _sweeper.Status,
        Market.Book.ListingCount,
        Market.Book.ItemCount,
        Market.Tape.TotalSales,
        Market.Tape.TrackedItems,
        Market.Tape.ObservedSpan,
        DateTime.UtcNow - _startedAt,
        _latencyEma,
        Volatile.Read(ref _recentFailures),
        _lastError);

    /// <summary>
    /// Refreshes one item on demand from the reserved interactive budget, so clicking around the
    /// UI never competes with the sniper.
    /// </summary>
    public async Task<int> RefreshItemAsync(string itemId, CancellationToken ct)
    {
        string search = ItemCatalog.StripNamespace(itemId);
        int added = 0;

        // Two pages of the cheap end is all a flip decision needs, and search is a fuzzy name
        // match, so results are filtered back down to the exact id on arrival.
        for (int page = 1; page <= 2; page++)
        {
            AhResponse response = await _client
                .GetListingsAsync(page, Lane.Interactive, AuctionSort.LowestPrice, search, ct)
                .ConfigureAwait(false);

            AhEntry[] entries = (response.Result ?? [])
                .Where(e => e.Item?.Id == itemId)
                .ToArray();

            added += Market.IngestListings(entries, fromSniper: false);
        }

        return added;
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _client.Dispose();
        _persistence.Dispose();
    }
}
