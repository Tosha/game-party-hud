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
}
