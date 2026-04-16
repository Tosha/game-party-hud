using System;
using System.Windows;

namespace GamePartyHud;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

#if DEBUG
        foreach (var arg in e.Args)
        {
            if (string.Equals(arg, GamePartyHud.Hud.HudSmokeHarness.CliFlag, StringComparison.Ordinal))
            {
                GamePartyHud.Hud.HudSmokeHarness.Run(this);
                return;
            }
        }
#endif

        // Composition root wired in later milestones.
    }
}
