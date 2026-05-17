using System;
using System.Windows;
using GamePartyHud.Config;
using GamePartyHud.Diagnostics;
using Wpf.Ui.Controls;

namespace GamePartyHud;

/// <summary>
/// Small modal opened from the gear icon on <see cref="MainWindow"/>.
/// Hosts the Party HUD customisation: per-bar color pickers, panel
/// opacity slider, and the master "Reset to defaults" button.
///
/// All controls write changes live (no Apply / Cancel) — each one calls
/// _ctl.UpdateConfig(...) which propagates to HudTheme and repaints the
/// HUD on the next render tick.
/// </summary>
public partial class SettingsWindow : FluentWindow
{
    private readonly MainWindow.IController _ctl;
    // Initialised true so handlers triggered during InitializeComponent
    // (e.g. Slider.Minimum=0.10 coerces Value 0 → 0.10, firing
    // OnOpacityChanged before any named element is reachable) early-return
    // instead of dereferencing not-yet-constructed XAML names or pushing a
    // bogus UpdateConfig before PopulateFromConfig seeds the real values.
    private bool _populating = true;

    public SettingsWindow(MainWindow.IController controller)
    {
        InitializeComponent();
        _ctl = controller;
        PopulateFromConfig();
    }

    private void PopulateFromConfig()
    {
        _populating = true;
        try
        {
            var cfg = _ctl.Config;
            HpPicker.ColorHex      = cfg.HpBarColor;
            StaminaPicker.ColorHex = cfg.StaminaBarColor;
            ManaPicker.ColorHex    = cfg.ManaBarColor;
            OpacitySlider.Value    = cfg.HudBackgroundOpacity;
            UpdateOpacityLabel(cfg.HudBackgroundOpacity);
        }
        finally { _populating = false; }
    }

    private void UpdateOpacityLabel(double v)
    {
        // Belt-and-suspenders against a XAML reorder making this fire
        // before the label exists. (The _populating guard in
        // OnOpacityChanged is the primary protection.)
        if (OpacityValueLabel is null) return;
        OpacityValueLabel.Text = $"{(int)Math.Round(v * 100)} %";
    }

    private void OnHpColorChanged(object? sender, EventArgs e)
    {
        if (_populating) return;
        _ctl.UpdateConfig(_ctl.Config with { HpBarColor = HpPicker.ColorHex });
    }

    private void OnStaminaColorChanged(object? sender, EventArgs e)
    {
        if (_populating) return;
        _ctl.UpdateConfig(_ctl.Config with { StaminaBarColor = StaminaPicker.ColorHex });
    }

    private void OnManaColorChanged(object? sender, EventArgs e)
    {
        if (_populating) return;
        _ctl.UpdateConfig(_ctl.Config with { ManaBarColor = ManaPicker.ColorHex });
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_populating) return;
        UpdateOpacityLabel(e.NewValue);
        _ctl.UpdateConfig(_ctl.Config with { HudBackgroundOpacity = e.NewValue });
    }

    private void OnResetHud(object sender, RoutedEventArgs e)
    {
        _ctl.ResetHudToDefaults();
        PopulateFromConfig();          // refresh the popup's own swatches + slider
        Log.Info("SettingsWindow: Reset to defaults clicked.");
        Close();
    }
}
