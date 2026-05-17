using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GamePartyHud.Capture;
using GamePartyHud.Diagnostics;
using GamePartyHud.Party;

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
            var raw = ParseWithMigration(json);
            var repaired = RepairInvariants(raw);

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
            return repaired with
            {
                RelayUrl = AppConfig.DefaultRelayUrl,
                RelayFallbackUrl = AppConfig.DefaultRelayFallbackUrl,
                PollIntervalMs = AppConfig.Defaults.PollIntervalMs,
                // HudScale stays user-owned (persists across launches) but is
                // clamped to [0.5, 2.0] so a hand-edited extreme can't break
                // the HUD layout — see SanitiseHudScale below.
                HudScale = SanitiseHudScale(repaired.HudScale),
                HudBackgroundOpacity = SanitiseHudBackgroundOpacity(repaired.HudBackgroundOpacity),
            };
        }
        catch (Exception)
        {
            // Corrupted file: move it aside and return defaults so the app can keep running.
            try { File.Move(_path, _path + ".bad-" + DateTime.UtcNow.Ticks, overwrite: true); } catch { }
            return AppConfig.Defaults;
        }
    }

    /// <summary>
    /// Detects legacy config.json (no top-level "Presets" key) and migrates it
    /// to the new shape on the fly: pack the old top-level Nickname / Role /
    /// HpCalibration / StaminaCalibration / ManaCalibration into one preset
    /// named "Default". New-shape files deserialise straight through.
    /// Returns AppConfig.Defaults if JSON is structurally invalid; the caller's
    /// catch block handles parse-time exceptions.
    /// </summary>
    private AppConfig ParseWithMigration(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject
            ?? throw new JsonException("Root is not a JSON object.");

        // Web defaults are case-insensitive on property lookup at Deserialize
        // time but JsonObject.ContainsKey is case-sensitive — check both casings.
        bool hasNewShape = node.ContainsKey("presets") || node.ContainsKey("Presets");
        if (hasNewShape)
        {
            return node.Deserialize<AppConfig>(_opts) ?? AppConfig.Defaults;
        }

        // Legacy shape — pull each old top-level field with a default fallback.
        var defaultPreset = AppConfig.Defaults.ActivePreset;
        var migrated = new Preset(
            Id: AppConfig.DefaultPresetId,
            Name: "Default",
            Nickname:           GetString(node, "nickname")       ?? defaultPreset.Nickname,
            Role:               GetEnum<Role>(node, "role")       ?? defaultPreset.Role,
            HpCalibration:      GetObject<BarCalibration>(node, "hpCalibration"),
            StaminaCalibration: GetObject<BarCalibration>(node, "staminaCalibration"),
            ManaCalibration:    GetObject<BarCalibration>(node, "manaCalibration"));

        // Strip the legacy keys we've consumed plus the three dead ones being
        // removed in this PR so they don't leak through.
        foreach (var legacy in new[]
        {
            "nickname", "role",
            "hpCalibration", "staminaCalibration", "manaCalibration",
            "nicknameRegion",
        })
        {
            node.Remove(legacy);
        }

        // Inject the new fields so we can use the normal deserialiser for everything else.
        node["presets"] = JsonSerializer.SerializeToNode(new[] { migrated }, _opts);
        node["activePresetId"] = AppConfig.DefaultPresetId;

        Log.Info($"ConfigStore: migrated legacy config.json to preset shape (preset='{migrated.Name}', nickname='{migrated.Nickname}').");

        return node.Deserialize<AppConfig>(_opts) ?? AppConfig.Defaults;
    }

    private static string? GetString(JsonObject node, string key)
    {
        if (!node.TryGetPropertyValue(key, out var v) || v is null) return null;
        try { return v.GetValue<string>(); }
        catch { return null; }
    }

    private static TEnum? GetEnum<TEnum>(JsonObject node, string key) where TEnum : struct, Enum
    {
        if (!node.TryGetPropertyValue(key, out var v) || v is null) return null;
        try
        {
            var raw = v.GetValue<string>();
            return Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed) ? parsed : null;
        }
        catch { return null; }
    }

    private TValue? GetObject<TValue>(JsonObject node, string key)
    {
        if (!node.TryGetPropertyValue(key, out var v) || v is null) return default;
        try { return v.Deserialize<TValue>(_opts); }
        catch { return default; }
    }

    /// <summary>
    /// Belt-and-suspenders enforcement of the AppConfig preset invariants:
    ///   1. Presets.Count &gt;= 1   (else inject the Defaults preset)
    ///   2. ActivePresetId matches one of the presets  (else point to Presets[0])
    ///   3. Preset ids are unique  (else regenerate dupes' ids)
    /// Repairs are logged so config drift shows up in app.log.
    /// </summary>
    private static AppConfig RepairInvariants(AppConfig cfg)
    {
        var presets = cfg.Presets;
        if (presets is null || presets.Count == 0)
        {
            Log.Warn("AppConfig: Presets was empty on load; seeding with the Defaults preset.");
            return cfg with
            {
                Presets = AppConfig.Defaults.Presets,
                ActivePresetId = AppConfig.DefaultPresetId,
            };
        }

        // Re-id duplicates (shouldn't happen via UI, but a hand-edit could).
        var seenIds = new HashSet<string>();
        var repaired = new List<Preset>(presets.Count);
        bool anyChange = false;
        foreach (var p in presets)
        {
            if (seenIds.Add(p.Id))
            {
                repaired.Add(p);
            }
            else
            {
                var newId = Guid.NewGuid().ToString();
                Log.Warn($"AppConfig: duplicate preset id '{p.Id}' on load; regenerated as '{newId}'.");
                repaired.Add(p with { Id = newId });
                anyChange = true;
                seenIds.Add(newId);
            }
        }
        if (anyChange) presets = repaired;

        string activeId = cfg.ActivePresetId ?? "";
        if (!seenIds.Contains(activeId))
        {
            var fallback = presets[0].Id;
            Log.Warn($"AppConfig: ActivePresetId '{activeId}' did not match any preset; repaired to '{fallback}'.");
            activeId = fallback;
        }

        return cfg with { Presets = presets, ActivePresetId = activeId };
    }

    private static double SanitiseHudScale(double raw)
    {
        // Out-of-range, NaN, or infinite values fall back to safe bounds so the
        // HUD's LayoutTransform never receives a value that would render it
        // invisible, infinitely large, or undefined.
        if (double.IsNaN(raw) || double.IsInfinity(raw)) return 1.0;
        return Math.Clamp(raw, 0.5, 2.0);
    }

    private static double SanitiseHudBackgroundOpacity(double raw)
    {
        // Out-of-range, NaN, or infinite values fall back to the default
        // (0.40). Clamped to [0.1, 1.0] so the user can never accidentally
        // hide the HUD chrome entirely (which would also hide the lock
        // button and trap them in dragged-off-screen territory).
        if (double.IsNaN(raw) || double.IsInfinity(raw)) return 0.40;
        return Math.Clamp(raw, 0.10, 1.0);
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
