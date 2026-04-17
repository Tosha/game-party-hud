using System;
using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace GamePartyHud.Tray;

/// <summary>System-tray icon + context menu. Emits events that App.xaml.cs wires to handlers.</summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public event Action? CalibrateRequested;
    public event Action? CreatePartyRequested;
    public event Action? JoinPartyRequested;
    public event Action? CopyPartyIdRequested;
    public event Action? ChangeNicknameRequested;
    public event Action? ChangeRoleRequested;
    public event Action? OpenLogFolderRequested;
    public event Action? SaveTestCaptureRequested;
    public event Action? QuitRequested;

    public TrayIcon()
    {
        _icon = new NotifyIcon
        {
            Text = "Game Party HUD",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
    }

    public void SetPartyId(string? id)
    {
        _icon.Text = id is null ? "Game Party HUD" : $"Game Party HUD \u2014 party {id}";
    }

    private ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("Calibrate character\u2026", null, (_, _) => CalibrateRequested?.Invoke());
        m.Items.Add("Change nickname\u2026",     null, (_, _) => ChangeNicknameRequested?.Invoke());
        m.Items.Add("Change role\u2026",         null, (_, _) => ChangeRoleRequested?.Invoke());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Create party",          null, (_, _) => CreatePartyRequested?.Invoke());
        m.Items.Add("Join party\u2026",         null, (_, _) => JoinPartyRequested?.Invoke());
        m.Items.Add("Copy party ID",         null, (_, _) => CopyPartyIdRequested?.Invoke());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Save test capture\u2026", null, (_, _) => SaveTestCaptureRequested?.Invoke());
        m.Items.Add("Open log folder",       null, (_, _) => OpenLogFolderRequested?.Invoke());
        m.Items.Add("Quit",                  null, (_, _) => QuitRequested?.Invoke());
        return m;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
