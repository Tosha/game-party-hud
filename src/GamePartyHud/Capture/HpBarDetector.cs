using System;

namespace GamePartyHud.Capture;

/// <summary>
/// Finds the HP bar strip within a larger region that contains nickname text
/// (above) and the HP bar (below). Classifies each row as "coloured" (majority
/// saturated pixels) or not, then returns the <em>tallest</em> contiguous run of
/// coloured rows. Picking the tallest — not the first — avoids latching onto
/// thin decorative elements (frames, underlines, glow strips) that sit between
/// the nickname and the real HP bar.
/// </summary>
public static class HpBarDetector
{
    /// <summary>Minimum saturation to count a pixel as "coloured" for band detection.</summary>
    public const float SaturationThreshold = 0.4f;
    /// <summary>Minimum value (brightness) to count a pixel as "coloured" for band detection.</summary>
    public const float ValueThreshold = 0.3f;
    /// <summary>Minimum fraction of pixels in a row that must be coloured for the row to count as part of a bar.</summary>
    public const float RowHitFraction = 0.5f;
    /// <summary>Minimum consecutive rows required to qualify as a bar (filters 1–2 px noise like cursor underlines).</summary>
    public const int MinBarRows = 3;

    /// <summary>
    /// Find the tallest horizontal bar inside the given BGRA region.
    /// Returns (yStart, yEnd) inclusive, or null if no bar was detected.
    /// Ties (multiple runs of the same height) resolve to the topmost run,
    /// which preserves the common "HP is the top bar of the status block"
    /// convention.
    /// </summary>
    public static (int YStart, int YEnd)? FindTopBar(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        if (bgra.Length < width * height * 4) throw new ArgumentException("bgra too small", nameof(bgra));

        int bestStart = -1;
        int bestLen = 0;
        int currentStart = -1;
        int currentLen = 0;

        void ConsiderCurrent()
        {
            if (currentLen >= MinBarRows && currentLen > bestLen)
            {
                bestLen = currentLen;
                bestStart = currentStart;
            }
        }

        for (int y = 0; y < height; y++)
        {
            int hits = 0;
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                var hsv = Hsv.FromBgra(bgra[i], bgra[i + 1], bgra[i + 2]);
                if (hsv.S >= SaturationThreshold && hsv.V >= ValueThreshold) hits++;
            }
            bool rowColoured = hits >= width * RowHitFraction;

            if (rowColoured)
            {
                if (currentStart < 0) currentStart = y;
                currentLen++;
            }
            else
            {
                ConsiderCurrent();
                currentStart = -1;
                currentLen = 0;
            }
        }
        ConsiderCurrent();

        if (bestStart < 0) return null;
        return (bestStart, bestStart + bestLen - 1);
    }
}
