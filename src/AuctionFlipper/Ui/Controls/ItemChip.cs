using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AuctionFlipper.Ui.Controls;

/// <summary>
/// The little coloured tile that stands in for an item icon.
///
/// Minecraft textures cannot be shipped or downloaded here, but a board of identical grey rows is
/// hard to scan, so each item gets a tile whose colour is derived from a hash of its id and whose
/// monogram comes from its name. The result is stable and unique enough that a regular user starts
/// recognising items by colour long before they read the label.
/// </summary>
public sealed class ItemChip : FrameworkElement
{
    public static readonly DependencyProperty HueProperty = DependencyProperty.Register(
        nameof(Hue), typeof(double), typeof(ItemChip),
        new FrameworkPropertyMetadata(210.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MonogramProperty = DependencyProperty.Register(
        nameof(Monogram), typeof(string), typeof(ItemChip),
        new FrameworkPropertyMetadata("?", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CornerProperty = DependencyProperty.Register(
        nameof(Corner), typeof(double), typeof(ItemChip),
        new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Hue
    {
        get => (double)GetValue(HueProperty);
        set => SetValue(HueProperty, value);
    }

    public string Monogram
    {
        get => (string)GetValue(MonogramProperty);
        set => SetValue(MonogramProperty, value);
    }

    public double Corner
    {
        get => (double)GetValue(CornerProperty);
        set => SetValue(CornerProperty, value);
    }

    private static readonly Typeface Face = new(
        new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var rect = new Rect(0, 0, w, h);

        // A slight vertical gradient keeps the tile from reading as a flat swatch.
        Color top = ColorUtil.FromHsl(Hue, 0.55, 0.42);
        Color bottom = ColorUtil.FromHsl(Hue, 0.60, 0.28);
        var fill = new LinearGradientBrush(top, bottom, 90);
        fill.Freeze();

        var border = new SolidColorBrush(ColorUtil.FromHsl(Hue, 0.65, 0.58)) { Opacity = 0.7 };
        border.Freeze();

        dc.DrawRoundedRectangle(fill, new Pen(border, 1), rect, Corner, Corner);

        string text = Monogram ?? "?";
        if (text.Length == 0) return;

        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            Face,
            Math.Max(8, h * 0.42),
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        // Centre on the glyphs themselves rather than on the text box.
        //
        // FormattedText.Height covers a whole line - ascent, descent and line gap - so centring on
        // it leaves capital letters sitting noticeably high, since nothing occupies the descender
        // space. Width has the same problem with side bearings. Measuring the ink and centring that
        // puts the two letters exactly in the middle of the square, which is the only placement
        // that looks right at this size.
        Rect ink = formatted.BuildGeometry(new Point(0, 0)).Bounds;
        if (ink.IsEmpty) return;

        var origin = new Point(
            (w - ink.Width) / 2 - ink.X,
            (h - ink.Height) / 2 - ink.Y);

        // Slight shadow so the monogram stays legible on the lighter hues.
        var shadow = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
        shadow.Freeze();
        formatted.SetForegroundBrush(shadow);
        dc.DrawText(formatted, new Point(origin.X + 1, origin.Y + 1));

        formatted.SetForegroundBrush(Brushes.White);
        dc.DrawText(formatted, origin);
    }
}
