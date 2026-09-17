using System.Windows;
using System.Windows.Media;

namespace AuctionFlipper.Ui.Controls;

/// <summary>
/// A small price-history trace drawn straight to the drawing context.
///
/// The series is sale prices over the valuation window, with the fair value marked as a dashed
/// line. What matters at this size is not the exact numbers but the shape: whether recent sales
/// sit above or below the reference, and whether the item prices tightly or thrashes around.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ReferenceProperty = DependencyProperty.Register(
        nameof(Reference), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Points
    {
        get => (IReadOnlyList<double>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public double Reference
    {
        get => (double)GetValue(ReferenceProperty);
        set => SetValue(ReferenceProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2 || h <= 2) return;

        IReadOnlyList<double>? pts = Points;
        if (pts is null || pts.Count < 2) return;

        double min = double.MaxValue, max = double.MinValue;
        foreach (double p in pts)
        {
            if (p < min) min = p;
            if (p > max) max = p;
        }

        double reference = Reference;
        if (reference > 0)
        {
            min = Math.Min(min, reference);
            max = Math.Max(max, reference);
        }

        // A flat series would otherwise divide by zero and collapse onto one edge.
        double range = max - min;
        if (range <= 0)
        {
            range = Math.Max(1, Math.Abs(max) * 0.02);
            min -= range / 2;
        }

        const double pad = 2;
        double plotH = h - pad * 2;

        double X(int i) => pts.Count <= 1 ? 0 : w * i / (pts.Count - 1.0);
        double Y(double v) => pad + plotH - (v - min) / range * plotH;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext gc = geometry.Open())
        {
            gc.BeginFigure(new Point(X(0), Y(pts[0])), false, false);
            for (int i = 1; i < pts.Count; i++)
                gc.LineTo(new Point(X(i), Y(pts[i])), true, false);
        }
        geometry.Freeze();

        // Soft fill under the trace so the direction reads before the line does.
        var fill = new StreamGeometry();
        using (StreamGeometryContext gc = fill.Open())
        {
            gc.BeginFigure(new Point(X(0), h), true, true);
            gc.LineTo(new Point(X(0), Y(pts[0])), true, false);
            for (int i = 1; i < pts.Count; i++)
                gc.LineTo(new Point(X(i), Y(pts[i])), true, false);
            gc.LineTo(new Point(X(pts.Count - 1), h), true, false);
        }
        fill.Freeze();

        var areaBrush = new LinearGradientBrush(
            Color.FromArgb(70, 53, 194, 228),
            Color.FromArgb(0, 53, 194, 228), 90);
        areaBrush.Freeze();
        dc.DrawGeometry(areaBrush, null, fill);

        if (reference > 0)
        {
            var refPen = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 176, 32)), 1)
            {
                DashStyle = new DashStyle([3, 3], 0),
            };
            refPen.Freeze();
            double y = Y(reference);
            dc.DrawLine(refPen, new Point(0, y), new Point(w, y));
        }

        var pen = new Pen(Stroke, 1.4) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geometry);

        // Mark the latest print, which is the one the user is actually comparing against.
        var dot = new SolidColorBrush(((SolidColorBrush)Stroke).Color);
        dot.Freeze();
        dc.DrawEllipse(dot, null, new Point(X(pts.Count - 1), Y(pts[^1])), 2, 2);
    }
}
