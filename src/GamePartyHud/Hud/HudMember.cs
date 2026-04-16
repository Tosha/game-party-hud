using System;
using System.ComponentModel;
using GamePartyHud.Party;

namespace GamePartyHud.Hud;

/// <summary>View-model for a single party member card. Raises INotifyPropertyChanged for WPF binding.</summary>
public sealed class HudMember : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string PeerId { get; }

    public HudMember(string peerId)
    {
        PeerId = peerId;
    }

    private string _nickname = "";
    public string Nickname
    {
        get => _nickname;
        set { if (_nickname != value) { _nickname = value; Raise(nameof(Nickname)); } }
    }

    private Role _role;
    public Role Role
    {
        get => _role;
        set
        {
            if (_role == value) return;
            _role = value;
            Raise(nameof(Role));
            Raise(nameof(RoleGlyph));
        }
    }

    public string RoleGlyph => GamePartyHud.Party.RoleGlyph.For(_role);

    private float _hpPercent;
    public float HpPercent
    {
        get => _hpPercent;
        set
        {
            float clamped = Math.Clamp(value, 0f, 1f);
            if (_hpPercent != clamped) { _hpPercent = clamped; Raise(nameof(HpPercent)); }
        }
    }

    private bool _isStale;
    public bool IsStale
    {
        get => _isStale;
        set
        {
            if (_isStale == value) return;
            _isStale = value;
            Raise(nameof(IsStale));
            Raise(nameof(Opacity));
        }
    }

    public double Opacity => _isStale ? 0.4 : 1.0;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
