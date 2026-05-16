using System;
using GamePartyHud.Capture;
using Xunit;
using Xunit.Abstractions;

namespace GamePartyHud.Tests.Capture;

/// <summary>
/// Diagnostic tests that inspect the real Mana-bar sample PNGs and print the
/// structure of their pixels. The Mana bar is dark blue on near-black (unlike
/// HP's bright red on light grey) and overlays a darker text-label box for the
/// "N/M" numbers — both make column classification more error-prone, so these
/// diagnostics exist to drive analyzer changes against real captures.
///
/// Run with: dotnet test --filter "FullyQualifiedName~ManaBarDiagnosticTests"
///                      --logger "console;verbosity=detailed"
/// </summary>
public class ManaBarDiagnosticTests
{
    private readonly ITestOutputHelper _out;

    public ManaBarDiagnosticTests(ITestOutputHelper output) { _out = output; }

    public static readonly (string File, float Expected)[] Samples =
    {
        ("MANA_BAR_7_PER_CENT.png",   0.07f),
        ("MANA_BAR_15_PER_CENT.png",  0.15f),
        ("MANA_BAR_23_PER_CENT.png",  0.23f),
        ("MANA_BAR_29_PER_CENT.png",  0.29f),
        ("MANA_BAR_39_PER_CENT.png",  0.39f),
        ("MANA_BAR_45_PER_CENT.png",  0.45f),
        ("MANA_BAR_53_PER_CENT.png",  0.53f),
        ("MANA_BAR_58_PER_CENT.png",  0.58f),
        ("MANA_BAR_71_PER_CENT.png",  0.71f),
        ("MANA_BAR_79_PER_CENT.png",  0.79f),
        ("MANA_BAR_88_PER_CENT.png",  0.88f),
        ("MANA_BAR_94_PER_CENT.png",  0.94f),
        ("MANA_BAR_100_PER_CENT.png", 1.00f),
    };

    [Fact]
    public void PrintPerColumnHsv_ForSeveralSamples()
    {
        foreach (var (file, expected) in new[] { Samples[0], Samples[3], Samples[6], Samples[9], Samples[12] })
        {
            var (bgra, w, h) = ImageLoader.Load(ImageLoader.ManaSamplePath(file));
            _out.WriteLine($"=== {file} (expected {expected:P0}) — {w}x{h}");
            for (int frac = 0; frac <= 20; frac++)
            {
                int col = Math.Min(w - 1, (frac * w) / 20);
                var avg = ColumnAverage(bgra, w, h, col);
                _out.WriteLine($"  col {col,3} ({frac * 5,3}%): H={avg.H,6:F1}°  S={avg.S:F3}  V={avg.V:F3}");
            }
        }
    }

    [Fact]
    public void CurrentAnalyzer_AgainstAllManaSamples_PrintsTable()
    {
        _out.WriteLine($"Threshold: FilledMinSaturation={BarAnalyzer.FilledMinSaturation:F2}");
        _out.WriteLine("");
        _out.WriteLine("file".PadRight(30) + "expected  actual   diff");

        float totalAbsDiff = 0;
        int n = 0;
        foreach (var (file, expected) in Samples)
        {
            var (bgra, w, h) = ImageLoader.Load(ImageLoader.ManaSamplePath(file));
            var cal = new BarCalibration(new CaptureRegion(0, 0, 0, w, h), FillDirection.LTR);
            var actual = new BarAnalyzer().Analyze(bgra, w, h, cal);
            float diff = actual - expected;
            totalAbsDiff += Math.Abs(diff);
            n++;
            _out.WriteLine($"{file.PadRight(30)}{expected:P0}     {actual:P0}     {diff:+0.00;-0.00}");
        }
        _out.WriteLine("");
        _out.WriteLine($"Mean absolute error: {(totalAbsDiff / n):P1}");
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
