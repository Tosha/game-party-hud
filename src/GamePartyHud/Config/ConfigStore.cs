using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GamePartyHud.Config;

public sealed class ConfigStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _opts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ConfigStore(string? overridePath = null)
    {
        _path = overridePath ?? DefaultPath();
    }

    public static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GamePartyHud");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "config.json");
    }

    public AppConfig Load()
    {
        if (!File.Exists(_path)) return AppConfig.Defaults;
        try
        {
            var json = File.ReadAllText(_path);
            var raw = JsonSerializer.Deserialize<AppConfig>(json, _opts) ?? AppConfig.Defaults;

            // RelayUrl, RelayFallbackUrl and PollIntervalMs are owned by the
            // binary, not by per-machine config. Always promote the build-time
            // defaults (RelayUrl/RelayFallbackUrl come from the GitHub Actions
            // secrets injected at publish time; PollIntervalMs is the hardcoded
            // default in AppConfig.Defaults) over whatever's persisted on disk.
            //
            // For RelayUrl this prevents a once-saved URL from shadowing future
            // binary rotations — the symptom we hit when config.json kept
            // routing the app to a deleted relay endpoint after a server
            // rename.
            //
            // For PollIntervalMs this means that when we tune the default in
            // the binary (e.g. 2000 → 1000 → 700 across recent releases),
            // existing installs pick up the new value on next launch instead
            // of being stuck on whatever they first persisted. The trade-off
            // is that user-tuned poll intervals are reset on launch, but
            // there's no UI surface to tune it today and the field is small
            // enough to live in source if a fork wants a different cadence.
            //
            // Forks that need different values set their own secrets / change
            // AppConfig.Defaults and rebuild.
            return raw with
            {
                RelayUrl = AppConfig.DefaultRelayUrl,
                RelayFallbackUrl = AppConfig.DefaultRelayFallbackUrl,
                PollIntervalMs = AppConfig.Defaults.PollIntervalMs
            };
        }
        catch (Exception)
        {
            // Corrupted file: move it aside and return defaults so the app can keep running.
            try { File.Move(_path, _path + ".bad-" + DateTime.UtcNow.Ticks, overwrite: true); } catch { }
            return AppConfig.Defaults;
        }
    }

    public void Save(AppConfig config)
    {
        // Don't persist RelayUrl or RelayFallbackUrl. Load always overrides
        // them with the build-time defaults, so writing them here would just
        // make config.json look authoritative when it isn't and confuse
        // anyone who opens the file to debug a connectivity issue.
        var json = JsonSerializer.Serialize(
            config with { RelayUrl = "", RelayFallbackUrl = "" }, _opts);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }
}
