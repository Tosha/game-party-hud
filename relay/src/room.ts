import {
  type ServerMessage,
  decodeClientMessage,
  encodeServerMessage,
} from "./protocol";

interface Member {
  socket: WebSocket;
  peerId: string;
}

const MAX_PEERS = 25;

export class PartyRoom {
  private state: DurableObjectState;
  private members = new Map<string, Member>(); // peerId -> Member

  constructor(state: DurableObjectState, _env: unknown) {
    this.state = state;
    // Rebuild in-memory roster from any hibernating sockets after a DO restart.
    for (const ws of this.state.getWebSockets()) {
      const peerId = this.peerIdOf(ws);
      if (peerId !== null) this.members.set(peerId, { socket: ws, peerId });
    }
  }

  async fetch(request: Request): Promise<Response> {
    if (request.headers.get("Upgrade") !== "websocket") {
      return new Response("expected WebSocket upgrade", { status: 426 });
    }
    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);
    this.state.acceptWebSocket(server);
    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(ws: WebSocket, raw: string | ArrayBuffer): Promise<void> {
    const text = typeof raw === "string" ? raw : "";
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
      this.members.set(newPeerId, { socket: ws, peerId: newPeerId });
      this.send(ws, { type: "welcome", peerId: newPeerId, members: existingMembers });
      this.broadcastExcept(newPeerId, { type: "peer-joined", peerId: newPeerId });
    }
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
