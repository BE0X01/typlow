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
    private readonly AutomationFocusChangedEventHandler focusCallback;
    private nint hook, inputFocusHook;
    private volatile bool stopping;
    private volatile Settings settings;
    private long epoch;
    private int focusQueued;
    private nint window;
    private InputMode lastMode;
    private string focusKey = "";
    private bool address, isInput;
    private AutomationElement? focusedElement;
    private DateTime nextFocusCheck;
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
                manualIndicator.Arm(target, lastMode, DateTime.UtcNow);
        }));
        foregroundCallback = (_, _, _, _, _, _, _) => QueueFocus();
        focusCallback = (_, _) => QueueFocus();
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
        // Duplicate accessibility focus notifications aren't a new focus entry and must not cancel a switch.
        if (Interlocked.Exchange(ref focusQueued, 1) != 0) return;
        Enqueue(() => { Interlocked.Exchange(ref focusQueued, 0); Observe(true); });
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
            if (!wasEnabled && copy.Enabled) policy.Reset();
            Try(() => Observe(true));
        });
    }
    private void Run()
    {
        try { Automation.AddAutomationFocusChangedEventHandler(focusCallback); }
        catch (Exception e) { Log?.Invoke("입력란 이벤트 연결 실패: " + e.GetType().Name); }
        try
        {
            Try(() => Observe(true));
            while (!stopping)
            {
                while (queue.TryDequeue(out var action)) Try(action);
                if (stopping) break;
                // Read-only IME sampling also shows manual Hangul/English changes. Writes require a policy transition.
                Try(() => Observe(DateTime.UtcNow >= nextFocusCheck));
                wake.WaitOne(250);
            }
        }
        finally
        {
            try { Automation.RemoveAutomationFocusChangedEventHandler(focusCallback); } catch { }
        }
    }
    private void Try(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is not OutOfMemoryException) { Log?.Invoke("관찰 중 오류: " + e.GetType().Name); }
    }
    private void Observe(bool refreshFocus)
    {
        nint current = Native.GetForegroundWindow();
        if (current == 0) return;
        bool changedWindow = current != window;
        if (changedWindow)
        {
            Interlocked.Increment(ref epoch);
            HideIndicator?.Invoke();
            window = current; lastMode = InputMode.Unknown; focusKey = ""; focusedElement = null; address = false; isInput = false;
            manualIndicator.Clear();
            foreach (nint old in paths.Keys.Where(w => !Native.IsWindow(w)).ToArray()) { paths.Remove(old); policy.Forget(old); }
        }
        if (!paths.TryGetValue(window, out string? path) || path.Length == 0 || changedWindow) paths[window] = path = Native.AppPath(window);
        indicatorVisit.Enter(current, path);
        bool changedInput = false;
        if (refreshFocus || changedWindow)
        {
            nextFocusCheck = DateTime.UtcNow.AddMilliseconds(BrowserDetection.IsBrowser(path) ? 250 : 1000);
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
                    if (changedInput) { Interlocked.Increment(ref epoch); HideIndicator?.Invoke(); }
                    focusKey = key; focusedElement = element;
                    address = BrowserDetection.IsBrowser(path) && BrowserDetection.IsAddress(element);
                    isInput = data.ControlType == ControlType.Edit ||
                        (data.ControlType == ControlType.ComboBox && element.TryGetCurrentPattern(ValuePattern.Pattern, out _));
                    if (data.ControlType == ControlType.Document && element.TryGetCurrentPattern(TextPattern.Pattern, out object text))
                        isInput = ((TextPattern)text).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is false;
                }
                else if (changedWindow)
                {
                    // UIA focus can lag the foreground event. Defer decisions rather than misclassifying an address bar.
                    nextFocusCheck = DateTime.UtcNow; return;
                }
            }
            catch (ElementNotAvailableException) { nextFocusCheck = DateTime.UtcNow; return; }
            catch (System.Runtime.InteropServices.COMException) { focusedElement = null; }
        }
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
        if ((indicatorVisit.Pending || manual) && FocusPolicy.ShouldShowIndicator(settings, path, isInput, true, state.Mode))
            anchor = CaretLocator.Find(window, focusedElement);
        bool visit = indicatorVisit.TryTake(settings, path, isInput, state.Mode, !anchor.IsEmpty);
        if (visit || (manual && !anchor.IsEmpty))
        {
            manualIndicator.Clear();
            Indicator?.Invoke(new(state.Mode, window, path, anchor, settings.IndicatorMilliseconds));
        }
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
