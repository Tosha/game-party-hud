using System;

namespace GamePartyHud.Capture;

/// <summary>Exponential moving average filter: current = alpha*x + (1-alpha)*previous.</summary>
public sealed class HpSmoother
{
    private readonly float _alpha;
    private float? _state;

    public HpSmoother(float alpha = 0.5f)
    {
        if (alpha <= 0f || alpha > 1f) throw new ArgumentOutOfRangeException(nameof(alpha));
        _alpha = alpha;
    }

    public float Push(float sample)
    {
        _state = _state is null ? sample : _alpha * sample + (1f - _alpha) * _state.Value;
        return _state.Value;
    }

    public void Reset() => _state = null;
}
