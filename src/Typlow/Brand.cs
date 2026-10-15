using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Typlow;

internal static class Brand
{
    // Original vector mark: paired language strokes and a mint insertion caret.
    public static DrawingImage Mark(bool paused = false)
    {
        var drawing = new DrawingGroup();
        using (DrawingContext d = drawing.Open())
        {
            d.DrawRoundedRectangle(new SolidColorBrush(paused ? Color.FromRgb(115, 115, 115) : Color.FromRgb(45, 51, 76)), null, new Rect(0, 0, 64, 64), 16, 16);
            var white = new Pen(Brushes.White, 3.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            d.DrawGeometry(null, white, Geometry.Parse("M 13,43 L 23,20 L 33,43 M 17,34 L 29,34"));
            d.DrawGeometry(null, new Pen(new SolidColorBrush(paused ? Color.FromRgb(206, 206, 206) : Color.FromRgb(187, 195, 241)), 3.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Geometry.Parse("M 38,24 L 48,24 L 48,35"));
            var mint = new Pen(new SolidColorBrush(paused ? Color.FromRgb(190, 190, 190) : Color.FromRgb(111, 221, 195)), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            d.DrawLine(mint, new Point(41, 40), new Point(47, 40));
            d.DrawLine(mint, new Point(41, 48), new Point(47, 48));
            d.DrawLine(mint, new Point(44, 40), new Point(44, 48));
        }
        drawing.Freeze(); return new(drawing);
    }
    public static BitmapSource Bitmap(int size, bool paused = false)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) context.DrawImage(Mark(paused), new Rect(0, 0, size, size));
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
    public static void Export(string folder)
    {
        Directory.CreateDirectory(folder);
        SavePng(Bitmap(256), Path.Combine(folder, "Typlow.png"));
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        byte[][] images = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++) { using var stream = new MemoryStream(); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Bitmap(sizes[i]))); encoder.Save(stream); images[i] = stream.ToArray(); }
        using var file = File.Create(Path.Combine(folder, "Typlow.ico")); using var writer = new BinaryWriter(file);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(images[i].Length); writer.Write(offset); offset += images[i].Length;
        }
        foreach (var bytes in images) writer.Write(bytes);
    }
    public static void SavePng(BitmapSource bitmap, string path)
    { using var stream = File.Create(path); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream); }
    public static System.Drawing.Icon TrayIcon(bool paused)
    {
        using var image = new MemoryStream(); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Bitmap(32, paused))); encoder.Save(image);
        using var ico = new MemoryStream(); using (var writer = new BinaryWriter(ico, System.Text.Encoding.UTF8, true))
        { writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1); writer.Write((byte)32); writer.Write((byte)32); writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write((int)image.Length); writer.Write(22); writer.Write(image.ToArray()); }
        ico.Position = 0; using var original = new System.Drawing.Icon(ico); return (System.Drawing.Icon)original.Clone();
    }
}
