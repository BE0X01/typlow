using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Typlow;

internal record ProcessEntry(int Id, string Name, string Label, string Path, bool HasWindow)
{
    internal bool Matches(string query) => string.IsNullOrWhiteSpace(query) ||
        Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) || Label.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) ||
        Id.ToString(CultureInfo.InvariantCulture).Contains(query.Trim(), StringComparison.OrdinalIgnoreCase) || Path.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
}

internal sealed class ProcessPicker : Window
{
    public string? SelectedPath { get; private set; }
    public string? SelectedLabel { get; private set; }
    private List<ProcessEntry> snapshot = new();
    private readonly TextBox search = new() { FontSize = 13, Margin = new Thickness(0, 0, 0, 12) };
    private readonly ListBox list = new() { Background = Brushes.White, BorderThickness = new Thickness(0) };
    private readonly TextBlock count = new() { FontSize = 12, Foreground = MainWindow.Brush("#7C8598") };
    private readonly Button add;
    private bool loading;
    public ProcessPicker(bool preview = false)
    {
        Title = "프로세스 검색"; Width = 620; Height = 530; MinWidth = 540; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Icon = Brand.Mark(); Background = MainWindow.Brush("#F7F8FB");
        Foreground = MainWindow.Brush("#252936"); FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
        FocusHelpers.Install(this); list.SetResourceReference(FrameworkElement.StyleProperty, "MinimalListBox");
        var panel = new DockPanel { Margin = new Thickness(22) };
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        heading.Children.Add(new TextBlock { Text = "실행 중인 프로세스 찾기", FontSize = 20, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "창이 없는 프로세스도 포함해 이름·프로세스명·PID로 찾을 수 있어요.", FontSize = 12, Foreground = MainWindow.Brush("#7C8598"), Margin = new Thickness(0, 8, 0, 12) });
        search.ToolTip = "앱 이름, 프로세스명 또는 PID";
        search.Tag = "앱 이름, 프로세스명 또는 PID";
        System.Windows.Automation.AutomationProperties.SetName(search, "프로세스 검색어");
        heading.Children.Add(search); DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var actions = new Grid { Margin = new Thickness(0, 15, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        count.VerticalAlignment = VerticalAlignment.Center; actions.Children.Add(count);
        var refresh = new Button { Content = "새로고침", FontSize = 13, Margin = new Thickness(0, 0, 8, 0) }; Grid.SetColumn(refresh, 1); actions.Children.Add(refresh);
        add = new Button { Content = "선택", Style = (Style)FindResource("PrimaryButton"), FontSize = 13, IsDefault = true, IsEnabled = false }; Grid.SetColumn(add, 2); actions.Children.Add(add);
        DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
        list.SelectionChanged += (_, _) => add.IsEnabled = list.SelectedItem is ListBoxItem item && ((ProcessEntry)item.Tag).Path.Length > 0;
        list.MouseDoubleClick += (_, _) => Select(); add.Click += (_, _) => Select();
        search.TextChanged += (_, _) => Filter(); refresh.Click += async (_, _) => await Reload();
        panel.Children.Add(list); Content = panel;
        if (preview)
        {
            snapshot = new() { new(14360, "Notepads", "Notepads · Store 앱", @"C:\Program Files\WindowsApps\Preview\Notepads.exe", false), new(11520, "chrome", "Google Chrome", @"C:\Preview\chrome.exe", true), new(4100, "ApplicationFrameHost", "Application Frame Host", @"C:\Windows\System32\ApplicationFrameHost.exe", true) };
            Filter(); search.Text = "note";
        }
        else Loaded += async (_, _) => await Reload();
    }
    private async Task Reload()
    {
        if (loading) return; loading = true; count.Text = "프로세스를 불러오는 중…";
        try { snapshot = await Task.Run(Native.RunningProcesses); Filter(); }
        catch { count.Text = "프로세스를 불러오지 못했습니다."; }
        finally { loading = false; }
    }
    private void Filter()
    {
        list.Items.Clear();
        foreach (var entry in snapshot.Where(p => Native.IsSelectableProcess(p.Name) && p.Matches(search.Text)).OrderByDescending(p => p.HasWindow).ThenBy(p => p.Label, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Id))
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var description = new StackPanel(); description.Children.Add(new TextBlock { Text = entry.Label, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            description.Children.Add(new TextBlock { Text = entry.Path.Length > 0 ? entry.Name + ".exe" : entry.Name + " · 경로 확인 불가", FontSize = 12, Foreground = MainWindow.Brush("#858E9F"), Margin = new Thickness(0, 4, 0, 0) }); row.Children.Add(description);
            var pid = new TextBlock { Text = "PID " + entry.Id, FontSize = 12, Foreground = MainWindow.Brush("#858E9F"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(pid, 1); row.Children.Add(pid);
            list.Items.Add(new ListBoxItem { Content = row, Tag = entry, ToolTip = entry.Path.Length > 0 ? entry.Path : "권한 제한으로 실행 파일 경로를 확인할 수 없습니다." });
        }
        add.IsEnabled = false; count.Text = $"{list.Items.Count}개 / 전체 {snapshot.Count}개";
    }
    private void Select()
    {
        if (list.SelectedItem is not ListBoxItem item || ((ProcessEntry)item.Tag).Path.Length == 0) return;
        var entry = (ProcessEntry)item.Tag; SelectedPath = entry.Path; SelectedLabel = entry.Label; DialogResult = true;
    }
}
