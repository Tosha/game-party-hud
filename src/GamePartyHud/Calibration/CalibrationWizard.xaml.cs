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
        NickText.Text = initial.Nickname;

        if (initial.HpCalibration is { } cal)
        {
            _hpCal = cal;
            RegionStatus.Text =
                $"Loaded saved HP bar region: {cal.Region.W}\u00D7{cal.Region.H} at ({cal.Region.X}, {cal.Region.Y}). " +
                $"Full-HP HSV=(H={cal.FullColor.H:F0}\u00B0, S={cal.FullColor.S:F2}, V={cal.FullColor.V:F2}).";
        }

        Steps.SelectionChanged += (_, _) => UpdateButtons();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        BackBtn.IsEnabled = Steps.SelectedIndex > 0;
        NextBtn.Content = Steps.SelectedIndex == 2 ? "Save" : "Next";
    }

    private async void OnPickRegion(object sender, RoutedEventArgs e)
    {
        Log.Info("CalibrationWizard: Pick-region button clicked.");
        // Hide via opacity rather than Hide() — Hide() on a ShowDialog'd window exits the modal loop.
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
            Log.Info($"CalibrationWizard: captured {bgra.Length} bytes of BGRA pixels.");

            var fullColor = SampleFullColor(bgra, region.W, region.H);
            _hpCal = new HpCalibration(region, fullColor, HsvTolerance.Default, FillDirection.LTR);

            RegionStatus.Foreground = System.Windows.Media.Brushes.DarkGreen;
            RegionStatus.Text =
                $"Captured {region.W}\u00D7{region.H} at ({region.X}, {region.Y}). " +
                $"Full-HP color: H={fullColor.H:F0}\u00B0, S={fullColor.S:F2}, V={fullColor.V:F2}.";
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
    /// Sample the full-HP color by averaging pixels from the top band and the bottom band
    /// of the captured HP bar, skipping the middle where games typically overlay numeric
    /// labels like "246/246" (whose white-ish text pixels would pull the average toward grey).
    /// </summary>
    private static Hsv SampleFullColor(byte[] bgra, int w, int h)
    {
        int band = Math.Max(1, h / 5); // ~20% of height per band
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

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (Steps.SelectedIndex > 0) Steps.SelectedIndex--;
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (Steps.SelectedIndex < 2)
        {
            Steps.SelectedIndex++;
            return;
        }

        try
        {
            var role = RoleCombo.SelectedItem is Role r ? r : _initial.Role;
            var nick = string.IsNullOrWhiteSpace(NickText.Text)
                ? _initial.Nickname
                : NickText.Text.Trim();
            Result = _initial with
            {
                HpCalibration = _hpCal ?? _initial.HpCalibration,
                NicknameRegion = null, // no longer captured — manual entry only
                Nickname = nick,
                Role = role
            };
            Log.Info($"CalibrationWizard: saving result. HP calibrated={(_hpCal is not null)}, Nickname='{nick}', Role={role}.");
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
