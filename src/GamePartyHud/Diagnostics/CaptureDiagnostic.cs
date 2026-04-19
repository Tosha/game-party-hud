using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using GamePartyHud.Capture;
using GamePartyHud.Config;

namespace GamePartyHud.Diagnostics;

/// <summary>
/// On-demand forensic snapshot of what the HP analyzer is seeing right now. Writes
/// two files side by side under <see cref="Log.LogDirectory"/>:
///
///   capture-{timestamp}.png       — raw BGRA pixels as PNG
///   capture-{timestamp}.png.txt   — full analysis report:
///       region coords, calibrated fullColor + tolerance + accept window,
///       analyzer HP result, per-column pass/partial/empty breakdown,
///       spatial 'match map' (one char per 10 columns), average HSV per
///       horizontal third, average HSV per row.
///
/// Triggered from the tray menu when a party is active; also auto-invoked by
/// <see cref="GamePartyHud.Party.PartyOrchestrator"/> when the raw HP reading
/// dips unexpectedly, so field bug reports include evidence captured at the
/// exact moment the bar showed wrong.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CaptureDiagnostic
{
    public static async Task<string?> RunAsync(AppConfig cfg, IScreenCapture capture, string reason = "manual")
    {
        if (cfg.HpCalibration is not { } cal)
        {
            Log.Warn($"CaptureDiagnostic ({reason}): no HP calibration in config — skipping.");
            return null;
        }

        byte[] bgra;
        try
        {
            bgra = await capture.CaptureBgraAsync(cal.Region).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"CaptureDiagnostic ({reason}): screen capture failed.", ex);
            return null;
        }

        int w = cal.Region.W;
        int h = cal.Region.H;
        float pct = new HpBarAnalyzer().Analyze(bgra, w, h, cal);

        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff");
        var pngPath = Path.Combine(Log.LogDirectory, $"capture-{stamp}.png");
        try { SaveBgraAsPng(bgra, w, h, pngPath); }
        catch (Exception ex) { Log.Error($"CaptureDiagnostic ({reason}): failed to write PNG.", ex); return null; }

        // Per-column match counts.
        var matchCount = new int[w];
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++)
            {
                int idx = (y * w + x) * 4;
                var hsv = Hsv.FromBgra(bgra[idx], bgra[idx + 1], bgra[idx + 2]);
                if (cal.Tolerance.Matches(cal.FullColor, hsv)) matchCount[x]++;
            }
        }
        int minMatches = Math.Max(2, h / 5);
        int colsPass = 0, colsPartial = 0, colsEmpty = 0;
        foreach (var c in matchCount)
        {
            if (c == 0) colsEmpty++;
            else if (c < minMatches) colsPartial++;
            else colsPass++;
        }

        // Compact 10-columns-per-char match map.
        const int bucketSize = 10;
        var map = new StringBuilder();
        for (int bStart = 0; bStart < w; bStart += bucketSize)
        {
            int bEnd = Math.Min(w, bStart + bucketSize);
            double avg = 0;
            for (int x = bStart; x < bEnd; x++) avg += matchCount[x];
            avg /= (bEnd - bStart);
            map.Append(avg >= minMatches ? '#' : avg > 0 ? '.' : ' ');
        }

        var leftAvg  = AverageHsv(bgra, w, h, 0,           w / 3);
        var midAvg   = AverageHsv(bgra, w, h, w / 3,       2 * w / 3);
        var rightAvg = AverageHsv(bgra, w, h, 2 * w / 3,   w);

        var perRow = new Hsv[h];
        for (int y = 0; y < h; y++) perRow[y] = AverageHsv(bgra, w, h, 0, w, rowStart: y, rowEnd: y + 1);

        var txtPath = pngPath + ".txt";
        try
        {
            using var sw = new StreamWriter(txtPath);
            sw.WriteLine($"Game Party HUD — capture diagnostic @ {stamp}  (reason={reason})");
            sw.WriteLine();
            sw.WriteLine($"Region:        {w}x{h} at screen ({cal.Region.X},{cal.Region.Y})");
            sw.WriteLine($"Calibrated:    H={cal.FullColor.H:F1}°, S={cal.FullColor.S:F3}, V={cal.FullColor.V:F3}");
            sw.WriteLine($"Tolerance:     ±{cal.Tolerance.H}° hue, ±{cal.Tolerance.S:F2} sat, ±{cal.Tolerance.V:F2} val");
            sw.WriteLine($"               accept H: [{(cal.FullColor.H - cal.Tolerance.H + 360f) % 360f:F0}°, {(cal.FullColor.H + cal.Tolerance.H) % 360f:F0}°]  " +
                         $"S: [{Math.Max(0, cal.FullColor.S - cal.Tolerance.S):F2}, {Math.Min(1, cal.FullColor.S + cal.Tolerance.S):F2}]  " +
                         $"V: [{Math.Max(0, cal.FullColor.V - cal.Tolerance.V):F2}, {Math.Min(1, cal.FullColor.V + cal.Tolerance.V):F2}]");
            sw.WriteLine($"Fill dir:      {cal.Direction}");
            sw.WriteLine();
            sw.WriteLine($"Analyzer HP:   {pct:F3}   (1.000 = full in-game HP)");
            sw.WriteLine($"Match thresh:  >= {minMatches} of {h} rows per column");
            sw.WriteLine($"Per-column:    {colsPass,3} pass  /  {colsPartial,3} partial  /  {colsEmpty,3} empty  (total {w})");
            sw.WriteLine();
            sw.WriteLine("Match map (each char = " + bucketSize + " columns; '#' fill, '.' partial, ' ' empty):");
            sw.WriteLine("  [" + map + "]");
            sw.WriteLine();
            sw.WriteLine("Average HSV by horizontal third:");
            sw.WriteLine($"  Left   third:  H={leftAvg.H,6:F1}°  S={leftAvg.S:F3}  V={leftAvg.V:F3}");
            sw.WriteLine($"  Middle third:  H={midAvg.H,6:F1}°  S={midAvg.S:F3}  V={midAvg.V:F3}");
            sw.WriteLine($"  Right  third:  H={rightAvg.H,6:F1}°  S={rightAvg.S:F3}  V={rightAvg.V:F3}");
            sw.WriteLine();
            sw.WriteLine("Average HSV per row (row 0 = top of bar):");
            for (int y = 0; y < h; y++)
                sw.WriteLine($"  row {y,2}:  H={perRow[y].H,6:F1}°  S={perRow[y].S:F3}  V={perRow[y].V:F3}");
        }
        catch (Exception ex)
        {
            Log.Error($"CaptureDiagnostic ({reason}): failed to write .txt report.", ex);
        }

        Log.Info($"CaptureDiagnostic ({reason}): HP={pct:F3} pass/partial/empty={colsPass}/{colsPartial}/{colsEmpty} of {w}; map=[{map}]; saved {Path.GetFileName(pngPath)}");
        return pngPath;
    }

    public static Hsv AverageHsv(byte[] bgra, int w, int h, int colStart, int colEnd,
                                  int rowStart = 0, int rowEnd = int.MaxValue)
    {
        rowEnd = Math.Min(rowEnd, h);
        double sr = 0, sg = 0, sb = 0;
        int n = 0;
        for (int y = rowStart; y < rowEnd; y++)
        {
            for (int x = colStart; x < colEnd; x++)
            {
                int i = (y * w + x) * 4;
                sb += bgra[i];
                sg += bgra[i + 1];
                sr += bgra[i + 2];
                n++;
            }
        }
        if (n == 0) return new Hsv(0, 0, 0);
        return Hsv.FromBgra((byte)(sb / n), (byte)(sg / n), (byte)(sr / n));
    }

    private static void SaveBgraAsPng(byte[] bgra, int width, int height, string path)
    {
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
