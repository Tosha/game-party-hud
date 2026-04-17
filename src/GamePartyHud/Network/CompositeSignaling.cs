using System;
using System.Threading;
using System.Threading.Tasks;

namespace GamePartyHud.Network;

/// <summary>
/// Tries the primary provider first; on join failure (exception or timeout), falls back
/// to the secondary. Inbound events from both providers are forwarded. Outbound sends go
/// to whichever provider successfully joined.
/// </summary>
public sealed class CompositeSignaling : ISignalingProvider
{
    public static TimeSpan JoinTimeout { get; set; } = TimeSpan.FromSeconds(8);

    private readonly ISignalingProvider _primary;
    private readonly ISignalingProvider _secondary;
    private ISignalingProvider? _active;

    public bool IsJoined => _active?.IsJoined == true;

    public event Func<string, string, Task>? OnOffer;
    public event Func<string, string, Task>? OnAnswer;
    public event Func<string, string, Task>? OnIce;

    public CompositeSignaling(ISignalingProvider primary, ISignalingProvider secondary)
    {
        _primary = primary;
        _secondary = secondary;
        Wire(_primary);
        Wire(_secondary);
    }

    private void Wire(ISignalingProvider p)
    {
        p.OnOffer  += (from, sdp) => OnOffer?.Invoke(from, sdp) ?? Task.CompletedTask;
        p.OnAnswer += (from, sdp) => OnAnswer?.Invoke(from, sdp) ?? Task.CompletedTask;
        p.OnIce    += (from, ice) => OnIce?.Invoke(from, ice) ?? Task.CompletedTask;
    }

    public async Task JoinAsync(string partyId, string selfPeerId, CancellationToken ct)
    {
        if (await TryJoinAsync(_primary, partyId, selfPeerId, ct))
        {
            _active = _primary;
            return;
        }
        if (await TryJoinAsync(_secondary, partyId, selfPeerId, ct))
        {
            _active = _secondary;
            return;
        }
        throw new InvalidOperationException("Signaling join failed on all providers.");
    }

    private static async Task<bool> TryJoinAsync(ISignalingProvider p, string id, string pid, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(JoinTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await p.JoinAsync(id, pid, linked.Token);
            return p.IsJoined;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public Task SendOfferAsync(string to, string sdp, CancellationToken ct) =>
        _active?.SendOfferAsync(to, sdp, ct) ?? throw new InvalidOperationException("Not joined.");
    public Task SendAnswerAsync(string to, string sdp, CancellationToken ct) =>
        _active?.SendAnswerAsync(to, sdp, ct) ?? throw new InvalidOperationException("Not joined.");
    public Task SendIceAsync(string to, string ice, CancellationToken ct) =>
        _active?.SendIceAsync(to, ice, ct) ?? throw new InvalidOperationException("Not joined.");

    public async ValueTask DisposeAsync()
    {
        await _primary.DisposeAsync();
        await _secondary.DisposeAsync();
    }
}
