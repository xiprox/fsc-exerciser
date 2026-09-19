namespace FsCopilot.Exerciser;

using Connection;

/// <summary>
/// Turns this window's pointer input into the gestures a panel would capture.
///
/// The thresholds are pointer.js's, copied deliberately: a press it would call a press has
/// to arrive as a press, or the exerciser tests a shape the cockpit never sends. Keep these
/// in step with Pointer.CLICK_SLOP and friends in
/// FsCopilot.Bridge/PackageSources/HTML_UI/FsCopilot/pointer.js.
/// </summary>
public sealed class GestureBuilder
{
    private const double ClickSlop = 4;      // within this, a move is still a press
    private const double SampleSlop = 2;     // a drag samples no finer than this
    private const int MinDragMs = 33;        // and no faster than this
    private const int MaxPoints = 240;       // the wire clamps here
    private const int MaxStepMs = 250;       // a pause longer than this replays as this
    private const int MaxGapMs = 1000;       // idle before a gesture, as the wire carries it

    private readonly List<(long At, Point P)> _path = [];
    private long _downAt;
    private Point _down;
    private int _button;
    private long _lastEnd;
    private bool _open;

    public bool Active => _open;

    public void Down(Point fraction, int button, long nowMs)
    {
        _open = true;
        _down = fraction;
        _downAt = nowMs;
        _button = button;
        _path.Clear();
        _path.Add((nowMs, fraction));
    }

    public void Move(Point fraction, long nowMs)
    {
        if (!_open || _path.Count == 0) return;
        var last = _path[^1];
        if (nowMs - last.At < MinDragMs) return;
        if (Math.Abs(fraction.X - last.P.X) * 1000 < SampleSlop && Math.Abs(fraction.Y - last.P.Y) * 1000 < SampleSlop) return;
        _path.Add((nowMs, fraction));
    }

    /// <summary>The gesture, or null if the pointer never went down. Coordinates are rect
    /// fractions; Session stamps Session and Seq.</summary>
    public PointerEvent? Up(Point fraction, string key, long nowMs)
    {
        if (!_open) return null;
        _open = false;
        _path.Add((nowMs, fraction));

        var gap = (ushort)Math.Clamp(_lastEnd == 0 ? 0 : nowMs - _lastEnd, 0, MaxGapMs);
        _lastEnd = nowMs;

        var moved = Math.Abs(fraction.X - _down.X) * 1000 > ClickSlop || Math.Abs(fraction.Y - _down.Y) * 1000 > ClickSlop;
        if (!moved || _path.Count < 3)
        {
            var hold = (ushort)Math.Clamp(nowMs - _downAt, 0, 1500);
            return new PointerEvent(key, 0, 0, 0, PointerKind.Press, (byte)_button, hold, gap,
                (float)_down.X, (float)_down.Y, (float)fraction.X, (float)fraction.Y, PointerEvent.NoPath);
        }

        var sampled = Thin(_path, MaxPoints);
        var points = new PointerEvent.Point[sampled.Count];
        var prev = sampled[0].At;
        for (var i = 0; i < sampled.Count; i++)
        {
            var dt = (ushort)Math.Clamp(sampled[i].At - prev, 0, MaxStepMs);
            prev = sampled[i].At;
            points[i] = new PointerEvent.Point(dt, (float)sampled[i].P.X, (float)sampled[i].P.Y);
        }
        return new PointerEvent(key, 0, 0, 0, PointerKind.Drag, (byte)_button, 0, gap,
            points[0].X, points[0].Y, points[^1].X, points[^1].Y, points);
    }

    /// <summary>Keeps the ends and thins the middle, so a long drag still starts and finishes
    /// where the pilot's did.</summary>
    private static List<(long At, Point P)> Thin(List<(long At, Point P)> path, int max)
    {
        if (path.Count <= max) return path;
        var keep = new List<(long, Point)> { path[0] };
        var step = (double)(path.Count - 2) / (max - 2);
        for (var i = 1.0; i < path.Count - 1; i += step) keep.Add(path[(int)i]);
        keep.Add(path[^1]);
        return keep;
    }
}
