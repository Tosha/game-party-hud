using System;
using System.Threading.Tasks;
using System.Windows;
using GamePartyHud.Capture;
using GamePartyHud.Config;
using GamePartyHud.Party;
using Wpf.Ui.Controls;

namespace GamePartyHud.Calibration;

public partial class CalibrationWizard : FluentWindow
{
    public AppConfig? Result { get; private set; }

    private readonly AppConfig _initial;
    private readonly IScreenCapture _capture;
    private readonly IOcrService _ocr;
    private HpCalibration? _hpCal;
    private HpRegion? _nickRegion;

    public CalibrationWizard(AppConfig initial, IScreenCapture capture, IOcrService ocr)
    {
        InitializeComponent();
        _initial = initial;
        _capture = capture;
        _ocr = ocr;

        RoleCombo.ItemsSource = Enum.GetValues<Role>();
        RoleCombo.SelectedItem = initial.Role;
        NickText.Text = initial.Nickname;

        if (initial.HpCalibration is { } cal && initial.NicknameRegion is { } nr)
        {
            _hpCal = cal;
            _nickRegion = nr;
            RegionStatus.Text =
                $"Loaded saved calibration: nickname {nr.W}\u00D7{nr.H}, HP bar {cal.Region.W}\u00D7{cal.Region.H}.";
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
        Hide();
        try
        {
            var picker = new RegionSelectorWindow(
                "Drag around your character name AND HP bar together");
            picker.ShowDialog();
            if (picker.Result is not { } region) return;

            // Capture the whole selected region so we can auto-split it.
            var bgra = await _capture.CaptureBgraAsync(region);

            var bar = HpBarDetector.FindTopBar(bgra, region.W, region.H);
            if (bar is null)
            {
                RegionStatus.Foreground = System.Windows.Media.Brushes.DarkRed;
                RegionStatus.Text =
                    "Couldn't detect an HP bar in the selected region. Make sure your HP is full " +
                    "(so the bar is clearly coloured) and the selection includes the whole bar. " +
                    "Try again.";
                _hpCal = null;
                _nickRegion = null;
                return;
            }

            int barY0 = bar.Value.YStart;
            int barY1 = bar.Value.YEnd;

            // HP bar sub-region within the full selection.
            var hpRegion = new HpRegion(
                Monitor: region.Monitor,
                X: region.X,
                Y: region.Y + barY0,
                W: region.W,
                H: barY1 - barY0 + 1);

            // Nickname region = everything above the HP bar. If the bar starts at row 0
            // (user selected only the bar), fall back to an empty nickname region above.
            int nickHeight = barY0; // rows [0, barY0-1]
            var nickRegion = new HpRegion(
                Monitor: region.Monitor,
                X: region.X,
                Y: region.Y,
                W: region.W,
                H: Math.Max(0, nickHeight));

            // Sample the full-HP color from the detected bar strip (use the middle row).
            int sampleRow = barY0 + (barY1 - barY0) / 2;
            var fullColor = SampleRowColor(bgra, region.W, sampleRow);

            _hpCal = new HpCalibration(hpRegion, fullColor, HsvTolerance.Default, FillDirection.LTR);
            _nickRegion = nickRegion;

            // OCR the nickname region (if it has any rows at all).
            string ocrText = "";
            if (nickHeight > 0)
            {
                try
                {
                    var nickBgra = CropRows(bgra, region.W, 0, nickHeight);
                    ocrText = await _ocr.RecognizeAsync(nickBgra, region.W, nickHeight);
                }
                catch
                {
                    ocrText = "";
                }
            }
            if (!string.IsNullOrWhiteSpace(ocrText))
            {
                NickText.Text = ocrText;
            }

            RegionStatus.Foreground = System.Windows.Media.Brushes.DarkGreen;
            RegionStatus.Text =
                $"Detected HP bar: {hpRegion.W}\u00D7{hpRegion.H} px at screen ({hpRegion.X}, {hpRegion.Y}). " +
                $"Full-HP color: H={fullColor.H:F0}\u00B0, S={fullColor.S:F2}, V={fullColor.V:F2}. " +
                (string.IsNullOrWhiteSpace(ocrText)
                    ? "OCR didn't read a nickname \u2014 you can type it in step 3."
                    : $"OCR read nickname: \"{ocrText}\".");
        }
        catch (Exception ex)
        {
            RegionStatus.Foreground = System.Windows.Media.Brushes.DarkRed;
            RegionStatus.Text = "Error: " + ex.Message;
        }
        finally
        {
            Show();
            Activate();
        }
    }

    private static Hsv SampleRowColor(byte[] bgra, int width, int y)
    {
        // Average the middle 50% of the row to avoid bar edges.
        double sr = 0, sg = 0, sb = 0;
        int n = 0;
        for (int x = width / 4; x < width * 3 / 4; x++)
        {
            int i = (y * width + x) * 4;
            sb += bgra[i];
            sg += bgra[i + 1];
            sr += bgra[i + 2];
            n++;
        }
        if (n == 0) return new Hsv(0, 0, 0);
        return Hsv.FromBgra(
            (byte)Math.Clamp(sb / n, 0, 255),
            (byte)Math.Clamp(sg / n, 0, 255),
            (byte)Math.Clamp(sr / n, 0, 255));
    }

    private static byte[] CropRows(byte[] bgra, int width, int y0, int rowCount)
    {
        var crop = new byte[width * rowCount * 4];
        int rowBytes = width * 4;
        Buffer.BlockCopy(bgra, y0 * rowBytes, crop, 0, rowCount * rowBytes);
        return crop;
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
