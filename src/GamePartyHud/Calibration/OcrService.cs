using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;

namespace GamePartyHud.Calibration;

/// <summary>OCR abstraction. <see cref="OcrService"/> is the real impl; <see cref="NullOcrService"/> is the no-op fallback.</summary>
public interface IOcrService
{
    Task<string> RecognizeAsync(byte[] bgra, int width, int height);
}

/// <summary>Returns empty text always. Used when Windows OCR isn't available on the machine.</summary>
public sealed class NullOcrService : IOcrService
{
    public Task<string> RecognizeAsync(byte[] bgra, int width, int height) => Task.FromResult(string.Empty);
}

/// <summary>Wrapper over <see cref="OcrEngine"/>. Creates a single engine once and reuses it.</summary>
public sealed class OcrService : IOcrService
{
    private readonly OcrEngine _engine;

    public OcrService()
    {
        _engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
            ?? OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException(
                "No OCR engine available on this system. Install an OCR language pack in Windows Settings → Time & Language → Language.");
    }

    public async Task<string> RecognizeAsync(byte[] bgra, int width, int height)
    {
        if (bgra.Length < width * height * 4)
            throw new ArgumentException("Buffer too small for the given dimensions.", nameof(bgra));

        var buffer = CryptographicBuffer.CreateFromByteArray(bgra);
        var bmp = SoftwareBitmap.CreateCopyFromBuffer(
            source: buffer,
            format: BitmapPixelFormat.Bgra8,
            width: width,
            height: height,
            alpha: BitmapAlphaMode.Premultiplied);

        var result = await _engine.RecognizeAsync(bmp);
        return string.Join(" ", result.Lines.Select(l => l.Text)).Trim();
    }
}
