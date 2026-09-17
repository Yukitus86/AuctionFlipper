using System.Windows.Media;

namespace AuctionFlipper.Ui;

public static class ColorUtil
{
    /// <summary>
    /// HSL to RGB. Item chips pick a hue from a hash of the item id and hold saturation and
    /// lightness fixed, so every colour on the board sits at the same weight and no item shouts
    /// louder than another just because of which letters are in its name.
    /// </summary>
    public static Color FromHsl(double hueDegrees, double saturation, double lightness)
    {
        double h = ((hueDegrees % 360) + 360) % 360 / 360.0;
        double s = Math.Clamp(saturation, 0, 1);
        double l = Math.Clamp(lightness, 0, 1);

        if (s <= 0)
        {
            byte g = (byte)Math.Round(l * 255);
            return Color.FromRgb(g, g, g);
        }

        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;

        return Color.FromRgb(
            (byte)Math.Round(HueToChannel(p, q, h + 1.0 / 3) * 255),
            (byte)Math.Round(HueToChannel(p, q, h) * 255),
            (byte)Math.Round(HueToChannel(p, q, h - 1.0 / 3) * 255));
    }

    private static double HueToChannel(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    /// <summary>
    /// Profit heat: a ramp from the base green to a near-white glow as profit approaches the
    /// reference. Large numbers should read as hot before they are read as numbers.
    /// </summary>
    public static Color ProfitHeat(double profit, double reference)
    {
        double t = reference <= 0 ? 0 : Math.Clamp(profit / reference, 0, 1);
        // Ease so mid-range profits still separate visually.
        t = Math.Sqrt(t);

        return Color.FromRgb(
            (byte)(0x2E + (0xE8 - 0x2E) * t),
            (byte)(0xD4 + (0xFF - 0xD4) * t),
            (byte)(0x7A + (0xC8 - 0x7A) * t));
    }

    public static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
