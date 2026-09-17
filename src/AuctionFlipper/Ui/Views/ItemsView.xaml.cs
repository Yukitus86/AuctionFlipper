using System.Windows.Controls;
using AuctionFlipper.Ui.ViewModels;

namespace AuctionFlipper.Ui.Views;

public partial class ItemsView : UserControl
{
    public ItemsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Holds the refresh while the user is scrolled into the catalogue.
    ///
    /// The list is alphabetical, so it does not churn like the tape does, but it does grow as new
    /// items are discovered - and an insert above the viewport shifts everything below it. Freezing
    /// while scrolled keeps the row under the cursor the row that was under the cursor.
    /// </summary>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.ItemsScrolled = e.VerticalOffset > 2;
    }
}
