using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AuctionFlipper.Ui.Controls;

/// <summary>
/// The request budget, split by collector.
///
/// The API allows 250 requests a minute and the tool is built around spending them well, so how
/// they are being spent is put on screen rather than hidden. Each segment is one collector, and the
/// notch marks the ceiling: if the bar is short, the sweeper has room to learn more of the book; if
/// it is pinned, something is competing for the budget.
/// </summary>
public sealed class BudgetGauge : FrameworkElement
{
    public static readonly DependencyProperty PerLaneProperty = DependencyProperty.Register(
        nameof(PerLane), typeof(int[]), typeof(BudgetGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LimitProperty = DependencyProperty.Register(
        nameof(Limit), typeof(int), typeof(BudgetGauge),
        new FrameworkPropertyMetadata(235, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HardCapProperty = DependencyProperty.Register(
        nameof(HardCap), typeof(int), typeof(BudgetGauge),
        new FrameworkPropertyMetadata(250, FrameworkPropertyMetadataOptions.AffectsRender));

    public int[]? PerLane
    {
        get => (int[]?)GetValue(PerLaneProperty);
        set => SetValue(PerLaneProperty, value);
    }

    public int Limit
    {
        get => (int)GetValue(LimitProperty);
        set => SetValue(LimitProperty, value);
    }

    public int HardCap
    {
        get => (int)GetValue(HardCapProperty);
        set => SetValue(HardCapProperty, value);
    }

    // Sniper, Tape, Interactive, Sweeper.
    private static readonly Color[] LaneColors =
    [
        Color.FromRgb(0xFF, 0xB0, 0x20),
        Color.FromRgb(0x2E, 0xD4, 0x7A),
        Color.FromRgb(0xC7, 0x7D, 0xFF),
        Color.FromRgb(0x35, 0xC2, 0xE4),
    ];

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 4 || h <= 2) return;

        var track = ColorUtil.Frozen(Color.FromRgb(0x1D, 0x24, 0x30));
        dc.DrawRoundedRectangle(track, null, new Rect(0, 0, w, h), h / 2, h / 2);

        int[]? lanes = PerLane;
        if (lanes is null || lanes.Length == 0) return;

        int cap = Math.Max(1, HardCap);
        double x = 0;

        for (int i = 0; i < lanes.Length && i < LaneColors.Length; i++)
        {
            if (lanes[i] <= 0) continue;

            double segment = w * lanes[i] / cap;
            if (segment < 1) segment = 1;

            var brush = ColorUtil.Frozen(LaneColors[i]);
            // Only the outer ends get rounded, so the segments read as one continuous bar.
            dc.DrawRectangle(brush, null, new Rect(x, 0, Math.Min(segment, w - x), h));
            x += segment;
            if (x >= w) break;
        }

        // The self-imposed ceiling, held below the real 250 so bursts cannot overshoot it.
        double notch = w * Limit / cap;
        var notchPen = new Pen(ColorUtil.Frozen(Color.FromArgb(200, 255, 255, 255)), 1);
        notchPen.Freeze();
        dc.DrawLine(notchPen, new Point(notch, 0), new Point(notch, h));
    }
}

/// <summary>A compact 0-100 bar, used for confidence. Colour carries the verdict.</summary>
public sealed class MeterBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(MeterBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowTextProperty = DependencyProperty.Register(
        nameof(ShowText), typeof(bool), typeof(MeterBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool ShowText
    {
        get => (bool)GetValue(ShowTextProperty);
        set => SetValue(ShowTextProperty, value);
    }

    private static readonly Typeface Mono = new(
        new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2 || h <= 1) return;

        double fraction = Math.Clamp(Value / 100.0, 0, 1);
        double barWidth = ShowText ? Math.Max(10, w - 26) : w;

        var track = ColorUtil.Frozen(Color.FromRgb(0x1D, 0x24, 0x30));
        dc.DrawRoundedRectangle(track, null, new Rect(0, 0, barWidth, h), h / 2, h / 2);

        // Red below 35, amber to 65, green above: the same thresholds the grades use.
        Color color = fraction switch
        {
            < 0.35 => Color.FromRgb(0xFF, 0x5C, 0x5C),
            < 0.65 => Color.FromRgb(0xFF, 0xB0, 0x20),
            _ => Color.FromRgb(0x2E, 0xD4, 0x7A),
        };

        if (fraction > 0)
        {
            dc.DrawRoundedRectangle(ColorUtil.Frozen(color), null,
                new Rect(0, 0, Math.Max(h, barWidth * fraction), h), h / 2, h / 2);
        }

        if (!ShowText) return;

        var text = new FormattedText(
            ((int)Math.Round(Value)).ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 10,
            ColorUtil.Frozen(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(text, new Point(w - text.Width, (h - text.Height) / 2));
    }
}
