using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Typlow;

public partial class MainWindow : Window
{
    private Settings settings;
    private readonly Action<Settings> persist;
    private bool ready, allowClose;
    public MainWindow(Settings settings, Action<Settings> persist, ObservableCollection<string> logs)
    {
        this.settings = settings; this.persist = persist;
        InitializeComponent(); BrandImage.Source = Brand.Mark(); Icon = Brand.Mark();
        FocusHelpers.Install(this);
        EnabledToggle.IsChecked = settings.Enabled; AddressToggle.IsChecked = settings.AddressEnglish; IndicatorToggle.IsChecked = settings.ShowIndicator;
        StartupToggle.IsChecked = settings.StartWithWindows;
        foreach (ComboBoxItem item in DurationCombo.Items) if (int.Parse((string)item.Tag) == settings.IndicatorMilliseconds) DurationCombo.SelectedItem = item;
        if (DurationCombo.SelectedIndex < 0) DurationCombo.SelectedIndex = 1;
        RenderRules(); UpdateEnabled(); ready = true;
        Closing += OnClosing;
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) { Hide(); WindowState = WindowState.Normal; } };
        SourceInitialized += (_, _) => { try { int preference = 2; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 33, ref preference, 4); } catch { } };
    }
    private void Save()
    {
        try { persist(settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { MessageBox.Show(this, "설정 저장 또는 자동 실행 등록을 완료하지 못했습니다.\n" + e.Message, "Typlow", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void OptionsChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        settings.Enabled = EnabledToggle.IsChecked == true; settings.AddressEnglish = AddressToggle.IsChecked == true; settings.ShowIndicator = IndicatorToggle.IsChecked == true;
        settings.StartWithWindows = StartupToggle.IsChecked == true;
        UpdateEnabled(); Save();
    }
    private void UpdateEnabled()
    {
        EnabledLabel.Text = settings.Enabled ? "포커스를 감지하고 있어요" : "잠시 쉬고 있어요";
        StatusDot.Fill = Brush(settings.Enabled ? "#55B692" : "#B8BDCA");
    }
    private void DurationChanged(object sender, SelectionChangedEventArgs e)
    { if (ready && DurationCombo.SelectedItem is ComboBoxItem item) { settings.IndicatorMilliseconds = int.Parse((string)item.Tag); Save(); } }
    private void RenderRules()
    {
        RulesPanel.Children.Clear(); EmptyRules.Visibility = settings.Rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var rule in settings.Rules.ToArray())
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3), Height = 42 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            var badge = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = Brush("#F1F3F8"), VerticalAlignment = VerticalAlignment.Center };
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(rule.Path);
                if (icon != null) badge.Child = new Image { Source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromWidthAndHeight(24, 24)), Width = 23, Height = 23 };
            }
            catch { }
            badge.Child ??= new TextBlock { Text = rule.Label.Length > 0 ? rule.Label[..1].ToUpperInvariant() : "A", Foreground = Brush("#8B93AD"), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(badge);
            var description = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0), ToolTip = rule.Path };
            description.Children.Add(new TextBlock { Text = rule.Label, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            description.Children.Add(new TextBlock { Text = Path.GetFileName(rule.Path), FontSize = 12, Foreground = Brush("#858E9F"), Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(description, 1); row.Children.Add(description);
            var choices = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var mode in new[] { InputMode.Korean, InputMode.English })
            {
                bool selected = rule.Mode == mode;
                var choice = new Button { Content = mode == InputMode.Korean ? "한글" : "영어", Padding = new Thickness(12, 7, 12, 7), FontSize = 13,
                    Background = Brush(selected ? "#FFFFFF" : "#EFF1F6"), Foreground = Brush(selected ? "#505BC5" : "#858E9F"), FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal };
                System.Windows.Automation.AutomationProperties.SetName(choice, rule.Label + " " + choice.Content);
                choice.Click += (_, _) => { int index = settings.Rules.IndexOf(rule); if (index >= 0) { settings.Rules[index] = rule with { Mode = mode }; RenderRules(); Save(); } };
                choices.Children.Add(choice);
            }
            var segment = new Border { Background = Brush("#EFF1F6"), CornerRadius = new CornerRadius(10), Padding = new Thickness(3), Child = choices, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(segment, 2); row.Children.Add(segment);
            var remove = new Button { Style = (Style)FindResource("DeleteButton"), Width = 30, Height = 30, Margin = new Thickness(4, 0, 0, 0), ToolTip = "앱 규칙 삭제" };
            var trash = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 2,4 L 14,4 M 6,2 L 10,2 M 4,4 L 5,15 L 11,15 L 12,4 M 7,7 L 7,12 M 9,7 L 9,12"), StrokeThickness = 1.3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Width = 16, Height = 17 };
            trash.SetBinding(System.Windows.Shapes.Path.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = remove }); remove.Content = trash;
            System.Windows.Automation.AutomationProperties.SetName(remove, rule.Label + " 규칙 삭제");
            remove.Click += (_, _) => { settings.Rules.Remove(rule); RenderRules(); Save(); };
            Grid.SetColumn(remove, 3); row.Children.Add(remove); RulesPanel.Children.Add(row);
        }
    }
    private void AddAppClick(object sender, RoutedEventArgs e)
    {
        var picker = new AppPicker { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedPath == null) return;
        string path = picker.SelectedPath;
        if (settings.Rules.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)))
        { MessageBox.Show(this, "이미 추가한 앱입니다. 목록에서 언어를 바꿔 주세요.", "Typlow"); return; }
        settings.Rules.Add(new(path, picker.SelectedLabel ?? Path.GetFileNameWithoutExtension(path), InputMode.English)); RenderRules(); Save();
    }
    public void BringBack() { Show(); WindowState = WindowState.Normal; Activate(); }
    public void CloseForExit() { allowClose = true; Close(); }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!allowClose) { e.Cancel = true; Hide(); } }
    private void HideClick(object sender, RoutedEventArgs e) => Hide();
    private void MinimizeClick(object sender, RoutedEventArgs e) => Hide();
    internal static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint handle, int attribute, ref int value, int size);
}

