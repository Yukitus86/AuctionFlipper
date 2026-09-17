using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuctionFlipper.Services;

/// <summary>
/// User settings, persisted to <c>%APPDATA%\AuctionFlipper\config.json</c>.
///
/// The API key lives here rather than in source or in the repository, so a checkout never carries
/// a credential.
/// </summary>
public sealed class AppConfig
{
    // ---- credentials ----
    public string ApiKey { get; set; } = "";

    // ---- economics ----
    /// <summary>Fraction the server takes when a listing sells. DonutSMP currently takes none.</summary>
    public double TaxRate { get; set; } = 0.0;

    /// <summary>Coins available to buy with. Flips above this are dimmed rather than hidden.</summary>
    public double Capital { get; set; } = 10_000_000;

    /// <summary>Listing slots the server allows a player to hold at once.</summary>
    public int ListingSlots { get; set; } = 25;

    /// <summary>Fraction of a container's contents value assumed recoverable after relisting.</summary>
    public double ContainerHaircut { get; set; } = 0.90;

    // ---- board filters ----
    public double MinNetProfit { get; set; } = 25_000;
    public double MinRoi { get; set; } = 0.15;
    public double MinConfidence { get; set; } = 35;
    public double MinSalesPerHour { get; set; } = 0.5;
    public double MaxBuyPrice { get; set; } = 0;           // 0 = no cap beyond Capital

    public bool IncludeContainers { get; set; } = true;

    // ---- alerts ----
    public bool AlertSoundEnabled { get; set; } = true;
    public bool AlertToastEnabled { get; set; } = true;
    public bool AlertAutoCopyEnabled { get; set; } = true;
    /// <summary>Minimum grade that fires an alert: 0=S, 1=A, 2=B, 3=C.</summary>
    public int AlertMinGrade { get; set; } = 1;
    public double AlertMinNetProfit { get; set; } = 100_000;
    public int AlertCooldownSeconds { get; set; } = 20;

    /// <summary>
    /// Whether a pinned item alerts regardless of the grade and profit thresholds.
    ///
    /// Pinning is a statement that you are working this item, so the thresholds that exist to keep
    /// the general feed quiet are the wrong ones to apply to it. The cooldown still holds, so a
    /// pinned item cannot turn into a siren.
    /// </summary>
    public bool AlertPinnedAlways { get; set; } = true;

    // ---- collectors ----
    public int RequestsPerMinute { get; set; } = 235;

    /// <summary>
    /// Requests a minute held for the new-listing watcher. Small on purpose: the listing feed is
    /// served from a slow-moving view and polling it harder returns the same rows again.
    /// </summary>
    public int SniperGuarantee { get; set; } = 30;
    public int TapeGuarantee { get; set; } = 12;
    public int InteractiveGuarantee { get; set; } = 20;
    public bool SweeperEnabled { get; set; } = true;

    // ---- dashboard ----
    public bool DashboardEnabled { get; set; } = true;
    public int DashboardPort { get; set; } = 8730;

    // ---- storage ----
    public int SaleRetentionDays { get; set; } = 14;

    // ---- ui ----
    public string BoardSort { get; set; } = "ProfitPerHour";

    /// <summary>Interface language: <c>en</c> or <c>de</c>.</summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// Item ids the user is working, kept across runs.
    ///
    /// Pins are per item rather than per listing on purpose. A listing is gone the moment somebody
    /// buys it, but the reason you were watching it - this item flips well - outlives it, and a
    /// watchlist that emptied itself every time a flip succeeded would be worthless.
    /// </summary>
    public List<string> PinnedItems { get; set; } = [];

    /// <summary>
    /// Show prices for one item rather than for the whole listing.
    ///
    /// Listings are sold as a lot, so the stack total is what actually leaves the wallet and stays
    /// the default. Per-item is for comparing across lot sizes, where a 64-stack and a 16-stack of
    /// the same block are otherwise four numbers apart for no real reason.
    /// </summary>
    public bool ShowPerUnit { get; set; } = false;

    /// <summary>Width of the flip board's detail panel, in device-independent pixels.</summary>
    public double DetailPanelWidth { get; set; } = 392;

    // -------------------------------------------------------------------------

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AuctionFlipper");

    public static string ConfigPath { get; } = Path.Combine(DataDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                AppConfig? cfg = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig);
                if (cfg is not null) return cfg;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable config should not stop the app starting.
        }

        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            string json = JsonSerializer.Serialize(this, ConfigJsonContext.Default.AppConfig);

            // Write-then-replace so a crash mid-save cannot leave a truncated config behind.
            string tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AppConfig))]
public sealed partial class ConfigJsonContext : JsonSerializerContext
{
}
