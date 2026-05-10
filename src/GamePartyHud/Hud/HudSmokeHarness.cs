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

    // Each seed describes a member's nick, role, and bar values. Stamina/Mana
    // are nullable so we can mock all four card-layout configurations:
    //   HP only            (Stam = null, Mana = null)
    //   HP + Stamina       (Mana = null)
    //   HP + Mana          (Stam = null)
    //   HP + Stamina + Mana
    //
    // First six entries cover the six roles. Layout-coverage entries 7-10
    // exercise each card configuration so a launch with `--hud-smoke=10`
    // shows all four layouts on a single screen.
    private static readonly (string Nick, Role Role, float Hp, float? Stamina, float? Mana, bool Stale)[] Seeds =
    {
        ("Yiawahuye",    Role.Tank,      0.72f, null,  null,  false),
        ("Kyrele",       Role.Healer,    1.00f, null,  null,  false),
        ("Stelis",       Role.Support,   0.66f, null,  null,  false),
        ("Arakh",        Role.MeleeDps,  0.30f, null,  null,  true),
        ("Thal",         Role.RangedDps, 0.85f, null,  null,  false),
        ("Riven",        Role.Utility,   0.50f, null,  null,  false),
        // Layout coverage:
        ("HpOnly",       Role.Tank,      0.55f, null,  null,  false),
        ("HpStam",       Role.MeleeDps,  0.80f, 0.40f, null,  false),
        ("HpMana",       Role.Healer,    0.90f, null,  0.60f, false),
        ("HpStamMana",   Role.Support,   0.65f, 0.30f, 0.45f, false),
        // Filler (HP-only, kept for backwards-compat with --hud-smoke=20+):
        ("StupidBeast",  Role.Tank,      0.55f, null,  null,  false),
        ("Barrakh",      Role.MeleeDps,  0.10f, null,  null,  false),
        ("ShalfeyHealz", Role.Healer,    0.95f, null,  null,  false),
        ("AboutFeeder",  Role.RangedDps, 0.68f, null,  null,  false),
        ("MinSu",        Role.MeleeDps,  0.40f, null,  null,  false),
        ("Gosling",      Role.RangedDps, 0.78f, null,  null,  false),
        ("YaGood",       Role.Tank,      1.00f, null,  null,  false),
        ("TyZok",        Role.MeleeDps,  0.22f, null,  null,  false),
        ("Aggressor",    Role.MeleeDps,  0.50f, null,  null,  false),
        ("Mir",          Role.Healer,    0.88f, null,  null,  false),
        ("Tomodo",       Role.RangedDps, 0.15f, null,  null,  true),
        ("GLIST",        Role.Tank,      0.61f, null,  null,  false),
    };

    public static void Run(Application app, int count = 4)
    {
        int n = System.Math.Clamp(count, 1, Seeds.Length);
        var hud = new HudWindow();
        for (int i = 0; i < n; i++)
        {
            var (nick, role, hp, stamina, mana, stale) = Seeds[i];
            hud.MemberList.Add(new HudMember($"p{i + 1}")
            {
                Nickname = nick,
                Role = role,
                HpPercent = hp,
                StaminaPercent = stamina,
                ManaPercent = mana,
                IsStale = stale,
            });
        }
        hud.Closed += (_, _) => app.Shutdown();
        hud.Show();
    }
}
#endif
