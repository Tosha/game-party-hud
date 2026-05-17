using System;
using System.ComponentModel;
using System.Windows.Media;
using GamePartyHud.Config;
using GamePartyHud.Settings;

namespace GamePartyHud.Hud;

/// <summary>
/// Live view-model exposing the four HUD theme settings as bindable
/// <see cref="Brush"/> properties. <see cref="HudWindow"/>'s RootBorder
/// binds <c>Background</c> into <see cref="PanelBackgroundBrush"/>;
/// <see cref="MemberCard"/>'s three bar rows bind into the per-bar
/// brushes. Owned by <c>App</c>; refreshed in-place whenever
/// <c>AppConfig</c> changes so WPF data binding picks up new brushes
/// on the next render tick (no polling, no per-tick capture cost).
/// </summary>
public sealed class HudTheme : INotifyPropertyChanged
{
    // The shared darkening factor for the auto-derived gradient bottom
    // stop. 0.7 = 30% darker, matches the hand-tuned palette the HUD
    // shipped with before the color picker.
    public const double DarkenFactor = 0.70;

    public Brush HpBarBrush { get; private set; } = Brushes.Transparent;
    public Brush StaminaBarBrush { get; private set; } = Brushes.Transparent;
    public Brush ManaBarBrush { get; private set; } = Brushes.Transparent;
    public Brush PanelBackgroundBrush { get; private set; } = Brushes.Transparent;

    public HudTheme(AppConfig cfg) => RefreshFrom(cfg);

    public void RefreshFrom(AppConfig cfg)
    {
        var newHp      = BuildBarBrush(cfg.HpBarColor);
        var newStamina = BuildBarBrush(cfg.StaminaBarColor);
        var newMana    = BuildBarBrush(cfg.ManaBarColor);
        var newPanel   = BuildPanelBrush(cfg.HudBackgroundOpacity);

        if (!BrushEquals(newHp, HpBarBrush))             { HpBarBrush = newHp;             Raise(nameof(HpBarBrush)); }
        if (!BrushEquals(newStamina, StaminaBarBrush))   { StaminaBarBrush = newStamina;   Raise(nameof(StaminaBarBrush)); }
        if (!BrushEquals(newMana, ManaBarBrush))         { ManaBarBrush = newMana;         Raise(nameof(ManaBarBrush)); }
        if (!BrushEquals(newPanel, PanelBackgroundBrush)){ PanelBackgroundBrush = newPanel;Raise(nameof(PanelBackgroundBrush)); }
    }

    private static Brush BuildBarBrush(string hex)
    {
        var parsed = HudColor.TryParse(hex);
        if (parsed is null) return Brushes.Transparent;
        var (a, r, g, b) = parsed.Value;
        var darker = HudColor.Darken((r, g, b), DarkenFactor);
        return new LinearGradientBrush(
            Color.FromArgb(a, r, g, b),
            Color.FromArgb(a, darker.R, darker.G, darker.B),
            new System.Windows.Point(0, 0),
            new System.Windows.Point(0, 1));
    }

    private static Brush BuildPanelBrush(double opacity)
    {
        // Today's two stops, with the alpha byte swapped for the user's
        // chosen opacity (clamped at the ConfigStore.Load layer).
        byte alpha = (byte)Math.Round(Math.Clamp(opacity, 0.0, 1.0) * 255.0);
        return new LinearGradientBrush(
            Color.FromArgb(alpha, 0x1B, 0x1B, 0x1E),
            Color.FromArgb(alpha, 0x12, 0x12, 0x15),
            new System.Windows.Point(0, 0),
            new System.Windows.Point(0, 1));
    }

    // Reference equality isn't enough (every Refresh builds new
    // LinearGradientBrush instances); compare the underlying GradientStop
    // colours to avoid pointless PropertyChanged notifications.
    private static bool BrushEquals(Brush a, Brush b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is LinearGradientBrush la && b is LinearGradientBrush lb)
        {
            if (la.GradientStops.Count != lb.GradientStops.Count) return false;
            for (int i = 0; i < la.GradientStops.Count; i++)
            {
                if (la.GradientStops[i].Color != lb.GradientStops[i].Color) return false;
                if (la.GradientStops[i].Offset != lb.GradientStops[i].Offset) return false;
            }
            return true;
        }
        return false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
