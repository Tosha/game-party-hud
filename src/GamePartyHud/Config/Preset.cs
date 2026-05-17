using GamePartyHud.Capture;
using GamePartyHud.Party;

namespace GamePartyHud.Config;

/// <summary>
/// One character profile bundle: the user-facing label plus all the per-character
/// data that today lives on <see cref="AppConfig"/>'s top level. Multiple presets
/// can coexist on the same install so a user with several alts can switch
/// nickname / role / bar-region calibrations in a single click.
/// </summary>
/// <param name="Id">
/// Stable id used by <see cref="AppConfig.ActivePresetId"/> to reference this
/// preset. GUID string for user-created presets; the literal sentinel
/// <c>"default"</c> for the auto-seeded preset that ships in
/// <see cref="AppConfig.Defaults"/> or is produced by the legacy-config
/// migration in <c>ConfigStore.Load</c>. Survives renames.
/// </param>
/// <param name="Name">User-facing label, unique within <see cref="AppConfig.Presets"/>.</param>
public sealed record Preset(
    string Id,
    string Name,
    string Nickname,
    Role Role,
    BarCalibration? HpCalibration,
    BarCalibration? StaminaCalibration,
    BarCalibration? ManaCalibration,
    bool StaminaEnabled = true,
    bool ManaEnabled = true);
