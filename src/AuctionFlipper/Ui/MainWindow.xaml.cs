using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AuctionFlipper.Core;
using AuctionFlipper.Services;
using AuctionFlipper.Ui.ViewModels;

namespace AuctionFlipper.Ui;

public partial class MainWindow : Window
{
    // The title bar is drawn by Windows, not by WPF, so it keeps the system light theme however
    // dark the client area is - which on this palette means a white strip across the top of a
    // near-black trading board. These attributes are the only way to change it without taking the
    // whole non-client area over and reimplementing minimise, maximise and snap by hand.
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Packs a colour into the COLORREF byte order the DWM expects: 0x00BBGGRR.</summary>
    private static int ColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    /// <summary>
    /// Paints the system title bar to match the app.
    ///
    /// Every call is allowed to fail: the caption, text and border colours need Windows 11, and on
    /// Windows 10 only the dark-mode flag takes effect. A failed attribute returns a non-zero HRESULT
    /// and changes nothing, which is exactly the desired fallback, so the results are not checked.
    /// </summary>
    private void ApplyDarkTitleBar()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        try
        {
            int enabled = 1;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));

            int caption = ColorRef(0x16, 0x1D, 0x28);     // B.HeaderFill, top stop
            DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));

            int text = ColorRef(0xE8, 0xEE, 0xF7);        // B.Text
            DwmSetWindowAttribute(handle, DwmwaTextColor, ref text, sizeof(int));

            int border = ColorRef(0x23, 0x2C, 0x39);      // B.Border
            DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // No dwmapi.dll means no composition, and therefore no title bar to recolour.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    private readonly AppConfig _config;
    private readonly Coordinator _coordinator;
    private readonly MainViewModel _viewModel;
    private ToastWindow? _toast;

    public MainWindow()
    {
        InitializeComponent();

        _config = AppConfig.Load();
        RestorePlacement();
        _coordinator = new Coordinator(_config);
        _viewModel = new MainViewModel(_coordinator, _config);
        DataContext = _viewModel;

        _coordinator.LogMessage += message =>
            Dispatcher.BeginInvoke(() => _viewModel.StatusMessageFromHost(message));

        _coordinator.Alerts.AlertRaised += flip =>
            Dispatcher.BeginInvoke(() => OnAlert(flip));

        // The handle exists by SourceInitialized but the window has not been painted yet, so the
        // title bar comes up dark rather than flashing white for a frame first.
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            // Nothing works without a key, so land the user on the one screen that matters.
            _viewModel.Section = NavSection.Settings;
            _viewModel.StatusMessageFromHost(Loc.T("StatusNoKey"));
            return;
        }

        _coordinator.Start();
    }

    private void OnAlert(FlipOpportunity flip)
    {
        bool copied = false;
        if (_config.AlertAutoCopyEnabled)
            copied = MainViewModel.TryCopy(flip.SearchText);

        if (!_config.AlertToastEnabled) return;

        _toast ??= CreateToast();
        _toast.ShowFlip(flip, copied);
    }

    private ToastWindow CreateToast()
    {
        var toast = new ToastWindow { Owner = null };
        toast.Clicked += () =>
        {
            // Clicking the popup should put the board in front, wherever the user was.
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            toast.FadeOut();
        };
        return toast;
    }

    /// <summary>
    /// Puts the window back where it was left.
    ///
    /// Size and position are restored before the window is shown so it never appears at the default
    /// size and jumps. The saved rectangle is checked against the virtual desktop first: a window
    /// restored onto a monitor that has since been unplugged opens somewhere the mouse cannot
    /// reach, and the only way out of that is editing the config by hand.
    /// </summary>
    private void RestorePlacement()
    {
        if (_config.WindowWidth >= MinWidth) Width = _config.WindowWidth;
        if (_config.WindowHeight >= MinHeight) Height = _config.WindowHeight;

        if (!double.IsNaN(_config.WindowLeft) && !double.IsNaN(_config.WindowTop))
        {
            double left = _config.WindowLeft;
            double top = _config.WindowTop;

            double screenLeft = SystemParameters.VirtualScreenLeft;
            double screenTop = SystemParameters.VirtualScreenTop;
            double screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
            double screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

            // A strip of title bar wide enough to grab has to land on a real monitor.
            bool reachable = left + Width > screenLeft + 120 && left < screenRight - 120
                             && top >= screenTop - 8 && top < screenBottom - 60;

            if (reachable)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }

        if (_config.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        _config.WindowMaximized = WindowState == WindowState.Maximized;

        // RestoreBounds is the size the window would return to, which is the one worth keeping -
        // saving a maximised window's own bounds would restore it un-maximised at screen size.
        Rect bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        if (bounds is { Width: > 0, Height: > 0 })
        {
            _config.WindowLeft = bounds.Left;
            _config.WindowTop = bounds.Top;
            _config.WindowWidth = bounds.Width;
            _config.WindowHeight = bounds.Height;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _viewModel.Dispose();
        _toast?.Close();
        SavePlacement();
        _config.Save();
        _coordinator.Dispose();
    }
}
