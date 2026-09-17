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
    private WebDashboardServer? _dashboard;
    private ToastWindow? _toast;

    public MainWindow()
    {
        InitializeComponent();

        _config = AppConfig.Load();
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
        StartDashboard();
    }

    private void StartDashboard()
    {
        if (!_config.DashboardEnabled) return;

        _dashboard = new WebDashboardServer(_config.DashboardPort, BuildDashboardSnapshot);
        _dashboard.Start();

        if (!_dashboard.IsRunning && _dashboard.LastError is { Length: > 0 } error)
            _viewModel.StatusMessageFromHost(error);
    }

    private DashboardSnapshot BuildDashboardSnapshot()
    {
        MarketStatus status = _coordinator.GetStatus();

        // The dashboard shows the same ranking the desktop board does, so the two never disagree
        // about which flip is best.
        FlipOpportunity[] flips = _coordinator.Market.CurrentFlips()
            .Where(f => f.NetProfit >= _config.MinNetProfit
                        && f.Roi >= _config.MinRoi
                        && f.Confidence >= _config.MinConfidence
                        && !f.Flags.HasFlag(FlipFlags.NbtRisk))
            .OrderByDescending(f => f.Score)
            .Take(120)
            .ToArray();

        return new DashboardSnapshot(
            status.Budget.Used,
            status.Budget.Limit,
            $"{status.BookListings:N0} listings / {status.BookItems:N0} items",
            status.TapeSales > 0
                ? $"{status.TapeSales:N0} sales over {Format.Duration(status.TapeSpan)}"
                : "warming up",
            $"{status.Sniper.NewListingsPerMinute:0.#}/min",
            status.Sweeper.Running
                ? $"{status.Sweeper.CoverageFraction:P0} ({status.Sweeper.PagesVisited:N0}/{status.Sweeper.EstimatedTotalPages:N0})"
                : "paused",
            Format.Duration(status.Uptime),
            flips);
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

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _viewModel.Dispose();
        _toast?.Close();
        _dashboard?.Dispose();
        _config.Save();
        _coordinator.Dispose();
    }
}
