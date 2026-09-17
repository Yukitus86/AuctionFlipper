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
