using System;
using System.Linq;
using GamePartyHud.Capture;
using Xunit;
using Xunit.Abstractions;

namespace GamePartyHud.Tests.Capture;

/// <summary>
/// Diagnostic tests that inspect the real HP-bar sample PNGs and print the structure
/// of their pixels. These aren't regression guards on their own — they're there to
/// reveal how the bar is actually rendered so we can design an analyzer that's robust
/// against the real shading / text / edge behaviour.
///
/// Run with: dotnet test --filter "FullyQualifiedName~SampleImageDiagnosticTests"
///                      --logger "console;verbosity=detailed"
/// </summary>
public class SampleImageDiagnosticTests
{
    private readonly ITestOutputHelper _out;

    public SampleImageDiagnosticTests(ITestOutputHelper output) { _out = output; }

    public static readonly (string File, float Expected)[] Samples =
    {
        ("HP_BAR_5_PER_CENT.png",   0.05f),
        ("HP_BAR_11_PER_CENT.png",  0.11f),
        ("HP_BAR_18_PER_CENT.png",  0.18f),
        ("HP_BAR_26_PER_CENT.png",  0.26f),
        ("HP_BAR_33_PER_CENT.png",  0.33f),
        ("HP_BAR_42_PER_CENT.png",  0.42f),
        ("HP_BAR_50_PER_CENT.png",  0.50f),
        ("HP_BAR_62_PER_CENT.png",  0.62f),
        ("HP_BAR_73_PER_CENT.png",  0.73f),
        ("HP_BAR_84_PER_CENT.png",  0.84f),
        ("HP_BAR_90_PER_CENT.png",  0.90f),
        ("HP_BAR_100_PER_CENT.png", 1.00f),
    };

    [Fact]
    public void PrintDimensionsAndPerRowAverageHsv()
    {
        var (bgra, w, h) = ImageLoader.Load(ImageLoader.SamplePath("HP_BAR_100_PER_CENT.png"));
        _out.WriteLine($"100% sample: {w}x{h} px");
        for (int y = 0; y < h; y++)
        {
            var avg = RowAverage(bgra, w, y);
            _out.WriteLine($"  row {y,2}: H={avg.H,6:F1}°  S={avg.S:F3}  V={avg.V:F3}");
        }
    }

    [Fact]
    public void PrintPerColumnHsvForSeveralSamples()
    {
        foreach (var (file, expected) in new[] { Samples[0], Samples[3], Samples[6], Samples[9], Samples[11] })
        {
            var (bgra, w, h) = ImageLoader.Load(ImageLoader.SamplePath(file));
            _out.WriteLine($"=== {file} (expected {expected:P0}) — {w}x{h}");
            // Sample every ~10% of columns so the output stays readable
            for (int frac = 0; frac <= 10; frac++)
            {
                int col = Math.Min(w - 1, (frac * w) / 10);
                var avg = ColumnAverage(bgra, w, h, col);
                _out.WriteLine($"  col {col,3} ({frac * 10,3}%): H={avg.H,6:F1}°  S={avg.S:F3}  V={avg.V:F3}");
            }
        }
    }

    [Fact]
    public void CurrentAnalyzer_AgainstAllSamples_PrintsTable()
    {
        _out.WriteLine($"Threshold: FilledMinSaturation={BarAnalyzer.FilledMinSaturation:F2}");
        _out.WriteLine("");
        _out.WriteLine("file".PadRight(30) + "expected  actual   diff");

        float totalAbsDiff = 0;
        int n = 0;
        foreach (var (file, expected) in Samples)
        {
            var (bgra, w, h) = ImageLoader.Load(ImageLoader.SamplePath(file));
            var cal = new BarCalibration(new CaptureRegion(0, 0, w, h), FillDirection.LTR);
            var actual = new BarAnalyzer().Analyze(bgra, w, h, cal);
            float diff = actual - expected;
            totalAbsDiff += Math.Abs(diff);
            n++;
            _out.WriteLine($"{file.PadRight(30)}{expected:P0}     {actual:P0}     {diff:+0.00;-0.00}");
        }
        _out.WriteLine("");
        _out.WriteLine($"Mean absolute error: {(totalAbsDiff / n):P1}");
    }

    private static Hsv RowAverage(byte[] bgra, int w, int y)
    {
        double sr = 0, sg = 0, sb = 0; int n = 0;
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            sb += bgra[i]; sg += bgra[i + 1]; sr += bgra[i + 2]; n++;
        }
        return Hsv.FromBgra((byte)(sb / n), (byte)(sg / n), (byte)(sr / n));
    }

    private static Hsv ColumnAverage(byte[] bgra, int w, int h, int col)
    {
        double sr = 0, sg = 0, sb = 0; int n = 0;
        for (int y = 0; y < h; y++)
        {
            int i = (y * w + col) * 4;
            sb += bgra[i]; sg += bgra[i + 1]; sr += bgra[i + 2]; n++;
        }
        return Hsv.FromBgra((byte)(sb / n), (byte)(sg / n), (byte)(sr / n));
    }
}
