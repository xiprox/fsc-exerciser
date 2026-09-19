namespace FsCopilot.Exerciser;

using System.Text.Json;
using Connection;

/// <summary>
/// Gestures to a file and back, so a scripted run can be repeated after every change to the
/// thing under test. One JSON object per line, the shape the panel channel uses, plus the
/// direction and the millisecond it happened - which is what lets a replay keep the pilot's
/// own pacing instead of firing everything at once.
/// </summary>
public sealed class Recorder
{
    private readonly List<(long At, PointerEvent Event, bool Mine)> _log = [];
    private long _startedAt;
    private bool _recording;

    public string Path { get; private set; } = "";

    public void Start()
    {
        _log.Clear();
        _startedAt = Environment.TickCount64;
        _recording = true;
    }

    public int Stop()
    {
        _recording = false;
        Path = System.IO.Path.GetFullPath($"exerciser-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.ndjson");
        using var file = new StreamWriter(Path);
        foreach (var (at, e, mine) in _log) file.WriteLine(Line(at, e, mine));
        return _log.Count;
    }

    public void Note(PointerEvent e, bool mine)
    {
        if (!_recording) return;
        _log.Add((Environment.TickCount64 - _startedAt, e, mine));
    }

    /// <summary>Replays what this window sent, at the gaps it was sent with, under
    /// <paramref name="key"/> - a recording made on one panel is useful on another of the
    /// same shape.</summary>
    public async Task<int> Replay(string key, Action<PointerEvent> send)
    {
        var mine = _log.Where(x => x.Mine).ToList();
        var played = 0;
        long previous = 0;
        foreach (var (at, e, _) in mine)
        {
            var wait = (int)Math.Clamp(at - previous, 0, 5000);
            previous = at;
            if (wait > 0) await Task.Delay(wait);
            send(e with { Key = key });
            played++;
        }
        return played;
    }

    private static string Line(long at, PointerEvent e, bool mine)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("at", at);
            w.WriteString("dir", mine ? "sent" : "received");
            w.WriteString("k", e.Kind == PointerKind.Drag ? "drag" : "press");
            w.WriteString("key", e.Key);
            w.WriteNumber("button", e.Button);
            w.WriteNumber("gap", e.GapMs);
            w.WriteNumber("hold", e.HoldMs);
            w.WriteNumber("nx", e.DownX);
            w.WriteNumber("ny", e.DownY);
            w.WriteNumber("ux", e.UpX);
            w.WriteNumber("uy", e.UpY);
            if (e.Path.Length > 0)
            {
                w.WriteStartArray("path");
                foreach (var p in e.Path)
                {
                    w.WriteStartArray();
                    w.WriteNumberValue(p.DtMs);
                    w.WriteNumberValue(p.X);
                    w.WriteNumberValue(p.Y);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
