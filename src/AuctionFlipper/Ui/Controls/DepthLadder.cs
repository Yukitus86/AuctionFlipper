using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AuctionFlipper.Ui.Controls;

public readonly record struct LadderRung(double UnitPrice, int Units, bool IsTarget);

/// <summary>
/// The sell side of one item, drawn as a price ladder.
///
/// This is the single most useful picture for deciding whether a flip is real. A cheap listing with
/// a wall of offers stacked just above it is a genuine gap in the market; the same listing with
/// nothing behind it for miles means there is no one to sell to at the price the numbers promise.
/// The bar length shows how many units sit at each rung, and the marker shows where the flip would
/// have to relist.
/// </summary>
public sealed class DepthLadder : FrameworkElement
{
    public static readonly DependencyProperty RungsProperty = DependencyProperty.Register(
        nameof(Rungs), typeof(IReadOnlyList<LadderRung>), typeof(DepthLadder),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FairValueProperty = DependencyProperty.Register(
        nameof(FairValue), typeof(double), typeof(DepthLadder),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<LadderRung>? Rungs
    {
        get => (IReadOnlyList<LadderRung>?)GetValue(RungsProperty);
        set => SetValue(RungsProperty, value);
    }

    public double FairValue
    {
        get => (double)GetValue(FairValueProperty);
        set => SetValue(FairValueProperty, value);
    }

    private static readonly Typeface Mono = new(
        new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    protected override void OnRender(DrawingContext dc)
    {
        IReadOnlyList<LadderRung>? rungs = Rungs;
        double w = ActualWidth, h = ActualHeight;
        if (rungs is null || rungs.Count == 0 || w <= 40 || h <= 10) return;

        const double rowHeight = 18;
        int visible = Math.Min(rungs.Count, (int)(h / rowHeight));
        if (visible <= 0) return;

        int maxUnits = 1;
        for (int i = 0; i < visible; i++) maxUnits = Math.Max(maxUnits, rungs[i].Units);

        double priceColumn = 78;
        double barArea = Math.Max(20, w - priceColumn - 58);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var barBrush = ColorUtil.Frozen(Color.FromArgb(200, 53, 120, 160));
        var cheapestBrush = ColorUtil.Frozen(Color.FromArgb(220, 46, 212, 122));
        var targetBrush = ColorUtil.Frozen(Color.FromArgb(230, 255, 176, 32));
        var textBrush = ColorUtil.Frozen(Color.FromRgb(0x8C, 0x99, 0xAC));
        var priceBrush = ColorUtil.Frozen(Color.FromRgb(0xE8, 0xEE, 0xF7));

        for (int i = 0; i < visible; i++)
        {
            LadderRung rung = rungs[i];
            double y = i * rowHeight;

            var price = new FormattedText(
                Format.Coins(rung.UnitPrice), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Mono, 11, i == 0 ? cheapestBrush : priceBrush, dpi);
            dc.DrawText(price, new Point(priceColumn - price.Width - 6, y + (rowHeight - price.Height) / 2));

            double barWidth = Math.Max(2, barArea * rung.Units / maxUnits);
            Brush brush = rung.IsTarget ? targetBrush : i == 0 ? cheapestBrush : barBrush;
            dc.DrawRoundedRectangle(brush, null,
                new Rect(priceColumn, y + 3, barWidth, rowHeight - 7), 2, 2);

            var units = new FormattedText(
                rung.Units.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Mono, 10, textBrush, dpi);
            dc.DrawText(units, new Point(priceColumn + barWidth + 5, y + (rowHeight - units.Height) / 2));
        }

        // Where the item has actually been selling, against where it is being offered.
        if (FairValue > 0)
        {
            double top = rungs[0].UnitPrice;
            double bottom = rungs[visible - 1].UnitPrice;
            if (FairValue >= top && bottom > top)
            {
                double t = (FairValue - top) / (bottom - top);
                double y = Math.Clamp(t * visible * rowHeight, 0, visible * rowHeight - 1);
                var pen = new Pen(ColorUtil.Frozen(Color.FromArgb(180, 255, 176, 32)), 1)
                {
                    DashStyle = new DashStyle([4, 3], 0),
                };
                pen.Freeze();
                dc.DrawLine(pen, new Point(0, y), new Point(w, y));
            }
        }
    }
}
