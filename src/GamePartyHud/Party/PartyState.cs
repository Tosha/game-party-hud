using System;
using System.Collections.Generic;
using System.Linq;

namespace GamePartyHud.Party;

/// <summary>
/// Authoritative local view of the party roster. Fed by <see cref="Apply"/> (reacting to
/// network messages or local state updates) and <see cref="Tick"/> (periodic disconnect
/// detection). Raises <see cref="Changed"/> exactly when the visible roster changed.
/// </summary>
public sealed class PartyState
{
    private const int StaleAfterSec = 6;
    private const int RemoveAfterSec = 60;

    private readonly Dictionary<string, MemberState> _members = new();
    private readonly HashSet<string> _kicked = new();
    private readonly HashSet<string> _staleSet = new();
    public long LastTickUnix { get; private set; }

    public IReadOnlyDictionary<string, MemberState> Members => _members;
    public event Action? Changed;

    public string? LeaderPeerId
    {
        get
        {
            if (_members.Count == 0) return null;
            return _members.Values
                .OrderBy(m => m.JoinedAtUnix)
                .ThenBy(m => m.PeerId, StringComparer.Ordinal)
                .First().PeerId;
        }
    }

    public bool IsKicked(string peerId) => _kicked.Contains(peerId);

    public bool IsStale(MemberState m, long nowUnix) =>
        nowUnix - m.LastUpdateUnix >= StaleAfterSec;

    public void Apply(PartyMessage msg, long nowUnix)
    {
        bool changed = false;
        switch (msg)
        {
            case StateMessage s:
                if (_kicked.Contains(s.PeerId)) break;
                if (_members.TryGetValue(s.PeerId, out var prev))
                {
                    _members[s.PeerId] = prev with
                    {
                        Nickname = s.Nick,
                        Role = s.Role,
                        HpPercent = s.Hp,
                        LastUpdateUnix = nowUnix
                    };
                }
                else
                {
                    _members[s.PeerId] = new MemberState(
                        s.PeerId, s.Nick, s.Role, s.Hp, nowUnix, nowUnix);
                }
                _staleSet.Remove(s.PeerId);
                changed = true;
                break;

            case ByeMessage b:
                if (_members.Remove(b.PeerId)) { _staleSet.Remove(b.PeerId); changed = true; }
                break;

            case KickMessage k:
                _kicked.Add(k.Target);
                if (_members.Remove(k.Target)) { _staleSet.Remove(k.Target); changed = true; }
                break;
        }
        LastTickUnix = nowUnix;
        if (changed) Changed?.Invoke();
    }

    public void Tick(long nowUnix)
    {
        LastTickUnix = nowUnix;
        var toRemove = new List<string>();
        bool changed = false;

        foreach (var m in _members.Values.ToList())
        {
            long age = nowUnix - m.LastUpdateUnix;
            if (age >= RemoveAfterSec)
            {
                toRemove.Add(m.PeerId);
                continue;
            }
            bool isNowStale = age >= StaleAfterSec;
            bool wasStale = _staleSet.Contains(m.PeerId);
            if (isNowStale && !wasStale)      { _staleSet.Add(m.PeerId);    changed = true; }
            else if (!isNowStale && wasStale) { _staleSet.Remove(m.PeerId); changed = true; }
        }

        foreach (var id in toRemove)
        {
            _members.Remove(id);
            _staleSet.Remove(id);
            changed = true;
        }

        if (changed) Changed?.Invoke();
    }
}
