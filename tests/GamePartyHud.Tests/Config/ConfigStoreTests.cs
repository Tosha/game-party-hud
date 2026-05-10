using System;
using System.IO;
using GamePartyHud.Capture;
using GamePartyHud.Config;
using GamePartyHud.Party;
using Xunit;

namespace GamePartyHud.Tests.Config;

public class ConfigStoreTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "gph_" + Guid.NewGuid() + ".json");

    public void Dispose()
    {
        if (File.Exists(_tmp)) File.Delete(_tmp);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var store = new ConfigStore(_tmp);
        var cfg = store.Load();
        Assert.Equal(AppConfig.Defaults, cfg);
    }

    [Fact]
    public void RoundTrip_PreservesEverythingExceptRelayUrl()
    {
        // RelayUrl is owned by the binary and not round-tripped through
        // config.json — see Load_AlwaysOverridesPersistedRelayUrl below.
        // Every other field is persisted verbatim.
        var store = new ConfigStore(_tmp);
        var cfg = AppConfig.Defaults with
        {
            HpCalibration = new BarCalibration(
                new CaptureRegion(0, 10, 20, 300, 18),
                FillDirection.LTR),
            NicknameRegion = new CaptureRegion(0, 10, 0, 300, 20),
            Nickname = "Yiawahuye",
            Role = Role.Tank,
            HudPosition = new HudPosition(500, 400, 1),
            HudLocked = false,
            LastPartyId = "X7K2P9",
            PollIntervalMs = 2500,
        };
        store.Save(cfg);
        Assert.Equal(cfg, store.Load());
    }

    [Fact]
    public void Load_CorruptFile_BacksUpAndReturnsDefaults()
    {
        File.WriteAllText(_tmp, "{not-json");
        var cfg = new ConfigStore(_tmp).Load();
        Assert.Equal(AppConfig.Defaults, cfg);
        Assert.False(File.Exists(_tmp));
        var backups = Directory.GetFiles(Path.GetDirectoryName(_tmp)!, Path.GetFileName(_tmp) + ".bad-*");
        Assert.NotEmpty(backups);
        foreach (var b in backups) File.Delete(b);
    }

    [Fact]
    public void Load_AlwaysOverridesPersistedRelayUrl_WithBuildDefault()
    {
        // Whatever's in config.json — a now-retired URL, a stale URL from a
        // previous build, even an attempted user override — Load returns
        // the binary's build-time default. Forks that need a different URL
        // rebuild with their own GPH_RELAY_URL secret.
        File.WriteAllText(_tmp, """
{
  "hpCalibration": null,
  "nicknameRegion": null,
  "nickname": "Test",
  "role": "Tank",
  "hudPosition": { "x": 0, "y": 0, "monitor": 0 },
  "hudLocked": true,
  "lastPartyId": null,
  "pollIntervalMs": 3000,
  "relayUrl": "wss://some-stale-url.example.invalid"
}
""");

        var loaded = new ConfigStore(_tmp).Load();
        Assert.Equal(AppConfig.DefaultRelayUrl, loaded.RelayUrl);
    }

    [Fact]
    public void Save_DoesNotPersistRelayUrlSoFutureRotationsDontStick()
    {
        // Both AppConfig.Defaults (where RelayUrl == DefaultRelayUrl) and
        // an explicit override should land on disk with no relayUrl value
        // — Load owns that field. Persisting it would just confuse
        // anyone opening config.json to debug a connectivity issue.
        var store = new ConfigStore(_tmp);
        store.Save(AppConfig.Defaults with { RelayUrl = "wss://does-not-matter.example.com" });

        var jsonOnDisk = File.ReadAllText(_tmp);
        Assert.DoesNotContain("does-not-matter", jsonOnDisk);
        Assert.DoesNotContain(AppConfig.DefaultRelayUrl, jsonOnDisk);

        // Round-trip still equal — Load promotes the build default.
        Assert.Equal(AppConfig.Defaults, store.Load());
    }

    [Fact]
    public void Load_AlwaysOverridesPersistedRelayFallbackUrl_WithBuildDefault()
    {
        // RelayFallbackUrl follows the same lifecycle as RelayUrl: owned by
        // the binary, never round-tripped through config.json. A stale value
        // on disk (e.g. from a previous build that pointed at a different
        // bridge) is replaced by the binary's compiled-in default on Load.
        File.WriteAllText(_tmp, """
{
  "hpCalibration": null,
  "nicknameRegion": null,
  "nickname": "Test",
  "role": "Tank",
  "hudPosition": { "x": 0, "y": 0, "monitor": 0 },
  "hudLocked": true,
  "lastPartyId": null,
  "pollIntervalMs": 3000,
  "relayUrl": "wss://stale.example.invalid",
  "relayFallbackUrl": "wss://stale-bridge.example.invalid"
}
""");

        var loaded = new ConfigStore(_tmp).Load();
        Assert.Equal(AppConfig.DefaultRelayFallbackUrl, loaded.RelayFallbackUrl);
    }

    [Fact]
    public void Save_DoesNotPersistRelayFallbackUrl()
    {
        // Same rationale as RelayUrl: the binary owns the value, persisting
        // it just makes config.json look authoritative when it isn't and
        // creates surprise when a binary rotation doesn't take effect.
        var store = new ConfigStore(_tmp);
        store.Save(AppConfig.Defaults with { RelayFallbackUrl = "wss://bridge-secret.example.com" });

        var jsonOnDisk = File.ReadAllText(_tmp);
        Assert.DoesNotContain("bridge-secret", jsonOnDisk);
    }

    [Fact]
    public void Load_OldShapeHpCalibrationJson_DropsFullColorAndTolerance()
    {
        // A config.json saved by a build before the BarCalibration redesign
        // contains hpCalibration.fullColor and hpCalibration.tolerance objects.
        // The new BarCalibration record only has Region and Direction, so those
        // two extra JSON keys must be silently ignored on load (System.Text.Json
        // default behaviour with JsonSerializerDefaults.Web). The next Save
        // re-serialises without them.
        File.WriteAllText(_tmp, """
{
  "hpCalibration": {
    "region": { "monitor": 0, "x": 10, "y": 20, "w": 300, "h": 18 },
    "fullColor": { "h": 5, "s": 0.9, "v": 0.7 },
    "tolerance": { "h": 15, "s": 0.25, "v": 0.25 },
    "direction": "LTR"
  },
  "nicknameRegion": null,
  "nickname": "Test",
  "role": "Tank",
  "hudPosition": { "x": 0, "y": 0, "monitor": 0 },
  "hudLocked": true,
  "lastPartyId": null,
  "pollIntervalMs": 2000,
  "relayUrl": ""
}
""");

        var store = new ConfigStore(_tmp);
        var loaded = store.Load();

        Assert.NotNull(loaded.HpCalibration);
        Assert.Equal(new CaptureRegion(0, 10, 20, 300, 18), loaded.HpCalibration!.Region);
        Assert.Equal(FillDirection.LTR, loaded.HpCalibration.Direction);

        // Round-trip: save and re-read; the reborn JSON must not contain the
        // legacy keys.
        store.Save(loaded);
        var reborn = File.ReadAllText(_tmp);
        Assert.DoesNotContain("\"fullColor\"", reborn);
        Assert.DoesNotContain("\"tolerance\"", reborn);
    }
}
