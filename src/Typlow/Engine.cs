using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Typlow;

public record IndicatorNotice(InputMode Mode, nint Window, string Path, Rect Anchor, int Duration);
public record EngineStatus(string App, string Mode, bool Address);

internal static class BrowserDetection
{
    private static readonly HashSet<string> browsers = new(StringComparer.OrdinalIgnoreCase)
        { "chrome", "msedge" };
    internal static bool IsBrowser(string path) => browsers.Contains(Path.GetFileNameWithoutExtension(path));
    internal static bool MatchesAddress(string name, string id)
    {
        if (id.Equals("addressEditBox", StringComparison.OrdinalIgnoreCase) || id.Equals("omnibox", StringComparison.OrdinalIgnoreCase)) return true;
        string normalized = name.Trim().TrimEnd(':', '：').ToLowerInvariant();
        if (normalized.Contains("주소") && (normalized.Contains("검색") || normalized.Contains("창"))) return true;
        return normalized is "주소창" or "주소 및 검색창" or "주소 및 검색 표시줄" or "주소 및 검색" or
            "address and search bar" or "search or enter web address" or "search or enter address" or "address bar";
    }
    internal static bool IsAddress(AutomationElement element)
    {
        // A webpage can label an input "Address and search bar" too. Its Document ancestor excludes it.
        bool candidate = false;
        AutomationElement? current = element;
        for (int depth = 0; current != null && depth < 18; depth++)
        {
            var data = current.Current;
            if (data.ControlType == ControlType.Document) return false;
            if (data.IsKeyboardFocusable && MatchesAddress(data.Name, data.AutomationId)) candidate = true;
            if (data.ControlType == ControlType.Window) break;
            current = TreeWalker.ControlViewWalker.GetParent(current);
        }
        return candidate;
    }
}

internal sealed class FocusEngine : IDisposable
{
    private readonly ConcurrentQueue<Action> queue = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly Thread worker;
    private readonly FocusPolicy policy = new();
    private readonly IndicatorVisit indicatorVisit = new();
    private readonly ImeController ime = new();
    private readonly ManualIndicatorTrigger manualIndicator = new();
    private readonly HangulKeyObserver hangulKey;
    private readonly Dictionary<nint, string> paths = new();
    private readonly Native.WinEventProc foregroundCallback;
    private nint hook, inputFocusHook;
    private volatile bool stopping;
    private volatile Settings settings;
    private long epoch;
    private readonly FocusRefreshGate focusRefresh = new();
    private int caretAttempts;
    private long nextCaretCheck;
    private nint window;
    private InputMode lastMode;
    private string focusKey = "";
    private bool address, isInput, focusValid;
    private AutomationElement? focusedElement;
    private EngineStatus? lastStatus;
    public event Action<string>? Log;
    public event Action<IndicatorNotice>? Indicator;
    public event Action? HideIndicator;
    public event Action<EngineStatus>? Status;

