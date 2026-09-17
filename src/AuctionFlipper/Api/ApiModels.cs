using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuctionFlipper.Api;

/// <summary>
/// Sort modes accepted by <c>/v1/auction/list/{page}</c>. The API only reads these from a JSON
/// body on the GET request; passing them as a query string returns HTTP 500.
/// </summary>
public enum AuctionSort
{
    /// <summary>Server default ordering (arbitrary but stable within a page).</summary>
    Default,
    LowestPrice,
    HighestPrice,
    /// <summary>Newest listings first. This is the feed the sniper rides.</summary>
    RecentlyListed,
    /// <summary>Closest to expiry first.</summary>
    LastListed,
}

public static class AuctionSortExtensions
{
    public static string? ToWire(this AuctionSort sort) => sort switch
    {
        AuctionSort.LowestPrice => "lowest_price",
        AuctionSort.HighestPrice => "highest_price",
        AuctionSort.RecentlyListed => "recently_listed",
        AuctionSort.LastListed => "last_listed",
        _ => null,
    };
}

public sealed class AhResponse
{
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("result")] public AhEntry[]? Result { get; set; }

    // Present only on error payloads.
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class AhEntry
{
    [JsonPropertyName("item")] public ApiItem? Item { get; set; }
    [JsonPropertyName("price")] public double Price { get; set; }
    [JsonPropertyName("seller")] public ApiSeller? Seller { get; set; }

    /// <summary>Milliseconds left before the listing expires. Listings run for 24 h.</summary>
    [JsonPropertyName("time_left")] public long TimeLeft { get; set; }
}

public sealed class TransactionResponse
{
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("result")] public TransactionEntry[]? Result { get; set; }

    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class TransactionEntry
{
    [JsonPropertyName("item")] public ApiItem? Item { get; set; }
    [JsonPropertyName("price")] public double Price { get; set; }
    [JsonPropertyName("seller")] public ApiSeller? Seller { get; set; }
    [JsonPropertyName("unixMillisDateSold")] public long SoldAtUnixMs { get; set; }
}

public sealed class ApiItem
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }

    // The live API returns these as empty/null on every listing observed, so nothing may
    // depend on them. They are modelled anyway in case the server starts populating them.
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("lore")] public string[]? Lore { get; set; }
    [JsonPropertyName("enchants")] public ApiItemData? Enchants { get; set; }

    /// <summary>Populated for shulker boxes and bundles. This one is real and valuable.</summary>
    [JsonPropertyName("contents")] public ApiContainerItem[]? Contents { get; set; }
}

public sealed class ApiContainerItem
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("enchants")] public ApiItemData? Enchants { get; set; }
}

public sealed class ApiItemData
{
    [JsonPropertyName("enchantments")] public ApiEnchantments? Enchantments { get; set; }
    [JsonPropertyName("trim")] public ApiTrim? Trim { get; set; }
}

public sealed class ApiEnchantments
{
    [JsonPropertyName("levels")] public Dictionary<string, int>? Levels { get; set; }
}

public sealed class ApiTrim
{
    [JsonPropertyName("material")] public string? Material { get; set; }
    [JsonPropertyName("pattern")] public string? Pattern { get; set; }
}

public sealed class ApiSeller
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("uuid")] public string? Uuid { get; set; }
}

/// <summary>Request body for the auction list endpoint. Both fields are optional.</summary>
public sealed class AuctionRequestBody
{
    [JsonPropertyName("search")] public string? Search { get; set; }
    [JsonPropertyName("sort")] public string? Sort { get; set; }
}

/// <summary>
/// Source-generated JSON contracts. Reflection-based serialization is avoided so parsing stays
/// cheap: the collectors decode roughly 2 pages per second, every second, for hours.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(AhResponse))]
[JsonSerializable(typeof(TransactionResponse))]
[JsonSerializable(typeof(AuctionRequestBody))]
public sealed partial class ApiJsonContext : JsonSerializerContext
{
}

/// <summary>An error the API reported in its payload (its own <c>status</c> field, not HTTP).</summary>
public sealed class DonutApiException : Exception
{
    public int ApiStatus { get; }
    public string? Reason { get; }

    public DonutApiException(int apiStatus, string? reason, string? message)
        : base($"API status {apiStatus}: {reason} - {message}")
    {
        ApiStatus = apiStatus;
        Reason = reason;
    }
}
