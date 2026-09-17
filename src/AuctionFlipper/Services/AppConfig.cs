using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuctionFlipper.Services;

/// <summary>
/// User settings, persisted to <c>%APPDATA%\AuctionFlipper\config.json</c>.
///
/// The API key lives here rather than in source or in the repository, so a checkout never carries
/// a credential.
///
/// Every property writes through <see cref="Track{T}"/>, which marks the config dirty. The window
/// asks <see cref="SaveIfDirty"/> a few times a minute, so a setting is on disk seconds after it
/// changes instead of at shutdown. Waiting for shutdown was the old behaviour and it lost work in
/// every case where the process did not close cleanly - a crash, a sign-out, or task manager.
/// </summary>
public sealed class AppConfig
{
    private bool _dirty;

    /// <summary>Whether anything has changed since the last successful write.</summary>
    [JsonIgnore]
    public bool IsDirty => _dirty;

    /// <summary>
    /// Assigns a property and notes that the file no longer matches memory.
    ///
    /// Comparing before assigning matters more than it looks: the settings screen binds two-way, so
    /// WPF writes every visible field back on each refresh, and without this the config would be
    /// permanently dirty and rewritten on every tick.
    /// </summary>
    private void Track<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        _dirty = true;
    }

    // ---- credentials ----
    private string _apiKey = "";
    public string ApiKey { get => _apiKey; set => Track(ref _apiKey, value); }

    // ---- economics ----
    private double _taxRate;
    /// <summary>Fraction the server takes when a listing sells. DonutSMP currently takes none.</summary>
    public double TaxRate { get => _taxRate; set => Track(ref _taxRate, value); }

    private double _capital = 10_000_000;
    /// <summary>Coins available to buy with. Flips above this are dimmed rather than hidden.</summary>
    public double Capital { get => _capital; set => Track(ref _capital, value); }

    private int _listingSlots = 25;
    /// <summary>Listing slots the server allows a player to hold at once.</summary>
    public int ListingSlots { get => _listingSlots; set => Track(ref _listingSlots, value); }

    private double _containerHaircut = 0.90;
    /// <summary>Fraction of a container's contents value assumed recoverable after relisting.</summary>
    public double ContainerHaircut { get => _containerHaircut; set => Track(ref _containerHaircut, value); }

    // ---- board filters ----
    private double _minNetProfit = 25_000;
    public double MinNetProfit { get => _minNetProfit; set => Track(ref _minNetProfit, value); }

    private double _minRoi = 0.15;
    public double MinRoi { get => _minRoi; set => Track(ref _minRoi, value); }

    private double _minConfidence = 35;
    public double MinConfidence { get => _minConfidence; set => Track(ref _minConfidence, value); }

    private double _minSalesPerHour = 0.5;
    public double MinSalesPerHour { get => _minSalesPerHour; set => Track(ref _minSalesPerHour, value); }

    private double _maxBuyPrice;
    /// <summary>Hard ceiling on what a flip may cost. 0 means no cap beyond <see cref="Capital"/>.</summary>
    public double MaxBuyPrice { get => _maxBuyPrice; set => Track(ref _maxBuyPrice, value); }

    private bool _includeContainers = true;
    public bool IncludeContainers { get => _includeContainers; set => Track(ref _includeContainers, value); }

    // ---- alerts ----
    private bool _alertSoundEnabled = true;
    public bool AlertSoundEnabled { get => _alertSoundEnabled; set => Track(ref _alertSoundEnabled, value); }

    private bool _alertToastEnabled = true;
    public bool AlertToastEnabled { get => _alertToastEnabled; set => Track(ref _alertToastEnabled, value); }

    private bool _alertAutoCopyEnabled = true;
    public bool AlertAutoCopyEnabled { get => _alertAutoCopyEnabled; set => Track(ref _alertAutoCopyEnabled, value); }

    private int _alertMinGrade = 1;
    /// <summary>Minimum grade that fires an alert: 0=S, 1=A, 2=B, 3=C.</summary>
    public int AlertMinGrade { get => _alertMinGrade; set => Track(ref _alertMinGrade, value); }

    private double _alertMinNetProfit = 100_000;
    public double AlertMinNetProfit { get => _alertMinNetProfit; set => Track(ref _alertMinNetProfit, value); }

    private int _alertCooldownSeconds = 20;
    public int AlertCooldownSeconds { get => _alertCooldownSeconds; set => Track(ref _alertCooldownSeconds, value); }

    private bool _alertPinnedAlways = true;
    /// <summary>
    /// Whether a pinned item alerts regardless of the grade and profit thresholds.
    ///
    /// Pinning is a statement that you are working this item, so the thresholds that exist to keep
    /// the general feed quiet are the wrong ones to apply to it. The cooldown still holds, so a
    /// pinned item cannot turn into a siren.
    /// </summary>
    public bool AlertPinnedAlways { get => _alertPinnedAlways; set => Track(ref _alertPinnedAlways, value); }

    // ---- collectors ----
    private int _requestsPerMinute = 235;
    public int RequestsPerMinute { get => _requestsPerMinute; set => Track(ref _requestsPerMinute, value); }

    private int _sniperGuarantee = 30;
    /// <summary>
    /// Requests a minute held for the new-listing watcher. Small on purpose: the listing feed is
    /// served from a slow-moving view and polling it harder returns the same rows again.
    /// </summary>
    public int SniperGuarantee { get => _sniperGuarantee; set => Track(ref _sniperGuarantee, value); }

    private int _tapeGuarantee = 12;
    public int TapeGuarantee { get => _tapeGuarantee; set => Track(ref _tapeGuarantee, value); }

    private int _interactiveGuarantee = 20;
    public int InteractiveGuarantee { get => _interactiveGuarantee; set => Track(ref _interactiveGuarantee, value); }

    private bool _sweeperEnabled = true;
    public bool SweeperEnabled { get => _sweeperEnabled; set => Track(ref _sweeperEnabled, value); }

    // ---- dashboard ----
    private bool _dashboardEnabled = true;
    public bool DashboardEnabled { get => _dashboardEnabled; set => Track(ref _dashboardEnabled, value); }

    private int _dashboardPort = 8730;
    public int DashboardPort { get => _dashboardPort; set => Track(ref _dashboardPort, value); }

    // ---- storage ----
    private int _saleRetentionDays = 14;
    public int SaleRetentionDays { get => _saleRetentionDays; set => Track(ref _saleRetentionDays, value); }

    private bool _warmStart = true;
    /// <summary>
    /// Whether the standing book is written to disk and read back at the next launch.
    ///
    /// Without it every launch starts blind and the board stays empty until the scan has walked
    /// enough of the ~3,200 pages to price something, which costs a quarter of an hour and most of
    /// the request budget. Restored listings are marked unverified until the collectors see them
    /// again, because a listing saved an hour ago may well have been bought since.
    /// </summary>
    public bool WarmStart { get => _warmStart; set => Track(ref _warmStart, value); }

    // ---- ui ----
    private string _boardSort = "ProfitPerHour";
    public string BoardSort { get => _boardSort; set => Track(ref _boardSort, value); }

    private string _language = "en";
    /// <summary>Interface language: <c>en</c> or <c>de</c>.</summary>
    public string Language { get => _language; set => Track(ref _language, value); }

    private List<string> _pinnedItems = [];
    /// <summary>
    /// Item ids the user is working, kept across runs.
    ///
    /// Pins are per item rather than per listing on purpose. A listing is gone the moment somebody
    /// buys it, but the reason you were watching it - this item flips well - outlives it, and a
    /// watchlist that emptied itself every time a flip succeeded would be worthless.
    /// </summary>
    public List<string> PinnedItems
    {
        get => _pinnedItems;
        // A list is replaced wholesale rather than edited in place, so reference inequality is the
        // right test here - the default comparer would only ever see two different list objects.
        set { _pinnedItems = value; _dirty = true; }
    }

    private bool _showPerUnit;
    /// <summary>
    /// Show prices for one item rather than for the whole listing.
    ///
    /// Listings are sold as a lot, so the stack total is what actually leaves the wallet and stays
    /// the default. Per-item is for comparing across lot sizes, where a 64-stack and a 16-stack of
    /// the same block are otherwise four numbers apart for no real reason.
    /// </summary>
    public bool ShowPerUnit { get => _showPerUnit; set => Track(ref _showPerUnit, value); }

    private double _detailPanelWidth = 392;
    /// <summary>Width of the flip board's detail panel, in device-independent pixels.</summary>
    public double DetailPanelWidth { get => _detailPanelWidth; set => Track(ref _detailPanelWidth, value); }

    private string _lastSection = "Board";
    /// <summary>The screen that was open when the app last closed, so it reopens where it was left.</summary>
    public string LastSection { get => _lastSection; set => Track(ref _lastSection, value); }

    // ---- window placement ----
    //
    // Size and position are settings too. Dragging the window to the right monitor and sizing it to
    // the board is work, and doing it again at every launch is exactly the kind of thing this file
    // exists to prevent.

    private double _windowLeft = double.NaN;
    public double WindowLeft { get => _windowLeft; set => Track(ref _windowLeft, value); }

    private double _windowTop = double.NaN;
    public double WindowTop { get => _windowTop; set => Track(ref _windowTop, value); }

    private double _windowWidth = 1520;
    public double WindowWidth { get => _windowWidth; set => Track(ref _windowWidth, value); }

    private double _windowHeight = 900;
    public double WindowHeight { get => _windowHeight; set => Track(ref _windowHeight, value); }

    private bool _windowMaximized;
    public bool WindowMaximized { get => _windowMaximized; set => Track(ref _windowMaximized, value); }

    // -------------------------------------------------------------------------

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AuctionFlipper");

    public static string ConfigPath { get; } = Path.Combine(DataDirectory, "config.json");

    public static AppConfig Load() => LoadFrom(ConfigPath);

    public static AppConfig LoadFrom(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                AppConfig? cfg = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);
                if (cfg is not null)
                {
                    // Deserialisation runs through the same setters, so a freshly loaded config
                    // would otherwise believe it had unsaved changes and rewrite the file at once.
                    cfg._dirty = false;
                    return cfg;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable config should not stop the app starting.
        }

        return new AppConfig();
    }

    public void Save() => SaveTo(ConfigPath);

    /// <summary>Writes only when something changed. Cheap enough to call on a timer.</summary>
    public bool SaveIfDirty()
    {
        if (!_dirty) return false;
        Save();
        return true;
    }

    public void SaveTo(string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);

            string json = JsonSerializer.Serialize(this, ConfigJsonContext.Default.AppConfig);

            // Write-then-replace so a crash mid-save cannot leave a truncated config behind.
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);

            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leave the dirty flag set so the next tick tries again.
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppConfig))]
public sealed partial class ConfigJsonContext : JsonSerializerContext
{
}
