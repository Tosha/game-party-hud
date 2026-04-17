using System;

namespace GamePartyHud.Capture;

/// <summary>
/// Finds the HP bar strip within a larger region that contains nickname text
/// (above) and the HP bar (below). Works by detecting the first contiguous
/// run of rows where a majority of pixels are saturated — HP bars are usually
/// filled with a saturated color (red/green/blue) while name text on a dim
/// background is either unsaturated white or low-value.
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
    /// Find the top-most horizontal bar inside the given BGRA region.
    /// Returns (yStart, yEnd) inclusive, or null if no bar was detected.
    /// </summary>
    public static (int YStart, int YEnd)? FindTopBar(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        if (bgra.Length < width * height * 4) throw new ArgumentException("bgra too small", nameof(bgra));

        int currentStart = -1;
        int currentLen = 0;

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
                if (currentLen >= MinBarRows)
                {
                    return (currentStart, currentStart + currentLen - 1);
                }
                currentStart = -1;
                currentLen = 0;
            }
        }

        if (currentLen >= MinBarRows)
        {
            return (currentStart, currentStart + currentLen - 1);
        }
        return null;
    }
}
