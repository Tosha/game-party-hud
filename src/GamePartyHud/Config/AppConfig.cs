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
    string? CustomTurnUrl,
    string? CustomTurnUsername,
    string? CustomTurnCredential)
{
    public static AppConfig Defaults { get; } = new(
        HpCalibration: null,
        NicknameRegion: null,
        Nickname: "Player",
        Role: Role.Utility,
        HudPosition: new HudPosition(100, 100, 0),
        HudLocked: true,
        LastPartyId: null,
        PollIntervalMs: 3000,
        CustomTurnUrl: null,
        CustomTurnUsername: null,
        CustomTurnCredential: null);
}

public sealed record HudPosition(double X, double Y, int Monitor);
