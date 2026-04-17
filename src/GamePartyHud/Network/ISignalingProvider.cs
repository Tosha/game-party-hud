using System;
using System.Threading;
using System.Threading.Tasks;

namespace GamePartyHud.Network;

/// <summary>
/// Rendezvous channel used once at connection time. Signals carry SDP offers/answers
/// and ICE candidates between peers. Never carries HP/party-state traffic — that flows
/// peer-to-peer after WebRTC negotiation completes.
/// </summary>
public interface ISignalingProvider : IAsyncDisposable
{
    bool IsJoined { get; }

    Task JoinAsync(string partyId, string selfPeerId, CancellationToken ct);

    event Func<string, string, Task>? OnOffer;    // (fromPeerId, sdp)
    event Func<string, string, Task>? OnAnswer;   // (fromPeerId, sdp)
    event Func<string, string, Task>? OnIce;      // (fromPeerId, candidateJson)

    Task SendOfferAsync(string toPeerId, string sdp, CancellationToken ct);
    Task SendAnswerAsync(string toPeerId, string sdp, CancellationToken ct);
    Task SendIceAsync(string toPeerId, string candidateJson, CancellationToken ct);
}
