namespace FsCopilot.Exerciser;

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Connection;
using Network;
using Simulation;

/// <summary>
/// The other pilot: a peer of a running FS Copilot, plus a watcher on its panel channel.
///
/// Two connections, because they answer different questions. The peer link carries the
/// feature - gestures out, gestures in, and every outage the session state machine reacts
/// to - and it is the reason the exerciser is a peer rather than a mode inside the app:
/// what runs is the shipping build, wire and all. The panel channel answers "what is there
/// to press": FS Copilot already knows every panel's key, its size, and which ones the
/// profile opted in, so nothing here detects aircraft or reads profiles.
///
/// Packets are registered in FS Copilot's own order, with FS Copilot's own types, because
/// Codecs.Schema hashes each type's assembly-qualified name and a mismatched peer is
/// refused outright. MasterSwitch registers SetMaster before the Coordinator's five. Three of
/// those types are not public; see Packets.
/// </summary>
public sealed class Session : IDisposable
{
    private readonly HybridNetwork _net;
    private readonly CompositeDisposable _d = new();
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _wsCts;
    private ulong _session;
    private string? _lastChannelError;
    private uint _seq;

    public string PeerId { get; }
    public string RelayHost { get; }

    /// <summary>Panels FS Copilot has heard from, newest list wins.</summary>
    public event Action<IReadOnlyList<PanelInfo>>? PanelsChanged;

    /// <summary>The profile's pointer opt-in list, as the app broadcasts it.</summary>
    public event Action<IReadOnlyList<string>>? ConfigChanged;

    /// <summary>Sync state and role as a panel would be told them: none, connecting, live, degraded.</summary>
    public event Action<string, string>? SyncChanged;

    /// <summary>A gesture the pilot made, arriving from the app.</summary>
    public event Action<PointerEvent>? GestureReceived;

    /// <summary>Whether the app is there at all, which is the panel channel being up. The
    /// peer link cannot answer it: the app can be running with no session.</summary>
    public event Action<bool>? AppChanged;

    public event Action<string>? Log;

    public Session(string relayHost)
    {
        RelayHost = relayHost;
        PeerId = "EXRC" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        _session = (ulong)System.Random.Shared.NextInt64();

        _net = new HybridNetwork(relayHost, PeerId, "exerciser");
        Packets.Register(_net, Packets.SetMaster, Packets.SetMasterCodec);
        Packets.Register(_net, Packets.Update, Packets.UpdateCodec);
        Packets.Register(_net, typeof(Interact), Packets.InteractCodec);
        _net.RegisterPacket<Physics, Physics.Codec>();
        _net.RegisterPacket<Surfaces, Surfaces.Codec>();
        _net.RegisterPacket<PointerEvent, PointerEvent.Codec>();
        _net.RegisterPacket<PointerAck, PointerAck.Codec>();

        _d.Add(_net.Peers.Subscribe(ps =>
        {
            Peers = ps.Select(p => $"{p.PeerId} {p.Transport}{(p.Connected ? "" : " connecting")} {p.Ping}ms").ToArray();
            // This side's own link, which is not the app's sync state: the app reports
            // degraded when it lost whoever it was talking to, and that may not be us.
            Linked = ps.Any(p => p.Connected);
            PeersChanged?.Invoke(Peers);
        }));
        _d.Add(_net.Stream<PointerEvent>().Subscribe(e =>
        {
            GestureReceived?.Invoke(e);
            // The app acks what it receives; so does a peer, or the app holds its history
            // for us forever and resends it on every reconnect.
            _net.SendAll(new PointerAck(e.Session, e.Seq, _session));
        }));
    }

    public string[] Peers { get; private set; } = [];

    /// <summary>A peer of ours is connected right now.</summary>
    public bool Linked { get; private set; }
    public event Action<string[]>? PeersChanged;

    public async Task<string> Join(string code, CancellationToken ct)
    {
        Log?.Invoke($"joining {code} via {RelayHost}");
        var result = await _net.Connect(code.Trim(), ct);
        Log?.Invoke($"join: {result}");
        return result.ToString();
    }

    /// <summary>Leaves on purpose, which the peer can tell from an outage.</summary>
    public void Leave()
    {
        _net.Disconnect();
        _net.DrainDisconnect(TimeSpan.FromSeconds(1));
        Log?.Invoke("left");
    }

