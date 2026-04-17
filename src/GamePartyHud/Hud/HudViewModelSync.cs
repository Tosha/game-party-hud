using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using GamePartyHud.Party;

namespace GamePartyHud.Hud;

/// <summary>
/// Bridges <see cref="PartyState"/> (plain POCO, touched from background threads) to
/// the HUD's <see cref="ObservableCollection{T}"/> of <see cref="HudMember"/> (touched
/// from the UI thread). Every roster change is marshalled via the Dispatcher.
/// </summary>
public sealed class HudViewModelSync
{
    private readonly PartyState _state;
    private readonly ObservableCollection<HudMember> _target;

    public HudViewModelSync(PartyState state, ObservableCollection<HudMember> target)
    {
        _state = state;
        _target = target;
        _state.Changed += OnStateChanged;
    }

    private void OnStateChanged()
    {
        Application.Current?.Dispatcher.Invoke(Sync);
    }

    private void Sync()
    {
        long now = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var liveIds = _state.Members.Keys.ToHashSet();

        // Drop cards for peers that are no longer in the roster.
        for (int i = _target.Count - 1; i >= 0; i--)
        {
            if (!liveIds.Contains(_target[i].PeerId)) _target.RemoveAt(i);
        }

        // Add or update cards for live peers. Preserve the user's local ordering by
        // only appending new cards to the end; the existing order is never rewritten here.
        foreach (var id in liveIds)
        {
            var m = _state.Members[id];
            var existing = _target.FirstOrDefault(x => x.PeerId == id);
            if (existing is null)
            {
                existing = new HudMember(id);
                _target.Add(existing);
            }
            existing.Nickname = m.Nickname;
            existing.Role = m.Role;
            existing.HpPercent = m.HpPercent ?? 0f;
            existing.IsStale = _state.IsStale(m, now);
        }
    }
}
