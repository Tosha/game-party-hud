# Reliability & scalability review — P2P signaling

**Status:** Decision needed
**Date:** 2026-04-22
**Author:** Based on three debugging sessions (2026-04-19 → 2026-04-22)
**Scope:** Whether to keep the current zero-cost P2P architecture, harden it, or replace it with a hosted relay before scaling up from 2-peer tests to the advertised 20-peer limit.

---

## TL;DR

In three test cycles with **two peers**, we hit three independent, silent-failure bugs in the signaling/transport stack — each in a different layer. Every bug required reading C++ / Node / SIPSorcery source code to diagnose because the failure mode was "the other peer simply isn't there."

The architecture works on the reference path (loopback integration test passes, LAN works), but the production path is a stack of **six independent layers, each with its own failure modes**, none of which we own. Scaling from N=2 to N=20 moves every per-pair failure mode from "sometimes one pair breaks" to "almost always at least one pair breaks" (expected ≥ 1 at any per-pair success rate below 99.5%).

Recommendation at the end.

---

## 1. What the current architecture actually is

```
┌─────────────────────────────────────────────────────────────────┐
│  Peer A                          Peer B                         │
│  ┌─────────────────┐             ┌─────────────────┐            │
│  │ PartyOrch /     │             │ PartyOrch /     │            │
│  │ HUD / State     │             │ HUD / State     │            │
│  └────────┬────────┘             └────────┬────────┘            │
│           │ RTCDataChannel ("party")       │                    │
│           │◄──────────── WebRTC ──────────►│                    │
│           │                                │                    │
│           │ setRemoteDesc /      setLocalDesc /                 │
│           │ addIceCandidate      createOffer/Answer             │
│  ┌────────┴────────┐             ┌────────┴────────┐            │
│  │ SIPSorcery      │             │ SIPSorcery      │            │
│  │ RTCPeerConn     │             │ RTCPeerConn     │            │
│  └────────┬────────┘             └────────┬────────┘            │
│           │                                │                    │
│    ICE: STUN         ─────────────►  STUN servers               │
│    (stun.l.google / stun.cloudflare)                            │
│                                                                 │
│  ┌────────┴────────┐             ┌────────┴────────┐            │
│  │ Bittorrent      │  WebSocket  │ Bittorrent      │            │
│  │ Signaling       │◄───────────►│ Signaling       │            │
│  └─────────────────┘   ▲         └─────────────────┘            │
│                        │                                        │
│                        ▼                                        │
│          ┌─────────────────────────┐                            │
│          │ Public WebTorrent       │                            │
│          │ trackers (3× WSS)       │                            │
│          │ - openwebtorrent.com    │                            │
│          │ - btorrent.xyz          │                            │
│          │ - webtorrent.dev        │                            │
│          └─────────────────────────┘                            │
└─────────────────────────────────────────────────────────────────┘
```

**Topology:** full mesh. For N peers, each peer holds N−1 `RTCPeerConnection`s, each with its own data channel, DTLS session, ICE state machine, and candidate list.

**Signaling path (offer → answer):** 6 independent services must all cooperate for one pair to connect:

1. WebSocket to public tracker (3 candidates, need ≥ 1 shared between the peers)
2. Tracker's JSON parse & length validator — WebTorrent's 20-byte Latin-1 format
3. Tracker swarm state (peer matching, offer forwarding, `idleTimeout`)
4. STUN binding request round-trip (for srflx candidates)
5. SIPSorcery's ICE gatherer and SDP generator
6. NAT behaviour on both endpoints (cone vs symmetric, UDP blocking)

Each of 1–5 is operated by someone else. We've reported a bug upstream in zero of them because the workarounds sat in our code.

---

## 2. Reliability in practice — field evidence

Three test cycles, each between two real users on the public internet. **Every test exposed a new failure mode in a different layer of the stack.**

| Date | Observed symptom | Root cause | Layer |
|---|---|---|---|
| 2026-04-20 | Tracker ack `complete=2` but no offers ever forwarded | `info_hash` / `peer_id` sent as hex; Node `bittorrent-tracker` validates `length === 20` and silently drops | Tracker protocol |
| 2026-04-21 | Offer/answer exchanged, ICE → `failed` in 16 s | Joiner's STUN took > 5 s, 5 s gathering timeout published a host-only SDP | ICE gathering timeout |
| 2026-04-22 | STUN reachable, `srflx` logged, but still host-only SDP on the wire | SIPSorcery `localDescription.sdp` is frozen at `setLocalDescription` time, trickle candidates never retrofitted | SIPSorcery API contract |

Notable: **every single one was silent.** No exception, no failure log, no error from any of the services involved. Diagnosis required (a) reading the C++ `openwebtorrent-tracker` `FastTracker.h` to verify the swarm semantics, (b) reading Node `bittorrent-tracker/lib/server/parse-websocket.js` to find the length validator, and (c) reading SIPSorcery's `RTCPeerConnection.cs` to find `setLocalDescription` storing a frozen SDP.