    /// <summary>Claims master, the way pressing Take Control does. Last writer wins,
    /// which is the app's own handover policy.</summary>
    public void TakeControl()
    {
        Packets.SendSetMaster(_net, PeerId);
        Log?.Invoke("took control");
    }

    /// <summary>Hands master to the peer, which is named by its own id - the session code.</summary>
    public void GiveControl(string peerId)
    {
        if (peerId.Length == 0) { Log?.Invoke("no peer to give control to"); return; }
        Packets.SendSetMaster(_net, peerId);
        Log?.Invoke($"gave control to {peerId}");
    }

    /// <summary>A press or a drag, stamped like the app stamps its own.</summary>
    public void Send(PointerEvent e)
    {
        var stamped = e with { Session = _session, Seq = ++_seq };
        _net.SendAll(stamped);
        Log?.Invoke($"sent {stamped.Kind} {stamped.Key} seq={stamped.Seq}");
    }

    /* ---- the panel channel ---- */

    /// <summary>Watches the app's panel channel: config, sync state, and the panel list.
    /// Reconnects the way channel.js does, rotating the port range.</summary>
    public void WatchPanels()
    {
        _wsCts = new CancellationTokenSource();
        _ = Task.Run(() => WatchLoop(_wsCts.Token));
    }

    private async Task WatchLoop(CancellationToken ct)
    {
        int[] ports = [9020, 9021, 9022, 9023, 9024];
        var i = 0;
        while (!ct.IsCancellationRequested)
        {
            var port = ports[i++ % ports.Length];
            var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), ct);
                _ws = ws;
                _lastChannelError = null;
                AppChanged?.Invoke(true);
                Log?.Invoke($"panel channel on {port}");
                await ws.SendAsync(Encoding.UTF8.GetBytes("{\"t\":\"watch\"}"),
                    WebSocketMessageType.Text, true, ct);
                await Receive(ws, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                // One line per outage, not one per port per sweep: five ports every two
                // seconds is a log nobody can read while the app is simply not running.
                var reason = $"panel channel: {e.Message}";
                if (reason != _lastChannelError) { _lastChannelError = reason; Log?.Invoke(reason); }
            }
            finally
            {
                _ws = null;
                AppChanged?.Invoke(false);
                SyncChanged?.Invoke("none", "");
            }
            // A full sweep of the range failed, so the app is not up yet.
            if (i % ports.Length == 0) await Task.Delay(1500, ct).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    private async Task Receive(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, r.Count);
            if (!r.EndOfMessage) continue;
            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            try { Handle(JsonDocument.Parse(text).RootElement); }
            catch (Exception e) { Log?.Invoke($"panel channel: {e.Message}"); }
        }
    }

    private void Handle(JsonElement json)
    {
        switch (json.TryGetProperty("t", out var t) ? t.GetString() : null)
        {
            case "config":
                ConfigChanged?.Invoke(json.TryGetProperty("pointer", out var keys)
                    ? keys.EnumerateArray().Select(k => k.GetString() ?? "").ToArray()
                    : []);
                break;
            case "state":
                SyncChanged?.Invoke(json.GetProperty("sync").GetString() ?? "?",
                    json.TryGetProperty("role", out var role) ? role.GetString() ?? "" : "");
                break;
            case "panels":
                var panels = new List<PanelInfo>();
                foreach (var p in json.GetProperty("panels").EnumerateArray())
                {
                    var key = p.GetProperty("key").GetString() ?? "";
                    int[]? rect = null;
                    if (p.TryGetProperty("rect", out var r) && r.ValueKind == JsonValueKind.Array)
                        rect = [r[0].GetInt32(), r[1].GetInt32()];
                    panels.Add(new PanelInfo(key, rect));
                }
                PanelsChanged?.Invoke(panels);
                break;
            case "bye":
                Log?.Invoke("the app said goodbye");
                break;
        }
    }

    public void Dispose()
    {
        _wsCts?.Cancel();
        try { _ws?.Abort(); } catch (Exception) { /* going anyway */ }
        _d.Dispose();
        _net.Dispose();
    }
}

/// <summary>One panel the app has heard from. Rect is the instrument's own size, which is
/// what a click on a capture has to be resolved against; null means the panel reported
/// none, which today is a placeholder element rather than a display.</summary>
public record PanelInfo(string Key, int[]? Rect)
{
    public string Identifier => Key.Split('|')[0];
    public override string ToString() => Rect == null ? $"{Key}  (no size)" : $"{Key}  {Rect[0]}x{Rect[1]}";
}
