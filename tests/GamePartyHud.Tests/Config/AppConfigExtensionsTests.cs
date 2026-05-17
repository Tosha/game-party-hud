using GamePartyHud.Capture;
using GamePartyHud.Config;
using GamePartyHud.Party;
using Xunit;

namespace GamePartyHud.Tests.Config;

public class AppConfigExtensionsTests
{
    private static AppConfig WithTwoPresets()
    {
        var preset1 = new Preset(
            Id: "p1", Name: "One",
            Nickname: "Alice", Role: Role.Tank,
            HpCalibration: null, StaminaCalibration: null, ManaCalibration: null);
        var preset2 = new Preset(
            Id: "p2", Name: "Two",
            Nickname: "Bob", Role: Role.Healer,
            HpCalibration: new BarCalibration(new CaptureRegion(0, 0, 100, 10), FillDirection.LTR),
            StaminaCalibration: null, ManaCalibration: null);
        return AppConfig.Defaults with
        {
            Presets = new[] { preset1, preset2 },
            ActivePresetId = "p1",
        };
    }

    [Fact]
    public void UpdatePreset_MutatesOnlyTheActivePreset()
    {
        var cfg = WithTwoPresets();

        var updated = cfg.UpdatePreset(p => p with { Nickname = "Alice2" });

        Assert.Equal("Alice2", updated.Presets[0].Nickname);
        Assert.Equal("Bob", updated.Presets[1].Nickname); // unchanged
    }

    [Fact]
    public void UpdatePreset_LeavesNonActivePresetReferenceIntact()
    {
        var cfg = WithTwoPresets();
        var originalP2 = cfg.Presets[1];

        var updated = cfg.UpdatePreset(p => p with { Nickname = "Alice2" });

        // Non-active preset is passed through by reference (record `with` was
        // not invoked on it), so the reference equality holds.
        Assert.Same(originalP2, updated.Presets[1]);
    }

    [Fact]
    public void UpdatePreset_ReturnsNewAppConfigInstance()
    {
        var cfg = WithTwoPresets();

        var updated = cfg.UpdatePreset(p => p with { Nickname = "Alice2" });

        Assert.NotSame(cfg, updated);
        Assert.NotEqual(cfg, updated); // structurally different (new Presets list)
    }

    [Fact]
    public void ActivePreset_ReturnsMatchingPreset()
    {
        var cfg = WithTwoPresets();
        Assert.Equal("p1", cfg.ActivePreset.Id);
    }

    [Fact]
    public void ActivePreset_StaleIdFallsBackToFirstPreset()
    {
        var cfg = WithTwoPresets() with { ActivePresetId = "does-not-exist" };
        Assert.Equal("p1", cfg.ActivePreset.Id);
    }

    [Fact]
    public void EffectiveStaminaCalibration_ReturnsNullWhenDisabled()
    {
        var preset = new Preset(
            Id: "p", Name: "P", Nickname: "N", Role: Role.Utility,
            HpCalibration: null,
            StaminaCalibration: new BarCalibration(new CaptureRegion(0, 0, 100, 20), FillDirection.LTR),
            ManaCalibration: null,
            StaminaEnabled: false);

        Assert.Null(preset.EffectiveStaminaCalibration());
    }

    [Fact]
    public void EffectiveStaminaCalibration_ReturnsCalibrationWhenEnabled()
    {
        var cal = new BarCalibration(new CaptureRegion(0, 0, 100, 20), FillDirection.LTR);
        var preset = new Preset(
            Id: "p", Name: "P", Nickname: "N", Role: Role.Utility,
            HpCalibration: null,
            StaminaCalibration: cal,
            ManaCalibration: null,
            StaminaEnabled: true);

        Assert.Same(cal, preset.EffectiveStaminaCalibration());
    }

    [Fact]
    public void EffectiveManaCalibration_ReturnsNullWhenDisabled()
    {
        var preset = new Preset(
            Id: "p", Name: "P", Nickname: "N", Role: Role.Utility,
            HpCalibration: null,
            StaminaCalibration: null,
            ManaCalibration: new BarCalibration(new CaptureRegion(0, 0, 100, 20), FillDirection.LTR),
            ManaEnabled: false);

        Assert.Null(preset.EffectiveManaCalibration());
    }
}
