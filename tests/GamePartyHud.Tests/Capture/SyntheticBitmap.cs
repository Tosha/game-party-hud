using System;

namespace GamePartyHud.Tests.Capture;

/// <summary>
/// Builds a flat-color horizontal bar: left <fillRatio> fraction is <fillBgr>,
/// right remainder is <emptyBgr>. Stride = width * 4. Alpha = 255 throughout.
/// </summary>
internal static class SyntheticBitmap
{
    public static byte[] HorizontalBar(int width, int height, float fillRatio,
        (byte b, byte g, byte r) fillBgr, (byte b, byte g, byte r) emptyBgr)
    {
        if (fillRatio < 0f) fillRatio = 0f;
        if (fillRatio > 1f) fillRatio = 1f;
        var buf = new byte[width * height * 4];
        int split = (int)MathF.Round(width * fillRatio);
        for (int y = 0; y < height; y++)
        {
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                var c = x < split ? fillBgr : emptyBgr;
                int i = row + x * 4;
                buf[i + 0] = c.b;
                buf[i + 1] = c.g;
                buf[i + 2] = c.r;
                buf[i + 3] = 255;
            }
        }
        return buf;
    }
}
