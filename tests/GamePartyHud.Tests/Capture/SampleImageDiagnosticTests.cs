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
        ("HP_BAR_4_PER_CENT.png",   0.04f),
        ("HP_BAR_6_PER_CENT.png",   0.06f),
        ("HP_BAR_13_PER_CENT.png",  0.13f),
        ("HP_BAR_22_PER_CENT.png",  0.22f),
        ("HP_BAR_28_PER_CENT.png",  0.28f),
        ("HP_BAR_34_PER_CENT.png",  0.34f),
        ("HP_BAR_43_PER_CENT.png",  0.43f),
        ("HP_BAR_53_PER_CENT.png",  0.53f),
        ("HP_BAR_63_PER_CENT.png",  0.63f),
        ("HP_BAR_75_PER_CENT.png",  0.75f),
        ("HP_BAR_89_PER_CENT.png",  0.89f),
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
    public void CurrentAnalyzer_AgainstAllSamples_ShowsError()
    {
        // Calibrate using the 100% sample, top+bottom band averaging (matches wizard).
        var (fullBgra, fullW, fullH) = ImageLoader.Load(ImageLoader.SamplePath("HP_BAR_100_PER_CENT.png"));
        var fullColor = SampleFullColor(fullBgra, fullW, fullH);
        var cal = new HpCalibration(
            new HpRegion(0, 0, 0, fullW, fullH),
            fullColor,
            HsvTolerance.Default,
            FillDirection.LTR);

        _out.WriteLine($"Calibrated fullColor HSV=(H={fullColor.H:F1}°, S={fullColor.S:F3}, V={fullColor.V:F3})");
        _out.WriteLine($"Tolerance ±H={cal.Tolerance.H}° ±S={cal.Tolerance.S:F2} ±V={cal.Tolerance.V:F2}");
        _out.WriteLine($"Accept window: H∈[{(fullColor.H - cal.Tolerance.H + 360f) % 360f:F0}°,{(fullColor.H + cal.Tolerance.H) % 360f:F0}°] S∈[{Math.Max(0, fullColor.S - cal.Tolerance.S):F2},{Math.Min(1, fullColor.S + cal.Tolerance.S):F2}] V∈[{Math.Max(0, fullColor.V - cal.Tolerance.V):F2},{Math.Min(1, fullColor.V + cal.Tolerance.V):F2}]");
        _out.WriteLine("");
        _out.WriteLine("file".PadRight(30) + "expected  actual   diff");

        float totalAbsDiff = 0;
        int n = 0;
        foreach (var (file, expected) in Samples)
        {
            var (bgra, w, h) = ImageLoader.Load(ImageLoader.SamplePath(file));
            var actual = new HpBarAnalyzer().Analyze(bgra, w, h, cal);
            float diff = actual - expected;
            totalAbsDiff += Math.Abs(diff);
            n++;
            _out.WriteLine($"{file.PadRight(30)}{expected:P0}     {actual:P0}     {diff:+0.00;-0.00}");
        }
        _out.WriteLine("");
        _out.WriteLine($"Mean absolute error: {(totalAbsDiff / n):P1}");
    }

    // Mirrors CalibrationWizard.SampleFullColor.
    private static Hsv SampleFullColor(byte[] bgra, int w, int h)
    {
        int band = Math.Max(1, h / 5);
        double sr = 0, sg = 0, sb = 0;
        int n = 0;
        void AddRow(int y)
        {
            int x0 = w / 4;
            int x1 = w * 3 / 4;
            for (int x = x0; x < x1; x++)
            {
                int i = (y * w + x) * 4;
                sb += bgra[i]; sg += bgra[i + 1]; sr += bgra[i + 2];
                n++;
            }
        }
        for (int y = 0; y < Math.Min(band, h); y++) AddRow(y);
        for (int y = Math.Max(0, h - band); y < h; y++) AddRow(y);
        return n == 0 ? new Hsv(0, 0, 0) : Hsv.FromBgra(
            (byte)Math.Clamp(sb / n, 0, 255),
            (byte)Math.Clamp(sg / n, 0, 255),
            (byte)Math.Clamp(sr / n, 0, 255));
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
