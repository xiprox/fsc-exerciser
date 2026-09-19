namespace FsCopilot.Exerciser;

using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Connection;

/// <summary>
/// The panel, as a picture you can press.
///
/// Draws the captured window scaled to fit, inverts a click back to rect fractions (see
/// <see cref="Fit"/>), and marks every gesture it knows about: the ones made here in one
/// colour, the ones arriving from the pilot in another. A drag from the pilot arrives whole,
/// at mouse-up, because the wire carries a drag as one packet - so it is drawn along its own
/// recorded timings, which is the same pacing the panel replays it at.
/// </summary>
public sealed class PanelView : Control
{
    private static readonly IBrush MineBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
    private static readonly IBrush TheirsBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x4D));
    private static readonly TimeSpan MarkLife = TimeSpan.FromSeconds(2);

    /// <summary>Breathing room between the capture and the pane's edges, in every mode, so a
    /// panel never sits flush against the frame.</summary>
    private const double Inset = 12;

    private readonly List<Mark> _marks = [];
    private readonly GestureBuilder _builder = new();
    private readonly DispatcherTimer _repaint;
    private WriteableBitmap? _frame;
    private double _zoom = 1;
    private Point _pan;
    private bool _panning;
    private Point _panFrom;

    /// <summary>Instrument size as the panel reported it, or null when it reported none.</summary>
    public int[]? PanelRect { get; set; }

    public string Key { get; set; } = "";

    /// <summary>Raised when the pilot of this window completes a gesture.</summary>
    public event Action<PointerEvent>? GestureMade;

    public PanelView()
    {
        ClipToBounds = true;
        Focusable = true;
        _repaint = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _repaint.Tick += (_, _) => { if (_marks.Count > 0 || _frame != null) InvalidateVisual(); };
        _repaint.Start();
    }

    public void ShowFrame(WriteableBitmap? frame)
    {
        _frame = frame;
        InvalidateVisual();
    }

    public void ZoomBy(double factor)
    {
        _zoom = Math.Clamp(_zoom * factor, 1, 40);
        if (Math.Abs(_zoom - 1) < 0.001) _pan = default;
        InvalidateVisual();
    }

    public void ResetView()
    {
        _zoom = 1;
        _pan = default;
        InvalidateVisual();
    }

    /// <summary>How big the capture is drawn, as a percentage of its own pixels. The A220's
    /// strip is 6.9:1, so fitted it lands near 20% and 1:1 is the readable mode.</summary>
    public double ZoomPercent => _frame == null ? 100 : CaptureInControl().Scale * 100;

    /// <summary>The capture's height fills the pane, so a wide strip runs off the sides and
    /// its displays are as tall as the window allows.</summary>
    public void FitHeight()
    {
        if (_frame == null || Bounds.Height <= 2 * Inset) return;
        var fit = Inner();
        var wanted = (Bounds.Height - 2 * Inset) / _frame.PixelSize.Height;
        if (fit.Scale > 0) _zoom = Math.Clamp(wanted / fit.Scale, 1, 40);
        _pan = default;
        InvalidateVisual();
    }

    /// <summary>One captured pixel per screen pixel.</summary>
    public void ActualSize()
    {
        if (_frame == null) return;
        var fit = Inner();
        if (fit.Scale > 0) _zoom = Math.Clamp(1 / fit.Scale, 1, 40);
        InvalidateVisual();
    }

    /// <summary>A gesture from the pilot, drawn as it would replay.</summary>
    public void ShowIncoming(PointerEvent e)
    {
        if (e.Kind == PointerKind.Press)
        {
            Add(new Mark(new Point(e.DownX, e.DownY), false, DateTime.UtcNow));
            return;
        }
        // Lay the path out on its own clock, which is what the panel's replay queue does.
        var at = DateTime.UtcNow;
        foreach (var p in e.Path)
        {
            at = at.AddMilliseconds(p.DtMs);
            Add(new Mark(new Point(p.X, p.Y), false, at));
        }
    }

    private void Add(Mark m)
    {
        _marks.Add(m);
        if (_marks.Count > 2000) _marks.RemoveRange(0, 1000);
    }

    /* ---- geometry ---- */

    /// <summary>Where the instrument sits inside the captured window: the simulator's own
    /// contain-fit. Without the instrument's size there is nothing to invert, so the whole
    /// capture is treated as the instrument and the caller says so in the UI.</summary>
    private Fit InstrumentInCapture()
    {
        if (_frame == null) return new Fit(1, 0, 0, 0, 0);
        var cw = _frame.PixelSize.Width;
        var ch = _frame.PixelSize.Height;
        if (PanelRect is not [> 0, > 0]) return new Fit(1, 0, 0, cw, ch);
        return Fit.Contain(PanelRect[0], PanelRect[1], cw, ch);
    }

    /// <summary>Where the captured window sits inside this control, including zoom and pan.</summary>
    private Fit CaptureInControl()
    {
        if (_frame == null) return new Fit(1, 0, 0, 0, 0);
        var fit = Inner();
        return fit with
        {
            Scale = fit.Scale * _zoom,
            Width = fit.Width * _zoom,
            Height = fit.Height * _zoom,
            OffsetX = fit.OffsetX + _pan.X - (fit.Width * (_zoom - 1) / 2),
            OffsetY = fit.OffsetY + _pan.Y - (fit.Height * (_zoom - 1) / 2)
        };
    }

    /// <summary>The unzoomed fit, inside the inset.</summary>
    private Fit Inner()
    {
        var width = Math.Max(1, Bounds.Width - 2 * Inset);
        var height = Math.Max(1, Bounds.Height - 2 * Inset);
        var fit = Fit.Contain(_frame!.PixelSize.Width, _frame.PixelSize.Height, width, height);
        return fit with { OffsetX = fit.OffsetX + Inset, OffsetY = fit.OffsetY + Inset };
    }

    /// <summary>Control point to rect fraction, or null when the point is off the instrument.</summary>
    public Point? ToFraction(Point p)
    {
        if (_frame == null) return null;
        var inCapture = CaptureInControl().ToContent(p);
        var inst = InstrumentInCapture();
        if (inst.Width <= 0) return null;
        var content = inst.ToContent(inCapture);
        return new Point(content.X / (PanelRect is [> 0, > 0] ? PanelRect[0] : _frame.PixelSize.Width),
            content.Y / (PanelRect is [> 0, > 0] ? PanelRect[1] : _frame.PixelSize.Height));
    }

    private Point FromFraction(Point f)
    {
        var instW = PanelRect is [> 0, > 0] ? PanelRect[0] : _frame?.PixelSize.Width ?? 1;
        var instH = PanelRect is [> 0, > 0] ? PanelRect[1] : _frame?.PixelSize.Height ?? 1;
        var inst = InstrumentInCapture();
        var inCapture = inst.ToOuter(new Point(f.X * instW, f.Y * instH));
        return CaptureInControl().ToOuter(inCapture);
    }

    /* ---- input ---- */

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsMiddleButtonPressed)
        {
            _panning = true;
            _panFrom = e.GetPosition(this);
            return;
        }
        var f = ToFraction(e.GetPosition(this));
        if (f == null || Key.Length == 0) return;
        _builder.Down(f.Value, props.IsRightButtonPressed ? 2 : 0, Now());
        Add(new Mark(f.Value, true, DateTime.UtcNow));
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_panning)
        {
            var now = e.GetPosition(this);
            _pan += now - _panFrom;
            _panFrom = now;
            InvalidateVisual();
            return;
        }
        if (!_builder.Active) return;
        var f = ToFraction(e.GetPosition(this));
        if (f == null) return;
        _builder.Move(f.Value, Now());
        Add(new Mark(f.Value, true, DateTime.UtcNow));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_panning) { _panning = false; return; }
        if (!_builder.Active) return;
        var f = ToFraction(e.GetPosition(this)) ?? default;
        var gesture = _builder.Up(f, Key, Now());
        e.Pointer.Capture(null);
        if (gesture != null) GestureMade?.Invoke(gesture);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ZoomBy(e.Delta.Y > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    private static long Now() => Environment.TickCount64;

    /* ---- render ---- */

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        if (_frame == null)
        {
            var text = new FormattedText("No capture. Pick a simulator window.",
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 14, Brushes.Gray);
            ctx.DrawText(text, new Point(12, 12));
            return;
        }

        var view = CaptureInControl();
        ctx.DrawImage(_frame, new Rect(0, 0, _frame.PixelSize.Width, _frame.PixelSize.Height),
            new Rect(view.OffsetX, view.OffsetY, view.Width, view.Height));

        // The instrument's own edges, so the bars the sim leaves are visible rather than
        // silently part of the picture.
        var inst = InstrumentInCapture();
        if (PanelRect is [> 0, > 0])
        {
            var tl = view.ToOuter(new Point(inst.OffsetX, inst.OffsetY));
            var br = view.ToOuter(new Point(inst.OffsetX + inst.Width, inst.OffsetY + inst.Height));
            ctx.DrawRectangle(new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 1),
                new Rect(tl, br));
        }

        var now = DateTime.UtcNow;
        _marks.RemoveAll(m => now - m.At > MarkLife);
        foreach (var m in _marks)
        {
            if (m.At > now) continue;              // an incoming drag point still to come
            var age = (now - m.At).TotalMilliseconds / MarkLife.TotalMilliseconds;
            var p = FromFraction(m.Where);
            var brush = m.Mine ? MineBrush : TheirsBrush;
            var radius = 4 + 14 * age;
            ctx.DrawEllipse(null, new Pen(brush, 2 * (1 - age)), p, radius, radius);
        }
    }

    private readonly record struct Mark(Point Where, bool Mine, DateTime At);
}
