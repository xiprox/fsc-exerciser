namespace FsCopilot.Exerciser;

using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;

/// <summary>
/// The two processes the exerciser would otherwise ask the pilot to start by hand, and the
/// reason it can: Sync put both of them in this repository.
///
/// The relay first. Peers find each other through one, and on one machine a local one costs
/// nothing while the public one costs its round trip twice - 145 ms each way through
/// p2p.fscopilot.com, which is visible in a recording. It is a separate process rather than
/// hosted here: it is an ASP.NET application, and an Avalonia test tool has no business
/// carrying that framework.
///
/// Then FS Copilot itself, launched with the relay this window is set to, because those two
/// settings have to agree and nothing else checks that. Given --peer-id, the session code is
/// known before the app starts and the exerciser can join without anyone copying anything -
/// a build without the flag just means typing the code.
///
/// Both are launched out of this repository, never out of an FS Copilot checkout. That is
/// what Sync is for: a bin directory is shared by every branch that ever built in it, so
/// picking the newest file out of one means driving whatever was compiled last rather than
/// what anybody meant.
/// </summary>
public static class Local
{
    private static Process? _relay;

    /// <summary>The synced FS Copilot. Named even when absent, so a failure points at a path
    /// the pilot can go and look at.</summary>
    public static string FsCopilotExe => Fsc.Exe;

    /// <summary>The relay is up if this window started it, or if something else already holds
    /// its port - a leftover from a previous run, or one started in a terminal. Trying to
    /// start a second is how the log filled with a bind failure.</summary>
    public static bool RelayRunning => _relay is { HasExited: false } || PortHeld(3600);

    public static bool FsCopilotRunning => Process.GetProcessesByName("FsCopilot").Length > 0;

    private static bool PortHeld(int port)
    {
        try
        {
            foreach (var endpoint in IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners())
                if (endpoint.Port == port) return true;
        }
        catch (Exception) { /* nothing to learn from a failed enumeration */ }
        return false;
    }

    /// <summary>Starts the relay if it is not already up, and waits for it to say so. Returns
    /// what to tell the pilot, or null when it is running.</summary>
    public static async Task<string?> StartRelay(Action<string> log)
    {
        if (_relay is { HasExited: false }) return null;
        if (PortHeld(3600))
        {
            log("relay: already listening on 3600; using it");
            return null;
        }

        if (!File.Exists(Fsc.RelayExe)) return "no relay here: press Re-sync FSC source, or run sync-and-rebuild.ps1";

        var started = new TaskCompletionSource();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(Fsc.RelayExe)
            {
                WorkingDirectory = Path.GetDirectoryName(Fsc.RelayExe)!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        // The HTTP side is only there to say "I'm fine" and is pinned high, so it cannot
        // collide with whatever else the developer is running.
        process.StartInfo.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:5399";
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (e.Data.Contains("UDP port: 3600")) started.TrySetResult();
            var line = Tidy(e.Data);
            if (line != null) log($"relay: {line}");
        };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) log($"relay: {Tidy(e.Data) ?? e.Data.Trim()}"); };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception e)
        {
            return $"could not start the relay: {e.Message}";
        }

        _relay = process;
        var up = await Task.WhenAny(started.Task, Task.Delay(20000)) == started.Task;
        return up ? null : "the relay did not come up; the port may be taken";
    }

    /// <summary>What is worth reading from the relay. Its own format is
    /// "[time INF] Category   message", it repeats a STUN introduction every twenty
    /// seconds, and a CONNECT carries the peer's whole schema hash - 700 characters that
    /// say nothing a reader of this log needs.</summary>
    private static string? Tidy(string raw)
    {
        var line = raw.Trim();
        // A stack frame says where a failure was raised, which is the relay's business and not
        // this log's. The message above it is the part worth reading.
        if (line.Length == 0 || line.Contains("INTRODUCE") || line.StartsWith("at ") ||
            line.StartsWith("---") || line.StartsWith("(")) return null;
        var close = line.IndexOf(']');
        if (close > 0 && close + 1 < line.Length) line = line[(close + 1)..].Trim();
        var schema = line.IndexOf("schema=", StringComparison.Ordinal);
        if (schema > 0) line = line[..schema].TrimEnd();
        line = string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return line.Length > 160 ? line[..160] + "…" : line;
    }

    public static void StopRelay()
    {
        if (_relay is not { HasExited: false }) { _relay = null; return; }
        try { _relay.Kill(entireProcessTree: true); } catch (Exception) { /* going anyway */ }
        _relay = null;
    }

    /// <summary>Closes a running app the way its own window's close button does, so it says
    /// goodbye to its panels and its peer before it goes. Killed only if it will not.</summary>
    public static async Task StopFsCopilot()
    {
        var running = Process.GetProcessesByName("FsCopilot");
        foreach (var process in running)
        {
            try { process.CloseMainWindow(); } catch (Exception) { /* about to be killed anyway */ }
        }
        for (var i = 0; i < 20 && running.Any(p => !p.HasExited); i++) await Task.Delay(250);
        foreach (var process in running.Where(p => !p.HasExited))
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* gone between checks */ }
        }
    }

    /// <summary>Starts the synced FS Copilot against <paramref name="relayHost"/>, with
    /// <paramref name="peerId"/> as its session code. Returns an error, or null.</summary>
    public static string? StartFsCopilot(string relayHost, string peerId)
    {
        if (!File.Exists(Fsc.Exe)) return $"no synced FS Copilot at {Fsc.Exe}: press Re-sync FSC source";
        // Empty means a build that takes neither flag: start it as the pilot would.
        var args = relayHost.Length == 0 || peerId.Length == 0 ? "" : $"--relay {relayHost} --peer-id {peerId}";
        try
        {
            Process.Start(new ProcessStartInfo(Fsc.Exe, args)
            {
                WorkingDirectory = Path.GetDirectoryName(Fsc.Exe)!,
                UseShellExecute = true
            });
            return null;
        }
        catch (Exception e)
        {
            return $"could not start FS Copilot: {e.Message}";
        }
    }

    /// <summary>Whether the synced FS Copilot takes the flags above. They exist for a harness,
    /// so a build cut without them is a real possibility, and passing them would leave the app
    /// on the wrong relay with a code nobody knows. The strings live in the managed assembly,
    /// not the little apphost beside it.</summary>
    public static bool TakesTestFlags()
    {
        try
        {
            var image = File.ReadAllBytes(File.Exists(Fsc.Dll) ? Fsc.Dll : Fsc.Exe);
            return HoldsLiteral(image, "--peer-id") && HoldsLiteral(image, "--relay");
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Searches the assembly image for a string literal, as raw UTF-16 bytes. Reading
    /// the file as Unicode text instead would decode from byte zero in pairs, and the literal
    /// heap packs its entries behind a length prefix rather than aligning them - so half of all
    /// literals start on an odd offset and never appear in the decoded text at all.</summary>
    private static bool HoldsLiteral(byte[] image, string literal)
    {
        var needle = Encoding.Unicode.GetBytes(literal);
        for (var i = 0; i + needle.Length <= image.Length; i++)
        {
            var j = 0;
            while (j < needle.Length && image[i + j] == needle[j]) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }
}
