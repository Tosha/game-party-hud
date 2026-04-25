import { SELF, env } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import type { ServerMessage } from "../src/protocol";
import { fixtures } from "./fixtures";

// Helper: open a WS to /party/:id, send a join frame, collect server frames.
async function openAndJoin(partyId: string, peerId: string): Promise<{
  socket: WebSocket;
  next: () => Promise<ServerMessage>;
}> {
  const url = `http://example.com/party/${partyId}`;
  const response = await SELF.fetch(url, { headers: { Upgrade: "websocket" } });
  expect(response.status).toBe(101);
  const socket = response.webSocket!;
  socket.accept();

  const inbox: ServerMessage[] = [];
  const waiters: ((m: ServerMessage) => void)[] = [];
  socket.addEventListener("message", (ev) => {
    const msg = JSON.parse(ev.data as string) as ServerMessage;
    const w = waiters.shift();
    if (w) w(msg);
    else inbox.push(msg);
  });

  const next = () =>
    new Promise<ServerMessage>((resolve) => {
      const m = inbox.shift();
      if (m) resolve(m);
      else waiters.push(resolve);
    });

  socket.send(JSON.stringify({ type: "join", peerId }));
  return { socket, next };
}

describe("PartyRoom", () => {
  it("replies with welcome + empty members for the first joiner", async () => {
    const { next, socket } = await openAndJoin("PARTY1", "peer-a");
    const msg = await next();
    expect(msg).toEqual({ type: "welcome", peerId: "peer-a", members: [] });
    socket.close();
  });
});
