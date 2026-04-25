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

  it("relays a broadcast from A to B and C, but not back to A", async () => {
    const a = await openAndJoin("PARTY4", "peer-a");
    const b = await openAndJoin("PARTY4", "peer-b");
    const c = await openAndJoin("PARTY4", "peer-c");

    // Drain welcome + peer-joined noise
    await a.next(); await a.next(); await a.next();
    await b.next(); await b.next();
    await c.next();

    a.socket.send(JSON.stringify({ type: "broadcast", payload: "hello" }));

    expect(await b.next()).toEqual({ type: "message", fromPeerId: "peer-a", payload: "hello" });
    expect(await c.next()).toEqual({ type: "message", fromPeerId: "peer-a", payload: "hello" });

    // A must NOT receive its own echo. Send a second broadcast from B and confirm
    // A's next message is B's broadcast, not A's earlier one.
    b.socket.send(JSON.stringify({ type: "broadcast", payload: "world" }));
    expect(await a.next()).toEqual({ type: "message", fromPeerId: "peer-b", payload: "world" });

    a.socket.close(); b.socket.close(); c.socket.close();
  });

  it("rejects a 26th joiner with party-full and closes the socket", async () => {
    const sockets: WebSocket[] = [];
    for (let i = 0; i < 25; i++) {
      const p = await openAndJoin("FULL", `peer-${i}`);
      expect((await p.next()).type).toBe("welcome");
      sockets.push(p.socket);
    }

    const overflow = await openAndJoin("FULL", "peer-26");
    const err = await overflow.next();
    expect(err).toEqual({ type: "error", reason: "party-full" });

    for (const s of sockets) s.close();
    overflow.socket.close();
  });

  it("rejects a duplicate peerId with duplicate-peer", async () => {
    const a1 = await openAndJoin("DUPE", "peer-dup");
    expect((await a1.next()).type).toBe("welcome");

    const a2 = await openAndJoin("DUPE", "peer-dup");
    expect(await a2.next()).toEqual({ type: "error", reason: "duplicate-peer" });

    a1.socket.close(); a2.socket.close();
  });

  it("rejects a broadcast larger than 4 KiB with message-too-large", async () => {
    const a = await openAndJoin("BIG", "peer-a");
    expect((await a.next()).type).toBe("welcome");

    const payload = "x".repeat(5000);
    a.socket.send(JSON.stringify({ type: "broadcast", payload }));

    expect(await a.next()).toEqual({ type: "error", reason: "message-too-large" });
    a.socket.close();
  });

  it("rejects bursts above the rate-limit token bucket capacity", async () => {
    // Per-peer bucket: capacity 20, refill 10/s. Sending 21 broadcasts in
    // immediate succession exhausts the burst budget before any meaningful
    // refill can happen — the 21st must come back as rate-limit.
    const a = await openAndJoin("RATE", "peer-a");
    expect((await a.next()).type).toBe("welcome");

    for (let i = 0; i < 21; i++) {
      a.socket.send(JSON.stringify({ type: "broadcast", payload: `m${i}` }));
    }

    // First server-to-A frame after the burst is the rate-limit error.
    expect(await a.next()).toEqual({ type: "error", reason: "rate-limit" });
    a.socket.close();
  });

  it("rejects non-party URLs with 404", async () => {
    const r = await SELF.fetch("http://example.com/healthcheck");
    expect(r.status).toBe(404);
  });

  it("rejects party IDs containing invalid characters with 404", async () => {
    const r = await SELF.fetch("http://example.com/party/hello world", { headers: { Upgrade: "websocket" } });
    expect(r.status).toBe(404);
  });

  it("rejects non-upgrade GETs to /party/:id with 426", async () => {
    const r = await SELF.fetch("http://example.com/party/ABC");
    expect(r.status).toBe(426);
  });

  it("rejects upgrade requests carrying a non-allowlisted Origin header with 403", async () => {
    // A browser-page WebSocket attempt always sets Origin to the page's URL
    // and can't be told not to. Our desktop client never sends Origin. Any
    // request with an Origin we don't allowlist is therefore unwelcome.
    const r = await SELF.fetch("http://example.com/party/ABC", {
      headers: {
        Upgrade: "websocket",
        Origin: "https://attacker.example.com",
      },
    });
    expect(r.status).toBe(403);
  });

  it("accepts upgrade requests with no Origin header (desktop client shape)", async () => {
    // The existing welcome-test path goes through here too, but pin it
    // explicitly so a regression to 'Origin required' is caught immediately.
    const r = await SELF.fetch("http://example.com/party/ABC", {
      headers: { Upgrade: "websocket" },
    });
    expect(r.status).toBe(101);
    r.webSocket?.accept();
    r.webSocket?.close();
  });
});
