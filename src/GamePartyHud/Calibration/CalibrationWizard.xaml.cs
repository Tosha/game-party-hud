using System;
using System.Threading.Tasks;
using System.Windows;
using GamePartyHud.Capture;
using GamePartyHud.Config;
using GamePartyHud.Diagnostics;
using GamePartyHud.Party;
using Wpf.Ui.Controls;

namespace GamePartyHud.Calibration;

public partial class CalibrationWizard : FluentWindow
{
    public AppConfig? Result { get; private set; }

    private readonly AppConfig _initial;
    private readonly IScreenCapture _capture;
    private HpCalibration? _hpCal;

    public CalibrationWizard(AppConfig initial, IScreenCapture capture)
    {
        InitializeComponent();
        _initial = initial;
        _capture = capture;

        RoleCombo.ItemsSource = Enum.GetValues<Role>();
        RoleCombo.SelectedItem = initial.Role;
        NickText.Text = initial.Nickname == AppConfig.Defaults.Nickname ? "" : initial.Nickname;

        if (initial.HpCalibration is { } cal)
        {
            _hpCal = cal;
            RegionStatus.Text =
                $"Saved region {cal.Region.W}\u00D7{cal.Region.H} at ({cal.Region.X}, {cal.Region.Y}). " +
                $"Full-HP HSV=(H={cal.FullColor.H:F0}\u00B0, S={cal.FullColor.S:F2}, V={cal.FullColor.V:F2}).";
        }
    }

    private async void OnPickRegion(object sender, RoutedEventArgs e)
    {
        Log.Info("CalibrationWizard: Pick-region button clicked.");
        // Opacity=0 (not Hide()) — Hide() on a ShowDialog'd window exits the modal loop.
        Opacity = 0;
        try
        {
            var picker = new RegionSelectorWindow(
                "Drag a box around your HP bar ONLY (no nickname, no other bars)");
            picker.ShowDialog();
            if (picker.Result is not { } region)
            {
                Log.Info("CalibrationWizard: region selection cancelled.");
                return;
            }
            Log.Info($"CalibrationWizard: region selected {region.W}x{region.H} at ({region.X},{region.Y}).");

            var bgra = await _capture.CaptureBgraAsync(region).ConfigureAwait(true);
            var fullColor = SampleFullColor(bgra, region.W, region.H);
            _hpCal = new HpCalibration(region, fullColor, HsvTolerance.Default, FillDirection.LTR);

            RegionStatus.Foreground = System.Windows.Media.Brushes.DarkGreen;
            RegionStatus.Text =
                $"Captured {region.W}\u00D7{region.H} at ({region.X}, {region.Y}). " +
                $"Full-HP: H={fullColor.H:F0}\u00B0, S={fullColor.S:F2}, V={fullColor.V:F2}.";
            Log.Info($"CalibrationWizard: HP calibrated. Full-HP HSV=({fullColor.H:F1}, {fullColor.S:F3}, {fullColor.V:F3}).");
        }
        catch (Exception ex)
        {
            Log.Error("CalibrationWizard: OnPickRegion failed.", ex);
            RegionStatus.Foreground = System.Windows.Media.Brushes.DarkRed;
            RegionStatus.Text = "Error: " + ex.Message;
        }
        finally
        {
            Opacity = 1;
            Activate();
        }
    }

    /// <summary>
    /// Average pixels from the top and bottom bands of the HP bar, skipping the middle
    /// where games typically overlay numeric labels like "246/246".
    /// </summary>
    private static Hsv SampleFullColor(byte[] bgra, int w, int h)
    {
        int band = Math.Max(1, h / 5);
        double sr = 0, sg = 0, sb = 0;
        int n = 0;

        void AddRow(int y)
        {
            int x0 = w / 4;
            int x1 = w * 3 / 4;
            for (int x = x0; x < x1; x++)
            {
                int i = (y * w + x) * 4;
                sb += bgra[i];
                sg += bgra[i + 1];
                sr += bgra[i + 2];
                n++;
            }
        }

        for (int y = 0; y < Math.Min(band, h); y++) AddRow(y);
        for (int y = Math.Max(0, h - band); y < h; y++) AddRow(y);

        if (n == 0) return new Hsv(0, 0, 0);
        return Hsv.FromBgra(
            (byte)Math.Clamp(sb / n, 0, 255),
            (byte)Math.Clamp(sg / n, 0, 255),
            (byte)Math.Clamp(sr / n, 0, 255));
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            var effectiveHpCal = _hpCal ?? _initial.HpCalibration;
            if (effectiveHpCal is null)
            {
                System.Windows.MessageBox.Show(
                    "Please pick your HP bar region first.",
                    "Game Party HUD",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }

            var nick = NickText.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(nick))
            {
                System.Windows.MessageBox.Show(
                    "Please enter a nickname.",
                    "Game Party HUD",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                NickText.Focus();
                return;
            }

            var role = RoleCombo.SelectedItem is Role r ? r : _initial.Role;

            Result = _initial with
            {
                HpCalibration = effectiveHpCal,
                NicknameRegion = null,
                Nickname = nick,
                Role = role
            };
            Log.Info($"CalibrationWizard: saving result. Nickname='{nick}', Role={role}.");
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("CalibrationWizard: Save step threw.", ex);
            throw;
        }
    }
}
