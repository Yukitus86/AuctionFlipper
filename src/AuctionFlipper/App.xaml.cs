using System.Windows;
using System.Windows.Threading;

namespace AuctionFlipper;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A collector fault must never take the whole app down mid-session; surface it and carry on.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        MainWindow = new Ui.MainWindow();
        MainWindow.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.ToString(),
            "Auction Flipper - unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
