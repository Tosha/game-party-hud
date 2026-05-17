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
    public void RoundTrip_PreservesEverythingExceptBinaryOwnedFields()
    {
        // Binary-owned fields (RelayUrl, RelayFallbackUrl, PollIntervalMs)
        // are not round-tripped through config.json — Load always promotes
        // the build-time defaults, so a custom value would be discarded on
        // the next launch. See the Load_AlwaysOverridesPersisted* tests
        // below for explicit coverage of each override.
        //
        // Every other field IS persisted verbatim; this test pins that down
        // by stuffing non-default values into all user-owned fields and
        // asserting the loaded record equals the saved one.
        var store = new ConfigStore(_tmp);
        var cfg = AppConfig.Defaults with
        {
            HpCalibration = new BarCalibration(
                new CaptureRegion(10, 20, 300, 18),
                FillDirection.LTR),
            StaminaCalibration = new BarCalibration(
                new CaptureRegion(10, 40, 300, 18),
                FillDirection.LTR),
            ManaCalibration = new BarCalibration(
                new CaptureRegion(10, 60, 300, 18),
                FillDirection.LTR),
            Nickname = "Yiawahuye",
            Role = Role.Tank,
            HudPosition = new HudPosition(500, 400),
            HudLocked = false,
            LastPartyId = "X7K2P9",
            FullscreenDisclaimerDismissed = true,
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
    public void Load_AlwaysOverridesPersistedPollIntervalMs_WithBuildDefault()
    {
        // PollIntervalMs is binary-owned: when the default is tuned across
        // releases (e.g. 2000 → 1000 → 700) existing installs must pick up
        // the new cadence on next launch rather than being stuck on whatever
        // they first persisted. Same lifecycle treatment as RelayUrl.
        File.WriteAllText(_tmp, """
{
  "hpCalibration": null,
  "nicknameRegion": null,
  "nickname": "Test",
  "role": "Tank",
  "hudPosition": { "x": 0, "y": 0, "monitor": 0 },
  "hudLocked": true,
  "lastPartyId": null,
  "pollIntervalMs": 9999,
  "relayUrl": ""
}
""");

        var loaded = new ConfigStore(_tmp).Load();
        Assert.Equal(AppConfig.Defaults.PollIntervalMs, loaded.PollIntervalMs);
        Assert.NotEqual(9999, loaded.PollIntervalMs);
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
        Assert.Equal(new CaptureRegion(10, 20, 300, 18), loaded.HpCalibration!.Region);
        Assert.Equal(FillDirection.LTR, loaded.HpCalibration.Direction);

        // Round-trip: save and re-read; the reborn JSON must not contain the
        // legacy keys.
        store.Save(loaded);
        var reborn = File.ReadAllText(_tmp);
        Assert.DoesNotContain("\"fullColor\"", reborn);
        Assert.DoesNotContain("\"tolerance\"", reborn);
    }

    [Fact]
    public void Load_OldShapeConfig_MissingStaminaAndManaCalibrations_ParseAsNull()
    {
        // A config.json saved before stamina/mana support contains only
        // hpCalibration. The new optional fields default to null on load
        // (System.Text.Json default unknown-field handling). Round-trip then
        // re-serialises with the two new keys present (as null) — that's
        // expected.
        File.WriteAllText(_tmp, """
{
  "hpCalibration": {
    "region": { "monitor": 0, "x": 10, "y": 20, "w": 300, "h": 18 },
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

        var loaded = new ConfigStore(_tmp).Load();
        Assert.NotNull(loaded.HpCalibration);
        Assert.Null(loaded.StaminaCalibration);
        Assert.Null(loaded.ManaCalibration);
    }

    [Fact]
    public void Load_OldShapeConfig_MissingFullscreenDisclaimerDismissed_DefaultsToFalse()
    {
        // A config.json saved before the fullscreen disclaimer banner shipped
        // doesn't contain the fullscreenDisclaimerDismissed key. The new field
        // must default to false on load so existing users see the banner once
        // on their next launch (and dismiss it, at which point the field
        // persists as true going forward).
        File.WriteAllText(_tmp, """
{
  "hpCalibration": null,
  "staminaCalibration": null,
  "manaCalibration": null,
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

        var loaded = new ConfigStore(_tmp).Load();
        Assert.False(loaded.FullscreenDisclaimerDismissed);
    }

    [Fact]
    public void Load_HudScale_AboveMax_ClampedToTwo()
    {
        // Hand-edited config with an extreme value must be clamped so the HUD
        // stays usable. The grip drag clamps too, but a curious user might edit
        // config.json directly to "stretch" the HUD.
        File.WriteAllText(_tmp, """
{
  "hpCalibration": null,
  "staminaCalibration": null,
  "manaCalibration": null,
  "nicknameRegion": null,
  "nickname": "Test",
  "role": "Tank",
  "hudPosition": { "x": 0, "y": 0, "monitor": 0 },
  "hudLocked": true,
  "lastPartyId": null,
  "pollIntervalMs": 2000,
  "relayUrl": "",
  "hudScale": 9.0
}
""");
        var loaded = new ConfigStore(_tmp).Load();
        Assert.Equal(2.0, loaded.HudScale);
    }

    [Fact]
    public void Load_HudScale_BelowMin_ClampedToHalf()
    {
        File.WriteAllText(_tmp, """
{
  "hpCalibration": null,
  "staminaCalibration": null,
  "manaCalibration": null,
  "nicknameRegion": null,
  "nickname": "Test",
  "role": "Tank",
  "hudPosition": { "x": 0, "y": 0, "monitor": 0 },
  "hudLocked": true,
  "lastPartyId": null,
  "pollIntervalMs": 2000,
  "relayUrl": "",
  "hudScale": 0.1
}
""");
        var loaded = new ConfigStore(_tmp).Load();
        Assert.Equal(0.5, loaded.HudScale);
    }
}
