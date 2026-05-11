# Relay-side proposal: evict zombie peers on duplicate join

**Status:** proposal for the relay repo (not in this repo's code path).
**Companion change:** the client-side regen workaround in
`src/GamePartyHud/Network/RelayClient.cs` (`DuplicatePeerRegenThreshold`).

## Problem

Field log on 2026-04-27 (party `QPFYYP`):

```
02:36:04  RelayClient: welcome received with 2 existing member(s).
…40 s of normal traffic…
02:36:45  WARN RelayClient: receive error — The remote party closed the
                WebSocket connection without completing the close handshake.
02:36:45  RelayClient: reconnected on attempt #1.
02:36:45  WARN RelayClient: relay error — duplicate-peer
02:36:45  RelayClient: server closed the socket; reconnecting.
…this triple repeats ~3×/sec until the process is killed…
```

Sequence:

1. Worker drops the WS without a close handshake (DO hibernation, idle GC,
   network blip).
2. Client reconnects with the **same** peerId.
3. Relay's DO still has the previous WS registered for that peerId. The join
   handler rejects with `{"type":"error","reason":"duplicate-peer"}` and
   closes the new socket.
4. Client treats the close as another drop and reconnects again. Same peerId,
   same rejection. Infinite loop, ~3 Hz.

The client-side mitigation (regenerate peerId after N rejections) lets the
session recover *eventually*, but the teammate's card briefly disappears from
everyone else's HUD and a new one appears under a different id. The real
fix belongs on the server.

## Root cause (server side)

When a peer reconnects, the relay should recognise it as the same identity
returning — not a stranger trying to crash an active session — and let the
new socket replace the old one. The current DO logic doesn't do this; it
treats the second `join` as an attack and refuses.

Two reasons the old WS is still registered:

- The previous socket's close handler hasn't fired yet (Cloudflare can lag a
  few seconds on ungraceful drops).
- The DO may even be the same instance, just slow to notice.

## Proposed fix

On `join`, if the peerId is already in the room, **evict the old WS and
accept the new one** instead of rejecting. The new socket replaces the old in
the room map, and the old socket gets closed with a meaningful reason so any
race in client-side bookkeeping (rare; usually irrelevant since the client
already considers itself disconnected) resolves cleanly.

### Sketch (TypeScript / Cloudflare Workers + DO)

```ts
// Inside the DO's WebSocket join handler:
async handleJoin(ws: WebSocket, peerId: string) {
  const existing = this.peers.get(peerId);
  if (existing) {
    // Same peer reconnecting — supersede the old socket. This is NOT a
    // duplicate-peer attack; if the peerId leaked, that's the trust model's
    // problem (peerIds are 160-bit random, treated as bearer secrets).
    try {
      existing.ws.close(1000, "superseded-by-reconnect");
    } catch { /* may already be half-closed */ }
    this.peers.delete(peerId);
    // Don't broadcast peer-left here — the same peerId is about to re-appear,
    // and a left/joined pair flickers the HUD on every reconnect.
  }

  this.peers.set(peerId, { ws, joinedAt: Date.now() });
  ws.send(JSON.stringify({
    type: "welcome",
    peerId,
    members: [...this.peers.keys()].filter(id => id !== peerId),
  }));

  // (existing peer-joined broadcast logic, only for FIRST join — gate on
  // whether `existing` was set above.)
  if (!existing) {
    this.broadcast({ type: "peer-joined", peerId }, exceptPeerId: peerId);
  }
}
```

### Behaviour

| Scenario | Before | After |
|---|---|---|
| Cold join, peerId unused | welcome | welcome (unchanged) |
| Same peerId reconnecting <5 s after drop | `error:duplicate-peer`, close | welcome, old WS evicted |
| Concurrent join attempts with same peerId | first wins, second errors | last-write-wins (rare edge case; both clients see welcome) |
| Different peerId joining | welcome, peer-joined broadcast | welcome, peer-joined broadcast (unchanged) |

## Trade-offs

- **Security:** "Last writer wins" means anyone who guesses or learns a
  peerId can boot the legitimate owner. PeerIds are 160 random bits drawn
  from the OS CSPRNG (`crypto.getRandomValues` / .NET
  `RandomNumberGenerator.Fill`); the probability of guessing one is
  ~2⁻¹⁶⁰, which is below practical concern. The risk of *leaking* a peerId
  is bounded by the relay log + client log; neither is shipped to
  third parties, and the value rotates every party. Acceptable.
- **Broadcast hygiene:** Gating the `peer-joined` broadcast on `!existing`
  prevents a flicker on every reconnect. Receivers see no visible change.
- **No `peer-left`:** We deliberately skip the `peer-left` broadcast on
  the evicted side for the same reason. Other clients keep showing the
  card; the next state update from the new WS refreshes it.

## How to test (relay-side)

A focused integration test in the relay repo:

```ts
test("reconnecting peer supersedes old WS", async () => {
  const room = newDurableObjectStub();
  const aliceA = await room.join("alice");
  expect(aliceA.frame).toEqual({ type: "welcome", peerId: "alice", members: [] });

  // Alice reconnects without leaving.
  const aliceB = await room.join("alice");
  expect(aliceB.frame).toEqual({ type: "welcome", peerId: "alice", members: [] });

  // The old socket got a meaningful close, not a hang.
  expect(aliceA.closeCode).toBe(1000);
  expect(aliceA.closeReason).toBe("superseded-by-reconnect");
});

test("no peer-joined broadcast on reconnect", async () => {
  const room = newDurableObjectStub();
  await room.join("alice");
  const bob = await room.join("bob");
  bob.clearReceived();

  // Alice reconnects — Bob shouldn't see a peer-joined for alice.
  await room.join("alice");
  expect(bob.received).toEqual([]);
});
```

## Rollout

1. Land this in the relay repo behind a default-on feature flag (e.g.
   `EVICT_ON_DUPLICATE_JOIN=true`).
2. Deploy. Client behaviour is unchanged; client just stops seeing
   `duplicate-peer` errors.
3. After a week with no regressions, remove the flag.

## Client-side companion

The client-side regen workaround (this repo, `RelayClient.cs`) stays in
place as a safety net — if a future relay version ever returns
`duplicate-peer` again, clients still recover. Once this server fix has
shipped and bake-time has passed, `DuplicatePeerRegenThreshold` becomes a
dormant fallback.
