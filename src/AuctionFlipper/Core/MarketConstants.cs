namespace AuctionFlipper.Core;

/// <summary>
/// Measured properties of the live DonutSMP market. These are observations, not guarantees, so
/// everything that consumes them treats them as starting estimates and re-measures at runtime.
/// </summary>
public static class MarketConstants
{
    /// <summary>How long a listing stays up before it expires.</summary>
    public const long ListingLifetimeMs = 24L * 60 * 60 * 1000;

    /// <summary>
    /// Highest auction page the server accepted when the market was surveyed (~143k listings).
    /// The sweeper re-probes this at runtime, since the book grows and shrinks.
    /// </summary>
    public const int KnownLastPage = 3242;

    /// <summary>
    /// Observed sales per second across the whole market. The transaction feed is genuinely live -
    /// the newest sale it returns is under a second old - unlike the listing feed.
    /// </summary>
    public const double InitialSalesPerSecond = 6.0;

    /// <summary>Half-life used when weighting older sales down in the fair-value estimate.</summary>
    public static readonly TimeSpan ValuationHalfLife = TimeSpan.FromHours(6);

    /// <summary>Sales older than this stop contributing to the live valuation.</summary>
    public static readonly TimeSpan ValuationWindow = TimeSpan.FromHours(24);

    /// <summary>Sales needed before a valuation is trusted over the ask ladder.</summary>
    public const int MinSalesForConfidentValue = 5;
}
