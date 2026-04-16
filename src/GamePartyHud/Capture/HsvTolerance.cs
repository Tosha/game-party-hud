using System;

namespace GamePartyHud.Capture;

public sealed record HsvTolerance(float H, float S, float V)
{
    public static HsvTolerance Default { get; } = new(15f, 0.25f, 0.25f);

    public bool Matches(Hsv reference, Hsv sample)
    {
        float dh = HueDistance(reference.H, sample.H);
        return dh <= H
            && MathF.Abs(reference.S - sample.S) <= S
            && MathF.Abs(reference.V - sample.V) <= V;
    }

    private static float HueDistance(float a, float b)
    {
        float d = MathF.Abs(a - b);
        return d > 180f ? 360f - d : d;
    }
}
