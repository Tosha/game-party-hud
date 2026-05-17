using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace GamePartyHud.Settings;

/// <summary>
/// Inline swatch + popup color picker. Picker UI inside the popup:
/// 200x160 SV (saturation/value) square + vertical hue strip + hex
/// input + 200x22 preview gradient (top = picked color, bottom =
/// HudColor.Darken(picked, 0.7) — matches the runtime HUD render).
/// Outside-click commits, Escape reverts to the popup-open value.
/// </summary>
public partial class BarColorPicker : UserControl
{
    public static readonly DependencyProperty ColorHexProperty =
        DependencyProperty.Register(
            nameof(ColorHex), typeof(string), typeof(BarColorPicker),
            new FrameworkPropertyMetadata("#FFFFFFFF",
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnColorHexChanged));

    public string ColorHex
    {
        get => (string)GetValue(ColorHexProperty);
        set => SetValue(ColorHexProperty, value);
    }

    public event EventHandler? ColorChanged;

    private const double SvW = 200;
    private const double SvH = 160;
    private const double HueH = 160;

    // Internal HSV state (single source of truth while popup open).
    private double _h, _s, _v;
    private bool _suppressFeedback;
    private bool _draggingSv;
    private bool _draggingHue;
    private string? _revertSnapshot;

    public BarColorPicker() => InitializeComponent();

    private static void OnColorHexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (BarColorPicker)d;
        if (picker._suppressFeedback) return;
        picker.SeedFromHex((string)e.NewValue);
    }

    private void OnSwatchClicked(object sender, MouseButtonEventArgs e) =>
        PickerPopup.IsOpen = !PickerPopup.IsOpen;

    private void OnPopupOpened(object sender, EventArgs e)
    {
        _revertSnapshot = ColorHex;
        SeedFromHex(ColorHex);
        HexInput.Text = ColorHex;
    }

    private void OnPopupClosed(object sender, EventArgs e)
    {
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _revertSnapshot is not null)
        {
            _suppressFeedback = true;
            try
            {
                ColorHex = _revertSnapshot;
                HexInput.Text = _revertSnapshot;
                SeedFromHex(_revertSnapshot);
            }
            finally { _suppressFeedback = false; }
            PickerPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void OnSvSquareMouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingSv = true;
        ((UIElement)sender).CaptureMouse();
        UpdateFromSv(e.GetPosition((IInputElement)sender));
    }
    private void OnSvSquareMouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingSv) return;
        UpdateFromSv(e.GetPosition((IInputElement)sender));
    }
    private void OnSvSquareMouseUp(object sender, MouseButtonEventArgs e)
    {
        _draggingSv = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void OnHueStripMouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingHue = true;
        ((UIElement)sender).CaptureMouse();
        UpdateFromHue(e.GetPosition((IInputElement)sender).Y);
    }
    private void OnHueStripMouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingHue) return;
        UpdateFromHue(e.GetPosition((IInputElement)sender).Y);
    }
    private void OnHueStripMouseUp(object sender, MouseButtonEventArgs e)
    {
        _draggingHue = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void UpdateFromSv(Point p)
    {
        _s = Math.Clamp(p.X / SvW, 0.0, 1.0);
        _v = 1.0 - Math.Clamp(p.Y / SvH, 0.0, 1.0);
        ApplyHsv();
    }

    private void UpdateFromHue(double y)
    {
        _h = Math.Clamp(y / HueH, 0.0, 0.999) * 360.0;
        SvHueBrush.Color = ToColor(HudColor.HsvToRgb(_h, 1.0, 1.0));
        ApplyHsv();
    }

    private void OnHexInputChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressFeedback) return;
        var parsed = HudColor.TryParse(HexInput.Text);
        if (parsed is null)
        {
            HexInput.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
            HexInput.ToolTip = "Use #RRGGBB or #AARRGGBB";
            return;
        }
        HexInput.BorderBrush = SystemColors.ControlDarkBrush;
        HexInput.ToolTip = null;
        var (_, r, g, b) = parsed.Value;
        (_h, _s, _v) = HudColor.RgbToHsv(r, g, b);
        SvHueBrush.Color = ToColor(HudColor.HsvToRgb(_h, 1.0, 1.0));
        UpdateCursors();
        UpdatePreview();
        // Push the just-parsed hex up to the DP, but don't loop back through
        // the TextChanged handler.
        _suppressFeedback = true;
        try { ColorHex = HexInput.Text; }
        finally { _suppressFeedback = false; }
    }

    private void SeedFromHex(string hex)
    {
        var parsed = HudColor.TryParse(hex);
        if (parsed is null) return;
        var (_, r, g, b) = parsed.Value;
        (_h, _s, _v) = HudColor.RgbToHsv(r, g, b);
        SvHueBrush.Color = ToColor(HudColor.HsvToRgb(_h, 1.0, 1.0));
        UpdateCursors();
        UpdatePreview();
    }

    private void ApplyHsv()
    {
        UpdateCursors();
        UpdatePreview();
        var rgb = HudColor.HsvToRgb(_h, _s, _v);
        var hex = HudColor.Format(0xFF, rgb.R, rgb.G, rgb.B);
        _suppressFeedback = true;
        try
        {
            HexInput.Text = hex;
            ColorHex = hex;
        }
        finally { _suppressFeedback = false; }
    }

    private void UpdateCursors()
    {
        // SV cursor — center on the picked S/V.
        Canvas.SetLeft(SvCursor, _s * SvW - 5);
        Canvas.SetTop(SvCursor, (1.0 - _v) * SvH - 5);
        // Hue cursor — vertical position along the strip.
        double y = (_h / 360.0) * HueH - 1.5;
        Canvas.SetTop(HueCursor, y);
    }

    private void UpdatePreview()
    {
        var rgb = HudColor.HsvToRgb(_h, _s, _v);
        var darker = HudColor.Darken(rgb, 0.70);
        var top = ToColor(rgb);
        var bot = Color.FromRgb(darker.R, darker.G, darker.B);
        PreviewBorder.Background = new LinearGradientBrush(top, bot,
            new Point(0, 0), new Point(0, 1));
    }

    private static Color ToColor((byte R, byte G, byte B) rgb) =>
        Color.FromRgb(rgb.R, rgb.G, rgb.B);
}

/// <summary>
/// Tiny value converter so the closed-state swatch's Background reacts
/// to the ColorHex DP without code-behind plumbing.
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && HudColor.TryParse(hex) is (byte a, byte r, byte g, byte b))
            return new SolidColorBrush(Color.FromArgb(a, r, g, b));
        return Brushes.Transparent;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
