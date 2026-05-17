using System;
using System.Globalization;

namespace GamePartyHud.Settings;

/// <summary>
/// Pure-logic helpers for the HUD colour pipeline:
///   - hex string parse / format (#RRGGBB or #AARRGGBB)
///   - RGB ↔ HSV conversion (HSV needed by the SV+hue picker to keep
///     hue precision when value=0)
///   - Darken: linear scale of an RGB triple, used to build the
///     auto-derived gradient bottom stop both in the picker preview and
///     at runtime in HudTheme.
/// </summary>
public static class HudColor
{
    /// <summary>Parse #RRGGBB or #AARRGGBB into ARGB bytes.
    /// Returns null on bad input (null, wrong length, non-hex chars).</summary>
    public static (byte A, byte R, byte G, byte B)? TryParse(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#') return null;
        var body = hex.AsSpan(1);
        byte a, r, g, b;
        if (body.Length == 6)
        {
            a = 0xFF;
            if (!TryHex(body.Slice(0, 2), out r)) return null;
            if (!TryHex(body.Slice(2, 2), out g)) return null;
            if (!TryHex(body.Slice(4, 2), out b)) return null;
        }
        else if (body.Length == 8)
        {
            if (!TryHex(body.Slice(0, 2), out a)) return null;
            if (!TryHex(body.Slice(2, 2), out r)) return null;
            if (!TryHex(body.Slice(4, 2), out g)) return null;
            if (!TryHex(body.Slice(6, 2), out b)) return null;
        }
        else return null;
        return (a, r, g, b);
    }

    private static bool TryHex(ReadOnlySpan<char> s, out byte value) =>
        byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

    /// <summary>Format ARGB bytes as #AARRGGBB (upper-case).</summary>
    public static string Format(byte a, byte r, byte g, byte b) =>
        $"#{a:X2}{r:X2}{g:X2}{b:X2}";

    /// <summary>
    /// RGB (each 0–255) → HSV (H 0–360°, S 0–1, V 0–1). Standard HSV
    /// formula; H=0 when S=0 (grey).
    /// </summary>
    public static (double H, double S, double V) RgbToHsv(byte r, byte g, byte b)
    {
        double rd = r / 255.0;
        double gd = g / 255.0;
        double bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double v = max;
        double delta = max - min;
        double s = max <= 0.0 ? 0.0 : delta / max;
        double h;
        if (delta <= 0.0)              h = 0.0;
        else if (max == rd)            h = 60.0 * (((gd - bd) / delta) % 6.0);
        else if (max == gd)            h = 60.0 * (((bd - rd) / delta) + 2.0);
        else                           h = 60.0 * (((rd - gd) / delta) + 4.0);
        if (h < 0) h += 360.0;
        return (h, s, v);
    }

    /// <summary>HSV (H 0–360°, S 0–1, V 0–1) → RGB (each 0–255).
    /// Integer rounding so RGB → HSV → RGB may differ by ±1.</summary>
    public static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360.0) + 360.0) % 360.0;
        s = Math.Clamp(s, 0.0, 1.0);
        v = Math.Clamp(v, 0.0, 1.0);
        double c = v * s;
        double x = c * (1.0 - Math.Abs(((h / 60.0) % 2.0) - 1.0));
        double m = v - c;
        double r, g, b;
        if      (h <  60.0) { r = c; g = x; b = 0; }
        else if (h < 120.0) { r = x; g = c; b = 0; }
        else if (h < 180.0) { r = 0; g = c; b = x; }
        else if (h < 240.0) { r = 0; g = x; b = c; }
        else if (h < 300.0) { r = x; g = 0; b = c; }
        else                { r = c; g = 0; b = x; }
        return (
            (byte)Math.Round((r + m) * 255.0),
            (byte)Math.Round((g + m) * 255.0),
            (byte)Math.Round((b + m) * 255.0)
        );
    }

    /// <summary>Scale an RGB triple by <paramref name="factor"/>; clamps
    /// each channel to [0, 255]. factor=0.7 gives the ~30% darkening used
    /// for the bar-gradient bottom stop.</summary>
    public static (byte R, byte G, byte B) Darken((byte R, byte G, byte B) rgb, double factor)
    {
        return (
            ClampByte(rgb.R * factor),
            ClampByte(rgb.G * factor),
            ClampByte(rgb.B * factor)
        );
    }

    private static byte ClampByte(double v) =>
        v <= 0.0 ? (byte)0 : v >= 255.0 ? (byte)255 : (byte)Math.Round(v);
}
