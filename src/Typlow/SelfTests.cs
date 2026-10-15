using System;
using System.Collections.Generic;
using System.IO;

namespace Typlow;

internal static class SelfTests
{
    public static void Run(string report)
    {
        var lines = new List<string>();
        int failures = 0;
        void Check(string name, Action test)
        { try { test(); lines.Add("PASS  " + name); } catch (Exception e) { failures++; lines.Add("FAIL  " + name + ": " + e.Message); } }
        void Assert(bool condition, string message = "Unexpected policy output") { if (!condition) throw new Exception(message); }
        var ko = new InputState((nint)0x0412, 1, InputMode.Korean);
        var en = new InputState((nint)0x0412, 0, InputMode.English);
        var us = new InputState((nint)0x0409, 0, InputMode.English);
        var unknown = new InputState(0, 0, InputMode.Unknown);
        var settings = new Settings { Rules = new() { new("editor.exe", "Editor", InputMode.English), new("other.exe", "Other", InputMode.Korean) } };
        Observation Obs(nint window, string path, bool address = false, InputState? state = null) => new(window, path, address, state ?? ko);

        Check("Focus events coalesce and obey a minimum interval", () =>
        {
            var gate = new FocusRefreshGate();
            Assert(gate.TryTake(0, false)); gate.Complete(20);
            Assert(gate.Request());
            for (int i = 0; i < 10000; i++) Assert(!gate.Request());
            Assert(!gate.TryTake(169, false));
            Assert(gate.TryTake(170, false)); gate.Complete(190);
            Assert(!gate.TryTake(340, false));
            Assert(!gate.TryTake(1189, false));
            Assert(gate.TryTake(1190, false));
        });
        Check("Focus fallback is measured after slow provider completes", () =>
        {
            var gate = new FocusRefreshGate();
            Assert(gate.TryTake(0, false)); gate.Complete(5000);
            Assert(!gate.TryTake(5001, false));
            Assert(!gate.TryTake(5999, false));
            Assert(gate.TryTake(6000, false));
        });
        Check("Events during inspection are retained without immediate reentry", () =>
        {
            var gate = new FocusRefreshGate();
            Assert(gate.TryTake(0, false));
            Assert(gate.Request()); gate.Complete(100);
            Assert(!gate.TryTake(249, false));
            Assert(gate.TryTake(250, false)); gate.Complete(260);
            Assert(gate.TryTake(261, true), "New foreground window must be observed promptly");
            gate.Complete(270); gate.Reset();
            Assert(gate.TryTake(271, false), "Settings change must invalidate focus classification");
        });

        Check("App rule applies exactly once on window entry", () =>
        {
            var policy = new FocusPolicy(); Assert(policy.Observe(Obs(1, "editor.exe"), settings)?.Mode == InputMode.English);
            for (int i = 0; i < 10000; i++) Assert(policy.Observe(Obs(1, "editor.exe", state: ko), settings) == null, "Repeated observation forced English");
        });
        Check("Manual Korean remains allowed until leaving and returning", () =>
        {
            var policy = new FocusPolicy(); policy.Observe(Obs(1, "editor.exe"), settings);
            Assert(policy.Observe(Obs(1, "editor.exe", state: ko), settings) == null);
            policy.Observe(Obs(2, "unconfigured.exe"), settings);
            Assert(policy.Observe(Obs(1, "editor.exe", state: ko), settings)?.Mode == InputMode.English);
        });
        Check("Different windows of the same app are separate focus entries", () =>
        {
            var policy = new FocusPolicy(); policy.Observe(Obs(1, "editor.exe"), settings);
            Assert(policy.Observe(Obs(2, "editor.exe"), settings)?.Mode == InputMode.English);
        });
        Check("Input field changes inside the same window do not reapply app rule", () =>
        {
            var policy = new FocusPolicy(); policy.Observe(Obs(1, "editor.exe"), settings);
            Assert(policy.Observe(Obs(1, "editor.exe", state: ko), settings) == null);
        });
        Check("Korean -> address English -> original Korean", () =>
        {
            var policy = new FocusPolicy(); policy.Observe(Obs(1, "chrome.exe"), settings);
            Assert(policy.Observe(Obs(1, "chrome.exe", true), settings)?.Mode == InputMode.English);
            Assert(policy.Observe(Obs(1, "chrome.exe", true, en), settings) == null);
            Assert(policy.Observe(Obs(1, "chrome.exe", false, en), settings)?.Restore == ko);
        });
        Check("English -> address English -> original English", () =>
        {
            var policy = new FocusPolicy(); policy.Observe(Obs(1, "chrome.exe", state: us), settings);
            policy.Observe(Obs(1, "chrome.exe", true, us), settings);
            Assert(policy.Observe(Obs(1, "chrome.exe", false, en), settings)?.Restore == us);
        });
        Check("Browser's own address-mode change doesn't overwrite content snapshot", () =>
        {
            var policy = new FocusPolicy(); policy.Observe(Obs(1, "chrome.exe", state: ko), settings);
            policy.Observe(Obs(1, "chrome.exe", true, en), settings);
            Assert(policy.Observe(Obs(1, "chrome.exe", false, en), settings)?.Restore == ko);
        });
        Check("Manual language change in address bar is not continuously overridden", () =>
        {
            var policy = new FocusPolicy(); policy.Observe(Obs(1, "chrome.exe"), settings); policy.Observe(Obs(1, "chrome.exe", true), settings);
            for (int i = 0; i < 1000; i++) Assert(policy.Observe(Obs(1, "chrome.exe", true, ko), settings) == null);
        });
        Check("Leaving browser applies other app rule, never browser restoration", () =>
        {
            var policy = new FocusPolicy(); policy.Observe(Obs(1, "chrome.exe", state: en), settings); policy.Observe(Obs(1, "chrome.exe", true, en), settings);
            var request = policy.Observe(Obs(2, "other.exe", false, en), settings);
            Assert(request?.Mode == InputMode.Korean && request.Restore == null);
        });
        Check("Returning to address bar retains its original snapshot", () =>
        {
            var policy = new FocusPolicy(); policy.Observe(Obs(1, "chrome.exe"), settings); policy.Observe(Obs(1, "chrome.exe", true), settings);
            policy.Observe(Obs(2, "other.exe", false, en), settings);
            Assert(policy.Observe(Obs(1, "chrome.exe", true, en), settings)?.Mode == InputMode.English);
            Assert(policy.Observe(Obs(1, "chrome.exe", false, en), settings)?.Restore == ko);
        });
        Check("Returning directly to page prioritizes restoration over browser app rule", () =>
        {
            var local = settings.Copy(); local.Rules.Add(new("chrome.exe", "Browser", InputMode.English));
            var policy = new FocusPolicy(); policy.Observe(Obs(1, "chrome.exe"), local); policy.Confirm(1, ko, false);
            policy.Observe(Obs(1, "chrome.exe", true), local); policy.Observe(Obs(2, "other.exe"), local);
            Assert(policy.Observe(Obs(1, "chrome.exe", false, en), local)?.Restore == ko);
        });
        Check("Multiple browser windows keep independent snapshots", () =>
        {
            var policy = new FocusPolicy(); policy.Observe(Obs(1, "chrome.exe", state: ko), settings); policy.Observe(Obs(1, "chrome.exe", true), settings);
            policy.Observe(Obs(2, "chrome.exe", false, en), settings); policy.Observe(Obs(2, "chrome.exe", true, en), settings);
            Assert(policy.Observe(Obs(1, "chrome.exe", false, en), settings)?.Restore == ko);
            Assert(policy.Observe(Obs(2, "chrome.exe", false, en), settings)?.Restore == en);
        });
        Check("Unknown previous mode is not guessed during restoration", () =>
        {
            var policy = new FocusPolicy(); policy.Observe(Obs(1, "chrome.exe", true, unknown), settings);
            Assert(policy.Observe(Obs(1, "chrome.exe", false, en), settings) == null);
        });
        Check("Address option disabled means no address switching", () =>
        {
            var local = settings.Copy(); local.AddressEnglish = false;
            var policy = new FocusPolicy(); Assert(policy.Observe(Obs(1, "chrome.exe", true), local) == null);
        });
        Check("Disabling address option restores a live address session once", () =>
        {
            var local = settings.Copy(); var policy = new FocusPolicy(); policy.Observe(Obs(1, "chrome.exe"), local); policy.Observe(Obs(1, "chrome.exe", true), local);
            local.AddressEnglish = false;
            Assert(policy.Observe(Obs(1, "chrome.exe", true, en), local)?.Restore == ko);
            Assert(policy.Observe(Obs(1, "chrome.exe", true, ko), local) == null);
        });
        Check("Paused program emits no switching commands", () =>
        {
            var local = settings.Copy(); local.Enabled = false; var policy = new FocusPolicy();
            Assert(policy.Observe(Obs(1, "editor.exe"), local) == null); Assert(policy.Observe(Obs(2, "chrome.exe", true), local) == null);
        });
        Check("Changing app configuration doesn't create a focus entry", () =>
        {
            var local = settings.Copy(); var policy = new FocusPolicy(); policy.Observe(Obs(1, "editor.exe"), local);
            local.Rules[0] = local.Rules[0] with { Mode = InputMode.Korean };
            Assert(policy.Observe(Obs(1, "editor.exe", state: en), local) == null);
        });
        Check("Rules match full paths case-insensitively", () =>
        {
            var policy = new FocusPolicy(); Assert(policy.Observe(Obs(1, "EDITOR.EXE"), settings)?.Mode == InputMode.English);
            Assert(policy.Observe(Obs(2, @"C:\Other\editor.exe"), settings) == null);
        });
        Check("Closed window cleanup removes old address snapshot", () =>
        {
            var policy = new FocusPolicy(); policy.Observe(Obs(1, "chrome.exe"), settings); policy.Observe(Obs(1, "chrome.exe", true), settings);
            policy.Forget(1); policy.Observe(Obs(2, "other.exe"), settings);
            Assert(policy.Observe(Obs(1, "chrome.exe", false, en), settings) == null);
        });
        Check("Address recognition accepts Korean/English labels and avoids generic search", () =>
        {
            Assert(BrowserDetection.MatchesAddress("주소 및 검색창", "")); Assert(BrowserDetection.MatchesAddress("Address and search bar", ""));
            Assert(BrowserDetection.MatchesAddress("", "addressEditBox")); Assert(!BrowserDetection.MatchesAddress("검색", ""));
            Assert(!BrowserDetection.MatchesAddress("Search", "")); Assert(!BrowserDetection.IsBrowser("firefox.exe"));
        });
        Check("Settings save and reload preserves rules and options", () =>
        {
            string dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "settings-test");
            var store = new SettingsStore(dir); var local = settings.Copy(); local.ShowIndicator = false; local.IndicatorMilliseconds = 1000;
            store.Save(local); var loaded = store.Load(); Assert(loaded.Rules.Count == 2 && !loaded.ShowIndicator && loaded.IndicatorMilliseconds == 1000);
        });
        Check("Malformed settings falls back and reports a warning", () =>
        {
            string dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "malformed-settings-test"); Directory.CreateDirectory(dir);
            var store = new SettingsStore(dir); File.WriteAllText(store.FilePath, "{broken"); Assert(store.Load().Enabled && store.LoadWarning != null);
        });
        Check("Indicator icon renders at all target resolutions", () =>
        { foreach (int size in new[] { 16, 32, 64, 256 }) Assert(Brand.Bitmap(size).PixelWidth == size); });
        Check("Chrome's actual Korean address label is recognized", () =>
        {
            Assert(BrowserDetection.MatchesAddress("주소창 및 검색창", "view_1012"));
            Assert(BrowserDetection.MatchesAddress("주소창 및 검색창:", ""));
            Assert(!BrowserDetection.MatchesAddress("페이지에서 찾기", ""));
        });
        Check("Popup requires an app explicitly configured by the user", () =>
        {
            Assert(FocusPolicy.ShouldShowIndicator(settings, "editor.exe", true, true, InputMode.English));
            Assert(FocusPolicy.ShouldShowIndicator(settings, "EDITOR.EXE", true, true, InputMode.Korean));
            Assert(!FocusPolicy.ShouldShowIndicator(settings, "unconfigured.exe", true, true, InputMode.English));
            Assert(!FocusPolicy.ShouldShowIndicator(settings, "chrome.exe", true, true, InputMode.English));
        });
        Check("Popup is triggered by input focus, not a background mode observation", () =>
        {
            Assert(!FocusPolicy.ShouldShowIndicator(settings, "editor.exe", true, false, InputMode.Korean));
            Assert(!FocusPolicy.ShouldShowIndicator(settings, "editor.exe", false, true, InputMode.Korean));
            Assert(!FocusPolicy.ShouldShowIndicator(settings, "editor.exe", true, true, InputMode.Unknown));
        });
        Check("Paused and disabled-indicator settings suppress all popups", () =>
        {
            var local = settings.Copy(); local.Enabled = false; Assert(!FocusPolicy.ShouldShowIndicator(local, "editor.exe", true, true, InputMode.English));
            local.Enabled = true; local.ShowIndicator = false; Assert(!FocusPolicy.ShouldShowIndicator(local, "editor.exe", true, true, InputMode.English));
        });
        Check("Closed Korean IME is English even if the stored native bit remains set", () =>
        {
            Assert(ImeController.InterpretMode(1, false) == InputMode.English);
            Assert(ImeController.InterpretMode(1, true) == InputMode.Korean);
            Assert(ImeController.InterpretMode(0, true) == InputMode.English);
        });
        Check("Primary button rendered text inherits white foreground and button font size", () =>
        {
            var button = new System.Windows.Controls.Button { Content = "추가", Style = (System.Windows.Style)System.Windows.Application.Current.FindResource("PrimaryButton"), FontSize = 12 };
            button.Measure(new System.Windows.Size(120, 40)); button.Arrange(new System.Windows.Rect(0, 0, 120, 40)); button.UpdateLayout();
            System.Windows.Controls.TextBlock? FindText(System.Windows.DependencyObject root)
            {
                if (root is System.Windows.Controls.TextBlock text) return text;
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
                { var found = FindText(System.Windows.Media.VisualTreeHelper.GetChild(root, i)); if (found != null) return found; }
                return null;
            }
            var text = FindText(button); Assert(text != null, "Button text visual not generated");
            Assert(text!.Foreground is System.Windows.Media.SolidColorBrush brush && brush.Color == System.Windows.Media.Colors.White, "Primary button text is not white");
            Assert(text.FontSize == 12, "Global text style overrode button font size");
        });
        Check("Minimal scroll viewer constrains its vertical scrollbar to six pixels", () =>
        {
            var content = new System.Windows.Controls.StackPanel();
            for (int i = 0; i < 10; i++) content.Children.Add(new System.Windows.Controls.TextBlock { Text = "App " + i, Height = 30 });
            var scroll = new System.Windows.Controls.ScrollViewer { Content = content, Style = (System.Windows.Style)System.Windows.Application.Current.FindResource("MinimalScrollViewer"), VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto };
            var host = new System.Windows.Window { Content = scroll, Width = 200, Height = 100, WindowStyle = System.Windows.WindowStyle.None, ShowActivated = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
            try
            {
                host.Show(); host.UpdateLayout();
                var bar = (System.Windows.Controls.Primitives.ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
                Assert(bar.ActualWidth == 6 && scroll.ScrollableHeight > 0, $"Scrollbar width={bar.ActualWidth}; minimum={bar.MinWidth}; requested={bar.Width}; scrollable height={scroll.ScrollableHeight}; viewport={scroll.ViewportHeight}");
            }
            finally { host.Close(); }
        });
        Check("Address and page input alternate with one popup on every entry", () =>
        {
            var visit = new IndicatorVisit();
            for (int i = 0; i < 20; i++)
            {
                foreach (string focus in new[] { "omnibox", "page-input" })
                {
                    visit.Enter(1, "editor.exe", focus, true);
                    Assert(visit.TryTake(settings, "editor.exe", true, InputMode.English, true));
                    for (int poll = 0; poll < 100; poll++)
                    {
                        visit.Enter(1, "EDITOR.EXE", focus, true);
                        Assert(!visit.TryTake(settings, "editor.exe", true, InputMode.Korean, true));
                    }
                }
            }
        });
        Check("Returning to the same input after a button or background re-arms popup", () =>
        {
            var visit = new IndicatorVisit(); visit.Enter(1, "editor.exe", "input", true);
            Assert(visit.TryTake(settings, "editor.exe", true, InputMode.Korean, true));
            visit.Enter(1, "editor.exe", "button", false);
            Assert(!visit.Pending);
            visit.Enter(1, "editor.exe", "input", true);
            Assert(visit.TryTake(settings, "editor.exe", true, InputMode.Korean, true));
            visit.Enter(2, "other.exe", "", false);
            visit.Enter(1, "editor.exe", "input", true);
            Assert(visit.TryTake(settings, "editor.exe", true, InputMode.Korean, true));
        });
        Check("Same-program windows with matching runtime IDs are separate input entries", () =>
        {
            var visit = new IndicatorVisit(); visit.Enter(1, "editor.exe", "input", true);
            Assert(visit.TryTake(settings, "editor.exe", true, InputMode.English, true));
            visit.Enter(2, "EDITOR.EXE", "input", true);
            Assert(visit.TryTake(settings, "editor.exe", true, InputMode.Korean, true));
        });
        Check("Input entry waits for caret and known mode without duplicate polling popups", () =>
        {
            var visit = new IndicatorVisit(); visit.Enter(1, "editor.exe", "input", true);
            Assert(!visit.TryTake(settings, "editor.exe", true, InputMode.Unknown, true));
            visit.Enter(1, "editor.exe", "input", true);
            Assert(!visit.TryTake(settings, "editor.exe", true, InputMode.English, false));
            Assert(visit.TryTake(settings, "editor.exe", true, InputMode.Korean, true));
            Assert(!visit.TryTake(settings, "editor.exe", true, InputMode.Korean, true));
        });
        Check("Leaving an input cancels a delayed caret popup; empty focus cannot arm", () =>
        {
            var visit = new IndicatorVisit(); visit.Enter(1, "editor.exe", "input", true);
            Assert(!visit.TryTake(settings, "editor.exe", true, InputMode.Korean, false));
            visit.Enter(1, "editor.exe", "button", false);
            Assert(!visit.TryTake(settings, "editor.exe", false, InputMode.Korean, true));
            visit.Enter(1, "editor.exe", "", true); Assert(!visit.Pending);
            visit.Enter(0, "editor.exe", "input", true); Assert(!visit.Pending);
        });
        Check("Unconfigured apps, pause and disabled indicator suppress focus popups", () =>
        {
            var visit = new IndicatorVisit(); visit.Enter(1, "unconfigured.exe", "input", true);
            Assert(!visit.TryTake(settings, "unconfigured.exe", true, InputMode.English, true));
            var local = settings.Copy(); local.Enabled = false;
            visit.Enter(2, "editor.exe", "input", true);
            Assert(!visit.TryTake(local, "editor.exe", true, InputMode.English, true));
            local.Enabled = true; Assert(!visit.TryTake(local, "editor.exe", true, InputMode.English, true));
            local.ShowIndicator = false; visit.Enter(2, "editor.exe", "other-input", true);
            Assert(!visit.TryTake(local, "editor.exe", true, InputMode.English, true));
            local.ShowIndicator = true; visit.Enter(2, "editor.exe", "input", true);
            Assert(visit.TryTake(local, "editor.exe", true, InputMode.Korean, true));
        });
        Check("Process search matches hidden Store processes by app name, executable and PID", () =>
        {
            var store = new ProcessEntry(14360, "Notepads", "메모장", @"C:\Program Files\WindowsApps\Notepads\Notepads.exe", false);
            Assert(store.Matches("메모장") && store.Matches("NOTEPADS") && store.Matches("14360") && store.Matches("WindowsApps"));
            Assert(!store.Matches("not-running-app") && store.Matches("  "));
        });
        Check("Paused tray mark is fully grayscale; active mark keeps its identity colors", () =>
        {
            var paused = Brand.Bitmap(32, true); var active = Brand.Bitmap(32);
            var gray = new byte[32 * 32 * 4]; var color = new byte[gray.Length]; paused.CopyPixels(gray, 32 * 4, 0); active.CopyPixels(color, 32 * 4, 0);
            bool colored = false;
            for (int i = 0; i < gray.Length; i += 4)
            {
                Assert(gray[i] == gray[i + 1] && gray[i + 1] == gray[i + 2], "Paused icon has colored pixels");
                colored |= color[i] != color[i + 1] || color[i + 1] != color[i + 2];
            }
            Assert(colored, "Active icon lost its colors");
            using var activeIcon = Brand.TrayIcon(false); using var pausedIcon = Brand.TrayIcon(true); Assert(activeIcon.Width == 32 && pausedIcon.Width == 32);
        });
        Check("Tab focus doesn't use dotted visuals on toggle, button or dropdown", () =>
        {
            var toggle = new System.Windows.Controls.CheckBox { Style = (System.Windows.Style)System.Windows.Application.Current.FindResource("Toggle") };
            var button = new System.Windows.Controls.Button { Style = (System.Windows.Style)System.Windows.Application.Current.FindResource(typeof(System.Windows.Controls.Button)) };
            var combo = new System.Windows.Controls.ComboBox { Style = (System.Windows.Style)System.Windows.Application.Current.FindResource(typeof(System.Windows.Controls.ComboBox)) };
            Assert(toggle.FocusVisualStyle == null && button.FocusVisualStyle == null && combo.FocusVisualStyle == null);
        });
        Check("Clearing a window's focus also clears its logical focus target", () =>
        {
            var window = new System.Windows.Window(); var toggle = new System.Windows.Controls.CheckBox(); window.Content = toggle;
            System.Windows.Input.FocusManager.SetFocusedElement(window, toggle); FocusHelpers.Clear(window);
            Assert(System.Windows.Input.FocusManager.GetFocusedElement(window) == null); window.Close();
        });
        Check("A missing foreground window never produces a guessed caret", () => Assert(CaretLocator.Find(0, null).IsEmpty));
        Check("INI preserves Unicode, escaped paths, labels and startup preference", () =>
        {
            var store = new SettingsStore(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "ini-roundtrip"));
            var local = settings.Copy(); local.StartWithWindows = true;
            local.Rules.Add(new(@"C:\앱\a=b;#[]\Notepads.exe", "메모 = ; # [x] \"따옴표\"\n줄", InputMode.Korean));
            store.Save(local); var loaded = store.Load();
            Assert(Path.GetFileName(store.FilePath) == "Typlow.ini" && loaded.StartWithWindows && loaded.Copy().StartWithWindows);
            Assert(loaded.Rules[2] == local.Rules[2]);
            Assert(File.ReadAllText(store.FilePath).Contains("[Settings]"));
        });
        Check("Saving unchanged INI never rewrites it; a changed option is persisted", () =>
        {
            var store = new SettingsStore(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "ini-write"));
            var local = settings.Copy(); store.Save(local);
            var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc); File.SetLastWriteTimeUtc(store.FilePath, stamp);
            store.Save(local); Assert(File.GetLastWriteTimeUtc(store.FilePath) == stamp);
            local.Enabled = false; store.Save(local); Assert(!store.Load().Enabled && File.GetLastWriteTimeUtc(store.FilePath) != stamp);
        });
        Check("Legacy JSON migrates once without deleting original or overriding INI", () =>
        {
            string dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "migration-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            string legacy = Path.Combine(dir, "old.json"); File.WriteAllText(legacy, System.Text.Json.JsonSerializer.Serialize(settings));
            var store = new SettingsStore(dir, legacy); var loaded = store.Load();
            Assert(loaded.Rules.Count == 2 && File.Exists(store.FilePath) && File.Exists(legacy));
            loaded.Rules.RemoveAt(0); store.Save(loaded); Assert(store.Load().Rules.Count == 1);
        });
        Check("Only requested helper processes are hidden, including case and extension variants", () =>
        {
            foreach (string name in new[] { "SystemSettings", "TextInputHost", "STEAMWEBHELPER.exe", "ApplicationFrameHost" }) Assert(!Native.IsSelectableProcess(name));
            Assert(Native.IsSelectableProcess("Notepads") && Native.IsSelectableProcess("notepad.exe") && Native.IsSelectableProcess("steam"));
        });
        Check("Startup command quotes the EXE path and starts silently in the tray", () =>
            Assert(StartupManager.Command(@"C:\My Apps\Typlow.exe") == "\"C:\\My Apps\\Typlow.exe\" --tray"));
        Check("Manual Hangul key cue waits for a mode change and is consumed once", () =>
        {
            var trigger = new ManualIndicatorTrigger(); DateTime now = DateTime.UtcNow;
            trigger.Arm(1, InputMode.English, now); Assert(!trigger.Ready(1, InputMode.English, now));
            Assert(trigger.Ready(1, InputMode.Korean, now)); trigger.Clear(); Assert(!trigger.Ready(1, InputMode.Korean, now));
            trigger.Arm(1, InputMode.English, now); Assert(!trigger.Ready(2, InputMode.Korean, now)); Assert(!trigger.Ready(1, InputMode.Korean, now));
            trigger.Arm(1, InputMode.English, now); Assert(!trigger.Ready(1, InputMode.Korean, now.AddSeconds(2)));
        });
        lines.Add($"\n{lines.Count - failures} passed, {failures} failed.");
        lines.Add("Policy/settings/UI tests only. Actual Store-app switching, caret location, and multi-monitor DPI behavior require interactive Windows testing.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!); File.WriteAllLines(report, lines);
        if (failures != 0) throw new Exception("Self-tests failed; see " + report);
    }
}
