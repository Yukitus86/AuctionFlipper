using System.Windows;
using System.Windows.Controls;
using AuctionFlipper.Ui.ViewModels;

namespace AuctionFlipper.Ui.Views;

public partial class FlipBoardView : UserControl
{
    /// <summary>
    /// Board width the detail panel is never allowed to eat into.
    ///
    /// Every row column is proportional now, but each still carries a minimum, and those minimums
    /// add up to 802 px. The row does not get all of the board: 10 px of its own right margin, 18 px
    /// of list margins, the item border and the 9 px scrollbar come off first, which is 39 px this
    /// figure has to cover on top of the columns - the first attempt left them out and the grade
    /// column duly hung off the right-hand edge. The row has no horizontal scrollbar, so anything
    /// that does not fit is simply gone. The window's own minimum width is set so this floor and
    /// the panel's can both be honoured.
    /// </summary>
    private const double MinBoardWidth = 880;

    public FlipBoardView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        DetailSplitter.DragCompleted += (_, _) => SaveWidth();
    }

    /// <summary>
    /// Restores the saved panel width.
    ///
    /// This is done once, in code, rather than by binding the column width: the splitter writes to
    /// the same property while dragging, so a two-way binding would have two owners for one value
    /// and the panel would visibly snap back mid-drag.
    /// </summary>
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            DetailColumn.Width = new GridLength(vm.DetailPanelWidth, GridUnitType.Pixel);

        FitToWindow();
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) FitToWindow();
    }

    /// <summary>
    /// Trims the panel when the window is too narrow to honour its saved width.
    ///
    /// A pixel-width column does not give ground when the grid runs out of room, so without this a
    /// panel dragged wide on a large monitor would push the board off the right edge on a small one.
    /// The saved width is deliberately not rewritten here: shrinking the window is not the user
    /// choosing a narrower panel, and maximising again should restore what they picked.
    /// </summary>
    private void FitToWindow()
    {
        double room = ActualWidth - MinBoardWidth - DetailSplitter.Width;
        double allowed = Math.Max(DetailColumn.MinWidth, room);

        double saved = DataContext is MainViewModel vm ? vm.DetailPanelWidth : DetailColumn.Width.Value;
        double target = Math.Min(saved, allowed);

        if (Math.Abs(DetailColumn.Width.Value - target) > 0.5)
            DetailColumn.Width = new GridLength(target, GridUnitType.Pixel);
    }

    private void SaveWidth()
    {
        if (DataContext is MainViewModel vm)
            vm.DetailPanelWidth = DetailColumn.ActualWidth;
    }
}
