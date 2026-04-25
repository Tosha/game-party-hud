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

  it("informs existing members when a new peer joins, and includes their ids in the newcomer's welcome", async () => {
    const a = await openAndJoin("PARTY2", "peer-a");
    expect(await a.next()).toEqual({ type: "welcome", peerId: "peer-a", members: [] });

    const b = await openAndJoin("PARTY2", "peer-b");
    expect(await b.next()).toEqual({ type: "welcome", peerId: "peer-b", members: ["peer-a"] });
    expect(await a.next()).toEqual({ type: "peer-joined", peerId: "peer-b" });

    const c = await openAndJoin("PARTY2", "peer-c");
    const welcomeC = await c.next();
    expect(welcomeC.type).toBe("welcome");
    expect((welcomeC as { members: string[] }).members.sort()).toEqual(["peer-a", "peer-b"]);
    expect(await a.next()).toEqual({ type: "peer-joined", peerId: "peer-c" });
    expect(await b.next()).toEqual({ type: "peer-joined", peerId: "peer-c" });

    a.socket.close();
    b.socket.close();
    c.socket.close();
  });

  it("broadcasts peer-left when a member disconnects", async () => {
    const a = await openAndJoin("PARTY3", "peer-a");
    const b = await openAndJoin("PARTY3", "peer-b");
    await a.next(); await a.next(); // welcome + peer-joined
    await b.next(); // welcome

    b.socket.close();
    expect(await a.next()).toEqual({ type: "peer-left", peerId: "peer-b" });
    a.socket.close();
  });
});
