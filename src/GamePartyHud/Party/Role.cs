namespace GamePartyHud.Party;

public enum Role { Tank, Healer, Support, MeleeDps, RangedDps, Utility }

/// <summary>Single-character glyphs for in-HUD role rendering. Placeholder art for v0.1.0.</summary>
public static class RoleGlyph
{
    public static string For(Role role) => role switch
    {
        Role.Tank      => "\u25C6",  // ◆
        Role.Healer    => "\u271A",  // ✚
        Role.Support   => "\u2699",  // ⚙
        Role.MeleeDps  => "\u2694",  // ⚔
        Role.RangedDps => "\u279A",  // ➚
        Role.Utility   => "\u2605",  // ★
        _              => "?"
    };
}

/// <summary>User-facing display labels for <see cref="Role"/>. Used by the main window's Role picker.</summary>
public static class RoleDisplay
{
    public static string For(Role role) => role switch
    {
        Role.Tank      => "Tank",
        Role.Healer    => "Healer",
        Role.Support   => "Support",
        Role.MeleeDps  => "Melee DPS",
        Role.RangedDps => "Ranged DPS",
        Role.Utility   => "Utility",
        _              => role.ToString()
    };
}
