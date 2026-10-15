using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Typlow;

internal static class CaretLocator
{
    internal static Rect Find(nint window, AutomationElement? focused)
    {
        if (window == 0 || Native.GetForegroundWindow() != window) return Rect.Empty;
        var info = Native.Gui(window);
        if (info.Caret != 0 && info.CaretRect.Bottom > info.CaretRect.Top)
        {
            var start = new Native.Point { X = info.CaretRect.Left, Y = info.CaretRect.Top };
            if (Native.ClientToScreen(info.Caret, ref start))
                return new(start.X, start.Y, Math.Max(1, info.CaretRect.Right - info.CaretRect.Left), info.CaretRect.Bottom - info.CaretRect.Top);
        }
        Rect native = NativeCaret();
        if (!native.IsEmpty) return native;
        try
        {
            if (focused == null || !focused.Current.HasKeyboardFocus || !focused.TryGetCurrentPattern(TextPattern.Pattern, out object pattern)) return Rect.Empty;
            var selection = ((TextPattern)pattern).GetSelection();
            if (selection.Length == 0) return Rect.Empty;
            var range = selection[0].Clone();
            if (range.CompareEndpoints(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End) != 0) return Rect.Empty;
            Rect[] rectangles = range.GetBoundingRectangles();
            if (rectangles.Length > 0 && rectangles[0].Height > 0) return rectangles[0];
            range.ExpandToEnclosingUnit(TextUnit.Character);
            rectangles = range.GetBoundingRectangles();
            return rectangles.Length > 0 && rectangles[0].Height > 0 ? new(rectangles[0].Left, rectangles[0].Top, 1, rectangles[0].Height) : Rect.Empty;
        }
        catch { return Rect.Empty; }
    }

    // These slots and GUIDs are defined by Microsoft's UIAutomationClient.h (IUnknown slots included).
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetObject(nint self, out nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPattern(nint self, int pattern, ref Guid iid, out nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetCaret(nint self, out int active, out nint range);
    private static T Method<T>(nint instance, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    private static Rect NativeCaret()
    {
        nint automation = 0, element = 0, pattern = 0, range = 0, array = 0;
        int initialized = CoInitializeEx(0, 0);
        try
        {
            var clsid = new Guid("ff48dba4-60ef-4201-aa87-54103eef594e");
            var iid = new Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");
            var patternIid = new Guid("506a921a-fcc9-409f-b23b-37eb74106872");
            if (CoCreateInstance(ref clsid, 0, 1, ref iid, out automation) < 0 || automation == 0) return Rect.Empty;
            if (Method<GetObject>(automation, 8)(automation, out element) < 0 || element == 0) return Rect.Empty;
            if (Method<GetPattern>(element, 14)(element, 10024, ref patternIid, out pattern) < 0 || pattern == 0) return Rect.Empty;
            if (Method<GetCaret>(pattern, 10)(pattern, out int active, out range) < 0 || active == 0 || range == 0) return Rect.Empty;
            if (Method<GetObject>(range, 10)(range, out array) < 0 || array == 0 || SafeArrayGetDim(array) != 1) return Rect.Empty;
            if (SafeArrayGetLBound(array, 1, out int lower) < 0 || SafeArrayGetUBound(array, 1, out int upper) < 0 || upper - lower + 1 < 4) return Rect.Empty;
            if (SafeArrayAccessData(array, out nint data) < 0) return Rect.Empty;
            try
            {
                var values = new double[4]; Marshal.Copy(data, values, 0, 4);
                if (!double.IsFinite(values[0]) || !double.IsFinite(values[1]) || !double.IsFinite(values[2]) || !double.IsFinite(values[3]) || values[2] < 0 || values[3] <= 0) return Rect.Empty;
                return new(values[0], values[1], Math.Max(1, values[2]), values[3]);
            }
            finally { SafeArrayUnaccessData(array); }
        }
        catch (COMException) { return Rect.Empty; }
        finally
        {
            if (array != 0) SafeArrayDestroy(array);
            foreach (nint value in new[] { range, pattern, element, automation }) if (value != 0) Marshal.Release(value);
            if (initialized >= 0) CoUninitialize();
        }
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint value);
    [DllImport("oleaut32.dll")] private static extern uint SafeArrayGetDim(nint array);
    [DllImport("oleaut32.dll")] private static extern int SafeArrayGetLBound(nint array, uint dimension, out int bound);
    [DllImport("oleaut32.dll")] private static extern int SafeArrayGetUBound(nint array, uint dimension, out int bound);
    [DllImport("oleaut32.dll")] private static extern int SafeArrayAccessData(nint array, out nint data);
    [DllImport("oleaut32.dll")] private static extern int SafeArrayUnaccessData(nint array);
    [DllImport("oleaut32.dll")] private static extern int SafeArrayDestroy(nint array);
}
