using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GamePartyHud.Capture;

namespace GamePartyHud.Bars;

/// <summary>
/// One bar's setup card: header (name + optional ToggleSwitch) over a
/// content row (live preview + status icon + pick button). HP cards have
/// no toggle (always-on); Stamina/Mana cards have the toggle.
///
/// Owns a BarPreviewSource. Caller is responsible for:
///   - setting BarName + IsToggleable at XAML construction time
///   - assigning Calibration + IsBarEnabled (typically from
///     MainWindow.PopulateFromConfig after a preset switch)
///   - calling AttachCapture(capture) once the screen capture is available
///   - calling SetPickTimeValidation(result) right after a fresh pick so the
///     low-fill warning gets cached as the displayed result until next pick
///   - handling PickRequested / EnabledChanged
/// </summary>
public partial class BarCard : UserControl
{
    private BarPreviewSource? _previewSource;
    private IScreenCapture? _capture;
    private BarCalibration? _calibration;
    private bool _isBarEnabled = true;
    private bool _isWindowVisible = true;
    // True once the status icon reflects a "deliberate" validation result
    // (either pick-time via SetPickTimeValidation, or the first live tick
    // after a fresh Calibration is assigned). While false, the next live
    // tick is allowed to set the status. Once true, live ticks update the
    // preview image only — never the status — so the icon reflects the
    // user's last conscious action (pick / load), not arbitrary live
    // variance from a moved game window.
    private bool _statusApplied;
    private bool _suppressToggleEvent;

    public BarCard() => InitializeComponent();

    /// <summary>"HP" / "Stamina" / "Mana"</summary>
    public string BarName
    {
        get => HeaderText.Text;
        set
        {
            HeaderText.Text = value;
            UpdateButtonAppearance();
        }
    }

