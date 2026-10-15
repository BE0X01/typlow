using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Typlow;

internal static class Native
{
    internal delegate void WinEventProc(nint hook, uint ev, nint hwnd, int obj, int child, uint thread, uint time);
    internal delegate bool EnumWindowProc(nint window, nint data);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct GuiInfo
    { public int Size; public uint Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rect CaretRect; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { public ushort Vk, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputUnion Data; }
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref int length);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll")] internal static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport("user32.dll")] internal static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] internal static extern int GetKeyboardLayoutList(int count, [Out] nint[] list);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(nint window, EnumWindowProc callback, nint data);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowProc callback, nint data);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] internal static extern nint SendMessageTimeout(nint hwnd, uint msg, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll")] internal static extern bool PostMessage(nint hwnd, uint msg, nuint wParam, nint lParam);
    [DllImport("imm32.dll")] internal static extern nint ImmGetDefaultIMEWnd(nint hwnd);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern nint SetWindowLongPtr(nint window, int index, nint value);

    internal static GuiInfo Gui(nint window)
    {
        uint thread = GetWindowThreadProcessId(window, out _);
        var info = new GuiInfo { Size = Marshal.SizeOf<GuiInfo>() };
        // Hosted Store apps can receive input on a different queue from ApplicationFrameHost.
        bool hosted = System.IO.Path.GetFileNameWithoutExtension(ProcessPath(window)).Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase);
        if (window == GetForegroundWindow() && GetGUIThreadInfo(0, ref info) &&
            info.Focus != 0 && GetAncestor(info.Focus, 2) == window &&
            (!hosted || GetWindowThreadProcessId(info.Focus, out uint focusPid) != 0 && ProcessPath(focusPid) != ProcessPath(window))) return info;
        GetGUIThreadInfo(thread, ref info);
        if (hosted)
        {
            GetWindowThreadProcessId(window, out uint hostPid);
            GuiInfo appInfo = default; nint appWindow = 0;
            EnumWindowProc find = (child, _) =>
            {
                uint childThread = GetWindowThreadProcessId(child, out uint pid);
                if (pid == hostPid || !IsWindowVisible(child)) return true;
                if (appWindow == 0) appWindow = child;
                var candidate = new GuiInfo { Size = Marshal.SizeOf<GuiInfo>() };
                if (GetGUIThreadInfo(childThread, ref candidate) && candidate.Focus != 0 && GetAncestor(candidate.Focus, 2) == window)
                { appInfo = candidate; return false; }
                return true;
            };
            EnumChildWindows(window, find, 0); GC.KeepAlive(find);
            if (appInfo.Focus != 0) return appInfo;
            if (appWindow != 0) { info.Focus = appWindow; info.Caret = 0; }
        }
        return info;
    }
    internal static bool IsSelectableProcess(string name) =>
        System.IO.Path.GetFileNameWithoutExtension(name).ToLowerInvariant() is not
            ("systemsettings" or "textinputhost" or "steamwebhelper" or "applicationframehost");
    internal static string ProcessPath(nint window)
    {
        GetWindowThreadProcessId(window, out uint pid);
        return ProcessPath(pid);
    }
    internal static string ProcessPath(uint pid)
    {
        nint handle = OpenProcess(0x1000, false, pid);
        if (handle == 0) return "";
        try { var path = new StringBuilder(32768); int length = path.Capacity; return QueryFullProcessImageName(handle, 0, path, ref length) ? path.ToString() : ""; }
        finally { CloseHandle(handle); }
    }
    internal static List<(string Path, string Label)> RunningApps()
    {
        var result = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        EnumWindowProc visit = (window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            string path = AppPath(window);
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            if (path.Length == 0 || !IsSelectableProcess(name) || name.Equals("Typlow", StringComparison.OrdinalIgnoreCase) || !seen.Add(path)) return true;
            string label = name;
            try
            {
                label = FileVersionInfo.GetVersionInfo(path).FileDescription ?? name;
            }
            catch { }
            result.Add((path, string.IsNullOrWhiteSpace(label) ? name : label));
            return true;
        };
        EnumWindows(visit, 0); GC.KeepAlive(visit);
        return result;
    }
    internal static string AppPath(nint window)
    {
        string path = ProcessPath(window);
        if (!System.IO.Path.GetFileNameWithoutExtension(path).Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) return path;
        GetWindowThreadProcessId(window, out uint hostPid);
        nint focus = Gui(window).Focus;
        if (focus != 0 && GetAncestor(focus, 2) == window)
        {
            GetWindowThreadProcessId(focus, out uint focusPid);
            if (focusPid != hostPid) { string focusedPath = ProcessPath(focusPid); if (focusedPath.Length > 0) return focusedPath; }
        }
        string childPath = "";
        EnumWindowProc findApp = (child, _) =>
        {
            GetWindowThreadProcessId(child, out uint pid);
            if (pid != hostPid && IsWindowVisible(child)) childPath = ProcessPath(pid);
            return childPath.Length == 0;
        };
        EnumChildWindows(window, findApp, 0);
        GC.KeepAlive(findApp);
        return childPath.Length > 0 ? childPath : path;
    }
    internal static List<ProcessEntry> RunningProcesses()
    {
        var entries = new List<ProcessEntry>();
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId) continue;
                    string name = process.ProcessName;
                    if (!IsSelectableProcess(name)) continue;
                    string path = ProcessPath((uint)process.Id), label = name;
                    if (path.Length > 0)
                    {
                        if (!labels.TryGetValue(path, out label!))
                        {
                            try { label = FileVersionInfo.GetVersionInfo(path).FileDescription ?? name; } catch { label = name; }
                            if (string.IsNullOrWhiteSpace(label)) label = name;
                            labels[path] = label;
                        }
                    }
                    entries.Add(new(process.Id, name, label, path, process.MainWindowHandle != 0));
                }
                catch { }
            }
        }
        return entries;
    }
}

