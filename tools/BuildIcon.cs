using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class BuildIcon
{
    private static GraphicsPath RoundedRectangle(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        float diameter = radius * 2;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Bitmap Draw(int size)
    {
        using (var large = new Bitmap(size * 4, size * 4, PixelFormat.Format32bppArgb))
        {
            using (var graphics = Graphics.FromImage(large))
            using (var tile = RoundedRectangle(6, 6, 244, 244, 49))
            using (var screen = RoundedRectangle(36, 52, 184, 126, 16))
            using (var stand = RoundedRectangle(91, 201, 74, 11, 5))
            using (var body = new SolidBrush(Color.FromArgb(17, 26, 45)))
            using (var edge = new Pen(Color.FromArgb(181, 205, 255), 10))
            using (var blue = new SolidBrush(Color.FromArgb(114, 158, 255)))
            using (var moon = new SolidBrush(Color.FromArgb(255, 222, 143)))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.ScaleTransform(size * 4 / 256f, size * 4 / 256f);
                graphics.FillPath(body, tile);
                graphics.FillPath(body, screen);
                graphics.SetClip(screen);
                graphics.FillRectangle(blue, 36, 52, 92, 126);
                graphics.ResetClip();
                graphics.DrawPath(edge, screen);
                graphics.DrawLine(edge, 128, 57, 128, 173);
                graphics.FillRectangle(blue, 121, 181, 14, 22);
                graphics.FillPath(blue, stand);
                graphics.FillEllipse(moon, 149, 85, 47, 47);
                graphics.FillEllipse(body, 165, 74, 43, 43);
            }
            var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(result))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(large, new Rectangle(0, 0, size, size));
            }
            return result;
        }
    }

    private static void Main(string[] args)
    {
        string outputDirectory = args[0];
        Directory.CreateDirectory(outputDirectory);
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var frames = new List<byte[]>();
        foreach (int size in sizes)
        {
            using (var bitmap = Draw(size))
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                frames.Add(stream.ToArray());
                if (size == 256) bitmap.Save(Path.Combine(outputDirectory, "app.png"), ImageFormat.Png);
            }
        }
        using (var file = File.Create(Path.Combine(outputDirectory, "app.ico")))
        using (var writer = new BinaryWriter(file))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)sizes.Length);
            int offset = 6 + sizes.Length * 16;
            for (int i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(frames[i].Length);
                writer.Write(offset);
                offset += frames[i].Length;
            }
            foreach (byte[] frame in frames) writer.Write(frame);
        }
    }
}
