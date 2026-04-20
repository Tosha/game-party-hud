#if DEBUG
using System.Windows;
using GamePartyHud.Party;

namespace GamePartyHud.Hud;

/// <summary>
/// Debug-only manual smoke harness for the HUD window. Invoked from <see cref="App"/>
/// when the process is launched with <c>--hud-smoke</c> (optionally
/// <c>--hud-smoke=N</c> to seed N members). Removed from Release builds by the
/// <c>#if DEBUG</c> guard; not part of the shipped app.
/// </summary>
internal static class HudSmokeHarness
{
    public const string CliFlag = "--hud-smoke";

    private static readonly (string Nick, Role Role, float Hp, bool Stale)[] Seeds =
    {
        ("Yiawahuye",    Role.Tank,      0.72f, false),
        ("Kyrele",       Role.Healer,    1.00f, false),
        ("Arakh",        Role.MeleeDps,  0.30f, true),
        ("Thal",         Role.RangedDps, 0.85f, false),
        ("StupidBeast",  Role.Tank,      0.55f, false),
        ("Barrakh",      Role.MeleeDps,  0.10f, false),
        ("ShalfeyHealz", Role.Healer,    0.95f, false),
        ("AboutFeeder",  Role.RangedDps, 0.68f, false),
        ("MinSu",        Role.MeleeDps,  0.40f, false),
        ("Gosling",      Role.RangedDps, 0.78f, false),
        ("YaGood",       Role.Tank,      1.00f, false),
        ("TyZok",        Role.MeleeDps,  0.22f, false),
        ("Aggressor",    Role.MeleeDps,  0.50f, false),
        ("Mir",          Role.Healer,    0.88f, false),
        ("Tomodo",       Role.RangedDps, 0.15f, true),
        ("GLIST",        Role.Tank,      0.61f, false),
        ("TinMiraqle",   Role.Healer,    0.33f, false),
        ("Zalbeng",      Role.MeleeDps,  0.80f, false),
        ("DoraLany",     Role.RangedDps, 0.47f, false),
        ("Feng",         Role.Tank,      0.92f, false),
    };

    public static void Run(Application app, int count = 4)
    {
        int n = System.Math.Clamp(count, 1, Seeds.Length);
        var hud = new HudWindow();
        for (int i = 0; i < n; i++)
        {
            var (nick, role, hp, stale) = Seeds[i];
            hud.MemberList.Add(new HudMember($"p{i + 1}")
            {
                Nickname = nick,
                Role = role,
                HpPercent = hp,
                IsStale = stale,
            });
        }
        hud.Closed += (_, _) => app.Shutdown();
        hud.Show();
    }
}
#endif
