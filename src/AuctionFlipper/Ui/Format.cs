using System.Globalization;
using AuctionFlipper.Core;

namespace AuctionFlipper.Ui;

/// <summary>
/// Number formatting for a board that has to be readable at a glance.
///
/// Auction prices span nine orders of magnitude, from a 90-coin stack of cobblestone to trillion-
/// coin troll listings. Printed in full they are unreadable and the columns stop lining up, so
/// every figure on the board is abbreviated to three significant digits and the exact value moves
/// to the tooltip.
/// </summary>
public static class Format
{
    public static string Coins(double value)
    {
        double abs = Math.Abs(value);
        string sign = value < 0 ? "-" : "";

        return abs switch
        {
            >= 1e12 => $"{sign}{abs / 1e12:0.##}T",
            >= 1e9 => $"{sign}{abs / 1e9:0.##}B",
            >= 1e6 => $"{sign}{abs / 1e6:0.##}M",
            >= 1e3 => $"{sign}{abs / 1e3:0.#}k",
            _ => $"{sign}{abs:0.##}",
        };
    }

    /// <summary>Same as <see cref="Coins"/> but always carries a sign, for profit figures.</summary>
    public static string Signed(double value) => (value >= 0 ? "+" : "") + Coins(value);

    /// <summary>
    /// The full figure, grouped, for the hover card and the detail panel.
    ///
    /// Cents are printed only below a thousand coins. Above it they are noise that pushes the
    /// digits that matter further from the eye - "200,000.00" spends four characters saying
    /// nothing, and a card full of them reads as a wall rather than as three numbers that add up.
    /// </summary>
    public static string Grouped(double value)
    {
        double abs = Math.Abs(value);
        return value.ToString(abs >= 1000 ? "#,##0" : "#,##0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>Same figure, always signed, for profit lines.</summary>
    public static string GroupedSigned(double value) => (value >= 0 ? "+" : "") + Grouped(value);

    public static string Percent(double fraction) => (fraction * 100).ToString("0.#") + "%";

    /// <summary>Compact age, for the freshness pill: 4s, 12m, 3.2h.</summary>
    public static string Age(long milliseconds)
    {
        double seconds = milliseconds / 1000.0;
        if (seconds < 60) return $"{seconds:0}s";
        if (seconds < 3600) return $"{seconds / 60:0}m";
        return $"{seconds / 3600:0.#}h";
    }

    public static string Duration(TimeSpan span)
    {
        if (span.TotalSeconds < 60) return $"{span.TotalSeconds:0}s";
        if (span.TotalMinutes < 60) return $"{span.TotalMinutes:0}m";
        if (span.TotalHours < 24) return $"{span.TotalHours:0.#}h";
        return $"{span.TotalDays:0.#}d";
    }

    /// <summary>A countdown read as a clock: 14:08, and 1:14:08 once past an hour.</summary>
    public static string Clock(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    /// <summary>Time for the market to absorb a stack, phrased as a wait rather than a number.</summary>
    public static string Absorb(double hours)
    {
        if (hours < 1 / 60.0) return Loc.T("Instant");
        if (hours < 1) return $"~{hours * 60:0}m";
        if (hours < 24) return $"~{hours:0.#}h";
        return $"~{hours / 24:0.#}d";
    }

    public static string Rate(double perHour)
    {
        if (perHour <= 0) return "-";
        if (perHour < 1) return $"{perHour:0.##}/h";
        if (perHour < 1000) return $"{perHour:0.#}/h";
        return $"{perHour / 1000:0.#}k/h";
    }

    public static string Grade(FlipGrade grade) => grade switch
    {
        FlipGrade.S => "S",
        FlipGrade.A => "A",
        FlipGrade.B => "B",
        _ => "C",
    };

    /// <summary>Two-letter monogram for an item chip: "Netherite Ingot" becomes "NI".</summary>
    public static string Monogram(string displayName)
    {
        string[] words = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "?";
        if (words.Length == 1)
            return words[0].Length >= 2 ? words[0][..2].ToUpperInvariant() : words[0].ToUpperInvariant();

        return string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(words[^1][0]));
    }

    public static string ValueSourceLabel(ValueSource source) => source switch
    {
        ValueSource.Sales => Loc.T("FromSales"),
        ValueSource.Book => Loc.T("FromAsks"),
        _ => Loc.T("SourceUnknown"),
    };

    /// <summary>
    /// Short badge text for each risk flag, for the row badges.
    ///
    /// These go through the string table like every other label. They used to be literals, which
    /// left a German board reading "THIN SWINGY FALLING" - and a badge nobody can read is worse
    /// than no badge, because it still takes the space and still looks like a warning.
    /// </summary>
    public static IEnumerable<string> Badges(FlipFlags flags)
    {
        if (flags.HasFlag(FlipFlags.NbtRisk)) yield return Loc.T("BadgeNbt");
        if (flags.HasFlag(FlipFlags.Container)) yield return Loc.T("BadgeBox");
        if (flags.HasFlag(FlipFlags.TooGoodToBeTrue)) yield return Loc.T("BadgeTrap");
        if (flags.HasFlag(FlipFlags.ThinBook)) yield return Loc.T("BadgeThin");
        if (flags.HasFlag(FlipFlags.Stale)) yield return Loc.T("BadgeStale");
        if (flags.HasFlag(FlipFlags.Volatile)) yield return Loc.T("BadgeSwingy");
        if (flags.HasFlag(FlipFlags.SellerWall)) yield return Loc.T("BadgeOneSeller");
        if (flags.HasFlag(FlipFlags.NoSaleHistory)) yield return Loc.T("BadgeNoSales");
        if (flags.HasFlag(FlipFlags.TrendingUp)) yield return Loc.T("BadgeRising");
        if (flags.HasFlag(FlipFlags.TrendingDown)) yield return Loc.T("BadgeFalling");
        if (flags.HasFlag(FlipFlags.Unverified)) yield return Loc.T("BadgeUnverified");
        if (flags.HasFlag(FlipFlags.OverBudget)) yield return Loc.T("BadgeOverBudget");
    }

    /// <summary>Item category, for the explorer. The ids stay English; the category is prose.</summary>
    public static string CategoryLabel(ItemCategory category) => category switch
    {
        ItemCategory.Commodity => Loc.T("CategoryCommodity"),
        ItemCategory.Block => Loc.T("CategoryBlock"),
        ItemCategory.Container => Loc.T("CategoryContainer"),
        ItemCategory.Gear => Loc.T("CategoryGear"),
        ItemCategory.Consumable => Loc.T("CategoryConsumable"),
        ItemCategory.Decoration => Loc.T("CategoryDecoration"),
        _ => Loc.T("CategoryOther"),
    };
}
