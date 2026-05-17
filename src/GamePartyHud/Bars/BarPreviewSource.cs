using System;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GamePartyHud.Capture;
using GamePartyHud.Diagnostics;

namespace GamePartyHud.Bars;

/// <summary>
/// Per-bar capture timer. Every ~333 ms (3 Hz) while running, grabs the
/// region returned by <c>getCalibration()</c>, validates it, and invokes
/// <c>onUpdate</c> on the UI thread with a refreshed <see cref="WriteableBitmap"/>
/// plus the <see cref="ValidationResult"/>.
///
/// The bitmap is reused across frames to avoid per-tick allocations; when
/// the captured region's width or height changes (typical case: user
/// re-picks the bar) the old bitmap is discarded and a new one is created
/// at the new dimensions.
///
/// Start/Stop are idempotent. Dispose stops the timer and releases the bitmap.
/// </summary>
public sealed class BarPreviewSource : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(333);

    private readonly IScreenCapture _capture;
    private readonly Func<BarCalibration?> _getCalibration;
    private readonly Action<WriteableBitmap?, ValidationResult> _onUpdate;
    private readonly DispatcherTimer _timer;

    private WriteableBitmap? _bitmap;
    private int _bitmapW;
    private int _bitmapH;
    private CancellationTokenSource? _inFlightCts;

    public BarPreviewSource(
        IScreenCapture capture,
        Func<BarCalibration?> getCalibration,
        Action<WriteableBitmap?, ValidationResult> onUpdate)
    {
        _capture = capture;
        _getCalibration = getCalibration;
        _onUpdate = onUpdate;
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += OnTick;
    }

    public void Start()
    {
        if (_timer.IsEnabled) return;
        _timer.Start();
    }

    public void Stop()
    {
        if (!_timer.IsEnabled) return;
        _timer.Stop();
        _inFlightCts?.Cancel();
        _inFlightCts = null;
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        var cal = _getCalibration();
        if (cal is null) return;

        // Cancel any in-flight capture from a previous tick (shouldn't happen
        // at 333 ms with a fast BitBlt, but defensive).
        _inFlightCts?.Cancel();
        var cts = _inFlightCts = new CancellationTokenSource();

        byte[] bgra;
        try
        {
            var result = await _capture.CaptureBgraAsync(cal.Region, cts.Token).ConfigureAwait(true);
            bgra = result;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log.Warn("BarPreviewSource: capture failed: " + ex.Message);
            return;
        }

        if (cts.IsCancellationRequested) return;

        int w = cal.Region.W;
        int h = cal.Region.H;
        if (w <= 0 || h <= 0 || bgra.Length < w * h * 4) return;

        // Reuse the WriteableBitmap if the dimensions match; otherwise re-create.
        if (_bitmap is null || _bitmapW != w || _bitmapH != h)
        {
            _bitmap = new WriteableBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            _bitmapW = w;
            _bitmapH = h;
        }

        var rect = new Int32Rect(0, 0, w, h);
        _bitmap.WritePixels(rect, bgra, w * 4, 0);

        var validation = BarRegionValidator.Validate(cal.Region, bgra, isPickTime: false);
        _onUpdate(_bitmap, validation);
    }

    public void Dispose()
    {
        Stop();
        _bitmap = null;
    }
}
