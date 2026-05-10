namespace GamePartyHud.Capture;

/// <summary>
/// The three bar types the app can track. <see cref="Hp"/> is required for joining
/// a party; <see cref="Stamina"/> and <see cref="Mana"/> are optional. Used by the
/// calibration wizard (per-bar prompt and config field selection) and by
/// PartyOrchestrator's diagnostic logging.
/// </summary>
public enum BarType { Hp, Stamina, Mana }
