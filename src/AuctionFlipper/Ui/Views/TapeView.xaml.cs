using System.Windows.Controls;
using AuctionFlipper.Ui.ViewModels;

namespace AuctionFlipper.Ui.Views;

public partial class TapeView : UserControl
{
    public TapeView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Tells the view model to stop rewriting the list while the user is reading down it.
    ///
    /// The tape is newest-first against a feed printing several sales a second, so a rebuild while
    /// scrolled down slides every row out from under the cursor. Once the list is back at the top
    /// there is nothing to lose by refreshing, so the freeze lifts itself.
    /// </summary>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.TapeScrolled = e.VerticalOffset > 2;
    }
}