    /// <summary>If true, the header shows a ToggleSwitch. HP cards set false.</summary>
    public bool IsToggleable
    {
        get => EnableToggle.Visibility == Visibility.Visible;
        set => EnableToggle.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Current bar calibration. Setting this triggers preview lifecycle
    /// updates (Start if non-null + enabled + window visible; Stop otherwise).
    /// Resets the status-applied flag so the next live tick performs a
    /// one-time validation against the new region.
    /// </summary>
    public BarCalibration? Calibration
    {
        get => _calibration;
        set
        {
            _calibration = value;
            _statusApplied = value is null;   // null calibration => nothing to validate against
            UpdateButtonAppearance();
            UpdatePlaceholderVisibility();
            UpdatePreviewLifecycle();
        }
    }

    /// <summary>
    /// Two-way mirror of the ToggleSwitch's IsChecked. Setting this updates
    /// the switch without firing EnabledChanged (programmatic vs user-driven).
    ///
    /// No early-exit on "same value" because the backing field defaults to
    /// true and the XAML ToggleSwitch defaults to unchecked; PopulateFromConfig
    /// setting IsBarEnabled=true on the default-enabled bar would otherwise
    /// skip syncing IsChecked and the user would see the toggle visually OFF
    /// even though the bar is enabled in config. Always syncing keeps the
    /// switch in lockstep with the backing field.
    /// </summary>
    public bool IsBarEnabled
    {
        get => _isBarEnabled;
        set
        {
            _isBarEnabled = value;
            // Update the switch silently — the Checked/Unchecked handler guards
            // on _suppressToggleEvent to avoid re-raising.
            _suppressToggleEvent = true;
            EnableToggle.IsChecked = value;
            _suppressToggleEvent = false;
            UpdateBodyOpacity();
            UpdatePreviewLifecycle();
        }
    }

    public event EventHandler? PickRequested;
    public event EventHandler? EnabledChanged;

    /// <summary>
    /// Wire up the screen-capture source. Called once by MainWindow during
    /// construction; the card lazily creates a BarPreviewSource on first
    /// Calibration assignment.
    /// </summary>
    public void AttachCapture(IScreenCapture capture)
    {
        _capture = capture;
    }

    /// <summary>
    /// Called by MainWindow whenever its own IsVisibleChanged fires — the
    /// preview captures should pause while the settings window is hidden.
    /// </summary>
    public void SetWindowVisible(bool visible)
    {
        _isWindowVisible = visible;
        UpdatePreviewLifecycle();
    }

    /// <summary>
    /// Apply a pick-time validation result. Becomes the displayed status
    /// until the next Calibration reassignment (re-pick or preset switch).
    /// Live preview ticks never overwrite this.
    /// </summary>
    public void SetPickTimeValidation(ValidationResult result)
    {
        ApplyValidation(result);
        _statusApplied = true;
    }

    private void OnEnableToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleEvent) return;
        _isBarEnabled = EnableToggle.IsChecked == true;
        UpdateBodyOpacity();
        UpdatePreviewLifecycle();
        EnabledChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPickClicked(object sender, RoutedEventArgs e)
    {
        PickRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateButtonAppearance()
    {
        if (_calibration is null)
        {
            PickButton.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
            PickButton.Content = $"Pick {HeaderText.Text.ToLowerInvariant()} bar region";
        }
        else
        {
            PickButton.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
            PickButton.Content = "Re-pick";
        }
    }

    // Colors for the preview Border background:
    //   default        — dark transparent panel (lets the live capture pop)
    //   disabled grey  — flat mid-grey so the bar reads clearly as "off",
    //                    not just dimmed dark.
    private static readonly Brush DefaultPreviewBackground =
        new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00));
    private static readonly Brush DisabledPreviewBackground =
        new SolidColorBrush(Color.FromArgb(0x55, 0x88, 0x88, 0x88));

    private void UpdatePlaceholderVisibility()
    {
        bool showImage = _calibration is not null && _isBarEnabled;
        PreviewImage.Visibility = showImage ? Visibility.Visible : Visibility.Collapsed;
        PreviewPlaceholder.Visibility = showImage ? Visibility.Collapsed : Visibility.Visible;
        if (_calibration is null)
        {
            PreviewPlaceholder.Text = "Pick a region to see a live preview";
        }
        else if (!_isBarEnabled)
        {
            PreviewPlaceholder.Text = "Inactive";
        }
    }

    private void UpdateBodyOpacity()
    {
        // When disabled, swap the preview Border background to a solid grey
        // (instead of the dark-transparent default) so the bar visibly
        // reads as inactive rather than just dimmed. Keep the toggle and
        // the status icon at full opacity so the user can still see + flip
        // them; only the preview area itself goes grey.
        PreviewBorder.Background = _isBarEnabled ? DefaultPreviewBackground : DisabledPreviewBackground;
        PickButton.IsEnabled = _isBarEnabled;
        StatusIcon.Opacity = _isBarEnabled ? 1.0 : 0.4;
        UpdatePlaceholderVisibility();
    }

    private void UpdatePreviewLifecycle()
    {
        bool shouldRun =
            _capture is not null &&
            _calibration is not null &&
            _isBarEnabled &&
            _isWindowVisible;

        if (shouldRun)
        {
            _previewSource ??= new BarPreviewSource(
                _capture!,
                () => _calibration,
                OnPreviewUpdated);
            _previewSource.Start();
        }
        else
        {
            _previewSource?.Stop();
        }
    }

    private void OnPreviewUpdated(WriteableBitmap? bitmap, ValidationResult result)
    {
        if (bitmap is not null)
        {
            PreviewImage.Source = bitmap;
            PreviewImage.Visibility = Visibility.Visible;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
        }

        // Validation only runs once per Calibration: the very first live tick
        // after a Calibration assignment seeds the status icon, then we lock
        // it in. Subsequent live ticks update the image only. The user's last
        // conscious action (pick / load) is what the icon reflects — random
        // variance from a moved game window doesn't flicker the status.
        if (!_statusApplied)
        {
            ApplyValidation(result);
            _statusApplied = true;
        }
    }

    private void ApplyValidation(ValidationResult result)
    {
        StatusIcon.Fill = result.Level switch
        {
            ValidationLevel.Ok      => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
            ValidationLevel.Warning => new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)),
            ValidationLevel.Error   => new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)),
            _                       => new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
        };
        StatusIcon.ToolTip = result.Message;
    }
}
