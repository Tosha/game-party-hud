namespace GamePartyHud.Party;

public sealed record MemberState(
    string PeerId,
    string Nickname,
    Role Role,
    float? HpPercent,
    float? StaminaPercent,
    float? ManaPercent,
    long JoinedAtUnix,
    long LastUpdateUnix);
