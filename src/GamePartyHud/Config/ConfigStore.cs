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

            // RelayUrl is owned by the binary, not by per-machine config.
            // Always promote the build-time default (set by the GPH_RELAY_URL
            // GitHub Actions secret at publish time) over whatever's
            // persisted on disk. This prevents a once-saved URL from
            // shadowing future binary rotations — the symptom we hit when
            // config.json kept routing the app to a deleted Worker after
            // the gph-relay → game-relay-* rotation. Forks that need a
            // different URL set their own GPH_RELAY_URL secret and rebuild;
            // there is no per-machine config.json override.
            return raw with { RelayUrl = AppConfig.DefaultRelayUrl };
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
        // Don't persist RelayUrl. Load always overrides it with the
        // build-time default, so writing it here would just make
        // config.json look authoritative when it isn't and confuse anyone
        // who opens the file to debug a connectivity issue.
        var json = JsonSerializer.Serialize(config with { RelayUrl = "" }, _opts);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }
}
