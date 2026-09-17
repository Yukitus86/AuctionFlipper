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

    public static string Exact(double value) => value.ToString("N2", CultureInfo.InvariantCulture);

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

    /// <summary>Short badge text for each risk flag, for the row badges.</summary>
    public static IEnumerable<string> Badges(FlipFlags flags)
    {
        if (flags.HasFlag(FlipFlags.NbtRisk)) yield return "NBT?";
        if (flags.HasFlag(FlipFlags.Container)) yield return "BOX";
        if (flags.HasFlag(FlipFlags.TooGoodToBeTrue)) yield return "TRAP?";
        if (flags.HasFlag(FlipFlags.ThinBook)) yield return "THIN";
        if (flags.HasFlag(FlipFlags.Stale)) yield return "STALE";
        if (flags.HasFlag(FlipFlags.Volatile)) yield return "SWINGY";
        if (flags.HasFlag(FlipFlags.SellerWall)) yield return "1 SELLER";
        if (flags.HasFlag(FlipFlags.NoSaleHistory)) yield return "NO SALES";
        if (flags.HasFlag(FlipFlags.TrendingUp)) yield return "RISING";
        if (flags.HasFlag(FlipFlags.TrendingDown)) yield return "FALLING";
        if (flags.HasFlag(FlipFlags.OverBudget)) yield return "OVER BUDGET";
    }
}
