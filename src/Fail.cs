namespace FsCopilot.Exerciser;

using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Serilog;

/// <summary>
/// What a startup failure says, and where it says it.
///
/// run.cmd starts this tool with `start`, so there is no console standing behind the window
/// to print to and nothing waiting on the exit code: a throw out of Main is a process that
/// looks like it never launched. Anything fatal therefore ends up in a message box, which is
/// the one output this process is certain to have.
///
/// The diagnosis is nearly always the same one. This tool compiles against the copy of FS
/// Copilot under fsc/ and calls into it directly, so a copy whose API has moved does not fail
/// politely - it fails as a missing type or a missing method, at the first line that touches
/// one. Saying that outright beats the runtime's own wording, which names a type and leaves
/// the reader to work out that a sync is what put it there.
/// </summary>
internal static class Fail
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    private const uint IconError = 0x10;

    private static string LogPath => Path.GetFullPath("exerciser-log");

    /// <summary>Says it, logs it, and ends the process: nothing that calls this can carry on.</summary>
    public static void Fatal(string message)
    {
        try
        {
            Log.Fatal("{Message}", message);
            Log.CloseAndFlush();
        }
        catch (Exception) { /* The message box is the point; the log is the copy for later. */ }

        MessageBoxW(IntPtr.Zero, message, "FS Copilot Exerciser", IconError);

        // Environment.Exit rather than a return: this can be called from the unhandled
        // handler, where returning lets the default one print to the console nobody has.
        Environment.Exit(1);
    }

    /// <summary>An exception that got out, read for the one cause worth naming.</summary>
    public static void Fatal(Exception e) => Fatal(Describe(e));

    private static string Describe(Exception e)
    {
        var root = Root(e);

        if (!IsApiDrift(root))
            return $"The exerciser stopped with an error.\n\n{root.GetType().Name}: {root.Message}\n\n" +
                   $"The stack is in {LogPath}.";

        return "FS Copilot's API has moved underneath the exerciser.\n\n" +
               $"{root.GetType().Name}: {root.Message}\n\n" +
               Sync.Origin() + "\n\n" +
               "Build the exerciser against that copy again:\n\n" +
               "    powershell -ExecutionPolicy Bypass -File sync-and-rebuild.ps1\n\n" +
               "If that build fails on missing types or members, the checkout you synced does " +
               "not have the FS Copilot work this exerciser drives. Sync one that does, with -Fsc.";
    }

    /// <summary>
    /// The shapes a moved API arrives in. A type that is gone is a TypeLoadException, a
    /// member that is gone a Missing*Exception, and an assembly that is gone a
    /// FileNotFoundException - the last only counts when the assembly is FS Copilot's, since
    /// any missing file would otherwise be blamed on the sync.
    /// </summary>
    private static bool IsApiDrift(Exception e) => e switch
    {
        TypeLoadException or MissingMemberException => true,
        FileNotFoundException f => f.FileName?.StartsWith("FsCopilot", StringComparison.OrdinalIgnoreCase) == true,
        _ => false,
    };

    /// <summary>
    /// Past the wrappers. A type that fails to load inside a constructor comes out of
    /// Avalonia as a TargetInvocationException, and the useful half is underneath.
    /// </summary>
    private static Exception Root(Exception e)
    {
        while (e.InnerException != null && e is TargetInvocationException or TypeInitializationException)
            e = e.InnerException;
        return e;
    }
}
