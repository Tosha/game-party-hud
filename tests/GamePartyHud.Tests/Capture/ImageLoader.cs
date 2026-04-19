using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GamePartyHud.Tests.Capture;

/// <summary>
/// Loads a PNG file into the contiguous BGRA byte layout that
/// <see cref="GamePartyHud.Capture.HpBarAnalyzer.Analyze"/> expects:
/// stride = width * 4, alpha forced to 255.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ImageLoader
{
    public static (byte[] Bgra, int Width, int Height) Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Sample image not found", path);

        using var bmp = new Bitmap(path);
        int w = bmp.Width;
        int h = bmp.Height;

        using var converted = bmp.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format32bppArgb);
        var data = converted.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            int rowBytes = w * 4;
            var buf = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + y * stride, buf, y * rowBytes, rowBytes);
            }
            for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;
            return (buf, w, h);
        }
        finally
        {
            converted.UnlockBits(data);
        }
    }

    public static string SamplePath(string filename) =>
        Path.Combine(AppContext.BaseDirectory, "Capture", "HpBarExamples", filename);
}
