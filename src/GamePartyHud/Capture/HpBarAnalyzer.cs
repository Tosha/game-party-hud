using System;

namespace GamePartyHud.Capture;

/// <summary>
/// Reads the HP bar fill percentage from a captured BGRA bitmap by classifying each
/// column as "filled" or "empty", then finding the fill → empty transition.
///
/// The pixel classifier is deliberately <em>calibration-free</em>: it identifies any
/// sufficiently saturated red pixel as part of the HP bar's filled region, regardless
/// of the exact brightness. This is robust to:
///   - users over-selecting the HP region and including dark frame rows above/below
///     (previously this polluted the calibrated "full colour" with near-black pixels
///     and caused the analyzer to match the frame instead of the bar);
///   - post-processing / HDR / colour grading in the game shifting brightness;
///   - the subtle vertical gradient inside the bar itself.
///
/// The <see cref="HpCalibration.FullColor"/> and <see cref="HsvTolerance"/> fields
/// are kept in the data model but no longer consulted here — they remain in place so
/// a future release can add non-red HP bar support without another config migration.
/// </summary>
public sealed class HpBarAnalyzer
{
    private const int StableRun = 3;

    /// <summary>Minimum saturation for a pixel to count as part of a filled HP bar.</summary>
    public const float FilledMinSaturation = 0.40f;

    /// <summary>Minimum value (brightness) for a pixel to count as part of a filled HP bar.
    /// Excludes dim red "shadow" pixels at the fill edge that are still technically red-hued
    /// but belong to the empty-bar gradient, not the lit HP fill.</summary>
    public const float FilledMinValue = 0.30f;

    /// <summary>Half-width of the hue window around red (0°) for a pixel to count as filled.</summary>
    public const float FilledHueHalfWindow = 30f;

    /// <summary>
    /// True if <paramref name="hsv"/> looks like a pixel from the filled portion of a red
    /// HP bar: saturated, bright, and red-hued. Desaturated pixels (dark frame, white text,
    /// empty bar background), dim red shadow pixels at the fill boundary, and non-red hues
    /// all return false.
    /// </summary>
    public static bool IsFilledPixel(Hsv hsv)
    {
        if (hsv.S < FilledMinSaturation) return false;
        if (hsv.V < FilledMinValue) return false;
        float h = ((hsv.H % 360f) + 360f) % 360f;
        float distanceToZero = h > 180f ? 360f - h : h;
        return distanceToZero <= FilledHueHalfWindow;
    }

    public float Analyze(ReadOnlySpan<byte> bgra, int width, int height, HpCalibration cal)
    {
        if (width <= 0 || height <= 0) return 0f;
        if (bgra.Length < width * height * 4) throw new ArgumentException("bgra too small", nameof(bgra));

        // Sample the full bar height and classify each column as "filled" if at least
        // ~20% of its rows are saturated-red pixels. 20% tolerates text overlays in the
        // middle and frame borders at the top/bottom while still firmly rejecting stray
        // single-pixel noise.
        int sampleRows = height;
        int minMatches = Math.Max(2, sampleRows / 5);

        bool ltr = cal.Direction == FillDirection.LTR;

        var colMatch = new bool[width];
        bool anyMatch = false;
        for (int i = 0; i < width; i++)
        {
            int col = ltr ? i : (width - 1 - i);
            int matches = 0;
            for (int y = 0; y < height; y++)
            {
                int idx = (y * width + col) * 4;
                var hsv = Hsv.FromBgra(bgra[idx], bgra[idx + 1], bgra[idx + 2]);
                if (IsFilledPixel(hsv)) matches++;
            }
            colMatch[i] = matches >= minMatches;
            anyMatch |= colMatch[i];
        }

        if (!anyMatch) return 0f;

        // Pass 2: establish the "stable initial state" — the first run of StableRun
        // consecutive columns with the same match value. Resilient to 1–2px anti-alias
        // noise at the anchor edge.
        int stableStart = -1;
        bool stableMatchState = false;
        for (int i = 0; i + StableRun - 1 < width; i++)
        {
            bool allSame = true;
            for (int k = 1; k < StableRun && allSame; k++)
                if (colMatch[i + k] != colMatch[i]) allSame = false;
            if (allSame)
            {
                stableStart = i;
                stableMatchState = colMatch[i];
                break;
            }
        }
        if (stableStart == -1) return 0f;

        // Pass 3: from the stable initial state, scan for StableRun consecutive columns
        // of the opposite state. The transition position (columns from the anchor)
        // divided by width is the fill fraction.
        int runOpposite = 0;
        int transition = width;
        for (int i = stableStart; i < width; i++)
        {
            if (colMatch[i] != stableMatchState)
            {
                runOpposite++;
                if (runOpposite >= StableRun)
                {
                    transition = i - runOpposite + 1;
                    break;
                }
            }
            else
            {
                runOpposite = 0;
            }
        }

        return Math.Clamp((float)transition / width, 0f, 1f);
    }
}
