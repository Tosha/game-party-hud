using System;

namespace GamePartyHud.Capture;

/// <summary>
/// Reads a bar's fill percentage from a captured BGRA bitmap by classifying each
/// column as "missing" (the bar's empty/grey background) or "not missing" (the
/// bar's filled portion or any overlay text), then finding the transition from
/// the anchor side. Current fraction = 1 - (missing columns / total columns).
///
/// The pixel classifier is colour-agnostic: it identifies any sufficiently
/// desaturated, mid-value pixel as part of the bar's empty region, regardless
/// of what colour the filled portion would be. This is robust to:
///   - text overlays in the middle of the bar (near-white, excluded by the V upper bound),
///   - the dark frame border above/below the bar (pure black, excluded by the V lower bound),
///   - users picking a region 1–2 px outside the bar,
///   - subtle vertical gradient inside both the filled and the empty regions,
/// and naturally extends to non-red bars (stamina, mana) without per-bar tuning.
/// </summary>
public sealed class BarAnalyzer
{
    // Number of consecutive same-state columns required to declare a stable
    // initial state and, separately, to detect a transition. With the tight S
    // threshold above, the per-column classifier is no longer fooled by text
    // anti-alias rows, so cross-column noise is rare. A run of 2 is sufficient
    // to absorb the 1-px anti-alias edge that separates the bar's filled
    // portion from its empty tail.
    private const int StableRun = 2;

    /// <summary>
    /// Maximum saturation for a pixel to count as part of the bar's empty/missing region.
    /// Set deliberately low: real captures of the empty bar (and its light-grey
    /// gradient end-cap) show S values of 0.000–0.04 — they're true neutrals.
    /// Anti-alias pixels along in-bar text glyph edges blend the white text onto
    /// the saturated red bar fill, so they retain a small but nonzero S
    /// (typically 0.08–0.20). A 0.05 threshold cleanly separates these
    /// populations and stops the analyzer from misclassifying text-edge rows
    /// as "missing" inside the filled portion of the bar.
    /// </summary>
    public const float MissingMaxSaturation = 0.05f;

    /// <summary>Minimum value for a pixel to count as missing — excludes the pure-black frame border.</summary>
    public const float MissingMinValue = 0.05f;

    /// <summary>Maximum value for a pixel to count as missing — excludes near-white text glyphs that overlay the bar.</summary>
    public const float MissingMaxValue = 0.70f;

    /// <summary>
    /// True if <paramref name="hsv"/> looks like a pixel from the empty/missing portion of the
    /// bar — desaturated grey within a bounded value range. Excludes the bar's frame border
    /// (pure black, V&lt;<see cref="MissingMinValue"/>) and any overlay text glyphs (near-white,
    /// V&gt;<see cref="MissingMaxValue"/>) so they are classified as "neither filled nor missing"
    /// rather than falsely counted as missing.
    /// </summary>
    public static bool IsMissingPixel(Hsv hsv)
    {
        return hsv.S <= MissingMaxSaturation
            && hsv.V >= MissingMinValue
            && hsv.V <= MissingMaxValue;
    }

    public float Analyze(ReadOnlySpan<byte> bgra, int width, int height, HpCalibration cal)
    {
        if (width <= 0 || height <= 0) return 0f;
        if (bgra.Length < width * height * 4) throw new ArgumentException("bgra too small", nameof(bgra));

        // A column is classified as "missing" if at least ~20% of its rows are
        // missing pixels. The 20% threshold tolerates white text glyphs in the
        // middle and the frame at top/bottom while still firmly rejecting stray
        // single-row noise.
        int sampleRows = height;
        int minMatches = Math.Max(2, sampleRows / 5);

        bool ltr = cal.Direction == FillDirection.LTR;

        var colMissing = new bool[width];
        int missingCount = 0;
        for (int i = 0; i < width; i++)
        {
            int col = ltr ? i : (width - 1 - i);
            int matches = 0;
            for (int y = 0; y < height; y++)
            {
                int idx = (y * width + col) * 4;
                var hsv = Hsv.FromBgra(bgra[idx], bgra[idx + 1], bgra[idx + 2]);
                if (IsMissingPixel(hsv)) matches++;
            }
            colMissing[i] = matches >= minMatches;
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
