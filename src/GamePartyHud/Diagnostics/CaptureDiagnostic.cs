using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using GamePartyHud.Capture;
using GamePartyHud.Config;

namespace GamePartyHud.Diagnostics;

/// <summary>
/// One-off diagnostic: captures the currently-calibrated HP region and saves a
/// PNG + a text summary so a user can visually check what the app is "seeing"
/// and compare against what's on screen. Triggered from the tray menu.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CaptureDiagnostic
{
    public static async Task<string> RunAsync(AppConfig cfg, IScreenCapture capture)
    {
        if (cfg.HpCalibration is not { } cal)
        {
            Log.Warn("CaptureDiagnostic: no HP calibration in config — run the calibration wizard first.");
            return "No calibration yet — run Calibrate character first.";
        }

        var bgra = await capture.CaptureBgraAsync(cal.Region).ConfigureAwait(false);
        var pct  = new HpBarAnalyzer().Analyze(bgra, cal.Region.W, cal.Region.H, cal);

        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var pngPath = Path.Combine(Log.LogDirectory, $"capture-{stamp}.png");
        SaveBgraAsPng(bgra, cal.Region.W, cal.Region.H, pngPath);

        var summary =
            $"Saved {pngPath}. " +
            $"Region {cal.Region.W}x{cal.Region.H} @({cal.Region.X},{cal.Region.Y}), " +
            $"analyzer HP={pct:F3} (expected ~1.0 when HP is full in-game). " +
            $"Calibrated full-HP HSV=({cal.FullColor.H:F0}, {cal.FullColor.S:F2}, {cal.FullColor.V:F2}), " +
            $"tolerance (h={cal.Tolerance.H}, s={cal.Tolerance.S}, v={cal.Tolerance.V}).";

        Log.Info("CaptureDiagnostic: " + summary);
        return summary;
    }

    private static void SaveBgraAsPng(byte[] bgra, int width, int height, string path)
    {
        // Copy the BGRA buffer into a System.Drawing Bitmap with matching stride.
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = width * 4;
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(bgra, y * rowBytes, data.Scan0 + y * data.Stride, rowBytes);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        bmp.Save(path, ImageFormat.Png);
    }
}