Each fix was small; each root cause required hours of log analysis and upstream source-reading. That is the debugging cost of owning the zero-cost P2P path.

### Failure modes we have NOT hit in testing but will

- **Symmetric NAT on either peer.** The srflx candidate we just un-broke is the server-reflexive address in a cone-NAT scenario. Symmetric NAT (common on mobile hotspots, enterprise networks, some ISPs — reported ~8–15% of home users) does not work with STUN alone; it requires TURN. We have no TURN.
- **UDP blocking.** Corporate networks and some ISPs block UDP entirely. Same workaround (TURN over TCP/TLS) and same lack.
- **Tracker rate-limiting or bans.** `openwebtorrent.com` sometimes rate-limits; `btorrent.xyz` has been down through our entire test window. We depend on third parties with no SLA.
- **Tracker protocol drift.** The C++ implementation currently running at `tracker.openwebtorrent.com` accepts the hex format lazily; an upgrade that tightens validation would break us overnight.
- **WebRTC API drift.** SIPSorcery's `localDescription`-doesn't-update-on-trickle quirk is unspecified behaviour — if they fix it, our `createOffer()`-twice workaround would double-count candidates, probably benignly but not audited.
- **SIPSorcery version-specific regressions.** We're pinned to 8.0.9. Upgrading is a multi-day audit of every ICE edge case.

We are about **half a year of testing** from having seen all the failure modes for even N = 2.

---

## 3. Scalability math — what N = 20 means

### 3.1 Connection count and establishment

Full mesh: N peers = **N × (N−1) / 2** unordered pairs = bidirectional connections.

| N | Pairs | Connections per peer |
|---|------:|---------------------:|
| 2 |     1 |                    1 |
| 3 |     3 |                    2 |
| 5 |    10 |                    4 |
| 10 |   45 |                    9 |
| 20 |   190 |                   19 |

Each pair independently runs the 6-layer handshake above. Assume p = per-pair success probability once the field stack is as hardened as we can make it. Expected fully-connected pairs in a party = (N−1) · p per peer; expected **broken** pairs per peer = (N−1)(1−p).

For a "nobody misses anyone" experience, we need `(1 − p)^(N−1) × N` broken pairs ≈ 0. At p = 0.95 (optimistic, even after fixing all our known bugs):

| N | Expected broken pairs per peer | Prob. a given peer has zero misses |
|---|-------------------------------:|----------------------------------:|
| 2 |                           0.05 |                              95 % |
| 5 |                            0.2 |                              81 % |
| 10 |                           0.45 |                              63 % |
| 20 |                           0.95 |                              38 % |

At N = 20, even with 95 % per-pair reliability, only 38 % of peers see the complete party. Users will perceive this as "one person is always missing."

To hit "everyone sees everyone 99 % of the time" at N = 20, per-pair reliability must be ≥ 99.95 %. Public-internet WebRTC has historically been around 92–95 % without TURN and 97–98 % with TURN. We would need something substantially more reliable than industry-average direct P2P — which is not realistic while we own the signaling bugs.

### 3.2 Tracker throughput

`openwebtorrent-tracker`'s `sendOffersToPeers` forwards `min(numwant, peers−1, offers)` offers per announce — at most one offer per other peer, randomly selected. For N = 20 peers and 5 offers per announce per peer:

- Each peer's announce forwards at most 5 offers, distributed at random across the other 19 peers.
- To reach every pair, each peer needs ≥ 4 announce rounds — 4 × 60 s = **4 minutes minimum to form a full mesh**. In practice many more because of random-selection collisions.

This is a hard lower bound from the tracker's forwarding policy, independent of our code.

### 3.3 Bandwidth and CPU

Per state message ≈ 120 bytes. Broadcast every 3 s. At N = 20:

- Upload per peer: 19 × 40 B/s = 760 B/s ≈ 6 kbit/s. Fine.
- Download per peer: same. Fine.
- CPU per peer: 19 DTLS-encrypted SCTP streams, each keepalive-pinging. SIPSorcery at this count — untested by us but expected to fit the "<1 % CPU" budget.
- Memory: 19 `RTCPeerConnection`s per peer × some kilobytes each. Expected well under 100 MB.

**Data-plane scaling isn't the concern.** Connection establishment and signaling-layer reliability are.

---

## 4. Options

Cost ranges below are monthly, for a modest user base (≤ 1000 active parties/day). "Code" is rough LOC delta from today's `Network/` folder.

### Option A — Stay as-is, rely on what we shipped

