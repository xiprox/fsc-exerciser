namespace FsCopilot.Exerciser;

using Avalonia;
using Serilog;

/*
 * The exerciser: the other pilot, on this machine, beside a running FS Copilot.
 *
 * It joins the app's session as a peer, so what it exercises is the shipping build's own
 * wire, session state machine and panel channel rather than a test mode inside the app.
 * Pointer forwarding is the first feature page; the next feature adds a page.
 *
 * Test-only, and not part of what the app ships: one project, dropped by dropping its
 * commit. FsCopilot's InternalsVisibleTo for this assembly goes with it.
 */
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Options.Current = Options.Parse(args);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File("exerciser-log", shared: true,
                outputTemplate: "[{Timestamp:HH:mm:ss.fff}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // Nothing below here is allowed to end the process quietly. Started from run.cmd
        // there is no console to print to and nobody reading the exit code, so an exception
        // that gets out is a window that never appears and no reason given.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Fail.Fatal(ex);
            else Fail.Fatal($"The exerciser stopped: {e.ExceptionObject}");
        };

        // Before the window, because the window is where the failure would otherwise land:
        // MainWindow's constructor names FS Copilot types, so the JIT resolves them as it
        // prepares that constructor, and a copy whose API has moved throws there. Main can
        // ask first precisely because nothing it touches - Options, Sync, Fail - names one.
        if (Sync.Mismatch() is { } mismatch) Fail.Fatal(mismatch);

        try
        {
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            Fail.Fatal(e);
        }
    }
}
