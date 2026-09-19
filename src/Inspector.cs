namespace FsCopilot.Exerciser;

using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;

/// <summary>
/// Drives a panel document through the simulator's own remote inspector, which is how the
/// overlays are tested.
///
/// The overlays answer to sync state, and sync state answers to what the session is really
/// doing, so showing one on demand would otherwise mean faking an outage. pointer.js exposes
/// window.fscOverlay(state) for exactly this - it holds an overlay against the two-second
/// state renewals until cleared - and window.fscUnlock() to force one off. Both were written
/// to be typed into the debugger console; this types them.
///
/// The inspector is on 19999 and works with DevMode off. Panel documents are titled
/// "VCockpitNN - identifier", which does not identify one panel when an aircraft has two of
/// the same instrument, so the right document is found by asking each one which key its
/// pointer agent holds.
/// </summary>
public static class Inspector
{
    private const string Host = "127.0.0.1";
    private const int Port = 19999;

    public static async Task<IReadOnlyList<(int Id, string Title)>> Pages(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        var json = await http.GetStringAsync($"http://{Host}:{Port}/pagelist.json", ct);
        var pages = new List<(int, string)>();
        foreach (var page in JsonDocument.Parse(json).RootElement.EnumerateArray())
        {
            if (page.TryGetProperty("id", out var id) && page.TryGetProperty("title", out var title))
                pages.Add((id.GetInt32(), title.GetString() ?? ""));
        }
        return pages;
    }

    /// <summary>Evaluates in one document and returns what it produced, as text.</summary>
    public static async Task<string?> Evaluate(int pageId, string expression, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://{Host}:{Port}/devtools/page/{pageId}"), ct);
        var request = JsonSerializer.Serialize(new
        {
            id = 1,
            method = "Runtime.evaluate",
            @params = new { expression, returnByValue = true }
        });
        await ws.SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, ct);

        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) break;
            message.Write(buffer, 0, r.Count);
            if (!r.EndOfMessage) continue;

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            var root = JsonDocument.Parse(text).RootElement;
            // Events share the socket with replies; ours is the one carrying our id.
            if (!root.TryGetProperty("id", out _)) continue;
            if (root.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.ToString());
            return root.TryGetProperty("result", out var result) && result.TryGetProperty("result", out var value)
                ? value.TryGetProperty("value", out var v) ? v.ToString() : value.ToString()
                : null;
        }
        return null;
    }

    /// <summary>The document whose pointer agent holds this key, or -1. Asks the panels
    /// rather than reading their titles, which carry no query string.</summary>
    public static async Task<int> PanelPage(string key, CancellationToken ct)
    {
        foreach (var (id, title) in await Pages(ct))
        {
            if (!title.StartsWith("VCockpit", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var held = await Evaluate(id, "window.fscPointer ? window.fscPointer.key : ''", ct);
                if (held == key) return id;
            }
            catch (Exception) { /* a document that will not answer is not the one */ }
        }
        return -1;
    }
}
