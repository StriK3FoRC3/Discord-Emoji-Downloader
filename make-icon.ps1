$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
public static class EmojiIconMaker
{
    static GraphicsPath Round(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath(); float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90); p.CloseFigure(); return p;
    }
    static Bitmap Draw(int size)
    {
        using (var large = new Bitmap(1024, 1024, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(large))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.ScaleTransform(4, 4);
                using (var tile = Round(8, 8, 240, 240, 54))
                using (var brush = new LinearGradientBrush(new Point(24, 8), new Point(232, 248), Color.FromArgb(143, 118, 255), Color.FromArgb(88, 75, 216))) g.FillPath(brush, tile);
                using (var face = new SolidBrush(Color.FromArgb(255, 212, 100))) g.FillEllipse(face, 38, 38, 160, 160);
                using (var ink = new SolidBrush(Color.FromArgb(39, 32, 70))) { g.FillEllipse(ink, 78, 91, 14, 20); g.FillEllipse(ink, 144, 91, 14, 20); }
                using (var smile = new Pen(Color.FromArgb(39, 32, 70), 10)) { smile.StartCap = LineCap.Round; smile.EndCap = LineCap.Round; g.DrawArc(smile, 82, 108, 72, 52, 15, 150); }
                using (var badge = Round(145, 145, 99, 99, 28))
                using (var fill = new SolidBrush(Color.FromArgb(23, 26, 42))) g.FillPath(fill, badge);
                using (var arrow = new Pen(Color.FromArgb(245, 246, 255), 10))
                {
                    arrow.StartCap = LineCap.Round; arrow.EndCap = LineCap.Round; arrow.LineJoin = LineJoin.Round;
                    g.DrawLine(arrow, 195, 164, 195, 200); g.DrawLines(arrow, new[] { new Point(179, 188), new Point(195, 204), new Point(211, 188) });
                    g.DrawLines(arrow, new[] { new Point(172, 211), new Point(172, 220), new Point(218, 220), new Point(218, 211) });
                }
            }
            var small = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.CompositingQuality = CompositingQuality.HighQuality; g.DrawImage(large, 0, 0, size, size); }
            return small;
        }
    }
    public static void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        var frames = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
            using (var image = Draw(sizes[i])) using (var stream = new MemoryStream())
            { image.Save(stream, ImageFormat.Png); frames[i] = stream.ToArray(); if (sizes[i] == 256) image.Save(Path.Combine(folder, "app.png"), ImageFormat.Png); }
        using (var file = File.Create(Path.Combine(folder, "app.ico"))) using (var writer = new BinaryWriter(file))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            { writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(frames[i].Length); writer.Write(offset); offset += frames[i].Length; }
            foreach (var frame in frames) writer.Write(frame);
        }
    }
}
'@
[EmojiIconMaker]::Save((Join-Path $PSScriptRoot 'assets'))
