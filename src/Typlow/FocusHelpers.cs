using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Typlow;

internal static class FocusHelpers
{
    internal static void Install(Window window)
    {
        window.PreviewMouseDown += (_, args) =>
        {
            DependencyObject? current = args.OriginalSource as DependencyObject;
            while (current != null && current != window)
            {
                if (current is ButtonBase or TextBoxBase or ComboBox or ListBoxItem or ScrollBar or Thumb) return;
                current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);
            }
            Clear(window);
        };
    }
    internal static void Clear(Window window) { FocusManager.SetFocusedElement(window, null); Keyboard.ClearFocus(); }
}
