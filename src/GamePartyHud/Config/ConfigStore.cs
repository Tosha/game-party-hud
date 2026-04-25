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
            // Migrate legacy config.json (pre-relay rewrite) — the missing RelayUrl
            // would otherwise leave the field null and blow up at use-site.
            if (string.IsNullOrWhiteSpace(raw.RelayUrl))
            {
                raw = raw with { RelayUrl = AppConfig.DefaultRelayUrl };
            }
            return raw;
        }
        catch (Exception)
        {
            try { File.Move(_path, _path + ".bad-" + DateTime.UtcNow.Ticks, overwrite: true); } catch { }
            return AppConfig.Defaults;
        }
    }

    public void Save(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, _opts);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }
}
