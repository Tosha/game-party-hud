using System;

namespace GamePartyHud.Capture;

public readonly record struct Hsv(float H, float S, float V)
{
    /// <summary>Convert 8-bit BGRA to HSV. H is in degrees [0, 360), S and V in [0, 1].</summary>
    public static Hsv FromBgra(byte b, byte g, byte r)
    {
        float rf = r / 255f, gf = g / 255f, bf = b / 255f;
        float max = MathF.Max(rf, MathF.Max(gf, bf));
        float min = MathF.Min(rf, MathF.Min(gf, bf));
        float v = max;
        float delta = max - min;
        float s = max == 0f ? 0f : delta / max;
        float h;
        if (delta == 0f) h = 0f;
        else if (max == rf) h = 60f * (((gf - bf) / delta) % 6f);
        else if (max == gf) h = 60f * (((bf - rf) / delta) + 2f);
        else h = 60f * (((rf - gf) / delta) + 4f);
        if (h < 0f) h += 360f;
        return new Hsv(h, s, v);
    }
}
