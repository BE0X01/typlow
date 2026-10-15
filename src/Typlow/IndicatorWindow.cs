using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Typlow;

internal sealed class IndicatorWindow : Window
{
    private readonly TextBlock label;
    private readonly DispatcherTimer timer = new();
    private readonly DispatcherTimer guard = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private nint ownerWindow;
    public IndicatorWindow()
    {
        Width = 44; Height = 30; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        Focusable = false; IsHitTestVisible = false;
        label = new TextBlock { Text = "한", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Content = new Border { CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(Color.FromArgb(240, 45, 51, 76)), Child = label };
        SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            var styles = Native.GetWindowLongPtr(handle, -20);
            Native.SetWindowLongPtr(handle, -20, (nint)((long)styles | 0x08000000 | 0x00000080 | 0x00000020));
        };
        timer.Tick += (_, _) => Dismiss();
        guard.Tick += (_, _) => { if (Native.GetForegroundWindow() != ownerWindow) Dismiss(); };
    }
    public void Present(IndicatorNotice notice)
    {
        label.Text = notice.Mode == InputMode.Korean ? "한" : "A";
        ownerWindow = notice.Window;
        if (Native.GetForegroundWindow() != ownerWindow) return;
        if (!IsVisible) Show();
        PositionAtCaret(notice.Anchor);
        timer.Stop(); timer.Interval = TimeSpan.FromMilliseconds(notice.Duration); timer.Start(); guard.Start();
    }
    private void PositionAtCaret(Rect anchor)
    {
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)anchor.Left, (int)anchor.Bottom));
        // Position in physical pixels after creation. SetWindowPos avoids mixed-DPI WPF coordinate ambiguity.
        var source = PresentationSource.FromVisual(this);
        var scale = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        int width = (int)Math.Ceiling(Width * scale.M11), height = (int)Math.Ceiling(Height * scale.M22);
        int x = Math.Clamp((int)anchor.Left + 7, screen.WorkingArea.Left, Math.Max(screen.WorkingArea.Left, screen.WorkingArea.Right - width));
        int y = Math.Clamp((int)anchor.Bottom + 6, screen.WorkingArea.Top, Math.Max(screen.WorkingArea.Top, screen.WorkingArea.Bottom - height));
        SetWindowPos(new WindowInteropHelper(this).Handle, (nint)(-1), x, y, width, height, 0x0010 | 0x0040);
    }
    public void Dismiss() { timer.Stop(); guard.Stop(); Hide(); }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint insert, int x, int y, int width, int height, uint flags);
}
