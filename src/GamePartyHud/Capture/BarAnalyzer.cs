using System;

namespace GamePartyHud.Capture;

/// <summary>
/// Reads a bar's fill percentage from a captured BGRA bitmap by classifying each
/// column as "filled" or "missing" and finding the transition from the anchor side.
/// Current fraction = 1 - (missing columns / total columns).
///
/// A column is classified as "filled" if it contains a contiguous vertical run
/// of saturated pixels (the bar's coloured fill) at least
/// <see cref="StableRun"/>+1 rows tall; otherwise it is "missing". This is
/// robust to:
///   - white text overlays anywhere inside the bar (text is desaturated and
///     never produces a long saturated run),
///   - dark text-label backgrounds that overlay either side of the bar (e.g.
///     the mana bar renders "138/260" centred over a darker box that
///     interrupts both the empty grey and the filled blue underneath),
///   - the dark frame border above/below the bar (a few rows of S≈0),
///   - users picking a region 1–2 px outside the bar,
///   - subtle vertical gradient inside both the filled and the empty regions,
/// and naturally extends to non-red bars (stamina, mana) without per-bar
/// tuning.
/// </summary>
public sealed class BarAnalyzer
{
    // Number of consecutive same-state columns required to declare a stable
    // initial state and, separately, to detect a transition. A run of 2 is
    // sufficient to absorb the 1-px anti-alias edge that separates the bar's
    // filled portion from its empty tail.
    private const int StableRun = 2;

    /// <summary>
    /// Minimum saturation for a pixel to count as part of the bar's coloured
    /// fill. Set deliberately low: even the darkest mana-bar pixels have
    /// S ≈ 0.4–0.6, while desaturated greys (empty bar, anti-alias edges of
    /// white text overlays, dark text-label backgrounds) are all under 0.05.
    /// A 0.10 threshold cleanly separates the two populations.
    /// </summary>
    public const float FilledMinSaturation = 0.10f;

    /// <summary>True if <paramref name="hsv"/> looks like a pixel from the bar's
    /// coloured fill — saturated enough to not be confused with text overlay
    /// anti-alias, dark frame, or empty-bar grey.</summary>
    public static bool IsFilledPixel(Hsv hsv) => hsv.S >= FilledMinSaturation;

    public float Analyze(ReadOnlySpan<byte> bgra, int width, int height, BarCalibration cal)
    {
        if (width <= 0 || height <= 0) return 0f;
        if (bgra.Length < width * height * 4) throw new ArgumentException("bgra too small", nameof(bgra));

        // A column is classified as "filled" if its longest contiguous run of
        // saturated pixels is at least ~1/5 of the bar's height. This is the
        // only reliable signature of an actual bar fill — text overlays (on
        // either filled or empty side) never produce a long run of saturated
        // pixels, while a real bar fill always does (typically the full bar
        // height with maybe a few rows of text interruption).
        int sampleRows = height;
        int minFilledRun = Math.Max(StableRun, sampleRows / 5);

        bool ltr = cal.Direction == FillDirection.LTR;

        var colMissing = new bool[width];
        int missingCount = 0;
        for (int i = 0; i < width; i++)
        {
            int col = ltr ? i : (width - 1 - i);
            int currentRun = 0;
            int longestRun = 0;
            for (int y = 0; y < height; y++)
            {
                int idx = (y * width + col) * 4;
                var hsv = Hsv.FromBgra(bgra[idx], bgra[idx + 1], bgra[idx + 2]);
                if (IsFilledPixel(hsv))
                {
                    currentRun++;
                    if (currentRun > longestRun) longestRun = currentRun;
                }
                else
                {
                    currentRun = 0;
                }
            }
            colMissing[i] = longestRun < minFilledRun;
            if (colMissing[i]) missingCount++;
        }

        // Fast path: full bar (no missing columns at all).
        if (missingCount == 0) return 1f;
        // Fast path: empty bar (every column is missing).
        if (missingCount == width) return 0f;

        // Pass 2: establish the "stable initial state" — the first run of StableRun
        // consecutive columns with the same missing/not-missing classification,
        // starting from the anchor side. Resilient to 1–2px anti-alias noise.
        int stableStart = -1;
        bool stableMissingState = false;
        for (int i = 0; i + StableRun - 1 < width; i++)
        {
            bool allSame = true;
            for (int k = 1; k < StableRun && allSame; k++)
                if (colMissing[i + k] != colMissing[i]) allSame = false;
            if (allSame)
            {
                stableStart = i;
                stableMissingState = colMissing[i];
                break;
            }
        }
        if (stableStart == -1) return 0f;

        // Pass 3: from the stable initial state, scan for StableRun consecutive
        // columns of the opposite state. The first column of that run is the
        // transition. filledFraction = transition / width (axis already flipped
        // for RTL so the anchor side is at i=0).
        int runOpposite = 0;
        int transition = width;
        for (int i = stableStart; i < width; i++)
        {
            if (colMissing[i] != stableMissingState)
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
