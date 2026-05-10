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

            // RelayUrl and RelayFallbackUrl are owned by the binary, not by
            // per-machine config. Always promote the build-time defaults (set
            // by the GPH_RELAY_URL / GPH_RELAY_FALLBACK_URL GitHub Actions
            // secrets at publish time) over whatever's persisted on disk.
            // This prevents a once-saved URL from shadowing future binary
            // rotations — the symptom we hit when config.json kept routing
            // the app to a deleted relay endpoint after a server rename.
            // Forks that need different URLs set their own secrets and
            // rebuild; there is no per-machine config.json override.
            return raw with
            {
                RelayUrl = AppConfig.DefaultRelayUrl,
                RelayFallbackUrl = AppConfig.DefaultRelayFallbackUrl
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
