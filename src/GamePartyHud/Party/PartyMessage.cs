namespace GamePartyHud.Party;

public abstract record PartyMessage;

public sealed record StateMessage(string PeerId, string Nick, Role Role, float? Hp, long T) : PartyMessage;
public sealed record ByeMessage(string PeerId) : PartyMessage;
public sealed record KickMessage(string Target) : PartyMessage;
