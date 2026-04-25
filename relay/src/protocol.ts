// Wire contract between RelayClient (C#) and PartyRoom (TS). MUST match
// src/GamePartyHud/Network/RelayProtocol.cs — the shared fixtures in
// test/fixtures.ts are the source of truth both sides parse.

export type ClientMessage =
  | { type: "join"; peerId: string }
  | { type: "broadcast"; payload: string };

export type ServerMessage =
  | { type: "welcome"; peerId: string; members: string[] }
  | { type: "peer-joined"; peerId: string }
  | { type: "peer-left"; peerId: string }
  | { type: "message"; fromPeerId: string; payload: string }
  | { type: "error"; reason: ErrorReason };

export type ErrorReason =
  | "party-full"
  | "invalid-join"
  | "duplicate-peer"
  | "rate-limit"
  | "message-too-large"
  | "protocol-error";

export function decodeClientMessage(raw: string): ClientMessage | null {
  let data: unknown;
  try {
    data = JSON.parse(raw);
  } catch {
    return null;
  }
  if (typeof data !== "object" || data === null) return null;
  const obj = data as Record<string, unknown>;

  if (obj.type === "join") {
    if (typeof obj.peerId !== "string" || obj.peerId.length === 0) return null;
    return { type: "join", peerId: obj.peerId };
  }
  if (obj.type === "broadcast") {
    if (typeof obj.payload !== "string") return null;
    return { type: "broadcast", payload: obj.payload };
  }
  return null;
}

export function encodeServerMessage(msg: ServerMessage): string {
  return JSON.stringify(msg);
}
