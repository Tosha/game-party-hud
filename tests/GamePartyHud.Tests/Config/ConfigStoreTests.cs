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
            HpCalibration = new HpCalibration(
                new HpRegion(0, 10, 20, 300, 18),
                new Hsv(5, 0.9f, 0.7f),
                new HsvTolerance(15, 0.25f, 0.25f),
                FillDirection.LTR),
            NicknameRegion = new HpRegion(0, 10, 0, 300, 20),
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
  "relayUrl": "wss://some-stale-url.workers.dev"
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
}
