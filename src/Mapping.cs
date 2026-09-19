namespace FsCopilot.Exerciser;

/// <summary>
/// Two fits stand between a click in this window and the fraction a panel reports.
///
/// The simulator draws the instrument into its pop-out scaled to fit and centred, so a
/// 7410x1110 strip in a 7394x1071 window fills the height at 0.965 and leaves ~122 px of
/// bar either side. Measured against five markers placed at known fractions, that rule
/// predicts positions within 6.5 instrument px, the error being the sim's horizontal scale
/// running 0.15% under its vertical one. Nothing corrects for it: it is under a pixel of
/// screen here and no button is that small.
///
/// This window then draws the capture the same way, which is a fit it controls and inverts
/// exactly. Both are recomputed from the live sizes every frame, so resizing the pop-out or
/// this window needs nothing.
/// </summary>
public readonly record struct Fit(double Scale, double OffsetX, double OffsetY, double Width, double Height)
{
    /// <summary>Content of <paramref name="cw"/> x <paramref name="ch"/> drawn to fit inside
    /// <paramref name="bw"/> x <paramref name="bh"/>, centred.</summary>
    public static Fit Contain(double cw, double ch, double bw, double bh)
    {
        if (cw <= 0 || ch <= 0 || bw <= 0 || bh <= 0) return new Fit(1, 0, 0, bw, bh);
        var scale = Math.Min(bw / cw, bh / ch);
        var w = cw * scale;
        var h = ch * scale;
        return new Fit(scale, (bw - w) / 2, (bh - h) / 2, w, h);
    }

    public Point ToContent(Point outer) => new((outer.X - OffsetX) / Scale, (outer.Y - OffsetY) / Scale);
    public Point ToOuter(Point content) => new(content.X * Scale + OffsetX, content.Y * Scale + OffsetY);
    public bool Contains(Point outer) =>
        outer.X >= OffsetX && outer.X <= OffsetX + Width && outer.Y >= OffsetY && outer.Y <= OffsetY + Height;
}
