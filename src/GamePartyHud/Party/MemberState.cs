namespace GamePartyHud.Party;

public sealed record MemberState(
    string PeerId,
    string Nickname,
    Role Role,
    float? HpPercent,
    long JoinedAtUnix,
    long LastUpdateUnix);
