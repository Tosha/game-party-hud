using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace GamePartyHud.Capture;

/// <summary>
/// Screen capture backed by GDI+ <c>Graphics.CopyFromScreen</c> (which calls Win32 BitBlt
/// under the hood). Works reliably on borderless-windowed games — the mode the app targets.
/// The process is PerMonitorV2 DPI-aware (see <c>app.manifest</c>), so X/Y and W/H in
/// <see cref="HpRegion"/> are interpreted as physical pixels on the virtual desktop.
///
/// NOTE: exclusive fullscreen DirectX games may defeat BitBlt (the captured region can
/// come back black). The app's design is borderless-windowed only; fullscreen capture
/// is out of scope for v0.1.0.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsScreenCapture : IScreenCapture, IDisposable
{
    public ValueTask<byte[]> CaptureBgraAsync(HpRegion region, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (region.W <= 0 || region.H <= 0)
            return ValueTask.FromResult(Array.Empty<byte>());

        // Format32bppArgb lays out bytes in memory as B, G, R, A on little-endian x86/x64.
        using var bmp = new Bitmap(region.W, region.H, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(region.X, region.Y, 0, 0,
                new Size(region.W, region.H), CopyPixelOperation.SourceCopy);
        }

        var rect = new Rectangle(0, 0, region.W, region.H);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            int rowBytes = region.W * 4;
            var buf = new byte[region.W * region.H * 4];
            // Copy row-by-row because Bitmap.Stride may include padding beyond rowBytes.
            IntPtr scan0 = data.Scan0;
            for (int y = 0; y < region.H; y++)
            {
                Marshal.Copy(scan0 + y * stride, buf, y * rowBytes, rowBytes);
            }
            // Force alpha = 255 (CopyFromScreen leaves it at 0 on some drivers).
            for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;
            return ValueTask.FromResult(buf);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public void Dispose() { /* no long-lived resources */ }
}
