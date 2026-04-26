using System;
using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace GamePartyHud.Tray;

/// <summary>
/// System-tray icon + context menu. Minimal surface: everything feature-facing lives
/// in the main window; the tray just offers a way back to it, log / capture access
/// for diagnosis, and Quit.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIcon : IDisposable
{
    private static readonly Color MenuBackground = Color.FromArgb(0x1B, 0x1B, 0x1E);
    private static readonly Color MenuForeground = Color.FromArgb(0xF2, 0xF2, 0xF2);
    private static readonly Color MenuHighlight  = Color.FromArgb(0x2D, 0x2D, 0x31);
    private static readonly Color MenuBorder     = Color.FromArgb(0x3A, 0x3A, 0x3D);

    private readonly NotifyIcon _icon;

    public event Action? ShowMainWindowRequested;
    public event Action? OpenLogFolderRequested;
    public event Action? QuitRequested;

    public TrayIcon()
    {
        _icon = new NotifyIcon
        {
            Text = "Game Party HUD",
            Icon = LoadAppIcon(),
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _icon.MouseDoubleClick += (_, _) => ShowMainWindowRequested?.Invoke();
    }

    /// <summary>
    /// Extracts the .exe's embedded <c>app.ico</c> (wired via <c>&lt;ApplicationIcon&gt;</c>
    /// in the csproj). Falls back to the generic system icon if extraction fails for any
    /// reason so the tray still shows something.
    /// </summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var extracted = Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null) return extracted;
            }
        }
        catch { /* fall through to system default */ }
        return SystemIcons.Application;
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
        AddItem(m, "Open Game Party HUD",       () => ShowMainWindowRequested?.Invoke(), bold: true);
        m.Items.Add(new ToolStripSeparator());
        AddItem(m, "Open log folder",           () => OpenLogFolderRequested?.Invoke());
        m.Items.Add(new ToolStripSeparator());
        AddItem(m, "Quit",                      () => QuitRequested?.Invoke());
        return m;
    }

    private static void AddItem(ContextMenuStrip menu, string text, Action onClick, bool bold = false)
    {
        var item = new ToolStripMenuItem(text)
        {
            BackColor = MenuBackground,
            ForeColor = MenuForeground,
        };
        if (bold) item.Font = new Font(item.Font, FontStyle.Bold);
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
