using System.Globalization;
using System.Runtime.InteropServices;

namespace AuctionFlipper;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Prices come off the wire as invariant decimals ("31899999.99"). Pinning the culture keeps
        // parsing and display consistent regardless of the machine's regional settings.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;

        if (args.Any(a => a.Equals("--logictest", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsoleForDiagnostics();
            return LogicTests.Run() == 0 ? 0 : 1;
        }

        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsoleForDiagnostics();

            // Logic first: if the scoring rules are wrong there is no point spending requests.
            int logicFailures = LogicTests.Run();
            Console.WriteLine();
            int apiFailures = SelfTest.RunAsync(args).GetAwaiter().GetResult();
            return logicFailures == 0 && apiFailures == 0 ? 0 : 1;
        }

        // One instance at a time. Two of them share one API key, so the pair quietly spends twice
        // the request budget and gets rate limited - and they share one config file, where the last
        // one to close overwrites whatever the other saved. That is how a pin, or a setting, can be
        // made and then vanish without anything appearing to go wrong.
        using var single = new Mutex(initiallyOwned: true, @"Local\AuctionFlipper.SingleInstance",
            out bool firstInstance);

        if (!firstInstance)
        {
            // Nothing has read the config yet in this process, and the one line the user will see
            // should still be in their language.
            Loc.Current.SetLanguage(Services.AppConfig.Load().Language);

            System.Windows.MessageBox.Show(
                Loc.T("AlreadyRunning"),
                "Auction Flipper",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return 2;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>
    /// The app is a WinExe so it never flashes a console, but the self-test is a console tool. Hook
    /// up the parent terminal's console when there is one, otherwise open a fresh window.
    /// </summary>
    private static void AttachConsoleForDiagnostics()
    {
        const int AttachParentProcess = -1;
        if (!AttachConsole(AttachParentProcess))
            AllocConsole();

        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();
}
