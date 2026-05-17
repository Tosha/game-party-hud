using System.Windows;
using GamePartyHud.Diagnostics;
using Wpf.Ui.Controls;

namespace GamePartyHud;

/// <summary>
/// Small modal dialog opened from the gear icon on <see cref="MainWindow"/>.
/// Currently exposes only the Reset HUD position action. Sized via
/// SizeToContent so the dialog grows naturally as more settings are added.
/// </summary>
public partial class SettingsWindow : FluentWindow
{
    private readonly MainWindow.IController _ctl;

    public SettingsWindow(MainWindow.IController controller)
    {
        InitializeComponent();
        _ctl = controller;
    }

    private void OnResetHud(object sender, RoutedEventArgs e)
    {
        _ctl.ResetHudToDefaults();
        Log.Info("SettingsWindow: Reset to defaults clicked.");
        Close();
    }
}
