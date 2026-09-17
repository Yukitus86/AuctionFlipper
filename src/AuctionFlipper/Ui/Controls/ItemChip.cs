using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AuctionFlipper.Core;

namespace AuctionFlipper.Ui.Controls;

/// <summary>
/// The item's picture on the board.
///
/// It draws the real inventory icon on a dark slot, the way the item looks in game, because that is
/// what a player recognises before any label is read. Every icon comes from the sheet embedded in
/// the app, so nothing is fetched while the tool runs.
///
/// The coloured monogram tile is still here as the fallback. The sheet cannot cover an id this
/// build has never heard of - the server adds items - and a hue derived from the id is far better
/// than a hole in the row.
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

    public static readonly DependencyProperty ItemIdProperty = DependencyProperty.Register(
        nameof(ItemId), typeof(string), typeof(ItemChip),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemIdChanged));

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

    /// <summary>The item id, with or without its namespace. Empty means "no icon, draw the tile".</summary>
    public string? ItemId
    {
        get => (string?)GetValue(ItemIdProperty);
        set => SetValue(ItemIdProperty, value);
    }

    private BitmapSource? _icon;

    private static void OnItemIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ItemChip)d)._icon = ItemIcons.Get(e.NewValue as string);

    private static readonly Typeface Face = new(
        new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    /// <summary>
    /// Pixel art needs the scaling mode chosen by direction, not once and for all.
    ///
    /// The sheet stores every icon at 32 px. Drawn at 32 or larger, nearest-neighbour keeps the
    /// blocky edges the textures are drawn with; shrunk to the 22 px chips in the tables, the same
    /// mode drops whole rows of pixels and the icon comes out visibly broken, so those downscales
    /// get the smooth filter instead.
    /// </summary>
    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        RenderOptions.SetBitmapScalingMode(this, info.NewSize.Height >= ItemIconAtlas.CellSize
            ? BitmapScalingMode.NearestNeighbor
            : BitmapScalingMode.HighQuality);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var rect = new Rect(0, 0, w, h);

        if (_icon is not null)
        {
            DrawSlot(dc, rect);
            dc.DrawImage(_icon, rect);
            return;
        }

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

    private static readonly Brush SlotFill = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x21, 0x2B)));
    private static readonly Pen SlotEdge = Freeze(new Pen(
        new SolidColorBrush(Color.FromArgb(0x55, 0x4A, 0x58, 0x6C)), 1));

    /// <summary>
    /// The inventory slot the icon sits in.
    ///
    /// Without it the darker items - coal, black dye, netherite - all but vanish against the page,
    /// and the row loses the block of colour the eye scans down.
    /// </summary>
    private void DrawSlot(DrawingContext dc, Rect rect) =>
        dc.DrawRoundedRectangle(SlotFill, SlotEdge, rect, Corner, Corner);

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static Pen Freeze(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
