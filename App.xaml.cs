using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Typlow;

public partial class App : Application
{
    private Mutex? singleton;
    private FocusEngine? engine;
    private IndicatorWindow? indicator;
    private Forms.NotifyIcon? tray;
    private MainWindow? settingsWindow;
    private System.Drawing.Icon? trayIcon;
    private System.Drawing.Icon? pausedTrayIcon;
    private Settings settings = new();
    private SettingsStore? settingsStore;
    private bool startupRegistered;
    private bool exiting;
    private readonly ObservableCollection<string> logs = new();
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? Option(string option) { int index = Array.IndexOf(e.Args, option); return index >= 0 && index + 1 < e.Args.Length ? e.Args[index + 1] : null; }
        try
        {
            if (e.Args.Contains("--self-test")) { SelfTests.Run(Option("--report") ?? "self-test.txt"); Shutdown(); return; }
            if (e.Args.Contains("--smoke-test")) { RunPassiveSmoke(Option("--report") ?? "smoke-test.txt"); return; }
            if (e.Args.Contains("--browser-probe")) { BrowserProbe.Run(Option("--report") ?? "browser-probe.txt"); Shutdown(); return; }
            if (e.Args.Contains("--process-probe")) { BrowserProbe.Processes(Option("--report") ?? "process-probe.txt"); Shutdown(); return; }
            if (Option("--export-brand") is string folder) { Brand.Export(folder); Shutdown(); return; }
            if (Option("--preview") is string preview) { RenderPreview(preview); return; }
            singleton = new Mutex(true, "Local\\Typlow.Prototype.0.1", out bool first);
            if (!first) { MessageBox.Show("Typlow가 이미 실행 중입니다. 트레이 아이콘을 두 번 눌러 설정을 여세요.", "Typlow"); Shutdown(); return; }
            var store = settingsStore = new SettingsStore(); settings = store.Load();
            startupRegistered = settings.StartWithWindows;
            if (startupRegistered)
                try { StartupManager.SetEnabled(true); }
                catch (Exception error) { AddLog("자동 실행 등록 실패: " + error.Message); }
            engine = new(settings); indicator = new();
            settingsWindow = new(settings, PersistSettings, logs);
            MainWindow = settingsWindow;
            engine.Log += text => Dispatcher.BeginInvoke(() => AddLog(text));
            engine.HideIndicator += () => Dispatcher.BeginInvoke(() => indicator.Dismiss());
            engine.Indicator += notice => Dispatcher.BeginInvoke(() => {
                if (!exiting && settings.Enabled && settings.ShowIndicator && Native.GetForegroundWindow() == notice.Window &&
                    settings.Rules.Any(r => string.Equals(r.Path, notice.Path, StringComparison.OrdinalIgnoreCase))) indicator.Present(notice);
            });
            DispatcherUnhandledException += (_, args) => { AddLog("화면 오류: " + args.Exception.GetType().Name); args.Handled = true; };
            CreateTray(); if (!e.Args.Contains("--tray")) settingsWindow.Show(); engine.Start();
            if (store.LoadWarning != null) AddLog(store.LoadWarning);
            AddLog("시작 · 포커스 진입 시 1회 전환. 상태 관찰은 반복 강제 전환을 하지 않습니다.");
        }
        catch (Exception error)
        {
            string? path = Option("--report") ?? Option("--preview");
            if (!string.IsNullOrEmpty(path)) File.WriteAllText(path + ".error.txt", error.ToString());
            else MessageBox.Show("Typlow를 시작하지 못했습니다.\n" + error.Message, "Typlow", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    private void AddLog(string text)
    { logs.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + text); while (logs.Count > 100) logs.RemoveAt(logs.Count - 1); }
    private void PersistSettings(Settings value)
    {
        settingsStore!.Save(value);
        if (startupRegistered != value.StartWithWindows)
        {
            StartupManager.SetEnabled(value.StartWithWindows);
            startupRegistered = value.StartWithWindows;
        }
        settings = value; engine?.Update(value);
        if (!value.Enabled || !value.ShowIndicator) indicator?.Dismiss(); UpdateTray();
    }
    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("설정 열기", null, (_, _) => Dispatcher.Invoke(() => settingsWindow?.BringBack()));
        menu.Items.Add("자동 전환 일시정지 / 재개", null, (_, _) => Dispatcher.Invoke(() =>
        {
            settings.Enabled = !settings.Enabled;
            try { settingsStore!.Save(settings); } catch { AddLog("설정을 저장하지 못했습니다."); }
            engine?.Update(settings);
            settingsWindow?.CloseForExit();
            settingsWindow = new(settings, PersistSettings, logs);
            MainWindow = settingsWindow;
            if (!settings.Enabled) indicator?.Dismiss(); UpdateTray();
        }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Typlow 종료", null, (_, _) => Dispatcher.Invoke(ExitApp));
        // Keep the icon alive for NotifyIcon's lifetime; no native HICON leak.
        trayIcon = Brand.TrayIcon(false); pausedTrayIcon = Brand.TrayIcon(true);
        tray = new Forms.NotifyIcon { Icon = trayIcon, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => settingsWindow?.BringBack()); UpdateTray();
    }
    private void UpdateTray() { if (tray != null) { tray.Text = settings.Enabled ? "Typlow · 자동 전환 켜짐" : "Typlow · 일시정지"; tray.Icon = settings.Enabled ? trayIcon : pausedTrayIcon; } }
    private void ExitApp()
    { if (exiting) return; exiting = true; engine?.Dispose(); indicator?.Close(); settingsWindow?.CloseForExit(); Shutdown(); }
    protected override void OnExit(ExitEventArgs e)
    {
        engine?.Dispose(); if (tray != null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); } trayIcon?.Dispose(); pausedTrayIcon?.Dispose(); singleton?.Dispose(); base.OnExit(e);
    }
    private void RenderPreview(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // Preview data is isolated: no settings file, event hook, input switch, or tray icon is created.
        var sample = new Settings { Rules = new() {
            new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"), "메모장", InputMode.Korean),
            new(@"C:\Preview\Code.exe", "Visual Studio Code", InputMode.English),
            new(@"C:\Preview\chrome.exe", "Google Chrome", InputMode.Korean) } };
        var window = new MainWindow(sample, _ => { }, new()) { WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
        window.Show();
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            try
            {
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window); Brand.SavePng(bitmap, path);
                window.Width = 550; window.Height = 730; window.UpdateLayout();
                var compact = new RenderTargetBitmap(550, 730, 96, 96, PixelFormats.Pbgra32); compact.Render(window);
                Brand.SavePng(compact, Path.Combine(Path.GetDirectoryName(path)!, "Typlow-preview-compact.png"));
                var picker = new AppPicker(true) { WindowStyle = WindowStyle.None, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
                picker.Show(); picker.UpdateLayout();
                var pickerBitmap = new RenderTargetBitmap((int)picker.ActualWidth, (int)picker.ActualHeight, 96, 96, PixelFormats.Pbgra32); pickerBitmap.Render(picker);
                Brand.SavePng(pickerBitmap, Path.Combine(Path.GetDirectoryName(path)!, "Typlow-app-picker.png")); picker.Close();
                var processPicker = new ProcessPicker(true) { WindowStyle = WindowStyle.None, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false };
                processPicker.Show(); processPicker.UpdateLayout();
                var processBitmap = new RenderTargetBitmap((int)processPicker.ActualWidth, (int)processPicker.ActualHeight, 96, 96, PixelFormats.Pbgra32); processBitmap.Render(processPicker);
                Brand.SavePng(processBitmap, Path.Combine(Path.GetDirectoryName(path)!, "Typlow-process-search.png")); processPicker.Close();
                Brand.SavePng(Brand.Bitmap(64), Path.Combine(Path.GetDirectoryName(path)!, "Typlow-tray-active.png"));
                Brand.SavePng(Brand.Bitmap(64, true), Path.Combine(Path.GetDirectoryName(path)!, "Typlow-tray-paused.png"));
                window.CloseForExit(); Shutdown();
            }
            catch (Exception error) { File.WriteAllText(path + ".error.txt", error.ToString()); Shutdown(1); }
        });
    }
    private void RunPassiveSmoke(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var observations = new System.Collections.Concurrent.ConcurrentQueue<string>();
        nint before = Native.GetForegroundWindow();
        var cue = new IndicatorWindow { Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
        cue.Show();
        nint cueHandle = new System.Windows.Interop.WindowInteropHelper(cue).Handle;
        long styles = (long)Native.GetWindowLongPtr(cueHandle, -20);
        observations.Enqueue("Indicator WS_EX_NOACTIVATE: " + ((styles & 0x08000000) != 0));
        observations.Enqueue("Indicator WS_EX_TRANSPARENT: " + ((styles & 0x00000020) != 0));
        observations.Enqueue("Foreground unchanged on indicator creation: " + (before == Native.GetForegroundWindow()));
        var visual = new RenderTargetBitmap(44, 30, 96, 96, PixelFormats.Pbgra32); visual.Render(cue);
        Brand.SavePng(visual, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "indicator-smoke.png"));
        cue.Hide();
        observations.Enqueue("Native INPUT size: " + System.Runtime.InteropServices.Marshal.SizeOf<Native.Input>());
        observations.Enqueue("Native GUITHREADINFO size: " + System.Runtime.InteropServices.Marshal.SizeOf<Native.GuiInfo>());
        var passive = new FocusEngine(new Settings { Enabled = false, ShowIndicator = false });
        int statusCount = 0;
        passive.Log += message => observations.Enqueue(message);
        passive.Status += status => { Interlocked.Increment(ref statusCount); observations.Enqueue("Observed: " + status.App + " / " + status.Mode); };
        passive.Start();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop(); passive.Dispose(); cue.Close();
            observations.Enqueue("Status observations: " + statusCount);
            observations.Enqueue("No input switching performed; engine disabled during passive smoke test.");
            File.WriteAllLines(path, observations); Shutdown(statusCount > 0 ? 0 : 1);
        };
        timer.Start();
    }
}
