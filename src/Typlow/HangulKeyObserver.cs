using System;
using System.Runtime.InteropServices;

namespace Typlow;

// Observe only the Hangul key; never swallow keys or retain typed characters.
internal sealed class HangulKeyObserver : IDisposable
{
    private delegate nint HookProc(int code, nuint message, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
    private readonly HookProc callback;
    private nint hook;
    private bool held;
    internal HangulKeyObserver(Action<nint> pressed)
    {
        callback = (code, message, data) =>
        {
            if (code >= 0 && Marshal.ReadInt32(data) == 0x15 && (Marshal.ReadInt32(data, 8) & 0x10) == 0)
            {
                if (message is 0x100 or 0x104)
                {
                    if (!held) { held = true; pressed(Native.GetForegroundWindow()); }
                }
                else if (message is 0x101 or 0x105) held = false;
            }
            return CallNextHookEx(hook, code, message, data);
        };
    }
    internal void Start() => hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
    public void Dispose() { if (hook != 0) { UnhookWindowsHookEx(hook); hook = 0; } }
}

internal sealed class ManualIndicatorTrigger
{
    private nint window;
    private InputMode before;
    private DateTime until;
    internal void Arm(nint target, InputMode mode, DateTime now) { window = target; before = mode; until = now.AddSeconds(1); }
    internal void Clear() => window = 0;
    internal bool Ready(nint target, InputMode mode, DateTime now)
    {
        if (window != target || now > until) { Clear(); return false; }
        return window != 0 && before != InputMode.Unknown && mode != InputMode.Unknown && before != mode;
    }
}
