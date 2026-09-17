using System.Windows.Controls;

namespace AuctionFlipper.Ui.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    // ------------------------------------------------------------------ API key

    /// <summary>
    /// Guards the two-way mirror between the masked box and the bound one.
    ///
    /// Each control's change event writes to the other, so without this the first keystroke would
    /// bounce between them forever.
    /// </summary>
    private bool _syncingApiKey;

    /// <summary>The bound box changed - either the user typing while revealed, or the binding
    /// delivering the stored key on first load. Either way the masked box follows it.</summary>
    private void OnApiKeyPlainChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingApiKey) return;

        _syncingApiKey = true;
        ApiKeyMasked.Password = ApiKeyPlain.Text;
        _syncingApiKey = false;
    }

    /// <summary>The masked box changed, so push it into the bound box and on into the config.</summary>
    private void OnApiKeyMaskedChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_syncingApiKey) return;

        _syncingApiKey = true;
        ApiKeyPlain.Text = ApiKeyMasked.Password;
        _syncingApiKey = false;
    }

    private void OnApiKeyRevealToggled(object sender, System.Windows.RoutedEventArgs e)
    {
        bool revealed = ApiKeyReveal.IsChecked == true;

        ApiKeyPlain.Visibility = revealed ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        ApiKeyMasked.Visibility = revealed ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        ApiKeyReveal.ToolTip = Loc.T(revealed ? "HideApiKey" : "ShowApiKey");

        // Focus follows the visible box, so the caret is where the next keystroke will land.
        if (revealed) ApiKeyPlain.Focus();
        else ApiKeyMasked.Focus();
    }
}