internal sealed class ImeController
{
    private const uint ImeControl = 0x0283;
    private bool Command(nint window, nuint operation, nint value, out nuint result)
    {
        nint focus = Native.Gui(window).Focus;
        nint ime = Native.ImmGetDefaultIMEWnd(focus != 0 ? focus : window);
        result = 0;
        return ime != 0 && Native.SendMessageTimeout(ime, ImeControl, operation, value, 0x0002, 80, out result) != 0;
    }
    public InputState Read(nint window)
    {
        nint focus = Native.Gui(window).Focus;
        uint thread = Native.GetWindowThreadProcessId(focus != 0 ? focus : window, out _);
        nint layout = Native.GetKeyboardLayout(thread);
        int language = (int)((long)layout & 0xFFFF);
        if ((language & 0x3FF) == 9) return new(layout, 0, InputMode.English);
        if (language != 0x0412) return new(layout, 0, InputMode.Unknown);
        if (!Command(window, 1, 0, out var conversion) || conversion == nuint.MaxValue || conversion == uint.MaxValue)
            return new(layout, 0, InputMode.Unknown);
        bool? open = Command(window, 5, 0, out var openStatus) ? openStatus != 0 : null;
        return new(layout, (int)conversion, InterpretMode((int)conversion, open));
    }
    internal static InputMode InterpretMode(int conversion, bool? open) =>
        open == false || (conversion & 1) == 0 ? InputMode.English : InputMode.Korean;
    internal string Diagnostic(nint window)
    {
        bool convOk = Command(window, 1, 0, out var conversion), openOk = Command(window, 5, 0, out var open);
        return $"Layout={Read(window).Layout:X}; Conversion={conversion:X} ({convOk}); Open={open} ({openOk})";
    }
    private nint FindLayout(int language)
    {
        var layouts = new nint[Native.GetKeyboardLayoutList(0, Array.Empty<nint>())];
        Native.GetKeyboardLayoutList(layouts.Length, layouts);
        foreach (var layout in layouts) if (((long)layout & 0xFFFF) == language) return layout;
        return 0;
    }
    // A bounded operation triggered by focus entry. No periodic enforcement or blind toggle.
    public (bool Success, string Detail) Apply(nint window, SwitchRequest request, Func<bool> stillCurrent)
    {
        if (!stillCurrent()) return (false, "포커스가 바뀌어 전환을 취소했습니다.");
        var current = Read(window);
        nint wantedLayout = request.Restore?.Layout ?? current.Layout;
        if (request.Restore == null && request.Mode == InputMode.Korean && ((long)current.Layout & 0xFFFF) != 0x0412)
            wantedLayout = FindLayout(0x0412);
        if (request.Mode == InputMode.Korean && wantedLayout == 0) return (false, "Windows에 한국어 입력기가 설치되어 있지 않습니다.");
        // Unknown IME states are never toggled. Use a known English layout if one is installed.
        if (request.Mode == InputMode.English && current.Mode == InputMode.Unknown && request.Restore == null)
        {
            wantedLayout = FindLayout(0x0409);
            if (wantedLayout == 0) return (false, "입력 상태를 확인할 수 없습니다. 이 앱의 입력기 지원을 확인해 주세요.");
        }
        if (wantedLayout != 0 && wantedLayout != current.Layout)
        {
            nint focus = Native.Gui(window).Focus;
            if (!stillCurrent() || !Native.PostMessage(focus != 0 ? focus : window, 0x0050, 0, wantedLayout))
                return (false, "입력기 변경 요청을 전달하지 못했습니다.");
            Thread.Sleep(55);
            if (!stillCurrent()) return (false, "포커스가 바뀌어 전환을 취소했습니다.");
            current = Read(window);
            if (current.Layout != wantedLayout) return (false, "앱이 입력기 변경을 적용하지 않았습니다.");
        }
        if (current.Mode == request.Mode) return (true, "이미 설정한 입력 상태입니다.");
        if (((long)current.Layout & 0xFFFF) != 0x0412 || current.Mode == InputMode.Unknown)
            return (false, "이 입력기의 한·영 상태를 확인할 수 없습니다.");
        int conversion = request.Restore?.Conversion ?? current.Conversion;
        conversion = request.Mode == InputMode.Korean ? conversion | 1 : conversion & ~1;
        if (!stillCurrent()) return (false, "포커스가 바뀌어 전환을 취소했습니다.");
        Command(window, 2, (nint)conversion, out _);
        // Chrome/TSF may keep the IME open even after a conversion-mode request. Set the desired open state too.
        if (stillCurrent()) Command(window, 6, request.Mode == InputMode.Korean ? 1 : 0, out _);
        Thread.Sleep(35);
        if (!stillCurrent()) return (false, "포커스가 바뀌어 전환을 취소했습니다.");
        var after = Read(window);
        if (after.Mode == request.Mode) return (true, "전환을 확인했습니다.");
        // Some TSF-based Korean IMEs ignore legacy set-mode messages. One verified Hangul key is the fallback.
        // Ctrl+L may still be held when UIA reports focus. Wait briefly for release within this one operation.
        for (int waited = 0; after.Mode != InputMode.Unknown && ModifiersDown() && stillCurrent() && waited < 180; waited += 15)
            Thread.Sleep(15);
        if (!stillCurrent()) return (false, "포커스가 바뀌어 전환을 취소했습니다.");
        after = Read(window);
        if (after.Mode == request.Mode) return (true, "전환을 확인했습니다.");
        if (after.Mode != InputMode.Unknown && !ModifiersDown() && stillCurrent())
        {
            var input = new Native.Input { Type = 1, Data = new Native.InputUnion { Keyboard = new Native.KeyboardInput { Vk = 0x15 } } };
            var up = input; up.Data.Keyboard.Flags = 2;
            Native.SendInput(2, new[] { input, up }, Marshal.SizeOf<Native.Input>());
            Thread.Sleep(40);
            if (stillCurrent() && Read(window).Mode == request.Mode) return (true, "한·영 키로 전환을 확인했습니다.");
        }
        return (false, "전환을 확인하지 못했습니다. 관리자 권한 또는 입력기 호환성을 확인해 주세요.");
    }
    private static bool ModifiersDown()
    { foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }) if (Native.GetAsyncKeyState(key) < 0) return true; return false; }
}
