namespace FsCopilot.Exerciser;

using System.Runtime.InteropServices;

/// <summary>
/// Pops a cockpit panel out into its own simulator window, which is what the pointer page
/// captures.
///
/// The simulator has no command for it. Pop-out is bound to Right-Alt + left click in the
/// 3D cockpit, and nothing in SimConnect or the panel documents exposes the same thing, so
/// this sends that click - the way MSFS pop-out managers do it.
///
/// What it cannot know is where the panel is on screen: that is a spot in a 3D cockpit
/// under whatever camera the pilot is using, and nothing the app reports locates it. So the
/// pilot points at it once and presses a key, and this sends Right-Alt + click at the
/// cursor. The pop-out then persists, so the pointing is once per panel, not once per run.
/// </summary>
public static class PopOut
{
    private const int VkRMenu = 0xA5;
    private const uint KeyEventExtended = 0x0001, KeyEventUp = 0x0002;
    private const uint MouseMove = 0x0001, MouseLeftDown = 0x0002, MouseLeftUp = 0x0004;
    private const int WhMouseLowLevel = 14, WmLeftButtonDown = 0x0201, WmLeftButtonUp = 0x0202;

    /// <summary>Whether the key is down now, or went down since the last call. Polled rather
    /// than hooked: a global hook needs a message loop of its own, and this only runs while
    /// the pilot is being asked to point at something. The "since last call" bit matters -
    /// a quick tap between two polls is otherwise lost, and the pilot presses again wondering
    /// what is broken.</summary>
    public static bool KeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8001) != 0;

    public static Point Cursor()
    {
        GetCursorPos(out var p);
        return new Point(p.X, p.Y);
    }

    /// <summary>Whether the cursor is within the window's bounds, rather than whether the
    /// window is the one on top there: the exerciser's own window may well be covering the
    /// spot, and the simulator is fronted before the click either way.</summary>
    public static bool CursorOver(IntPtr hwnd)
    {
        GetCursorPos(out var p);
        return Within(hwnd, p);
    }

    private static bool Within(IntPtr hwnd, NativePoint p) =>
        GetWindowRect(hwnd, out var r) && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    /* ---- arming ---- */

    private static HookProc? _hook;          // held against collection while installed
    private static IntPtr _hookHandle;
    private static Func<NativePoint, IntPtr>? _resolve;
    private static Action<IntPtr, Point>? _fire;
    private static bool _swallowUp;
    private static Action<IntPtr, Point>? _pending;
    private static (IntPtr Window, Point At) _pendingAt;

    /// <summary>Waits for the pilot to click a simulator window, and eats that click.
    ///
    /// Eating it is the point: a click that reached the cockpit would press whatever it
    /// landed on, which on a display is a button. So the press is swallowed, its release
    /// with it, and <paramref name="fire"/> is called with the window and the screen point -
    /// the caller then sends the Right-Alt + click that actually pops the panel out.
    ///
    /// A low-level hook needs a thread that pumps messages, which is why this is armed from
    /// the UI thread. Clicks anywhere but a simulator window pass through untouched, so the
    /// rest of the desktop still works while armed.</summary>
    public static bool ArmForClick(Func<NativePoint, IntPtr> resolve, Action<IntPtr, Point> fire)
    {
        Disarm();
        _resolve = resolve;
        _fire = fire;
        _swallowUp = false;
        _hook = HookCallback;
        _hookHandle = SetWindowsHookEx(WhMouseLowLevel, _hook, IntPtr.Zero, 0);
        if (_hookHandle != IntPtr.Zero) return true;
        Disarm();
        return false;
    }

    public static void Disarm()
    {
        if (_hookHandle != IntPtr.Zero) UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
        _hook = null;
        _resolve = null;
        _fire = null;
    }

    public static bool Armed => _hookHandle != IntPtr.Zero;

    private static IntPtr HookCallback(int code, IntPtr message, IntPtr data)
    {
        if (code < 0) return CallNextHookEx(_hookHandle, code, message, data);
        if (_resolve == null && !_swallowUp) return CallNextHookEx(_hookHandle, code, message, data);

        var msg = (int)message;
        if (msg == WmLeftButtonUp && _swallowUp)
        {
            // Both halves of the click are eaten, and only now is the hook gone and the
            // caller told. Sending input while this hook is still installed loses it: the
            // hook runs on the thread that installed it, and that thread is about to be
            // busy sending.
            var pending = _pending;
            var where = _pendingAt;
            _swallowUp = false;
            Disarm();
            pending?.Invoke(where.Window, where.At);
            return 1;
        }
        if (msg != WmLeftButtonDown) return CallNextHookEx(_hookHandle, code, message, data);

        var hook = Marshal.PtrToStructure<MouseLowLevelHook>(data);
        var target = _resolve(hook.Point);
        if (target == IntPtr.Zero) return CallNextHookEx(_hookHandle, code, message, data);

        // The release belongs to this click and is eaten too: letting it through reaches
        // the cockpit on its own, and the simulator reads a lone release as a click ending.
        _pending = _fire;
        _pendingAt = (target, new Point(hook.Point.X, hook.Point.Y));
        _swallowUp = true;
        _resolve = null;                      // one click only; further ones pass through
        return 1;
    }

    /// <summary>The window among <paramref name="candidates"/> that contains the point, or
    /// zero. Bounds again, not topmost: the click is being taken away from whatever is
    /// there.</summary>
    public static IntPtr WindowAt(IEnumerable<IntPtr> candidates, NativePoint p)
    {
        foreach (var h in candidates)
            if (Within(h, p)) return h;
        return IntPtr.Zero;
    }

    /// <summary>Sends Right-Alt + left click where the cursor is, into
    /// <paramref name="simWindow"/>, then gives the focus back to <paramref name="restoreTo"/>.
    ///
    /// Measured, because two of the three obvious ways do nothing. The window must be
    /// fronted: alt-clicking a simulator that does not have focus pops nothing out. And the
    /// first input after fronting it is swallowed, so something has to come before the
    /// click - a plain click works and is wrong, since it presses whatever the pilot is
    /// pointing at. A bare move event primes it just as well and presses nothing.</summary>
    public static void RightAltClick(IntPtr simWindow, IntPtr restoreTo, Point? where = null)
    {
        SetForegroundWindow(simWindow);
        Thread.Sleep(600);

        // The click that armed this one is where the panel is; the cursor may have moved
        // since, and fronting the window does not move it back.
        var at = where ?? Cursor();
        SetCursorPos((int)at.X, (int)at.Y);
        Thread.Sleep(100);
        SetCursorPos((int)at.X + 1, (int)at.Y);
        Send(Mouse(MouseMove));
        Thread.Sleep(150);
        SetCursorPos((int)at.X, (int)at.Y);
        Send(Mouse(MouseMove));
        Thread.Sleep(250);

        Send(Key(VkRMenu, down: true));
        Thread.Sleep(200);
        Send(Mouse(MouseLeftDown), Mouse(MouseLeftUp));
        Thread.Sleep(200);
        Send(Key(VkRMenu, down: false));

        Thread.Sleep(150);
        if (restoreTo != IntPtr.Zero) SetForegroundWindow(restoreTo);
    }

    private static void Send(params Input[] inputs) =>
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());

    private static Input Key(int vk, bool down) => new()
    {
        Type = 1,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = (ushort)vk,
                Scan = (ushort)MapVirtualKey((uint)vk, 0),
                Flags = KeyEventExtended | (down ? 0 : KeyEventUp)
            }
        }
    };

    private static Input Mouse(uint flags) => new()
    {
        Type = 0,
        Data = new InputUnion { Mouse = new MouseInput { Flags = flags } }
    };

    /* ---- win32 ---- */

    [StructLayout(LayoutKind.Sequential)] public struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseLowLevelHook
    {
        public NativePoint Point;
        public uint Data, Flags, Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey, Scan;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint p);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect r);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
}
