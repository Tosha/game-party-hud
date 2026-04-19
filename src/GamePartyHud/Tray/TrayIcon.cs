using System;
using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace GamePartyHud.Tray;

/// <summary>System-tray icon + context menu. Emits events that App.xaml.cs wires to handlers.</summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIcon : IDisposable
{
    private static readonly Color MenuBackground = Color.FromArgb(0x1B, 0x1B, 0x1E);
    private static readonly Color MenuForeground = Color.FromArgb(0xF2, 0xF2, 0xF2);
    private static readonly Color MenuHighlight  = Color.FromArgb(0x2D, 0x2D, 0x31);
    private static readonly Color MenuBorder     = Color.FromArgb(0x3A, 0x3A, 0x3D);

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
        var m = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new DarkGreyColorTable()) { RoundedEdges = false },
            BackColor = MenuBackground,
            ForeColor = MenuForeground,
            ShowImageMargin = false,
        };
        AddItem(m, "Calibrate character\u2026", () => CalibrateRequested?.Invoke());
        AddItem(m, "Change nickname\u2026",     () => ChangeNicknameRequested?.Invoke());
        AddItem(m, "Change role\u2026",         () => ChangeRoleRequested?.Invoke());
        m.Items.Add(new ToolStripSeparator());
        AddItem(m, "Create party",              () => CreatePartyRequested?.Invoke());
        AddItem(m, "Join party\u2026",          () => JoinPartyRequested?.Invoke());
        AddItem(m, "Copy party ID",             () => CopyPartyIdRequested?.Invoke());
        m.Items.Add(new ToolStripSeparator());
        AddItem(m, "Save test capture\u2026",   () => SaveTestCaptureRequested?.Invoke());
        AddItem(m, "Open log folder",           () => OpenLogFolderRequested?.Invoke());
        AddItem(m, "Quit",                      () => QuitRequested?.Invoke());
        return m;
    }

    private static void AddItem(ContextMenuStrip menu, string text, Action onClick)
    {
        var item = new ToolStripMenuItem(text)
        {
            BackColor = MenuBackground,
            ForeColor = MenuForeground,
        };
        item.Click += (_, _) => onClick();
        menu.Items.Add(item);
    }

    private sealed class DarkGreyColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground   => MenuBackground;
        public override Color MenuBorder                    => TrayIcon.MenuBorder;
        public override Color MenuItemBorder                => TrayIcon.MenuBorder;
        public override Color MenuItemSelected              => MenuHighlight;
        public override Color MenuItemSelectedGradientBegin => MenuHighlight;
        public override Color MenuItemSelectedGradientEnd   => MenuHighlight;
        public override Color MenuItemPressedGradientBegin  => MenuHighlight;
        public override Color MenuItemPressedGradientEnd    => MenuHighlight;
        public override Color ImageMarginGradientBegin      => MenuBackground;
        public override Color ImageMarginGradientMiddle     => MenuBackground;
        public override Color ImageMarginGradientEnd        => MenuBackground;
        public override Color SeparatorDark                 => TrayIcon.MenuBorder;
        public override Color SeparatorLight                => MenuBackground;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
