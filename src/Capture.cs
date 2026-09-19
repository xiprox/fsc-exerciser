namespace FsCopilot.Exerciser;

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

/// <summary>
/// A live picture of a simulator window, and the geometry needed to turn a click on that
/// picture back into what the panel would report.
///
/// PrintWindow with PW_RENDERFULLCONTENT is what works: MSFS draws with DirectX, and the
/// Coherent inspector - which does have Page.snapshotRect - refuses it ("Could not capture
/// snapshot"). The call re-renders the whole window, so cost follows window size rather
/// than the part on show: 33 ms for a 2401x1360 cockpit, 63 ms for a 7394x1071 pop-out.
/// Windows.Graphics.Capture would copy GPU frames instead and is the upgrade if recordings
/// look choppy.
///
/// The bitmap comes back from a DIB section, so the pixels are already BGRA, bottom-up
/// avoided by a negative height, and go straight into a WriteableBitmap with no
/// System.Drawing dependency in between.
/// </summary>
public static class Capture
{
    private const uint PwClientOnly = 1, PwRenderFullContent = 2;

    public static IReadOnlyList<SimWindow> SimWindows()
    {
        var found = new List<SimWindow>();
        var sims = System.Diagnostics.Process.GetProcesses()
            .Where(p => p.ProcessName.StartsWith("FlightSimulator", StringComparison.OrdinalIgnoreCase))
            .Select(p => (uint)p.Id)
            .ToHashSet();
        if (sims.Count == 0) return found;

        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (!sims.Contains(pid) || !IsWindowVisible(h)) return true;
            var title = new StringBuilder(256);
            GetWindowText(h, title, title.Capacity);
            GetClientRect(h, out var r);
            if (r.Right < 64 || r.Bottom < 64) return true;
            found.Add(new SimWindow(h, title.ToString(), r.Right, r.Bottom));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>The window a pop-out of this panel would be. MSFS titles a pop-out with the
    /// instrument identifier - DISPLAYUNITS for DisplayUnits|config=N324DU - which is a
    /// shortcut and not a rule: two instruments can share an identifier (the A220 has two
    /// CTPs, differing only in the query a title does not carry), so more than one match
    /// means the pilot picks.</summary>
    public static IReadOnlyList<SimWindow> Candidates(IReadOnlyList<SimWindow> windows, string identifier) =>
        windows.Where(w => string.Equals(w.Title, identifier, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Grabs the window into <paramref name="into"/>, reallocating if the window
    /// resized. Returns null when the window has gone.</summary>
    public static unsafe WriteableBitmap? Grab(IntPtr hwnd, ref WriteableBitmap? into)
    {
        if (!IsWindow(hwnd) || !GetClientRect(hwnd, out var r) || r.Right <= 0 || r.Bottom <= 0) return null;
        int w = r.Right, h = r.Bottom;

        if (into == null || into.PixelSize.Width != w || into.PixelSize.Height != h)
            into = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = w,
                Height = -h,          // top-down, so rows match the WriteableBitmap's order
                Planes = 1,
                BitCount = 32,
                Compression = 0
            }
        };
        var dib = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(dc, dib);
        try
        {
            if (!PrintWindow(hwnd, dc, PwClientOnly | PwRenderFullContent)) return null;
            using var fb = into.Lock();
            var stride = w * 4;
            if (fb.RowBytes == stride)
            {
                Buffer.MemoryCopy((void*)bits, (void*)fb.Address, (long)fb.RowBytes * h, (long)stride * h);
            }
            else
            {
                for (var y = 0; y < h; y++)
                    Buffer.MemoryCopy((void*)(bits + y * stride), (void*)(fb.Address + y * fb.RowBytes), fb.RowBytes, stride);
            }
            return into;
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(dib);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /* ---- win32 ---- */

    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect r);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }
}

public record SimWindow(IntPtr Handle, string Title, int Width, int Height)
{
    public override string ToString() => $"{(Title.Length == 0 ? "(untitled)" : Title)}  {Width}x{Height}";
}
