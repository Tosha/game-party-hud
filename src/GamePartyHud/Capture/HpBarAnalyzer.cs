using System;

namespace GamePartyHud.Capture;

/// <summary>
/// Reads the HP bar fill percentage from a captured BGRA bitmap.
/// Classifies each column of the middle 3-row band as "matches full-HP color" or not,
/// then finds the first stable run (3+ consecutive) and the first opposite stable run.
/// The position of the opposite run is reported as the fill fraction.
/// </summary>
public sealed class HpBarAnalyzer
{
    private const int StableRun = 3;

    public float Analyze(ReadOnlySpan<byte> bgra, int width, int height, HpCalibration cal)
    {
        if (width <= 0 || height <= 0) return 0f;
        if (bgra.Length < width * height * 4) throw new ArgumentException("bgra too small", nameof(bgra));

        int centerY = height / 2;
        int y0 = Math.Max(0, centerY - 1);
        int y1 = Math.Min(height - 1, centerY + 1);
        bool ltr = cal.Direction == FillDirection.LTR;

        // Pass 1: per-column match vote across the middle band.
        // index i runs along the bar from the "anchor" side outward.
        var colMatch = new bool[width];
        bool anyMatch = false;
        for (int i = 0; i < width; i++)
        {
            int col = ltr ? i : (width - 1 - i);
            int matches = 0;
            int total = 0;
            for (int y = y0; y <= y1; y++)
            {
                int idx = (y * width + col) * 4;
                var hsv = Hsv.FromBgra(bgra[idx], bgra[idx + 1], bgra[idx + 2]);
                if (cal.Tolerance.Matches(cal.FullColor, hsv)) matches++;
                total++;
            }
            colMatch[i] = matches * 2 > total;   // > 50% of band rows must match
            anyMatch |= colMatch[i];
        }

        if (!anyMatch) return 0f;

        // Pass 2: establish the "stable initial state" — the first run of StableRun
        // consecutive columns with the same match value. This is resilient to 1–2px
        // anti-alias noise at the anchor edge.
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
        // of the opposite state. The transition position (in units of columns from the
        // anchor) divided by width is the fill fraction.
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
