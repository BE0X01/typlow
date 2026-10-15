using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Automation;

namespace Typlow;

// Developer-only read-only probe: no URL values, page text, clicks, keystrokes or input switching.
internal static class BrowserProbe
{
    internal static void Processes(string report)
    {
        var lines = new List<string>();
        var thread = new Thread(() =>
        {
            try
            {
                var entries = Native.RunningProcesses(); lines.Add("Total processes: " + entries.Count);
                foreach (var app in Native.RunningApps())
                    if (app.Label.Contains("note", StringComparison.OrdinalIgnoreCase) || app.Label.Contains("메모"))
                        lines.Add("App picker includes: " + app.Label + "; " + Path.GetFileName(app.Path));
                lines.Add("Excluded processes present: " + entries.Exists(p => !Native.IsSelectableProcess(p.Name)));
                foreach (var entry in entries)
                    if (entry.Matches("note") || entry.Matches("메모")) lines.Add($"Candidate: {entry.Name}; Label={entry.Label}; PID={entry.Id}; PathAvailable={entry.Path.Length > 0}; MainWindow={entry.HasWindow}");
                Native.EnumWindowProc inspect = (root, _) =>
                {
                    if (!Native.IsWindowVisible(root)) return true;
                    string identity = Native.AppPath(root);
                    foreach (var entry in entries)
                        if (entry.Path.Length > 0 && (entry.Matches("note") || entry.Matches("메모")) && string.Equals(entry.Path, identity, StringComparison.OrdinalIgnoreCase))
                        {
                            lines.Add($"Store window identity matched selected process: {entry.Name}; Root={Path.GetFileName(Native.ProcessPath(root))}");
                            nint input = Native.Gui(root).Focus;
                            lines.Add("Store input target: " + Path.GetFileName(Native.ProcessPath(input)) + "; " + new ImeController().Diagnostic(root));
                        }
                    return true;
                };
                Native.EnumWindows(inspect, 0); GC.KeepAlive(inspect);
                nint window = Native.GetForegroundWindow();
                lines.Add("Root process: " + Path.GetFileName(Native.ProcessPath(window)));
                lines.Add("Effective app: " + Path.GetFileName(Native.AppPath(window)));
                lines.Add("Caret found: " + !CaretLocator.Find(window, AutomationElement.FocusedElement).IsEmpty);
            }
            catch (Exception e) { lines.Add("Process probe failed: " + e.GetType().Name + " / " + e.Message); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start();
        if (!thread.Join(12000)) { File.WriteAllText(report, "Read-only process probe timed out."); return; }
        File.WriteAllLines(report, lines);
    }
    internal static void Run(string report)
    {
        var output = new List<string>();
        var thread = new Thread(() =>
        {
            try
            {
                nint current = Native.GetForegroundWindow();
                output.Add("Foreground process: " + Path.GetFileName(Native.ProcessPath(current)));
                output.Add(new ImeController().Diagnostic(current));
                var focused = AutomationElement.FocusedElement;
                if (focused != null) output.Add("Focus control: " + focused.Current.ControlType.ProgrammaticName + " / " + focused.Current.AutomationId);
                int browsers = 0;
                foreach (var process in Process.GetProcesses())
                {
                    using (process)
                    {
                        if (process.ProcessName is not ("chrome" or "msedge") || process.MainWindowHandle == 0) continue;
                        browsers++;
                        var root = AutomationElement.FromHandle(process.MainWindowHandle);
                        var edits = root.FindAll(TreeScope.Descendants, new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox)));
                        int found = 0;
                        foreach (AutomationElement element in edits)
                        {
                            // Report browser chrome only, not website input labels.
                            if (!BrowserDetection.IsAddress(element)) continue;
                            var data = element.Current; found++;
                            output.Add(process.ProcessName + " address: Name=" + data.Name + "; Id=" + data.AutomationId + "; Focus=" + data.HasKeyboardFocus);
                        }
                        output.Add(process.ProcessName + " recognized address elements: " + found);
                    }
                }
                output.Add("Browser windows: " + browsers);
            }
            catch (Exception e) { output.Add("Probe failed: " + e.GetType().Name + " / " + e.Message); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start();
        if (!thread.Join(8000)) { File.WriteAllText(report, "Read-only browser probe timed out."); return; }
        File.WriteAllLines(report, output);
    }
}
