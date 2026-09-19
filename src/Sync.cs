namespace FsCopilot.Exerciser;

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

/// <summary>
/// Where the synced FS Copilot came from, whether it has gone stale, and how to replace it.
///
/// Replacing it is the part this class does not do. The assemblies under fsc/ are mapped into
/// this process, and Windows will not let a process overwrite or delete a file it has loaded
/// - a recursive delete gets as far as FsCopilot.dll, having already removed everything it
/// reached on the way, and throws. So the copy lives in sync-and-rebuild.ps1 and runs in the gap after
/// this process exits: Start hands it this pid to wait on and this exe to start again.
///
/// The copy is a copy rather than a build because anyone changing FS Copilot rebuilds it in
/// their own loop, and a second build here would be a slower way to the same files. The relay
/// is the exception - FS Copilot's discovery project is pinned linux-x64 self-contained for
/// the server it deploys to, so a normal build of it produces nothing that starts here.
/// The script also rebuilds this tool, which compiles against the copy: one whose API moved
/// would otherwise restart an exerciser built for the copy before it.
///
/// One thing is deliberately not copied: Community/, the panel package. FS Copilot deploys
/// whatever is in it to the simulator on startup, so syncing it would put this tool in charge
/// of what the sim runs - and would overwrite the package the developer just built in the
/// Project Editor with a copy of an older one. Left out, the app finds nothing to deploy and
/// the sim keeps what is mounted. The panel package is built by the simulator, not by
/// dotnet build, and it is the developer's to decide.
///
/// The stale check is the point of the whole arrangement. A harness silently talking to a
/// build nobody meant costs an afternoon: the schema hash refuses the join, or worse it does
/// not, and behaviour gets explained by code that is no longer there.
/// </summary>
public static class Sync
{
    public sealed record State(string FscPath, string? Commit, string? Hash, DateTime When);

    private static string ConfigPath => Path.Combine(Fsc.Root, "fsc-exerciser.json");
    private static string Script => Path.Combine(Fsc.Root, "sync-and-rebuild.ps1");

    public static State? Read()
    {
        try
        {
            return File.Exists(ConfigPath)
                ? JsonSerializer.Deserialize<State>(File.ReadAllText(ConfigPath))
                : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>What to tell the pilot, or null when the copy is current. Cheap enough to call
    /// on every refresh: one hash of about a megabyte.</summary>
    public static string? Stale()
    {
        var state = Read();
        if (state == null) return "not synced yet: run sync-and-rebuild.ps1 once";
        if (!File.Exists(Fsc.Dll)) return "the synced build is gone: press Re-sync FSC source";

        var source = SourceDll(state);
        if (!File.Exists(source)) return $"no FS Copilot build at {state.FscPath} to sync from";

        return Hash(source) == state.Hash
            ? null
            : $"FS Copilot was rebuilt {Since(File.GetLastWriteTimeUtc(source))}; press Re-sync FSC source";
    }

    /// <summary>
    /// Whether the copy under fsc/ is still the one the compiler saw, and what to say when
    /// it is not.
    ///
    /// Stale() asks whether FS Copilot has been rebuilt since the sync. This asks the other
    /// question, the one no build can: whether fsc/ was replaced after this exe was built
    /// against it. Nothing links the two on disk - the exe and the copy are separate files,
    /// and either can be swapped without touching the other - so the compiler stamps the
    /// copy's hash into the assembly and this compares it against what is there now.
    ///
    /// It stops here rather than letting the first line that touches a moved type do it,
    /// because that line is somewhere inside a window constructor and what it throws names a
    /// type, not the sync that took the type away.
    /// </summary>
    public static string? Mismatch()
    {
        if (!File.Exists(Fsc.Dll))
            return "There is no FS Copilot build under fsc/.\n\n" +
                   "Sync one:\n\n    powershell -ExecutionPolicy Bypass -File sync-and-rebuild.ps1";

        // Empty when there was no copy at build time, which RequireSync already refuses.
        if (SyncedBuild.CompiledAgainst.Length == 0) return null;
        if (string.Equals(Hash(Fsc.Dll, full: true), SyncedBuild.CompiledAgainst,
                StringComparison.OrdinalIgnoreCase)) return null;

        return "The exerciser was built against a different FS Copilot build than the one now " +
               "under fsc/.\n\n" +
               Origin() + "\n\n" +
               "Build the exerciser against that copy:\n\n" +
               "    powershell -ExecutionPolicy Bypass -File sync-and-rebuild.ps1\n\n" +
               "If that build fails on missing types or members, the checkout you synced does " +
               "not have the FS Copilot work this exerciser drives. Sync one that does, with -Fsc.";
    }

    /// <summary>Where the copy came from, for a message that would otherwise send the reader
    /// to fsc-exerciser.json to find out.</summary>
    public static string Origin()
    {
        var state = Read();
        if (state == null) return "Nothing in fsc-exerciser.json records where the copy came from.";

        var commit = state.Commit == null ? "" : $" ({state.Commit})";
        return $"The copy under fsc/ was synced from {state.FscPath}{commit}, " +
               $"{Since(state.When.ToUniversalTime())}.";
    }

    private static string SourceDll(State state) =>
        Path.Combine(state.FscPath, "FsCopilot", "bin", "Debug", "net9.0", "win-x64", "FsCopilot.dll");

    /// <summary>
    /// Starts the copy and returns what to tell the pilot, or null when it is under way. On
    /// null the caller has to close the window: the script is waiting for this process to go
    /// before it touches fsc/, and it starts the exerciser again afterwards.
    /// </summary>
    public static string? Start()
    {
        var state = Read();
        if (state == null) return "no FS Copilot checkout recorded: run sync-and-rebuild.ps1 once";
        if (!File.Exists(Script)) return $"nothing to run at {Script}";
        if (!File.Exists(SourceDll(state))) return $"no FS Copilot build at {state.FscPath} to sync from";

        var exe = Environment.ProcessPath;
        var arguments =
            $"-NoProfile -ExecutionPolicy Bypass -File \"{Script}\" -Fsc \"{state.FscPath}\" " +
            $"-AfterPid {Environment.ProcessId}" +
            (exe == null ? "" : $" -Relaunch \"{exe}\"");

        try
        {
            // Its own window, and not a child that dies with this one: it outlives this
            // process on purpose, and the pilot should be able to read what it says.
            Process.Start(new ProcessStartInfo("powershell", arguments)
            {
                WorkingDirectory = Fsc.Root,
                UseShellExecute = true
            });
            return null;
        }
        catch (Exception e)
        {
            return $"could not start sync-and-rebuild.ps1: {e.Message}";
        }
    }

    /// <summary>The 16 characters fsc-exerciser.json records, or the whole digest, which is
    /// what the build stamps into the assembly.</summary>
    private static string Hash(string path, bool full = false)
    {
        using var stream = File.OpenRead(path);
        var hex = Convert.ToHexString(SHA256.HashData(stream));
        return full ? hex : hex[..16];
    }

    private static string Since(DateTime utc)
    {
        var ago = DateTime.UtcNow - utc;
        if (ago < TimeSpan.FromMinutes(1)) return "just now";
        if (ago < TimeSpan.FromHours(1)) return $"{(int)ago.TotalMinutes} min ago";
        if (ago < TimeSpan.FromDays(1)) return $"{(int)ago.TotalHours} h ago";
        return $"{(int)ago.TotalDays} days ago";
    }
}
