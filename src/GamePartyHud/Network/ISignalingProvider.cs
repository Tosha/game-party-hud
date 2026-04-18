using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GamePartyHud.Network;

/// <summary>
/// One pre-generated WebRTC offer. The signaling layer hands these out to trackers
/// so peers can discover each other without first knowing peer IDs — the tracker
/// forwards an offer to another peer, who creates an answer carrying the same
/// <see cref="OfferId"/>. The originator matches the answer back to the pending
/// offer by that id.
/// </summary>
public sealed record PreGeneratedOffer(string OfferId, string Sdp);

/// <summary>
/// Rendezvous channel used once at connection time. Signals carry SDP offers/answers
/// and ICE candidates between peers. Never carries HP/party-state traffic — that
/// flows peer-to-peer after WebRTC negotiation completes.
///
/// Peer discovery is driven by the WebTorrent tracker protocol: each party peer
/// announces with N pre-generated offers; the tracker matches them to other
/// announcers; the matched peer creates an answer carrying the original
/// <c>offer_id</c>; the answer is routed back via the tracker.
/// </summary>
public interface ISignalingProvider : IAsyncDisposable
{
    bool IsJoined { get; }

    /// <summary>
    /// Supplies pre-generated offers on demand. Must be set before <see cref="JoinAsync"/>.
    /// The signaling layer calls this on every announce / re-announce to include fresh
    /// offers that new peers can pick up.
    /// </summary>
    Func<int, Task<IReadOnlyList<PreGeneratedOffer>>>? OfferFactory { get; set; }

    Task JoinAsync(string partyId, string selfPeerId, CancellationToken ct);

    /// <summary>Raised when another peer sent an offer to us through the signaling layer.</summary>
    event Func<string, string, string, Task>? OnOffer;      // (fromPeerId, offerId, sdp)

    /// <summary>Raised when a peer sent an answer that matches one of our pre-generated offers.</summary>
    event Func<string, string, string, Task>? OnAnswer;     // (fromPeerId, offerId, sdp)

    /// <summary>Raised if the signaling layer supports out-of-band ICE candidate relay.</summary>
    event Func<string, string, Task>? OnIce;                // (fromPeerId, candidateJson)

    /// <summary>Send an answer back to a peer whose offer (identified by <paramref name="offerId"/>) we just accepted.</summary>
    Task SendAnswerAsync(string toPeerId, string offerId, string sdp, CancellationToken ct);

    /// <summary>Optional — for providers that relay ICE separately from SDP.</summary>
    Task SendIceAsync(string toPeerId, string candidateJson, CancellationToken ct);
}
