using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using GamePartyHud.Party;

namespace GamePartyHud.Hud;

/// <summary>
/// Pre-frozen brush palette for member-card role tiles. Each role has one
/// vivid accent colour; that accent drives both the tile border (alpha 0x55)
/// and a top-to-bottom background gradient (alpha 0x44 → 0x22). Same alpha
/// pattern as the original red placeholder tile, so the visual depth is
/// preserved — only the hue differs per role.
///
/// Palette follows the genre convention you'll find across MMOs / party
/// games: Tank-blue, Healer-green, melee-red, ranged-orange, support-purple,
/// utility-yellow. Specific shades come from Tailwind's 500-row, picked for
/// vivid-but-not-eye-searing on a dark, semi-transparent overlay.
/// </summary>
internal static class RoleBrushes
{
    private static readonly IReadOnlyDictionary<Role, Color> Accents = new Dictionary<Role, Color>
    {
        [Role.Tank]      = Color.FromRgb(0x3B, 0x82, 0xF6), // blue   — defensive, cool, near-universal "tank" colour
        [Role.Healer]    = Color.FromRgb(0x22, 0xC5, 0x5E), // green  — life / restoration, near-universal
        [Role.Support]   = Color.FromRgb(0xA8, 0x55, 0xF7), // purple — buffs / utility caster, FF14 / OW family
        [Role.MeleeDps]  = Color.FromRgb(0xEF, 0x44, 0x44), // red    — close-range damage, hot / aggressive
        [Role.RangedDps] = Color.FromRgb(0xF9, 0x73, 0x16), // orange — distinct from melee but still warm "damage" hue
        [Role.Utility]   = Color.FromRgb(0xEA, 0xB3, 0x08), // yellow — generalist; visually distinct from the five above
    };

    private static readonly IReadOnlyDictionary<Role, Brush> _borders = MakeAll(MakeBorder);
    private static readonly IReadOnlyDictionary<Role, Brush> _backgrounds = MakeAll(MakeBackground);

    public static Brush BorderFor(Role role) => _borders[role];
    public static Brush BackgroundFor(Role role) => _backgrounds[role];

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Brush MakeBorder(Color accent)
    {
        var b = new SolidColorBrush(WithAlpha(accent, 0x55));
        b.Freeze();
        return b;
    }

    private static Brush MakeBackground(Color accent)
    {
        var b = new LinearGradientBrush(
            WithAlpha(accent, 0x44),
            WithAlpha(accent, 0x22),
            new Point(0, 0),
            new Point(0, 1));
        b.Freeze();
        return b;
    }

    private static IReadOnlyDictionary<Role, Brush> MakeAll(Func<Color, Brush> factory)
    {
        var d = new Dictionary<Role, Brush>();
        foreach (var r in Enum.GetValues<Role>())
        {
            d[r] = factory(Accents[r]);
        }
        return d;
    }
}
