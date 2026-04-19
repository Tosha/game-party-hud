using System;

namespace GamePartyHud.Capture;

/// <summary>
/// Rolling-median temporal filter for HP readings.
///
/// Why median instead of EMA: live game captures occasionally produce a spike or dip
/// (shimmer animation, HP-bar flash, capture racing with a game render frame, etc.).
/// A single outlier can push an EMA several percentage points away from reality and
/// stays visible for multiple ticks as it decays. A median of the last N samples
/// rejects any outlier that doesn't persist for ⌈N/2⌉ samples in a row — so a
/// one-tick spike is gone instantly, while a real sustained change still propagates
/// through cleanly with at most (N/2) ticks of lag.
/// </summary>
public sealed class HpSmoother
{
    private readonly float?[] _window;
    private int _cursor;

    public HpSmoother(int windowSize = 3)
    {
        if (windowSize < 1) throw new ArgumentOutOfRangeException(nameof(windowSize));
        _window = new float?[windowSize];
    }

    public float Push(float sample)
    {
        _window[_cursor] = sample;
        _cursor = (_cursor + 1) % _window.Length;

        // Copy populated slots into a small array and sort.
        Span<float> sorted = stackalloc float[_window.Length];
        int count = 0;
        foreach (var v in _window)
            if (v is { } x) sorted[count++] = x;
        sorted = sorted[..count];
        sorted.Sort();

        // Classic median: middle value for odd count, mean of the two middles for even count.
        if (count % 2 == 1) return sorted[count / 2];
        return (sorted[count / 2 - 1] + sorted[count / 2]) / 2f;
    }

    public void Reset()
    {
        for (int i = 0; i < _window.Length; i++) _window[i] = null;
        _cursor = 0;
    }
}