internal sealed class AppPicker : Window
{
    public string? SelectedPath { get; private set; }
    public string? SelectedLabel { get; private set; }
    private readonly ListBox list = new() { BorderThickness = new Thickness(0), Background = Brushes.White };
    public AppPicker(bool preview = false)
    {
        Title = "앱 추가"; Width = 470; Height = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner; Icon = Brand.Mark(); Background = MainWindow.Brush("#F7F8FB"); Foreground = MainWindow.Brush("#252936"); FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
        list.SetResourceReference(FrameworkElement.StyleProperty, "MinimalListBox");
        FocusHelpers.Install(this);
        var panel = new DockPanel { Margin = new Thickness(22) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        header.Children.Add(new TextBlock { Text = "어떤 앱에 적용할까요?", FontSize = 20, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "앱 목록·프로세스 검색·파일 찾기 중에서 선택해 주세요.", FontSize = 12, Foreground = MainWindow.Brush("#7F879A"), Margin = new Thickness(0, 8, 0, 0) });
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var processes = new Button { Content = "프로세스 검색", FontSize = 13, Margin = new Thickness(0, 0, 8, 0) };
        processes.Click += (_, _) =>
        {
            var picker = new ProcessPicker { Owner = this };
            if (picker.ShowDialog() == true && picker.SelectedPath != null) { SelectedPath = picker.SelectedPath; SelectedLabel = picker.SelectedLabel; DialogResult = true; }
        };
        actions.Children.Add(processes);
        var browse = new Button { Content = "파일 찾기…", Margin = new Thickness(0, 0, 8, 0) };
        browse.Click += (_, _) => { var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "프로그램 (*.exe)|*.exe", Title = "프로그램 선택" }; if (dialog.ShowDialog(this) == true) { SelectedPath = dialog.FileName; DialogResult = true; } };
        var add = new Button { Content = "추가", Style = (Style)FindResource("PrimaryButton"), IsDefault = true, IsEnabled = false };
        add.Click += (_, _) => Select(); list.SelectionChanged += (_, _) => add.IsEnabled = list.SelectedItem != null;
        list.MouseDoubleClick += (_, _) => Select(); actions.Children.Add(browse); actions.Children.Add(add); DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
        var apps = preview ? new System.Collections.Generic.List<(string Path, string Label)> { (@"C:\Preview\notepad.exe", "메모장"), (@"C:\Preview\chrome.exe", "Google Chrome") } : Native.RunningApps();
        foreach (var app in apps.DistinctBy(a => a.Path, StringComparer.OrdinalIgnoreCase).OrderBy(a => a.Label))
            list.Items.Add(new ListBoxItem { Content = app.Label, Tag = app.Path, FontSize = 13, Padding = new Thickness(10, 9, 10, 9), ToolTip = app.Path });
        panel.Children.Add(list); Content = panel;
        if (preview) list.SelectedIndex = 0;
    }
    private void Select() { if (list.SelectedItem is ListBoxItem item) { SelectedPath = (string)item.Tag; DialogResult = true; } }
}
