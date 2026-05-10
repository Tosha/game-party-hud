using System.ComponentModel;
using System.Collections.Generic;
using GamePartyHud.Hud;
using Xunit;

namespace GamePartyHud.Tests.Hud;

public class HudMemberTests
{
    [Fact]
    public void StaminaPercent_DefaultsToNull_AndHasStaminaIsFalse()
    {
        var m = new HudMember("p1");
        Assert.Null(m.StaminaPercent);
        Assert.False(m.HasStamina);
    }

    [Fact]
    public void ManaPercent_DefaultsToNull_AndHasManaIsFalse()
    {
        var m = new HudMember("p1");
        Assert.Null(m.ManaPercent);
        Assert.False(m.HasMana);
    }

    [Fact]
    public void SettingStaminaPercent_RaisesPropertyChangedAndFlipsHasStamina()
    {
        var m = new HudMember("p1");
        var changes = new List<string>();
        m.PropertyChanged += (_, e) => changes.Add(e.PropertyName ?? "");

        m.StaminaPercent = 0.5f;

        Assert.Equal(0.5f, m.StaminaPercent);
        Assert.True(m.HasStamina);
        Assert.Contains(nameof(HudMember.StaminaPercent), changes);
        Assert.Contains(nameof(HudMember.HasStamina), changes);
    }

    [Fact]
    public void ClearingStaminaPercent_RaisesPropertyChangedAndFlipsHasStamina()
    {
        var m = new HudMember("p1") { StaminaPercent = 0.5f };
        var changes = new List<string>();
        m.PropertyChanged += (_, e) => changes.Add(e.PropertyName ?? "");

        m.StaminaPercent = null;

        Assert.Null(m.StaminaPercent);
        Assert.False(m.HasStamina);
        Assert.Contains(nameof(HudMember.StaminaPercent), changes);
        Assert.Contains(nameof(HudMember.HasStamina), changes);
    }

    [Fact]
    public void SettingManaPercent_RaisesPropertyChangedAndFlipsHasMana()
    {
        var m = new HudMember("p1");
        var changes = new List<string>();
        m.PropertyChanged += (_, e) => changes.Add(e.PropertyName ?? "");

        m.ManaPercent = 0.5f;

        Assert.Equal(0.5f, m.ManaPercent);
        Assert.True(m.HasMana);
        Assert.Contains(nameof(HudMember.ManaPercent), changes);
        Assert.Contains(nameof(HudMember.HasMana), changes);
    }

    [Theory]
    [InlineData(-0.1f, 0f)]
    [InlineData(0f, 0f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(1f, 1f)]
    [InlineData(1.5f, 1f)]
    public void StaminaPercent_ClampsToZeroOne(float input, float expected)
    {
        var m = new HudMember("p1") { StaminaPercent = input };
        Assert.Equal(expected, m.StaminaPercent);
    }

    [Theory]
    [InlineData(-0.1f, 0f)]
    [InlineData(0f, 0f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(1f, 1f)]
    [InlineData(1.5f, 1f)]
    public void ManaPercent_ClampsToZeroOne(float input, float expected)
    {
        var m = new HudMember("p1") { ManaPercent = input };
        Assert.Equal(expected, m.ManaPercent);
    }
}
