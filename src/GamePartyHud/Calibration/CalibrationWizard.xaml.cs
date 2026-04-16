using System;
using System.Threading.Tasks;
using System.Windows;
using GamePartyHud.Capture;
using GamePartyHud.Config;
using GamePartyHud.Party;

namespace GamePartyHud.Calibration;

public partial class CalibrationWizard : Window
{
    public AppConfig? Result { get; private set; }

    private readonly AppConfig _initial;
    private readonly IScreenCapture _capture;
    private readonly OcrService _ocr;
    private HpCalibration? _hpCal;
    private HpRegion? _nickRegion;
    private string _ocrText = "";

    public CalibrationWizard(AppConfig initial, IScreenCapture capture, OcrService ocr)
    {
        InitializeComponent();
        _initial = initial;
        _capture = capture;
        _ocr = ocr;

        RoleCombo.ItemsSource = Enum.GetValues<Role>();
        RoleCombo.SelectedItem = initial.Role;
        NickText.Text = initial.Nickname;

        if (initial.HpCalibration is { } cal)
        {
            _hpCal = cal;
            HpStatus.Text = $"Loaded saved region {cal.Region.W}\u00D7{cal.Region.H} at ({cal.Region.X}, {cal.Region.Y}).";
        }
        if (initial.NicknameRegion is { } nr)
        {
            _nickRegion = nr;
            NickStatus.Text = $"Loaded saved region {nr.W}\u00D7{nr.H}.";
        }

        Steps.SelectionChanged += (_, _) => UpdateButtons();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        BackBtn.IsEnabled = Steps.SelectedIndex > 0;
        NextBtn.Content = Steps.SelectedIndex == 3 ? "Save" : "Next";
    }

    private async void OnPickHp(object sender, RoutedEventArgs e)
    {
        Hide();
        try
        {
            var picker = new RegionSelectorWindow("Full HP \u2014 drag around your HP bar");
            picker.ShowDialog();
            if (picker.Result is not { } region) return;

            var bgra = await _capture.CaptureBgraAsync(region);
            var color = SampleFullColor(bgra, region.W, region.H);
            _hpCal = new HpCalibration(region, color, HsvTolerance.Default, FillDirection.LTR);
            HpStatus.Text =
                $"Captured {region.W}\u00D7{region.H} px at ({region.X}, {region.Y}). " +
                $"Full-HP color: H={color.H:F0}\u00B0, S={color.S:F2}, V={color.V:F2}";
        }
        catch (Exception ex)
        {
            HpStatus.Text = "Error: " + ex.Message;
            HpStatus.Foreground = System.Windows.Media.Brushes.DarkRed;
        }
        finally
        {
            Show();
            Activate();
        }
    }

    private static Hsv SampleFullColor(byte[] bgra, int w, int h)
    {
        // Average the middle horizontal strip, ignoring the outer 25% on each side
        // to avoid border/shadow pixels.
        int y0 = Math.Max(0, h / 2 - 1);
        int y1 = Math.Min(h - 1, h / 2 + 1);
        double sr = 0, sg = 0, sb = 0;
        int n = 0;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = w / 4; x < w * 3 / 4; x++)
            {
                int i = (y * w + x) * 4;
                sb += bgra[i];
                sg += bgra[i + 1];
                sr += bgra[i + 2];
                n++;
            }
        }
        if (n == 0) return new Hsv(0, 0, 0);
        return Hsv.FromBgra(
            (byte)Math.Clamp(sb / n, 0, 255),
            (byte)Math.Clamp(sg / n, 0, 255),
            (byte)Math.Clamp(sr / n, 0, 255));
    }

    private async void OnPickNick(object sender, RoutedEventArgs e)
    {
        Hide();
        try
        {
            var picker = new RegionSelectorWindow("Drag around your character name");
            picker.ShowDialog();
            if (picker.Result is not { } region) return;

            _nickRegion = region;
            var bgra = await _capture.CaptureBgraAsync(region);
            try
            {
                _ocrText = await _ocr.RecognizeAsync(bgra, region.W, region.H);
            }
            catch
            {
                _ocrText = "";
            }

            if (string.IsNullOrWhiteSpace(_ocrText))
            {
                NickStatus.Text = "Captured. OCR didn't read any text \u2014 you can type the name in step 4.";
            }
            else
            {
                NickStatus.Text = $"Captured. OCR read: \"{_ocrText}\"";
                NickText.Text = _ocrText;
            }
        }
        catch (Exception ex)
        {
            NickStatus.Text = "Error: " + ex.Message;
            NickStatus.Foreground = System.Windows.Media.Brushes.DarkRed;
        }
        finally
        {
            Show();
            Activate();
        }
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (Steps.SelectedIndex > 0) Steps.SelectedIndex--;
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (Steps.SelectedIndex < 3)
        {
            Steps.SelectedIndex++;
            return;
        }

        var role = RoleCombo.SelectedItem is Role r ? r : _initial.Role;
        Result = _initial with
        {
            HpCalibration = _hpCal ?? _initial.HpCalibration,
            NicknameRegion = _nickRegion ?? _initial.NicknameRegion,
            Nickname = string.IsNullOrWhiteSpace(NickText.Text)
                ? _initial.Nickname
                : NickText.Text.Trim(),
            Role = role
        };
        DialogResult = true;
        Close();
    }
}