| | |
|---|---|
| Cost | $0 |
| Reliability at N = 2 | ~70 % after this week's fixes; still depending on public tracker availability |
| Reliability at N = 20 | See 3.1 — not viable without TURN |
| Code | 0 |

**When this works:** the user base is LAN-only or entirely home-cone-NAT users and tolerates occasional failures. Given we've already seen 3 distinct production-breaking bugs in 2-peer tests, **we are not there.**

---

### Option B — Harden the current stack (TURN + reconnect + richer diagnostics)

Keep P2P mesh. Add:

- A free/cheap TURN fallback — e.g. `openrelay.metered.ca` free tier (pre-configured), or instruct users to run `coturn` on a cheap VPS.
- Aggressive tracker reconnect, multi-tracker fan-out (already shipped).
- Cap at N = 5–8 peers rather than advertising 20.

| | |
|---|---|
| Cost | $0 (public TURN) to $5/mo (own `coturn` on Hetzner/DO) |
| Reliability at N = 2 | ~95 % with TURN, ~85 % without |
| Reliability at N = 20 | ~38–60 % (see 3.1) — still not viable |
| Code | +~200 LOC; public TURN onboarding is UX-fiddly |

**When this works:** we scope the product down to small parties (≤ 5) and accept that "failed to connect, open CustomTurnUrl" is a supported error path.

**Risks:** free public TURN tiers are unreliable and rate-limited; `openrelay.metered.ca` has been down multiple times in 2024–2025. Self-hosted TURN is cheap but "zero-cost" is no longer true and the user now has a server to babysit.

---

### Option C — Move signaling off the tracker, keep WebRTC

Replace `BitTorrentSignaling` with a ~100-line WebSocket server we control. Peers still talk peer-to-peer over WebRTC; we only relay offer/answer/ICE-candidate JSON between them.

| | |
|---|---|
| Cost | $0 on Cloudflare Workers + Durable Objects (100 k req/day free); $2–5/mo on Fly.io shared-cpu-1x |
| Reliability at N = 2 | ~95 % for signaling; WebRTC data-plane reliability unchanged (~92–95 %) |
| Reliability at N = 20 | Same mesh problem — see 3.1. This option fixes signaling reliability but not the underlying mesh scaling problem. |
| Code | +~150 LOC server, −~250 LOC client (drop BitTorrent protocol, the Latin-1 binary conversion, the 3-tracker fan-out); net **simpler** |

**When this works:** we want to keep P2P for privacy/data-plane reasons but recognize that public trackers are the least reliable link. Still leaves ICE/NAT as a failure mode; still needs TURN at scale.

**Honest read:** this halves the complexity (removes the tracker protocol entirely) but doesn't materially change the scaling story. It's a good migration **step** but not the destination.

---

### Option D — Move transport off WebRTC entirely: WebSocket relay

Drop WebRTC. Every peer opens a single WebSocket to a small relay server. Server maintains "rooms" keyed by party ID; every message is broadcast to the other members of the room.

| | |
|---|---|
| Cost | $0 on Cloudflare Durable Objects (likely sufficient for forecast usage); $5/mo Fly.io dedicated; $0/1000-users on PartyKit free tier |
| Reliability at N = 2 | ≥ 99 % — one TCP connection per peer, no NAT traversal, no ICE |
| Reliability at N = 20 | ~99 % — server fans out O(N) messages per peer's broadcast; no pairwise connections to fail |
| Code | +~80 LOC server, −~500 LOC client (drops SIPSorcery, tracker, PeerNetwork, ICE logging) |

**When this works:** we accept running (or renting) one piece of infrastructure and earn back weeks of debugging capacity + a product that scales to 20 peers out of the box.

**Concrete options for the relay:**

- **PartyKit** (free tier for hobby, ~$20/mo for production) — Cloudflare-hosted, purpose-built for "rooms with broadcast." SDK is roughly 30 lines of client code.
- **Cloudflare Workers + Durable Objects** — free tier covers 100 k req/day. Server is ~80 lines TypeScript. No cold-start concern (DOs stay warm per-room).
- **Fly.io + Node `ws`** — $2/mo shared-cpu-1x, full control. Server is ~100 lines Node. Needs a Dockerfile.
- **Self-host on a $4/mo Hetzner VPS** — maximum control, our data, but ops overhead.

Message payloads are ≤ 200 bytes; the forecast bandwidth is trivial on any of these.

**What's lost:** technically not peer-to-peer anymore. For the HP-bar use case (already broadcasting unencrypted HP values to strangers you put in your party), nothing privacy-sensitive flows.

---

### Option E — Hybrid: WebSocket primary, WebRTC on demand

WebSocket relay for presence, roster, small messages. WebRTC direct pipe opened opportunistically for large/latency-critical payloads (none in this product — so why bother).

| | |
|---|---|
| Cost | Same as D |
| Complexity | Both stacks. **Do not do this.** |

