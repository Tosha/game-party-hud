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
    public void RoundTrip_PreservesEverything()
    {
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
            RelayUrl = "wss://relay.example.com"
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
}
