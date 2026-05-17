using System;
using System.Linq;

namespace GamePartyHud.Config;

public static class AppConfigExtensions
{
    /// <summary>
    /// Returns a new <see cref="AppConfig"/> with <paramref name="mutate"/> applied
    /// to the currently-active preset. Other presets are passed through unchanged.
    /// Avoids the verbose
    /// <c>_config with { Presets = _config.Presets.Select(p => p.Id == _config.ActivePresetId ? p with { ... } : p).ToList() }</c>
    /// at every call site that today does <c>_config with { Nickname = ... }</c>.
    /// </summary>
    public static AppConfig UpdatePreset(this AppConfig cfg, Func<Preset, Preset> mutate)
    {
        var presets = cfg.Presets
            .Select(p => p.Id == cfg.ActivePresetId ? mutate(p) : p)
            .ToList();
        return cfg with { Presets = presets };
    }

    /// <summary>Returns the Stamina calibration if it's enabled, else null.
    /// The toggle controls whether the bar is broadcast / tracked at runtime
    /// without clearing the saved calibration, so re-enabling restores it.</summary>
    public static Capture.BarCalibration? EffectiveStaminaCalibration(this Preset p) =>
        p.StaminaEnabled ? p.StaminaCalibration : null;

    /// <summary>Returns the Mana calibration if it's enabled, else null.</summary>
    public static Capture.BarCalibration? EffectiveManaCalibration(this Preset p) =>
        p.ManaEnabled ? p.ManaCalibration : null;
}
