using System;
using System.Windows;
using GamePartyHud.Calibration;
using GamePartyHud.Capture;
using GamePartyHud.Config;
using GamePartyHud.Tray;

namespace GamePartyHud;

public partial class App : Application
{
    private TrayIcon? _tray;
    private ConfigStore? _store;
    private AppConfig _config = AppConfig.Defaults;
    private WindowsScreenCapture? _capture;
    private IOcrService _ocr = new NullOcrService();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

#if DEBUG
        foreach (var arg in e.Args)
        {
            if (string.Equals(arg, GamePartyHud.Hud.HudSmokeHarness.CliFlag, StringComparison.Ordinal))
            {
                GamePartyHud.Hud.HudSmokeHarness.Run(this);
                return;
            }
        }
#endif

        _store = new ConfigStore();
        _config = _store.Load();
        _capture = new WindowsScreenCapture();

        try
        {
            _ocr = new OcrService();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "OCR is not available on this system. Nickname will need to be typed manually during calibration.\n\n" + ex.Message,
                "Game Party HUD",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        _tray = new TrayIcon();
        _tray.CalibrateRequested += RunCalibration;
        _tray.ChangeNicknameRequested += RunChangeNickname;
        _tray.ChangeRoleRequested += RunChangeRole;
        _tray.CreatePartyRequested += ShowM5PendingNotice;
        _tray.JoinPartyRequested += ShowM5PendingNotice;
        _tray.CopyPartyIdRequested += ShowM5PendingNotice;
        _tray.QuitRequested += Shutdown;
    }

    private static void ShowM5PendingNotice()
    {
        MessageBox.Show(
            "Party features (create / join / copy ID) arrive in the next milestone. " +
            "For now, you can calibrate your HP region, change your nickname, and change your role.",
            "Game Party HUD",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void RunCalibration()
    {
        var wiz = new CalibrationWizard(_config, _capture!, _ocr);
        if (wiz.ShowDialog() == true && wiz.Result is { } updated)
        {
            _config = updated;
            _store!.Save(_config);
        }
    }

    private void RunChangeNickname()
    {
        var dlg = new RenameDialog(_config.Nickname);
        if (dlg.ShowDialog() == true && dlg.Value is { Length: > 0 } v)
        {
            _config = _config with { Nickname = v };
            _store!.Save(_config);
        }
    }

    private void RunChangeRole()
    {
        var dlg = new RolePickerDialog(_config.Role);
        if (dlg.ShowDialog() == true && dlg.Value is { } r)
        {
            _config = _config with { Role = r };
            _store!.Save(_config);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _capture?.Dispose();
        base.OnExit(e);
    }
}
