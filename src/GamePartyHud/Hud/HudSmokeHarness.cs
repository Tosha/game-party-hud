#if DEBUG
using System.Windows;
using GamePartyHud.Party;

namespace GamePartyHud.Hud;

/// <summary>
/// Debug-only manual smoke harness for the HUD window. Invoked from <see cref="App"/>
/// when the process is launched with <c>--hud-smoke</c>. Removed from Release builds
/// by the <c>#if DEBUG</c> guard; not part of the shipped app.
/// </summary>
internal static class HudSmokeHarness
{
    public const string CliFlag = "--hud-smoke";

    public static void Run(Application app)
    {
        var hud = new HudWindow();
        hud.MemberList.Add(new HudMember("p1") { Nickname = "Yiawahuye", Role = Role.Tank,      HpPercent = 0.72f });
        hud.MemberList.Add(new HudMember("p2") { Nickname = "Kyrele",    Role = Role.Healer,    HpPercent = 1.00f });
        hud.MemberList.Add(new HudMember("p3") { Nickname = "Arakh",     Role = Role.MeleeDps,  HpPercent = 0.30f, IsStale = true });
        hud.MemberList.Add(new HudMember("p4") { Nickname = "Thal",      Role = Role.RangedDps, HpPercent = 0.85f });
        hud.Closed += (_, _) => app.Shutdown();
        hud.Show();
    }
}
#endif
