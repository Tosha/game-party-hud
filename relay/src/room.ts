import {
  type ServerMessage,
  decodeClientMessage,
  encodeServerMessage,
} from "./protocol";

interface Member {
  socket: WebSocket;
  peerId: string;
  // Leaky-bucket rate limit: capacity 20, refill 10/s. Stored per-member so a
  // single noisy peer can't starve others.
  rlTokens: number;
  rlLastRefillMs: number;
}

const MAX_PEERS = 25;

// Per-peer leaky bucket — first line of defense, isolates a noisy peer
// without affecting others in the same party.
const RL_CAPACITY = 20;
const RL_REFILL_PER_MS = 10 / 1000; // 10 tokens per second.

// Per-party leaky bucket — backstop for "many peers individually behaving
// but collectively flooding". Sized to bound abuse at a 25-peer party-full
// while leaving 2× headroom over the worst legitimate sustained workload
// (25 peers broadcasting at 1 Hz = 25 broadcasts/s; refill is 50/s).
// Capacity 100 absorbs synchronized burst events (e.g. all peers reacting
// to a boss-fight start within the same 2-second window).
const PARTY_RL_CAPACITY = 100;
const PARTY_RL_REFILL_PER_MS = 50 / 1000; // 50 tokens per second.

export class PartyRoom {
  private state: DurableObjectState;
  private members = new Map<string, Member>(); // peerId -> Member

  // Per-party (DO-instance) bucket. Initialized full; refilled lazily on
  // every broadcast. Resets to full when the DO is rebuilt from hibernation
  // — same trade-off as the per-peer buckets: simplicity of in-memory state
  // over correctness across hibernation, fine because hibernation only
  // happens during quiet periods.
  private partyTokens = PARTY_RL_CAPACITY;
  private partyLastRefillMs = Date.now();

  constructor(state: DurableObjectState, _env: unknown) {
    this.state = state;
    // Rebuild in-memory roster from any hibernating sockets after a DO restart.
    // Rate-limit buckets reset to full on rebuild — a hibernated client gets a
    // fresh burst budget when the DO wakes up. That's a reasonable price for
    // not having to persist bucket state.
    const now = Date.now();
    this.partyLastRefillMs = now;
    for (const ws of this.state.getWebSockets()) {
      const peerId = this.peerIdOf(ws);
      if (peerId !== null) {
        this.members.set(peerId, {
          socket: ws,
          peerId,
          rlTokens: RL_CAPACITY,
          rlLastRefillMs: now,
        });
      }
    }
  }

  async fetch(request: Request): Promise<Response> {
    if (request.headers.get("Upgrade") !== "websocket") {
      return new Response("expected WebSocket upgrade", { status: 426 });
    }
    const pair = new WebSocketPair();
    // WebSocketPair always returns exactly two sockets; the `!` placates
    // tsconfig's `noUncheckedIndexedAccess`.
    const client = pair[0]!;
    const server = pair[1]!;
    this.state.acceptWebSocket(server);
    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(ws: WebSocket, raw: string | ArrayBuffer): Promise<void> {
    const text = typeof raw === "string" ? raw : "";
    if (text.length > 4096) {
      this.send(ws, { type: "error", reason: "message-too-large" });
      return;
    }
    const msg = decodeClientMessage(text);
    if (!msg) return;

    if (msg.type === "join") {
      if (this.peerIdOf(ws) !== null) return; // ignore duplicate join
      if (this.members.size >= MAX_PEERS) {
        this.send(ws, { type: "error", reason: "party-full" });
        ws.close(1008, "party-full");
        return;
      }
      if (this.members.has(msg.peerId)) {
        this.send(ws, { type: "error", reason: "duplicate-peer" });
        ws.close(1008, "duplicate-peer");
        return;
      }
      const newPeerId = msg.peerId;
      const existingMembers = [...this.members.keys()];
      ws.serializeAttachment({ peerId: newPeerId });
      this.members.set(newPeerId, {
        socket: ws,
        peerId: newPeerId,
        rlTokens: RL_CAPACITY,
        rlLastRefillMs: Date.now(),
      });
      this.send(ws, { type: "welcome", peerId: newPeerId, members: existingMembers });
      this.broadcastExcept(newPeerId, { type: "peer-joined", peerId: newPeerId });
    }

    if (msg.type === "broadcast") {
      const peerId = this.peerIdOf(ws);
      if (peerId === null) return; // ignore until joined
      const member = this.members.get(peerId);
      if (member === undefined) return;
      if (!this.consumeRateLimitTokens(member)) {
        this.send(ws, { type: "error", reason: "rate-limit" });
        return;
      }
      this.broadcastExcept(peerId, {
        type: "message",
        fromPeerId: peerId,
        payload: msg.payload,
      });
    }
  }

  /// Refill both the per-peer and per-party leaky buckets to current time
  /// and atomically spend one token from each (the DO is single-threaded,
  /// so "atomically" here just means "in one synchronous block"). Returns
  /// true if the broadcast is allowed; false if either bucket is empty,
  /// in which case neither bucket is debited — a rate-limit reject costs
  /// the peer nothing beyond the failed broadcast itself.
  private consumeRateLimitTokens(member: Member): boolean {
    const now = Date.now();

    // Refill per-peer bucket.
    const peerElapsed = Math.max(0, now - member.rlLastRefillMs);
    member.rlTokens = Math.min(RL_CAPACITY, member.rlTokens + peerElapsed * RL_REFILL_PER_MS);
    member.rlLastRefillMs = now;

    // Refill per-party bucket.
    const partyElapsed = Math.max(0, now - this.partyLastRefillMs);
    this.partyTokens = Math.min(PARTY_RL_CAPACITY, this.partyTokens + partyElapsed * PARTY_RL_REFILL_PER_MS);
    this.partyLastRefillMs = now;

    if (member.rlTokens < 1 || this.partyTokens < 1) return false;

    member.rlTokens -= 1;
    this.partyTokens -= 1;
    return true;
  }

  async webSocketClose(ws: WebSocket, _code: number, _reason: string, _wasClean: boolean): Promise<void> {
    const peerId = this.peerIdOf(ws);
    if (peerId === null) return;
    this.members.delete(peerId);
    this.broadcastExcept(peerId, { type: "peer-left", peerId });
  }

  async webSocketError(ws: WebSocket, _error: unknown): Promise<void> {
    const peerId = this.peerIdOf(ws);
    if (peerId === null) return;
    this.members.delete(peerId);
    this.broadcastExcept(peerId, { type: "peer-left", peerId });
  }

  private broadcastExcept(excludePeerId: string, msg: ServerMessage): void {
    const encoded = encodeServerMessage(msg);
    for (const m of this.members.values()) {
      if (m.peerId === excludePeerId) continue;
      try { m.socket.send(encoded); } catch { /* dead socket; close event will clean up */ }
    }
  }

  private peerIdOf(ws: WebSocket): string | null {
    const att = ws.deserializeAttachment() as { peerId?: string } | null;
    return att?.peerId ?? null;
  }

  private send(socket: WebSocket, msg: ServerMessage): void {
    socket.send(encodeServerMessage(msg));
  }
}
