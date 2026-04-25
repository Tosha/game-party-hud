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
    /// Default relay endpoint. Replace with your deployed
    /// <c>wss://...workers.dev</c> URL from <c>relay/</c> before building the
    /// shipped executable (or override via <c>config.json</c> at runtime).
    /// </summary>
    public const string DefaultRelayUrl = "wss://gph-relay.example.workers.dev";

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