    public FocusEngine(Settings settings)
    {
        this.settings = settings.Copy();
        hangulKey = new(target => Enqueue(() => {
            if (target == window && target == Native.GetForegroundWindow() && this.settings.Enabled && this.settings.ShowIndicator)
            {
                caretAttempts = 0;
                manualIndicator.Arm(target, lastMode, DateTime.UtcNow);
            }
        }));
        foregroundCallback = (_, ev, target, _, _, _, _) =>
        {
            nint foreground = Native.GetForegroundWindow();
            if (ev == 3 || target != 0 && Native.GetAncestor(target, 2) == foreground) QueueFocus();
        };
        worker = new Thread(Run) { IsBackground = true, Name = "Typlow focus observer" };
        worker.SetApartmentState(ApartmentState.MTA);
    }
    public void Start()
    {
        // The hook is registered on WPF's message-loop thread and never injects into other processes.
        hook = Native.SetWinEventHook(3, 3, 0, foregroundCallback, 0, 0, 0);
        inputFocusHook = Native.SetWinEventHook(0x8005, 0x8005, 0, foregroundCallback, 0, 0, 0);
        if (hook == 0) Log?.Invoke("창 포커스 이벤트를 연결하지 못했습니다. 보조 감지로 동작합니다.");
        worker.Start();
        hangulKey.Start();
    }
    private void QueueFocus()
    {
        // Coalesce notifications without queuing an expensive observation for each event.
        if (!stopping && focusRefresh.Request()) wake.Set();
    }
    private void Enqueue(Action action) { if (!stopping) { queue.Enqueue(action); wake.Set(); } }
    public void Update(Settings value)
    {
        // Immediately cancel any in-flight switch; configuration edits aren't app focus entries.
        Interlocked.Increment(ref epoch);
        var copy = value.Copy();
        Enqueue(() =>
        {
            bool wasEnabled = settings.Enabled;
            if (wasEnabled && !copy.Enabled)
            {
                var restore = policy.TakeActiveRestore();
                if (restore != null && window == Native.GetForegroundWindow())
                    ime.Apply(window, new(restore.Mode, restore, "일시정지 · 이전 상태 복원"), () => Native.GetForegroundWindow() == window);
                policy.Reset();
            }
            settings = copy;
            manualIndicator.Clear();
            focusKey = ""; focusedElement = null;
            focusValid = false;
            focusRefresh.Reset();
            if (!wasEnabled && copy.Enabled) policy.Reset();
            Try(Observe);
        });
    }
    private void Run()
    {
        // WinEvent focus notifications plus a slow fallback avoid a desktop-wide UIA subscription.
        Try(Observe);
        while (!stopping)
        {
            while (queue.TryDequeue(out var action)) Try(action);
            if (stopping) break;
            Try(Observe);
            wake.WaitOne(250);
        }
    }
    private void Try(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is not OutOfMemoryException) { Log?.Invoke("관찰 중 오류: " + e.GetType().Name); }
    }
    private void Observe()
    {
        // Pausing must stop accessibility and IME reads as well as input switching.
        if (!settings.Enabled) return;
        nint current = Native.GetForegroundWindow();
        if (current == 0) return;
        bool changedWindow = current != window;
        if (changedWindow)
        {
            Interlocked.Increment(ref epoch);
            HideIndicator?.Invoke();
            window = current; lastMode = InputMode.Unknown; focusKey = ""; focusedElement = null; address = false; isInput = false; focusValid = false;
            manualIndicator.Clear();
            foreach (nint old in paths.Keys.Where(w => !Native.IsWindow(w)).ToArray()) { paths.Remove(old); policy.Forget(old); }
        }
        if (!paths.TryGetValue(window, out string? path) || path.Length == 0 || changedWindow) paths[window] = path = Native.AppPath(window);
        indicatorVisit.Enter(current, path);
        bool inspectAddress = settings.AddressEnglish && BrowserDetection.IsBrowser(path);
        bool inspectInput = settings.ShowIndicator && settings.Rules.Exists(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        bool changedInput = false;
        if (!inspectAddress && !inspectInput)
        {
            focusedElement = null; focusKey = ""; address = false; isInput = false; focusValid = true;
        }
        else if (focusRefresh.TryTake(Environment.TickCount64, changedWindow))
        {
            try
            {
                var element = AutomationElement.FocusedElement;
                Native.GetWindowThreadProcessId(window, out uint pid);
                if (element != null && (element.Current.ProcessId == pid ||
                    string.Equals(Native.ProcessPath((uint)element.Current.ProcessId), path, StringComparison.OrdinalIgnoreCase) || BelongsToWindow(element, window)))
                {
                    var data = element.Current;
                    string key = string.Join(".", element.GetRuntimeId());
                    changedInput = focusKey != key;
                    if (changedInput) { Interlocked.Increment(ref epoch); HideIndicator?.Invoke(); caretAttempts = 0; nextCaretCheck = 0; }
                    focusKey = key; focusedElement = element;
                    if (changedInput)
                    {
                        address = inspectAddress && BrowserDetection.IsAddress(element);
                        isInput = inspectInput && IsEditable(element, data.ControlType);
                    }
                    focusValid = true;
                }
                else
                {
                    // UIA focus can lag the foreground event. Defer decisions rather than misclassifying an address bar.
                    focusedElement = null; focusKey = ""; isInput = false; focusValid = false;
                    focusRefresh.Request(); return;
                }
            }
            catch (ElementNotAvailableException) { focusedElement = null; focusKey = ""; isInput = false; focusValid = false; focusRefresh.Request(); return; }
            catch (System.Runtime.InteropServices.COMException) { focusedElement = null; focusKey = ""; isInput = false; focusValid = false; focusRefresh.Request(); return; }
            finally { focusRefresh.Complete(Environment.TickCount64); }
        }
        if (!focusValid) return;
        InputState state = ime.Read(window);
        var request = policy.Observe(new(window, path, address, state), settings);
        if (request != null)
        {
            manualIndicator.Clear();
            long operationEpoch = Interlocked.Read(ref epoch);
            nint target = window;
            string elementKey = focusKey;
            AutomationElement? targetElement = focusedElement;
            bool StillCurrent() => !stopping && settings.Enabled && target == Native.GetForegroundWindow() &&
                operationEpoch == Interlocked.Read(ref epoch) && elementKey == focusKey && HasFocus(targetElement);
            var result = ime.Apply(target, request, StillCurrent);
            Log?.Invoke(request.Reason + " — " + result.Detail);
            if (target != Native.GetForegroundWindow()) return;
            state = ime.Read(target);
            if (result.Success) policy.Confirm(target, state, address);
        }
        lastMode = state.Mode;
        var status = new EngineStatus(string.IsNullOrEmpty(path) ? "상태 확인 중" : Path.GetFileNameWithoutExtension(path),
            state.Mode switch { InputMode.Korean => "한글", InputMode.English => "영어", _ => "확인 불가" }, address);
        if (status != lastStatus) { lastStatus = status; Status?.Invoke(status); }
        Rect anchor = Rect.Empty;
        bool manual = manualIndicator.Ready(window, state.Mode, DateTime.UtcNow);
        if ((indicatorVisit.Pending || manual) && caretAttempts < 3 && Environment.TickCount64 >= nextCaretCheck &&
            FocusPolicy.ShouldShowIndicator(settings, path, isInput, true, state.Mode))
        {
            caretAttempts++;
            anchor = CaretLocator.Find(window, focusedElement);
            nextCaretCheck = Environment.TickCount64 + 500;
        }
        bool visit = indicatorVisit.TryTake(settings, path, isInput, state.Mode, !anchor.IsEmpty);
        if (visit || (manual && !anchor.IsEmpty))
        {
            manualIndicator.Clear();
            Indicator?.Invoke(new(state.Mode, window, path, anchor, settings.IndicatorMilliseconds));
        }
    }
    private static bool IsEditable(AutomationElement element, ControlType type)
    {
        if (type == ControlType.Edit) return true;
        if (type == ControlType.ComboBox) return element.TryGetCurrentPattern(ValuePattern.Pattern, out _);
        if (type != ControlType.Document || !element.TryGetCurrentPattern(TextPattern.Pattern, out object text)) return false;
        // Web editors may expose Document. Collapse a copy to one endpoint, never inspect DocumentRange.
        var selection = ((TextPattern)text).GetSelection();
        if (selection.Length != 1) return false;
        var caret = selection[0].Clone();
        caret.MoveEndpointByRange(TextPatternRangeEndpoint.End, caret, TextPatternRangeEndpoint.Start);
        return caret.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is false;
    }
    private static bool HasFocus(AutomationElement? element)
    {
        try { return element == null || element.Current.HasKeyboardFocus; }
        catch { return false; }
    }
    private static bool BelongsToWindow(AutomationElement element, nint target)
    {
        AutomationElement? current = element;
        for (int i = 0; current != null && i < 24; i++)
        {
            nint handle = (nint)current.Current.NativeWindowHandle;
            if (handle != 0) return Native.GetAncestor(handle, 2) == target;
            current = TreeWalker.ControlViewWalker.GetParent(current);
        }
        return false;
    }
    public void Dispose()
    {
        if (stopping) return;
        hangulKey.Dispose();
        if ((worker.ThreadState & System.Threading.ThreadState.Unstarted) != 0)
        {
            stopping = true;
            if (hook != 0) Native.UnhookWinEvent(hook);
            if (inputFocusHook != 0) Native.UnhookWinEvent(inputFocusHook);
            return;
        }
        if (window == Native.GetForegroundWindow())
        {
            // The background thread owns all policy state; queue the final restoration before stopping.
            var restored = new ManualResetEventSlim();
            Enqueue(() =>
            {
                try { var state = policy.TakeActiveRestore(); if (state != null && window == Native.GetForegroundWindow())
                    ime.Apply(window, new(state.Mode, state, "종료 · 이전 상태 복원"), () => window == Native.GetForegroundWindow()); }
                finally { restored.Set(); }
            });
            restored.Wait(600);
        }
        stopping = true; wake.Set();
        if (hook != 0) Native.UnhookWinEvent(hook);
        if (inputFocusHook != 0) Native.UnhookWinEvent(inputFocusHook);
        worker.Join(800);
        // A provider may still be blocked in UIA; don't dispose its synchronization object underneath it.
    }
}

// All timing state belongs to the observer thread; only Request is called by event callbacks.
internal sealed class FocusRefreshGate
{
    private int pending = 1;
    private long nextAllowed, nextFallback;
    internal bool Request() => Interlocked.Exchange(ref pending, 1) == 0;
    internal void Reset() { nextAllowed = nextFallback = 0; Request(); }
    internal bool TryTake(long now, bool changedWindow)
    {
        if (!changedWindow && (now < nextAllowed || Volatile.Read(ref pending) == 0 && now < nextFallback)) return false;
        Interlocked.Exchange(ref pending, 0);
        return true;
    }
    internal void Complete(long now) { nextAllowed = now + 150; nextFallback = now + 1000; }
}