Mentioned only to rule it out: hybrid doubles the bug surface without addressing the actual symptoms we're seeing.

---

## 5. Comparison matrix

|                                | A (as-is)  | B (+TURN)    | C (own signaling) | **D (WS relay)** |
|--------------------------------|-----------|--------------|-------------------|------------------|
| Monthly cost                   | $0        | $0–5         | $0–5              | $0–5             |
| Code complexity (Network/)     | Same      | +200 LOC     | Net −100 LOC      | **Net −420 LOC** |
| Observed failure modes ≥ N=2   | 3         | 3 + TURN ops | ≤ 2               | **0 (new stack)**|
| Expected reliability at N=2    | ~70 %     | ~95 %        | ~95 %             | **≥ 99 %**       |
| Expected reliability at N=20   | — *       | ~38–60 %     | ~38–60 %          | **~99 %**        |
| Engineer-days to implement     | 0         | 2–3          | 3–5               | **3–4**          |
| Ongoing ops burden             | None      | TURN babysitting | 1 small service | **1 small service** |
| Survives a public-tracker outage | ✗       | ✗            | ✓                 | ✓                |
| Survives a symmetric-NAT user  | ✗        | ✓ (TURN)     | ✗                 | ✓                |
| "Just works" for 20 peers      | ✗        | ✗            | ✗                 | **✓**            |

\* Option A at N = 20 is not meaningfully measurable — we wouldn't ship it.

---

## 6. Recommendation

**Go with Option D (WebSocket relay), specifically Cloudflare Workers + Durable Objects or PartyKit.** Reasons:

1. **Failure-mode count drops to zero observed and one plausible** (the relay itself). Every observed failure from the past three debugging sessions comes from a layer we'd no longer use.
2. **Scales to the advertised 20-peer limit.** No per-pair handshake; a joiner becomes visible to everyone the moment it opens its WebSocket and says `{type:"join", partyId}`.
3. **Net −400 LOC in the most complex folder of the codebase.** `BitTorrentSignaling.cs` (~340 LOC), `PeerNetwork.cs` (~450 LOC), plus the integration tests and the wire-format adapter all go away, replaced by ~200 LOC total.
4. **Hosting cost is $0 in the range that matters** (Cloudflare free tier covers O(thousands) of parties). If the product grows past that, $20/mo PartyKit or $5/mo Fly.io dedicated is a rounding error against what we're saving in user frustration.
5. **Keeps the "no accounts, no server setup for users" promise.** That was the real user-facing requirement; "serverless" was always an *implementation* detail. A relay we operate for free is invisible to them.

The "zero hosting cost" rule served its purpose: it kept us from over-engineering the MVP. It was never a product requirement; it was a bootstrap constraint. We are past bootstrap. The true cost now is the engineering time we've spent debugging the P2P path (confirmed: **three long sessions for three separate silent bugs, all in 2-peer tests**) plus the user-visible "my teammate doesn't see me" rate we'll hit at N > 5.

### Suggested rollout

Two phases, ~1 week of wall-clock each:

**Phase 1 — Dual-stack behind a config flag.**
- Ship a new `WebSocketSignaling : ISignalingProvider` and a matching `WebSocketTransport : (replaces PeerNetwork for the relay path)`.
- `AppConfig.SignalingMode = "webrtc" | "relay"`; default `webrtc`; opt-in per-user to `relay`.
- Testing pool flips to `relay`, confirms reliability at N = 5–10.

**Phase 2 — Flip the default.**
- Default to `relay`, keep `webrtc` as a deprecated opt-in for one release.
- Next release: delete WebRTC + tracker paths; net ~−400 LOC.

### If D is not acceptable

Fall-through order: **C → B → A**.

- **C** is the best "we really want to stay P2P" answer. It fixes signaling reliability and is a stepping stone toward D (same server, same deploy, different message schema).
- **B** is the least-change answer. It is the answer if the zero-hosting-cost rule is *really* non-negotiable and we are willing to cap user-visible party size at ~5.
- **A** is status quo. We should not advertise N = 20 under A.

---

## 7. Open questions for the author to decide

1. **Is zero-hosting-cost a product requirement or an implementation preference?** The requirements doc ([requirements.md line 40](../../requirements.md)) says "no centralized server" — but "server for party data" (peer list, state) ≠ "server that relays realtime messages." Is the spirit-of-the-rule satisfied by a relay that stores nothing?
2. **What's the realistic user-count ceiling?** Cloudflare DO free tier is generous but not infinite. We should size before we commit.
3. **What's the cost of a debugging cycle like the last three to you, versus $5/mo?** The honest version of this question.
4. **Are we willing to drop support for users on symmetric-NAT networks if we stay P2P?** That's an implicit choice in B / C.

I'd like to nail down (1) before writing Phase 1 code.
