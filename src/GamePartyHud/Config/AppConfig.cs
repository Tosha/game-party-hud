using System.Linq;
using System.Reflection;
using GamePartyHud.Capture;
using GamePartyHud.Party;

namespace GamePartyHud.Config;

public sealed record AppConfig(
    HpCalibration? HpCalibration,
    HpRegion? NicknameRegion,
    string Nickname,
    Role Role,
    HudPosition HudPosition,
    bool HudLocked,
    string? LastPartyId,
    int PollIntervalMs,
    string RelayUrl)
{
    /// <summary>
    /// Default relay endpoint, injected at build time via the
    /// <c>RelayUrl</c> MSBuild property (see <c>GamePartyHud.csproj</c>). Local
    /// dev builds inherit the <c>example.workers.dev</c> placeholder; release
    /// builds in CI substitute the real URL from the <c>GPH_RELAY_URL</c>
    /// GitHub Actions secret. End users override per-machine via the
    /// <c>RelayUrl</c> field in <c>%AppData%\GamePartyHud\config.json</c>.
    /// </summary>
    public static string DefaultRelayUrl { get; } = ResolveDefaultRelayUrl();

    private static string ResolveDefaultRelayUrl()
    {
        var fromMetadata = typeof(AppConfig).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RelayUrl")
            ?.Value;
        if (!string.IsNullOrWhiteSpace(fromMetadata)) return fromMetadata;
        return "wss://gph-relay.example.workers.dev";
    }

    public static AppConfig Defaults { get; } = new(
        HpCalibration: null,
        NicknameRegion: null,
        Nickname: "Player",
        Role: Role.Utility,
        HudPosition: new HudPosition(100, 100, 0),
        HudLocked: true,
        LastPartyId: null,
        PollIntervalMs: 3000,
        RelayUrl: DefaultRelayUrl);
}

public sealed record HudPosition(double X, double Y, int Monitor);
