# WebSocket relay rewrite — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the WebRTC-mesh + BitTorrent-tracker signaling stack with a Cloudflare-Workers-hosted WebSocket relay ("Option D" from [the reliability review](../specs/2026-04-22-reliability-scalability-review.md)). Net result: ~−400 LOC in the client, a tiny TypeScript server, zero observed-layer failure modes, scales to N=20.

**Architecture:** One TypeScript Cloudflare Worker that routes `wss://.../party/<partyId>` to a per-`partyId` Durable Object. The Durable Object holds a roster of connected WebSockets, broadcasts `peer-joined` / `peer-left` / `message` frames between them, and evicts itself when empty. The C# client (`RelayClient`) opens a single WebSocket, dispatches inbound frames as `OnPeerConnected` / `OnPeerDisconnected` / `OnMessage` events, and auto-reconnects with backoff on transient drops. The existing `PartyOrchestrator` / `PartyState` / `HUD` layers stay untouched — `RelayClient` matches the narrow surface that `PeerNetwork` exposed to them (`BroadcastAsync`, `OnMessage`, `OnPeerConnected`, `OnPeerDisconnected`, `DisposeAsync`).

**Tech stack:** Cloudflare Workers + Durable Objects (TypeScript, wrangler), Vitest + Miniflare for server tests, C# `System.Net.WebSockets.ClientWebSocket` (BCL, no new dep), existing xUnit for client tests. `SIPSorcery` NuGet package is **removed**.

**Non-goals:** authentication, per-message encryption (WSS is enough for HP values), multi-region pinning (Cloudflare routes automatically), backwards compatibility with WebRTC clients (pre-release, zero shipped users).

**Deploy prerequisite** (one-time, by repo maintainer): free Cloudflare account, `npm i -g wrangler`, `wrangler login`, `wrangler deploy` from the `relay/` folder. After first deploy, paste the returned `wss://…workers.dev` URL into `AppConfig.Defaults.RelayUrl`.

---

## File structure

### New files (server — `relay/` at repo root, standalone Node/TypeScript project)

- `relay/package.json` — dependencies + npm scripts
- `relay/tsconfig.json` — TypeScript config for Cloudflare Workers runtime
- `relay/wrangler.toml` — Cloudflare deploy config with the `PartyRoom` Durable Object binding
- `relay/src/protocol.ts` — wire-type unions (JSON shapes sent/received)
- `relay/src/room.ts` — `PartyRoom` Durable Object (one instance per party id)
- `relay/src/worker.ts` — Worker entry point, routes `/party/:id` to the DO
- `relay/test/protocol.test.ts` — protocol encode/decode round-trip, fixture parity
- `relay/test/room.test.ts` — Miniflare integration tests (join/broadcast/leave/caps)
- `relay/test/fixtures.ts` — canonical JSON strings, ALSO duplicated in C# tests — changing one MUST break the other
- `relay/README.md` — deploy/operate instructions for the maintainer
- `relay/.gitignore` — `node_modules/`, `.wrangler/`, `dist/`
- `relay/vitest.config.ts` — Vitest with `@cloudflare/vitest-pool-workers`

### New files (client — C#)

- `src/GamePartyHud/Network/RelayProtocol.cs` — wire records (mirror of `relay/src/protocol.ts`)
- `src/GamePartyHud/Network/RelayClient.cs` — single class replacing `PeerNetwork` + `BitTorrentSignaling`
- `tests/GamePartyHud.Tests/Network/RelayProtocolTests.cs` — encode/decode round-trip, fixture parity against `relay/test/fixtures.ts`
- `tests/GamePartyHud.Tests/Network/FakeRelayServer.cs` — in-process `HttpListener`-based WS server for unit tests (no sockets-to-outside-world)
- `tests/GamePartyHud.Tests/Network/RelayClientTests.cs` — `RelayClient` behaviour against `FakeRelayServer`

### Modified files

- `src/GamePartyHud/GamePartyHud.csproj` — remove `SIPSorcery` package reference
- `src/GamePartyHud/App.xaml.cs` — construct `RelayClient` in place of `PeerNetwork` + `BitTorrentSignaling`
- `src/GamePartyHud/Party/PartyOrchestrator.cs` — change `_net` field/ctor parameter type from `PeerNetwork` to `RelayClient`
- `src/GamePartyHud/Config/AppConfig.cs` — remove TURN fields, add `RelayUrl` with a default
- `docs/requirements.md` — reword the "no centralized server" constraint to "no centralized *storage*"
- `CLAUDE.md` — revise constraint #2 (zero-hosting-cost) to reflect the free-tier relay
- `README.md` (if present) — deploy/operate note

### Deleted files

- `src/GamePartyHud/Network/BitTorrentSignaling.cs`
- `src/GamePartyHud/Network/ISignalingProvider.cs`
- `src/GamePartyHud/Network/PeerNetwork.cs`
- `tests/GamePartyHud.Tests/Network/BitTorrentSignalingWireFormatTests.cs`
- `tests/GamePartyHud.Tests/Network/LoopbackSignaling.cs`
- `tests/GamePartyHud.Tests/Network/SdpCandidateSummaryTests.cs`
- `tests/GamePartyHud.Tests/Network/TwoPeerDiscoveryTests.cs`

---

## Wire protocol

**Format:** JSON objects, one per WebSocket text frame. `camelCase` keys (matches existing `MessageJson` style, driven by `JsonSerializerDefaults.Web`).

### Client → Server

| `type` | Fields | Semantics |
|---|---|---|
| `"join"` | `peerId: string` (40-char lowercase hex, 20 random bytes) | Must be the first frame after connection. `partyId` is in the URL (`/party/<partyId>`), not the body. |
| `"broadcast"` | `payload: string` (opaque to relay — the client puts a `MessageJson`-encoded `PartyMessage` here) | Delivers `payload` to every other connected peer in the same party. |

### Server → Client

| `type` | Fields | Semantics |
|---|---|---|
| `"welcome"` | `peerId: string`, `members: string[]` (current peer ids, excluding self, in arbitrary order) | Sent once, in response to `join`. `members` is the party roster at join time. |
| `"peer-joined"` | `peerId: string` | Another peer joined the party after us. |
| `"peer-left"` | `peerId: string` | Another peer disconnected (graceful or abrupt). |
| `"message"` | `fromPeerId: string`, `payload: string` | Another peer's `broadcast`. |
| `"error"` | `reason: string` | Before a graceful close. Reasons: `"party-full"`, `"invalid-join"`, `"duplicate-peer"`, `"rate-limit"`, `"message-too-large"`, `"protocol-error"`. Relay then closes with WebSocket code 1008 (policy violation) or 1013 (try again later). |

### Limits (enforced server-side)

- Party capacity: **25 peers** (requirements say 20; 5-peer buffer)
- Max frame size: **4 KiB** (real-world `StateMessage` ~120 B — 33× headroom)
- Per-peer rate: **10 messages / second** (burst 20, leaky bucket). Orchestrator ticks at ~0.33 Hz so this is effectively unlimited for honest clients.
- Max `partyId` length: 32 chars, `[A-Za-z0-9_-]`

All limits rejected via `{type:"error", reason:...}` + graceful close. A malformed frame is dropped silently; two consecutive malformed frames close the connection.

---

## Task index

| # | Area | What |
|---|---|---|
| 0 | Prep | Worktree, branch, CLAUDE.md constraint update |
| 1 | Server scaffold | `relay/` folder, package.json, tsconfig, wrangler.toml, vitest config |
| 2 | Protocol types | `protocol.ts` with types + encode/decode |
| 3 | Protocol fixtures | Canonical JSON fixtures shared across TS and C# |
| 4 | Room — join/welcome | Durable Object skeleton, accept one peer, echo `welcome` |
| 5 | Room — roster broadcast | Multiple peers see each other's `peer-joined` / `peer-left` |
| 6 | Room — message relay | `broadcast` from A reaches B and C but not A |
| 7 | Room — caps | Party capacity, message size, rate limit, bad frame |
| 8 | Worker entry | Route `/party/:id` to DO, reject other paths |
| 9 | Server deploy | `wrangler deploy`, record URL, smoke-test with `wscat` |
| 10 | Client protocol types | `RelayProtocol.cs` + parity test against TS fixtures |
| 11 | Client fake server | `FakeRelayServer` test helper |
| 12 | Client — connect + welcome | `RelayClient` opens WS, sends join, parses welcome |
| 13 | Client — inbound events | `OnPeerConnected` / `OnPeerDisconnected` / `OnMessage` |
| 14 | Client — broadcast | `BroadcastAsync` sends `broadcast` frame |
| 15 | Client — reconnect | Exponential backoff, resume with fresh peer id |
| 16 | Integration | Swap `PeerNetwork` for `RelayClient` in App and Orchestrator |
| 17 | Config migration | Remove TURN fields, add `RelayUrl` with default |
| 18 | Delete old code | `Network/*.cs` and obsolete tests |
| 19 | Remove SIPSorcery | Package reference and any stragglers |
| 20 | Docs | `requirements.md`, `CLAUDE.md`, `README.md`, `relay/README.md` |
| 21 | Manual smoke | Two real peers across NATs, 20-peer stress |

---

## Phase 0 — Prep

### Task 0: Create worktree and branch

**Files:**
- None (git state only)

- [ ] **Step 1: Create a worktree off `main` for this rewrite**

```bash
cd /c/Users/tosha/IdeaProjects/game-party-hud
git fetch origin main
git worktree add ../game-party-hud-relay -b feat/websocket-relay origin/main
cd ../game-party-hud-relay
```

Expected: new folder `../game-party-hud-relay` exists and its `git status` says "On branch feat/websocket-relay".

- [ ] **Step 2: Sanity-check build + tests on the fresh branch**

Run:
```bash
dotnet build
dotnet test --nologo
```

Expected: `0 Warning(s)`, `0 Error(s)`, `Passed! - Failed: 0` with 90 tests. If anything else, stop and investigate before proceeding.

- [ ] **Step 3: Commit an empty scaffold marker so later tasks have a stable point to diff against**

```bash
git commit --allow-empty -m "chore: kick off websocket relay rewrite

See docs/superpowers/plans/2026-04-22-websocket-relay-rewrite.md"
```

Expected: `git log --oneline -1` shows the new commit.

---

## Phase 1 — Server scaffold

### Task 1: Scaffold the relay/ folder

**Files:**
- Create: `relay/package.json`
- Create: `relay/tsconfig.json`
- Create: `relay/wrangler.toml`
- Create: `relay/vitest.config.ts`
- Create: `relay/.gitignore`
- Create: `relay/src/worker.ts` (stub, returns 501)

- [ ] **Step 1: Create `relay/package.json`**

File: `relay/package.json`
```json
{
  "name": "gph-relay",
  "version": "0.1.0",
  "private": true,
  "description": "Game Party HUD — WebSocket relay for party signaling and state broadcast.",
  "type": "module",
  "scripts": {
    "test": "vitest run",
    "test:watch": "vitest",
    "dev": "wrangler dev",
    "deploy": "wrangler deploy",
    "typecheck": "tsc --noEmit"
  },
  "devDependencies": {
    "@cloudflare/vitest-pool-workers": "^0.5.0",
    "@cloudflare/workers-types": "^4.20250101.0",
    "typescript": "^5.4.0",
    "vitest": "^1.6.0",
    "wrangler": "^3.80.0"
  }
}
```

- [ ] **Step 2: Create `relay/tsconfig.json`**

File: `relay/tsconfig.json`
```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "ES2022",
    "moduleResolution": "bundler",
    "lib": ["ES2022"],
    "types": ["@cloudflare/workers-types"],
    "strict": true,
    "noUncheckedIndexedAccess": true,
    "noImplicitOverride": true,
    "noFallthroughCasesInSwitch": true,
    "esModuleInterop": true,
    "forceConsistentCasingInFileNames": true,
    "skipLibCheck": true,
    "jsx": "react"
  },
  "include": ["src/**/*.ts", "test/**/*.ts"]
}
```

- [ ] **Step 3: Create `relay/wrangler.toml`**

File: `relay/wrangler.toml`
```toml
name = "gph-relay"
main = "src/worker.ts"
compatibility_date = "2025-01-01"

[[durable_objects.bindings]]
name = "PARTY_ROOM"
class_name = "PartyRoom"

[[migrations]]
tag = "v1"
new_classes = ["PartyRoom"]

# Observability / limits — stay within free tier
[limits]
cpu_ms = 50

[placement]
mode = "smart"
```

- [ ] **Step 4: Create `relay/vitest.config.ts`**

File: `relay/vitest.config.ts`
```typescript
import { defineWorkersConfig } from "@cloudflare/vitest-pool-workers/config";

export default defineWorkersConfig({
  test: {
    poolOptions: {
      workers: {
        wrangler: { configPath: "./wrangler.toml" },
      },
    },
  },
});
```

- [ ] **Step 5: Create `relay/.gitignore`**

File: `relay/.gitignore`
```
node_modules/
.wrangler/
dist/
*.log
.dev.vars
```

- [ ] **Step 6: Create `relay/src/worker.ts` stub**

File: `relay/src/worker.ts`
```typescript
export default {
  async fetch(_request: Request, _env: unknown): Promise<Response> {
    return new Response("not implemented", { status: 501 });
  },
};

// Exported so wrangler's migrations resolve the class name. Actual implementation
// lands in Task 4 onwards.
export class PartyRoom {
  constructor(_state: DurableObjectState, _env: unknown) {}
  async fetch(_request: Request): Promise<Response> {
    return new Response("not implemented", { status: 501 });
  }
}
```

- [ ] **Step 7: Install deps and confirm TS compiles**

```bash
cd relay
npm install
npm run typecheck
```

Expected: no errors from `tsc`. If `npm install` fails with network errors, retry; otherwise investigate before proceeding.

- [ ] **Step 8: Commit**

```bash
git add relay/
git commit -m "feat(relay): scaffold cloudflare worker project

Empty worker that returns 501; empty PartyRoom DO class. Wrangler can
still deploy this, and all type-check tooling resolves."
```

---

## Phase 2 — Protocol

### Task 2: Define wire protocol in TypeScript

**Files:**
- Create: `relay/src/protocol.ts`
- Create: `relay/test/protocol.test.ts`

- [ ] **Step 1: Write the failing protocol round-trip test**

File: `relay/test/protocol.test.ts`
```typescript
import { describe, expect, it } from "vitest";
import {
  type ClientMessage,
  type ServerMessage,
  decodeClientMessage,
  encodeServerMessage,
} from "../src/protocol";

describe("protocol", () => {
  it("decodes a join frame", () => {
    const raw = '{"type":"join","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe"}';
    const msg = decodeClientMessage(raw);
    expect(msg).toEqual({
      type: "join",
      peerId: "a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe",
    });
  });

  it("decodes a broadcast frame", () => {
    const raw = '{"type":"broadcast","payload":"{\\"type\\":\\"state\\",\\"hp\\":0.5}"}';
    const msg = decodeClientMessage(raw);
    expect(msg).toEqual({ type: "broadcast", payload: '{"type":"state","hp":0.5}' });
  });

  it("rejects unknown client types", () => {
    expect(decodeClientMessage('{"type":"something"}')).toBeNull();
  });

  it("rejects malformed JSON", () => {
    expect(decodeClientMessage("not json")).toBeNull();
  });

  it("rejects a join without peerId", () => {
    expect(decodeClientMessage('{"type":"join"}')).toBeNull();
  });

  it("encodes welcome", () => {
    const raw = encodeServerMessage({
      type: "welcome",
      peerId: "abc",
      members: ["def", "ghi"],
    });
    expect(JSON.parse(raw)).toEqual({
      type: "welcome",
      peerId: "abc",
      members: ["def", "ghi"],
    });
  });

  it("encodes peer-joined", () => {
    const raw = encodeServerMessage({ type: "peer-joined", peerId: "xyz" });
    expect(JSON.parse(raw)).toEqual({ type: "peer-joined", peerId: "xyz" });
  });

  it("encodes a message relay frame", () => {
    const msg: ServerMessage = {
      type: "message",
      fromPeerId: "a",
      payload: '{"hp":0.3}',
    };
    const raw = encodeServerMessage(msg);
    expect(JSON.parse(raw)).toEqual(msg);
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd relay
npm test
```

Expected: failures for all tests, either "Cannot find module '../src/protocol'" or undefined exports.

- [ ] **Step 3: Write `relay/src/protocol.ts`**

File: `relay/src/protocol.ts`
```typescript
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
```

- [ ] **Step 4: Run tests to verify all pass**

```bash
npm test
```

Expected: all 8 tests pass.

- [ ] **Step 5: Commit**

```bash
git add relay/src/protocol.ts relay/test/protocol.test.ts
git commit -m "feat(relay): wire protocol types + decoder with round-trip tests"
```

### Task 3: Canonical JSON fixtures shared with the C# client

**Files:**
- Create: `relay/test/fixtures.ts`
- Modify: `relay/test/protocol.test.ts` (assert fixture parity)

- [ ] **Step 1: Create `relay/test/fixtures.ts` with canonical wire strings**

File: `relay/test/fixtures.ts`
```typescript
// CANONICAL wire strings. Exact byte-for-byte match is the contract between
// TS server and C# client — src/GamePartyHud/Network/RelayProtocol.cs and
// tests/GamePartyHud.Tests/Network/RelayProtocolTests.cs MUST reproduce
// these exact strings. If you change one, update the other.

export const fixtures = {
  join:        '{"type":"join","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe"}',
  broadcast:   '{"type":"broadcast","payload":"{\\"type\\":\\"state\\",\\"hp\\":0.5}"}',
  welcome:     '{"type":"welcome","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe","members":["peer-b","peer-c"]}',
  peerJoined:  '{"type":"peer-joined","peerId":"peer-b"}',
  peerLeft:    '{"type":"peer-left","peerId":"peer-b"}',
  message:     '{"type":"message","fromPeerId":"peer-b","payload":"{\\"type\\":\\"state\\",\\"hp\\":0.5}"}',
  errorFull:   '{"type":"error","reason":"party-full"}',
} as const;
```

- [ ] **Step 2: Add a fixture-parity test case to `relay/test/protocol.test.ts`**

Edit `relay/test/protocol.test.ts` — append this inside the `describe` block:

```typescript
  it("client-to-server fixtures round-trip through the decoder", () => {
    const join = decodeClientMessage(fixtures.join);
    expect(join).toEqual({ type: "join", peerId: "a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe" });

    const bc = decodeClientMessage(fixtures.broadcast);
    expect(bc).toEqual({ type: "broadcast", payload: '{"type":"state","hp":0.5}' });
  });

  it("server-to-client fixtures match the encoder output byte-for-byte", () => {
    expect(encodeServerMessage({
      type: "welcome",
      peerId: "a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe",
      members: ["peer-b", "peer-c"],
    })).toBe(fixtures.welcome);

    expect(encodeServerMessage({ type: "peer-joined", peerId: "peer-b" })).toBe(fixtures.peerJoined);
    expect(encodeServerMessage({ type: "peer-left",   peerId: "peer-b" })).toBe(fixtures.peerLeft);
    expect(encodeServerMessage({
      type: "message",
      fromPeerId: "peer-b",
      payload: '{"type":"state","hp":0.5}',
    })).toBe(fixtures.message);
    expect(encodeServerMessage({ type: "error", reason: "party-full" })).toBe(fixtures.errorFull);
  });
```

Add the fixtures import at the top of `relay/test/protocol.test.ts`:
```typescript
import { fixtures } from "./fixtures";
```

- [ ] **Step 3: Run tests**

```bash
npm test
```

Expected: all 10 tests pass.

- [ ] **Step 4: Commit**

```bash
git add relay/test/fixtures.ts relay/test/protocol.test.ts
git commit -m "test(relay): canonical JSON fixtures shared with C# client tests"
```

---

## Phase 3 — Server room

### Task 4: PartyRoom — join/welcome

**Files:**
- Modify: `relay/src/worker.ts`
- Create: `relay/src/room.ts`
- Create: `relay/test/room.test.ts`

- [ ] **Step 1: Write the failing test**

File: `relay/test/room.test.ts`
```typescript
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
```

- [ ] **Step 2: Run test to verify it fails**

```bash
npm test
```

Expected: failure ("webSocket is null", "not implemented", or similar). This is the TDD red.

- [ ] **Step 3: Write `relay/src/room.ts` minimum**

File: `relay/src/room.ts`
```typescript
import {
  type ClientMessage,
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
  private members = new Map<string, Member>(); // peerId -> Member

  constructor(_state: DurableObjectState, _env: unknown) {}

  async fetch(request: Request): Promise<Response> {
    if (request.headers.get("Upgrade") !== "websocket") {
      return new Response("expected WebSocket upgrade", { status: 426 });
    }
    const pair = new WebSocketPair();
    const [client, server] = Object.values(pair);
    server.accept();
    this.attach(server);
    return new Response(null, { status: 101, webSocket: client });
  }

  private attach(socket: WebSocket): void {
    let peerId: string | null = null;

    socket.addEventListener("message", (ev) => {
      const raw = typeof ev.data === "string" ? ev.data : "";
      const msg = decodeClientMessage(raw);
      if (!msg) return;

      if (msg.type === "join") {
        if (peerId !== null) return; // ignore duplicate join
        if (this.members.size >= MAX_PEERS) {
          this.send(socket, { type: "error", reason: "party-full" });
          socket.close(1008, "party-full");
          return;
        }
        if (this.members.has(msg.peerId)) {
          this.send(socket, { type: "error", reason: "duplicate-peer" });
          socket.close(1008, "duplicate-peer");
          return;
        }
        peerId = msg.peerId;
        const members = [...this.members.keys()];
        this.members.set(peerId, { socket, peerId });
        this.send(socket, { type: "welcome", peerId, members });
      }
    });

    socket.addEventListener("close", () => {
      if (peerId !== null) this.members.delete(peerId);
    });
  }

  private send(socket: WebSocket, msg: ServerMessage): void {
    socket.send(encodeServerMessage(msg));
  }
}
```

- [ ] **Step 4: Update `relay/src/worker.ts` to route to the DO**

File: `relay/src/worker.ts`
```typescript
export { PartyRoom } from "./room";

interface Env {
  PARTY_ROOM: DurableObjectNamespace;
}

const PARTY_PATH = /^\/party\/([A-Za-z0-9_-]{1,32})$/;

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    const match = PARTY_PATH.exec(url.pathname);
    if (!match) return new Response("not found", { status: 404 });

    const partyId = match[1]!;
    const id = env.PARTY_ROOM.idFromName(partyId);
    const stub = env.PARTY_ROOM.get(id);
    return stub.fetch(request);
  },
};
```

- [ ] **Step 5: Run test to verify it passes**

```bash
npm test -- --run room
```

Expected: the welcome test passes.

- [ ] **Step 6: Commit**

```bash
git add relay/src/room.ts relay/src/worker.ts relay/test/room.test.ts
git commit -m "feat(relay): PartyRoom DO — single-peer join + welcome"
```

### Task 5: PartyRoom — multi-peer roster, peer-joined and peer-left

**Files:**
- Modify: `relay/src/room.ts`
- Modify: `relay/test/room.test.ts`

- [ ] **Step 1: Add failing tests for roster broadcast**

Append to `relay/test/room.test.ts` inside the `describe("PartyRoom"...)` block:

```typescript
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
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
npm test -- --run room
```

Expected: the two new tests fail (no peer-joined / peer-left broadcast yet).

- [ ] **Step 3: Implement the broadcasts in `relay/src/room.ts`**

Replace the body of `attach` with this version:

```typescript
  private attach(socket: WebSocket): void {
    let peerId: string | null = null;

    socket.addEventListener("message", (ev) => {
      const raw = typeof ev.data === "string" ? ev.data : "";
      const msg = decodeClientMessage(raw);
      if (!msg) return;

      if (msg.type === "join") {
        if (peerId !== null) return;
        if (this.members.size >= MAX_PEERS) {
          this.send(socket, { type: "error", reason: "party-full" });
          socket.close(1008, "party-full");
          return;
        }
        if (this.members.has(msg.peerId)) {
          this.send(socket, { type: "error", reason: "duplicate-peer" });
          socket.close(1008, "duplicate-peer");
          return;
        }
        peerId = msg.peerId;
        const members = [...this.members.keys()];
        this.members.set(peerId, { socket, peerId });
        this.send(socket, { type: "welcome", peerId, members });
        this.broadcastExcept(peerId, { type: "peer-joined", peerId });
      }
    });

    socket.addEventListener("close", () => {
      if (peerId === null) return;
      this.members.delete(peerId);
      this.broadcastExcept(peerId, { type: "peer-left", peerId });
    });
  }

  private broadcastExcept(excludePeerId: string, msg: ServerMessage): void {
    const encoded = encodeServerMessage(msg);
    for (const m of this.members.values()) {
      if (m.peerId === excludePeerId) continue;
      try { m.socket.send(encoded); } catch { /* dead socket; close event will clean up */ }
    }
  }
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
npm test -- --run room
```

Expected: all 3 PartyRoom tests + both protocol tests pass.

- [ ] **Step 5: Commit**

```bash
git add relay/src/room.ts relay/test/room.test.ts
git commit -m "feat(relay): broadcast peer-joined / peer-left to roster"
```

### Task 6: PartyRoom — message relay

**Files:**
- Modify: `relay/src/room.ts`
- Modify: `relay/test/room.test.ts`

- [ ] **Step 1: Add failing test for message relay**

Append to `relay/test/room.test.ts`:

```typescript
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
```

- [ ] **Step 2: Run test to verify it fails**

```bash
npm test -- --run room
```

Expected: failure — broadcasts are ignored by the server today.

- [ ] **Step 3: Implement broadcast handling**

In `relay/src/room.ts`, extend the message handler inside `attach`:

```typescript
      if (msg.type === "broadcast") {
        if (peerId === null) return; // ignore until joined
        this.broadcastExcept(peerId, {
          type: "message",
          fromPeerId: peerId,
          payload: msg.payload,
        });
      }
```

Place this as a second branch after the existing `if (msg.type === "join") { ... }` block (still inside the same `addEventListener("message", ...)`).

- [ ] **Step 4: Run tests to verify they pass**

```bash
npm test -- --run room
```

Expected: all 4 PartyRoom tests pass.

- [ ] **Step 5: Commit**

```bash
git add relay/src/room.ts relay/test/room.test.ts
git commit -m "feat(relay): relay broadcast messages to other party members"
```

### Task 7: PartyRoom — safety caps

**Files:**
- Modify: `relay/src/room.ts`
- Modify: `relay/test/room.test.ts`

- [ ] **Step 1: Add failing tests**

Append to `relay/test/room.test.ts`:

```typescript
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
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
npm test -- --run room
```

Expected: party-full test passes already (implemented in Task 4), duplicate-peer test passes already, but message-too-large fails — we don't enforce size yet.

- [ ] **Step 3: Enforce frame-size cap**

In `relay/src/room.ts`, at the top of the message handler in `attach`, before `decodeClientMessage`:

```typescript
      const raw = typeof ev.data === "string" ? ev.data : "";
      if (raw.length > 4096) {
        this.send(socket, { type: "error", reason: "message-too-large" });
        return;
      }
      const msg = decodeClientMessage(raw);
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
npm test -- --run room
```

Expected: all 7 PartyRoom tests pass.

- [ ] **Step 5: Commit**

```bash
git add relay/src/room.ts relay/test/room.test.ts
git commit -m "feat(relay): enforce party capacity, duplicate peer, and frame-size caps"
```

### Task 8: Worker entry point — 404 and invalid partyId rejection

**Files:**
- Modify: `relay/test/room.test.ts` (add worker-level tests; the `SELF.fetch` helper goes through the whole worker)
- Already done in Task 4: `relay/src/worker.ts`

- [ ] **Step 1: Add failing tests**

Append to `relay/test/room.test.ts`:

```typescript
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
```

- [ ] **Step 2: Run tests to verify they pass (behavior already matches)**

```bash
npm test -- --run room
```

Expected: all pass. Current `worker.ts` returns 404 for non-matching paths and `room.ts` returns 426 when upgrade header is absent.

- [ ] **Step 3: Commit**

```bash
git add relay/test/room.test.ts
git commit -m "test(relay): worker-level rejection of invalid paths and non-upgrade GETs"
```

### Task 9: Deploy to Cloudflare and smoke-test

**Files:**
- Modify: `relay/wrangler.toml` (record post-deploy URL for reference)
- Create: `relay/README.md`

- [ ] **Step 1: Authenticate and deploy**

```bash
cd relay
npx wrangler login     # Opens browser; authorize once
npx wrangler deploy
```

Expected: output includes a line like `https://gph-relay.<subdomain>.workers.dev`. Copy this URL.

- [ ] **Step 2: Smoke-test with `wscat`**

```bash
# Install wscat if not present: npm i -g wscat
wscat -c wss://gph-relay.<subdomain>.workers.dev/party/SMOKE
# In the interactive prompt:
> {"type":"join","peerId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}
< {"type":"welcome","peerId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","members":[]}
```

Expected: welcome frame arrives within a second.

- [ ] **Step 3: Write `relay/README.md`**

File: `relay/README.md`
````markdown
# gph-relay — WebSocket relay for Game Party HUD

Stateless Cloudflare Worker + Durable Object that routes party-state broadcasts
between GamePartyHud clients. One Durable Object per party id; each broadcast
fans out to the other members. No storage.

## Setup (one-time)

1. Create a free [Cloudflare account](https://dash.cloudflare.com/sign-up).
2. `npm i` inside this folder.
3. `npx wrangler login`.
4. `npx wrangler deploy`.
5. Paste the deployed URL (e.g. `https://gph-relay.<you>.workers.dev`) into
   `AppConfig.Defaults.RelayUrl` — replacing `https://` with `wss://`.

## Costs

Well inside the Cloudflare free tier for the forecast usage (≤ 100 k requests /
day, ≤ 1 GiB egress, Durable Objects free on Workers Paid only — so if you're on
free plan, use the `workerd` DO free beta or upgrade to Workers Paid at $5/mo
flat).

## Local dev

```bash
npm run dev          # wrangler dev — serves on http://localhost:8787
npm test             # vitest with Miniflare
```

## Wire protocol

See `src/protocol.ts` and the fixtures in `test/fixtures.ts`. Those strings are
also the source of truth for the C# client's JSON parsing; keep them in sync.
````

- [ ] **Step 4: Commit and record the URL**

```bash
git add relay/README.md
git commit -m "docs(relay): deploy instructions"
```

---

## Phase 4 — C# client

### Task 10: C# protocol types + fixture parity

**Files:**
- Create: `src/GamePartyHud/Network/RelayProtocol.cs`
- Create: `tests/GamePartyHud.Tests/Network/RelayProtocolTests.cs`

- [ ] **Step 1: Write failing test**

File: `tests/GamePartyHud.Tests/Network/RelayProtocolTests.cs`
```csharp
using System.Text.Json;
using GamePartyHud.Network;
using Xunit;

namespace GamePartyHud.Tests.Network;

/// <summary>
/// Wire-format parity with relay/test/fixtures.ts. Any change here must be
/// mirrored on the TypeScript side — the tests on both sides pin the same
/// canonical JSON strings so the server and client can't drift apart silently.
/// </summary>
public class RelayProtocolTests
{
    // Exact copies of relay/test/fixtures.ts.
    private const string FxJoin       = """{"type":"join","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe"}""";
    private const string FxBroadcast  = """{"type":"broadcast","payload":"{\"type\":\"state\",\"hp\":0.5}"}""";
    private const string FxWelcome    = """{"type":"welcome","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe","members":["peer-b","peer-c"]}""";
    private const string FxPeerJoined = """{"type":"peer-joined","peerId":"peer-b"}""";
    private const string FxPeerLeft   = """{"type":"peer-left","peerId":"peer-b"}""";
    private const string FxMessage    = """{"type":"message","fromPeerId":"peer-b","payload":"{\"type\":\"state\",\"hp\":0.5}"}""";

    [Fact]
    public void EncodeJoin_MatchesTsFixture()
    {
        Assert.Equal(FxJoin, RelayProtocol.EncodeJoin("a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe"));
    }

    [Fact]
    public void EncodeBroadcast_MatchesTsFixture()
    {
        Assert.Equal(FxBroadcast, RelayProtocol.EncodeBroadcast("""{"type":"state","hp":0.5}"""));
    }

    [Fact]
    public void DecodeWelcome_ParsesAllFields()
    {
        var msg = RelayProtocol.DecodeServerMessage(FxWelcome);
        var welcome = Assert.IsType<RelayProtocol.Welcome>(msg);
        Assert.Equal("a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe", welcome.PeerId);
        Assert.Equal(new[] { "peer-b", "peer-c" }, welcome.Members);
    }

    [Fact]
    public void DecodePeerJoined_Parses()
    {
        var msg = RelayProtocol.DecodeServerMessage(FxPeerJoined);
        var joined = Assert.IsType<RelayProtocol.PeerJoined>(msg);
        Assert.Equal("peer-b", joined.PeerId);
    }

    [Fact]
    public void DecodePeerLeft_Parses()
    {
        var msg = RelayProtocol.DecodeServerMessage(FxPeerLeft);
        var left = Assert.IsType<RelayProtocol.PeerLeft>(msg);
        Assert.Equal("peer-b", left.PeerId);
    }

    [Fact]
    public void DecodeMessage_Parses()
    {
        var msg = RelayProtocol.DecodeServerMessage(FxMessage);
        var m = Assert.IsType<RelayProtocol.Message>(msg);
        Assert.Equal("peer-b", m.FromPeerId);
        Assert.Equal("""{"type":"state","hp":0.5}""", m.Payload);
    }

    [Fact]
    public void DecodeError_Parses()
    {
        var msg = RelayProtocol.DecodeServerMessage("""{"type":"error","reason":"party-full"}""");
        var err = Assert.IsType<RelayProtocol.ErrorMessage>(msg);
        Assert.Equal("party-full", err.Reason);
    }

    [Fact]
    public void DecodeMalformedJson_ReturnsNull()
    {
        Assert.Null(RelayProtocol.DecodeServerMessage("not json"));
    }

    [Fact]
    public void DecodeUnknownType_ReturnsNull()
    {
        Assert.Null(RelayProtocol.DecodeServerMessage("""{"type":"hello"}"""));
    }
}
```

- [ ] **Step 2: Run test to verify it fails (compile error on missing type)**

```bash
dotnet test --nologo
```

Expected: compile failure "The type or namespace name 'RelayProtocol' could not be found".

- [ ] **Step 3: Write `RelayProtocol.cs`**

File: `src/GamePartyHud/Network/RelayProtocol.cs`
```csharp
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace GamePartyHud.Network;

/// <summary>
/// Wire contract between the C# <see cref="RelayClient"/> and the TypeScript
/// Cloudflare Worker in the repo's <c>relay/</c> folder. Keep in lockstep with
/// <c>relay/src/protocol.ts</c>; the canonical JSON strings live in
/// <c>relay/test/fixtures.ts</c> and are mirrored verbatim in this project's
/// <c>RelayProtocolTests</c>.
/// </summary>
public static class RelayProtocol
{
    public abstract record ServerMessage;
    public sealed record Welcome(string PeerId, IReadOnlyList<string> Members) : ServerMessage;
    public sealed record PeerJoined(string PeerId)                             : ServerMessage;
    public sealed record PeerLeft(string PeerId)                               : ServerMessage;
    public sealed record Message(string FromPeerId, string Payload)            : ServerMessage;
    public sealed record ErrorMessage(string Reason)                           : ServerMessage;

    // Serializer options chosen so the output matches relay/test/fixtures.ts
    // byte-for-byte: camelCase (web default), no insertion of whitespace,
    // standard JSON escapes for the payload.
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static string EncodeJoin(string peerId) =>
        JsonSerializer.Serialize(new { type = "join", peerId }, WireJson);

    public static string EncodeBroadcast(string payload) =>
        JsonSerializer.Serialize(new { type = "broadcast", payload }, WireJson);

    public static ServerMessage? DecodeServerMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return null;

            return typeEl.GetString() switch
            {
                "welcome" => new Welcome(
                    root.GetProperty("peerId").GetString() ?? "",
                    ReadStringArray(root.GetProperty("members"))),
                "peer-joined" => new PeerJoined(root.GetProperty("peerId").GetString() ?? ""),
                "peer-left"   => new PeerLeft(root.GetProperty("peerId").GetString() ?? ""),
                "message"     => new Message(
                    root.GetProperty("fromPeerId").GetString() ?? "",
                    root.GetProperty("payload").GetString() ?? ""),
                "error"       => new ErrorMessage(root.GetProperty("reason").GetString() ?? ""),
                _ => null
            };
        }
        catch (JsonException) { return null; }
        catch (KeyNotFoundException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement el)
    {
        var list = new List<string>(el.GetArrayLength());
        foreach (var item in el.EnumerateArray())
        {
            list.Add(item.GetString() ?? "");
        }
        return list;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test --nologo --filter FullyQualifiedName~RelayProtocolTests
```

Expected: 9 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/GamePartyHud/Network/RelayProtocol.cs tests/GamePartyHud.Tests/Network/RelayProtocolTests.cs
git commit -m "feat(network): RelayProtocol — wire types with fixture parity against TS server"
```

### Task 11: FakeRelayServer test helper

**Files:**
- Create: `tests/GamePartyHud.Tests/Network/FakeRelayServer.cs`

- [ ] **Step 1: Write the helper**

File: `tests/GamePartyHud.Tests/Network/FakeRelayServer.cs`
```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GamePartyHud.Tests.Network;

/// <summary>
/// Minimal in-process WebSocket server for testing <c>RelayClient</c>. Binds to
/// a loopback port, accepts exactly one connection at a time, and exposes the
/// active <see cref="WebSocket"/> so tests can drive it: send server frames,
/// receive client frames, close from the server side.
///
/// Not a full relay implementation — tests that care about routing logic drive
/// that behaviour via <see cref="SendFromServerAsync"/>.
/// </summary>
public sealed class FakeRelayServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private WebSocket? _active;
    private readonly ConcurrentQueue<string> _received = new();
    private readonly SemaphoreSlim _receivedSignal = new(0);

    public string WsUrl { get; }

    public FakeRelayServer()
    {
        var port = FindFreePort();
        var prefix = $"http://localhost:{port}/";
        _listener.Prefixes.Add(prefix);
        _listener.Start();
        WsUrl = $"ws://localhost:{port}/party/TEST";
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { return; }

            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 426;
                ctx.Response.Close();
                continue;
            }

            var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
            _active = wsCtx.WebSocket;
            _ = Task.Run(() => ReadLoopAsync(wsCtx.WebSocket));
        }
    }

    private async Task ReadLoopAsync(WebSocket ws)
    {
        var buf = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
        {
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(buf, _cts.Token).ConfigureAwait(false); }
            catch { return; }

            if (r.MessageType == WebSocketMessageType.Close) return;
            var text = Encoding.UTF8.GetString(buf, 0, r.Count);
            _received.Enqueue(text);
            _receivedSignal.Release();
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for the next frame the client sent us.</summary>
    public async Task<string> NextReceivedAsync(TimeSpan timeout)
    {
        if (!await _receivedSignal.WaitAsync(timeout).ConfigureAwait(false))
            throw new TimeoutException("FakeRelayServer: no client frame arrived in time.");
        _received.TryDequeue(out var msg);
        return msg!;
    }

    /// <summary>Sends a text frame from the server to the currently-connected client.</summary>
    public async Task SendFromServerAsync(string text)
    {
        var ws = _active ?? throw new InvalidOperationException("No active WebSocket connection.");
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, _cts.Token).ConfigureAwait(false);
    }

    /// <summary>Closes the active connection from the server side.</summary>
    public async Task CloseFromServerAsync(WebSocketCloseStatus code = WebSocketCloseStatus.NormalClosure, string reason = "bye")
    {
        var ws = _active;
        if (ws is null) return;
        try { await ws.CloseAsync(code, reason, CancellationToken.None).ConfigureAwait(false); } catch { }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        return ValueTask.CompletedTask;
    }

    private static int FindFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
```

- [ ] **Step 2: Verify it compiles (no tests use it yet; build should succeed)**

```bash
dotnet build
```

Expected: Build succeeded. 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add tests/GamePartyHud.Tests/Network/FakeRelayServer.cs
git commit -m "test: FakeRelayServer helper for in-process RelayClient testing"
```

### Task 12: RelayClient — connect, send join, parse welcome

**Files:**
- Create: `src/GamePartyHud/Network/RelayClient.cs`
- Create: `tests/GamePartyHud.Tests/Network/RelayClientTests.cs`

- [ ] **Step 1: Write failing test**

File: `tests/GamePartyHud.Tests/Network/RelayClientTests.cs`
```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using GamePartyHud.Network;
using Xunit;

namespace GamePartyHud.Tests.Network;

public class RelayClientTests
{
    private const string PeerA = "a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe";

    [Fact(Timeout = 10_000)]
    public async Task JoinAsync_SendsJoinFrameAndAwaitsWelcome()
    {
        await using var server = new FakeRelayServer();
        var client = new RelayClient(PeerA, new Uri(server.WsUrl));

        var joinTask = client.JoinAsync(CancellationToken.None);

        // Server sees the join frame.
        var joinFrame = await server.NextReceivedAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("""{"type":"join","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe"}""", joinFrame);

        // Server replies with welcome.
        await server.SendFromServerAsync("""{"type":"welcome","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe","members":[]}""");

        // Client's JoinAsync completes.
        await joinTask;
        Assert.True(client.IsJoined);

        await client.DisposeAsync();
    }
}
```

- [ ] **Step 2: Run test (expect compile failure)**

```bash
dotnet test --nologo --filter FullyQualifiedName~RelayClientTests
```

Expected: "The type or namespace name 'RelayClient' could not be found".

- [ ] **Step 3: Write minimal `RelayClient.cs`**

File: `src/GamePartyHud/Network/RelayClient.cs`
```csharp
using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GamePartyHud.Diagnostics;

namespace GamePartyHud.Network;

/// <summary>
/// WebSocket client for the relay server in <c>relay/</c>. Replaces the
/// earlier <c>PeerNetwork</c> + <c>BitTorrentSignaling</c> duo: one persistent
/// WebSocket to the relay, protocol frames mapped onto the same three events
/// (<see cref="OnPeerConnected"/>, <see cref="OnPeerDisconnected"/>,
/// <see cref="OnMessage"/>) that <c>PartyOrchestrator</c> already consumes.
/// </summary>
public sealed class RelayClient : IAsyncDisposable
{
    private readonly string _selfPeerId;
    private readonly Uri _relayWsUri;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _readCts;
    private TaskCompletionSource? _welcomeTcs;

    public bool IsJoined { get; private set; }
    public string SelfPeerId => _selfPeerId;

    public event Action<string>? OnPeerConnected;
    public event Action<string>? OnPeerDisconnected;
    public event Action<string, string>? OnMessage;

    public RelayClient(string selfPeerId, Uri relayWsUri)
    {
        _selfPeerId = selfPeerId;
        _relayWsUri = relayWsUri;
    }

    public async Task JoinAsync(CancellationToken ct)
    {
        Log.Info($"RelayClient: connecting to {_relayWsUri} as {_selfPeerId[..Math.Min(8, _selfPeerId.Length)]}….");

        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await _ws.ConnectAsync(_relayWsUri, ct).ConfigureAwait(false);

        _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _welcomeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() => ReadLoopAsync(_ws, _readCts.Token));

        // Send join.
        var joinFrame = RelayProtocol.EncodeJoin(_selfPeerId);
        await SendTextAsync(joinFrame, ct).ConfigureAwait(false);

        // Await welcome (or the linked CT firing).
        using (ct.Register(() => _welcomeTcs.TrySetCanceled(ct)))
        {
            await _welcomeTcs.Task.ConfigureAwait(false);
        }

        IsJoined = true;
        Log.Info($"RelayClient: joined. Self peer id={_selfPeerId}.");
    }

    private async Task SendTextAsync(string text, CancellationToken ct)
    {
        var ws = _ws ?? throw new InvalidOperationException("WebSocket not connected.");
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                int total = 0;
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(new ArraySegment<byte>(buf, total, buf.Length - total), ct).ConfigureAwait(false);
                    total += r.Count;
                    if (total >= buf.Length) break;
                } while (!r.EndOfMessage);

                if (r.MessageType == WebSocketMessageType.Close) break;
                var text = Encoding.UTF8.GetString(buf, 0, total);
                Dispatch(text);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"RelayClient: read loop ended — {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private void Dispatch(string text)
    {
        var msg = RelayProtocol.DecodeServerMessage(text);
        if (msg is null)
        {
            Log.Warn($"RelayClient: dropping unparseable frame ({text.Length}B).");
            return;
        }

        switch (msg)
        {
            case RelayProtocol.Welcome w:
                Log.Info($"RelayClient: welcome received with {w.Members.Count} existing member(s).");
                foreach (var id in w.Members) OnPeerConnected?.Invoke(id);
                _welcomeTcs?.TrySetResult();
                break;
            case RelayProtocol.PeerJoined j:
                Log.Info($"RelayClient: peer-joined {j.PeerId[..Math.Min(8, j.PeerId.Length)]}….");
                OnPeerConnected?.Invoke(j.PeerId);
                break;
            case RelayProtocol.PeerLeft l:
                Log.Info($"RelayClient: peer-left {l.PeerId[..Math.Min(8, l.PeerId.Length)]}….");
                OnPeerDisconnected?.Invoke(l.PeerId);
                break;
            case RelayProtocol.Message m:
                OnMessage?.Invoke(m.FromPeerId, m.Payload);
                break;
            case RelayProtocol.ErrorMessage e:
                Log.Warn($"RelayClient: relay error — {e.Reason}");
                _welcomeTcs?.TrySetException(new InvalidOperationException($"relay rejected join: {e.Reason}"));
                break;
        }
    }

    public Task BroadcastAsync(string json)
    {
        if (_ws is not { State: WebSocketState.Open }) return Task.CompletedTask;
        var frame = RelayProtocol.EncodeBroadcast(json);
        return SendTextAsync(frame, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _readCts?.Cancel();
        if (_ws is { State: WebSocketState.Open } ws)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false); } catch { }
        }
        _ws?.Dispose();
        IsJoined = false;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test --nologo --filter FullyQualifiedName~RelayClientTests
```

Expected: `JoinAsync_SendsJoinFrameAndAwaitsWelcome` passes.

- [ ] **Step 5: Commit**

```bash
git add src/GamePartyHud/Network/RelayClient.cs tests/GamePartyHud.Tests/Network/RelayClientTests.cs
git commit -m "feat(network): RelayClient — connect, send join, await welcome"
```

### Task 13: RelayClient — inbound event dispatch

**Files:**
- Modify: `tests/GamePartyHud.Tests/Network/RelayClientTests.cs`
- No client-code changes expected — dispatch is already implemented in Task 12.

- [ ] **Step 1: Add failing tests**

Append inside the `RelayClientTests` class in `tests/GamePartyHud.Tests/Network/RelayClientTests.cs`:

```csharp
    [Fact(Timeout = 10_000)]
    public async Task Welcome_WithMembers_FiresOnPeerConnectedForEach()
    {
        await using var server = new FakeRelayServer();
        var client = new RelayClient(PeerA, new Uri(server.WsUrl));

        var seen = new List<string>();
        client.OnPeerConnected += id => { lock (seen) seen.Add(id); };

        var joinTask = client.JoinAsync(CancellationToken.None);
        await server.NextReceivedAsync(TimeSpan.FromSeconds(5));
        await server.SendFromServerAsync("""{"type":"welcome","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe","members":["peer-b","peer-c"]}""");
        await joinTask;

        // Give the dispatch a moment; it's synchronous off ReadLoop but OnPeerConnected
        // fires before _welcomeTcs so ordering is guaranteed by the time JoinAsync returns.
        Assert.Equal(new[] { "peer-b", "peer-c" }, seen);

        await client.DisposeAsync();
    }

    [Fact(Timeout = 10_000)]
    public async Task PeerJoined_FiresOnPeerConnected()
    {
        await using var server = new FakeRelayServer();
        var client = new RelayClient(PeerA, new Uri(server.WsUrl));

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnPeerConnected += id => { if (id != "ignore-me") tcs.TrySetResult(id); };

        var joinTask = client.JoinAsync(CancellationToken.None);
        await server.NextReceivedAsync(TimeSpan.FromSeconds(5));
        await server.SendFromServerAsync("""{"type":"welcome","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe","members":[]}""");
        await joinTask;

        await server.SendFromServerAsync("""{"type":"peer-joined","peerId":"peer-b"}""");
        var id = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("peer-b", id);

        await client.DisposeAsync();
    }

    [Fact(Timeout = 10_000)]
    public async Task PeerLeft_FiresOnPeerDisconnected()
    {
        await using var server = new FakeRelayServer();
        var client = new RelayClient(PeerA, new Uri(server.WsUrl));

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnPeerDisconnected += id => tcs.TrySetResult(id);

        var joinTask = client.JoinAsync(CancellationToken.None);
        await server.NextReceivedAsync(TimeSpan.FromSeconds(5));
        await server.SendFromServerAsync("""{"type":"welcome","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe","members":[]}""");
        await joinTask;

        await server.SendFromServerAsync("""{"type":"peer-left","peerId":"peer-b"}""");
        var id = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("peer-b", id);

        await client.DisposeAsync();
    }

    [Fact(Timeout = 10_000)]
    public async Task Message_FiresOnMessageWithFromPeerIdAndPayload()
    {
        await using var server = new FakeRelayServer();
        var client = new RelayClient(PeerA, new Uri(server.WsUrl));

        var tcs = new TaskCompletionSource<(string from, string payload)>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnMessage += (from, payload) => tcs.TrySetResult((from, payload));

        var joinTask = client.JoinAsync(CancellationToken.None);
        await server.NextReceivedAsync(TimeSpan.FromSeconds(5));
        await server.SendFromServerAsync("""{"type":"welcome","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe","members":[]}""");
        await joinTask;

        await server.SendFromServerAsync("""{"type":"message","fromPeerId":"peer-b","payload":"{\"hp\":0.42}"}""");
        var (from, payload) = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("peer-b", from);
        Assert.Equal("""{"hp":0.42}""", payload);

        await client.DisposeAsync();
    }
```

Add `using System.Collections.Generic;` at the top of the file if not already present.

- [ ] **Step 2: Run tests — all should pass**

```bash
dotnet test --nologo --filter FullyQualifiedName~RelayClientTests
```

Expected: 5 tests pass (the join test from Task 12 + 4 new ones).

- [ ] **Step 3: Commit**

```bash
git add tests/GamePartyHud.Tests/Network/RelayClientTests.cs
git commit -m "test(network): RelayClient dispatches server frames onto peer + message events"
```

### Task 14: RelayClient — BroadcastAsync

**Files:**
- Modify: `tests/GamePartyHud.Tests/Network/RelayClientTests.cs`

- [ ] **Step 1: Add failing test**

Append to `RelayClientTests`:

```csharp
    [Fact(Timeout = 10_000)]
    public async Task BroadcastAsync_SendsBroadcastFrameWithPayload()
    {
        await using var server = new FakeRelayServer();
        var client = new RelayClient(PeerA, new Uri(server.WsUrl));

        var joinTask = client.JoinAsync(CancellationToken.None);
        await server.NextReceivedAsync(TimeSpan.FromSeconds(5));
        await server.SendFromServerAsync("""{"type":"welcome","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe","members":[]}""");
        await joinTask;

        await client.BroadcastAsync("""{"type":"state","hp":0.5}""");

        var frame = await server.NextReceivedAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("""{"type":"broadcast","payload":"{\"type\":\"state\",\"hp\":0.5}"}""", frame);

        await client.DisposeAsync();
    }
```

- [ ] **Step 2: Run tests**

```bash
dotnet test --nologo --filter FullyQualifiedName~RelayClientTests
```

Expected: 6 tests pass (`BroadcastAsync` is already implemented in Task 12; this test is a regression pin).

- [ ] **Step 3: Commit**

```bash
git add tests/GamePartyHud.Tests/Network/RelayClientTests.cs
git commit -m "test(network): pin RelayClient BroadcastAsync frame format"
```

### Task 15: RelayClient — reconnect with exponential backoff

**Files:**
- Modify: `src/GamePartyHud/Network/RelayClient.cs`
- Modify: `tests/GamePartyHud.Tests/Network/RelayClientTests.cs`

- [ ] **Step 1: Add failing test**

Append to `RelayClientTests`:

```csharp
    [Fact(Timeout = 20_000)]
    public async Task ReadLoop_ServerCloses_ReconnectsAndResendsJoin()
    {
        await using var server = new FakeRelayServer();
        var client = new RelayClient(PeerA, new Uri(server.WsUrl));

        var joinTask = client.JoinAsync(CancellationToken.None);
        Assert.Equal("""{"type":"join","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe"}""",
            await server.NextReceivedAsync(TimeSpan.FromSeconds(5)));
        await server.SendFromServerAsync("""{"type":"welcome","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe","members":[]}""");
        await joinTask;

        // Server abruptly closes. Client should reconnect and re-send the join frame.
        await server.CloseFromServerAsync();

        // Second join frame arrives on a new socket.
        var rejoin = await server.NextReceivedAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("""{"type":"join","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe"}""", rejoin);

        await client.DisposeAsync();
    }
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test --nologo --filter FullyQualifiedName~ReadLoop_ServerCloses
```

Expected: TimeoutException — no reconnect wired up yet.

- [ ] **Step 3: Implement reconnect in `RelayClient.cs`**

Replace the body of `ReadLoopAsync` with the version below, and add the helper `ReconnectLoopAsync`:

```csharp
    private async Task ReadLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                int total = 0;
                WebSocketReceiveResult r;
                try
                {
                    do
                    {
                        r = await ws.ReceiveAsync(new ArraySegment<byte>(buf, total, buf.Length - total), ct).ConfigureAwait(false);
                        total += r.Count;
                        if (total >= buf.Length) break;
                    } while (!r.EndOfMessage);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Log.Warn($"RelayClient: receive error — {ex.Message}; reconnecting.");
                    _ = Task.Run(() => ReconnectLoopAsync(ct));
                    return;
                }

                if (r.MessageType == WebSocketMessageType.Close)
                {
                    Log.Info("RelayClient: server closed the socket; reconnecting.");
                    _ = Task.Run(() => ReconnectLoopAsync(ct));
                    return;
                }
                var text = Encoding.UTF8.GetString(buf, 0, total);
                Dispatch(text);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    // Monotonic backoff: 500 ms, 1 s, 2 s, 4 s, then cap at 8 s. Resets on success.
    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        var delays = new[] { 500, 1_000, 2_000, 4_000, 8_000 };
        int attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                _ws?.Dispose();
                _ws = new ClientWebSocket();
                _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await _ws.ConnectAsync(_relayWsUri, ct).ConfigureAwait(false);
                Log.Info($"RelayClient: reconnected on attempt #{attempt + 1}.");
                _ = Task.Run(() => ReadLoopAsync(_ws, ct));

                // Re-send the join frame so the server adds us to the roster again.
                var join = RelayProtocol.EncodeJoin(_selfPeerId);
                await SendTextAsync(join, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                var delay = delays[Math.Min(attempt, delays.Length - 1)];
                Log.Warn($"RelayClient: reconnect attempt #{attempt + 1} failed — {ex.Message}. Retrying in {delay} ms.");
                try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                attempt++;
            }
        }
    }
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test --nologo --filter FullyQualifiedName~ReadLoop_ServerCloses
```

Expected: passes. The test now completes because the reconnect loop reopens the socket and re-sends the join frame.

- [ ] **Step 5: Run the whole test suite**

```bash
dotnet test --nologo
```

Expected: all tests pass. Count should now be whatever was there (90) + new RelayProtocol + RelayClient tests, minus none so far (old Network tests are deleted in Task 18).

- [ ] **Step 6: Commit**

```bash
git add src/GamePartyHud/Network/RelayClient.cs tests/GamePartyHud.Tests/Network/RelayClientTests.cs
git commit -m "feat(network): RelayClient reconnects on socket drop with exponential backoff"
```

---

## Phase 5 — Integration

### Task 16: Swap PartyOrchestrator to depend on RelayClient

**Files:**
- Modify: `src/GamePartyHud/Party/PartyOrchestrator.cs` — lines referencing `PeerNetwork`

- [ ] **Step 1: Change the field type and constructor parameter**

In `src/GamePartyHud/Party/PartyOrchestrator.cs`:

Replace:
```csharp
using GamePartyHud.Network;
```
(No change — still needed.)

Replace:
```csharp
    private readonly PeerNetwork _net;
```
with:
```csharp
    private readonly RelayClient _net;
```

Replace the constructor signature:
```csharp
    public PartyOrchestrator(
        AppConfig cfg,
        IScreenCapture capture,
        PartyState state,
        PeerNetwork net,
        string selfPeerId)
```
with:
```csharp
    public PartyOrchestrator(
        AppConfig cfg,
        IScreenCapture capture,
        PartyState state,
        RelayClient net,
        string selfPeerId)
```

- [ ] **Step 2: Build — expect a compile error in `App.xaml.cs`**

```bash
dotnet build 2>&1 | tail -10
```

Expected: compile error in `App.xaml.cs` about the `PartyOrchestrator` constructor arguments (we haven't changed App yet). Next task fixes it.

- [ ] **Step 3: Do NOT commit yet — it doesn't build.** Proceed to Task 17.

### Task 17: Update AppConfig + App.xaml.cs composition root

**Files:**
- Modify: `src/GamePartyHud/Config/AppConfig.cs`
- Modify: `src/GamePartyHud/App.xaml.cs`

- [ ] **Step 1: Update `AppConfig.cs` — drop TURN fields, add RelayUrl**

Replace the entire content of `src/GamePartyHud/Config/AppConfig.cs` with:

```csharp
using GamePartyHud.Capture;
using GamePartyHud.Party;

namespace GamePartyHud.Config;

public sealed record AppConfig(
    HpCalibration? HpCalibration,
    HpRegion? NicknameRegion,
    string Nickname,
    Role Role,
    HudPosition HudPosition,
    bool HudLocked,
    string? LastPartyId,
    int PollIntervalMs,
    string RelayUrl)
{
    /// <summary>
    /// Default relay endpoint. Replace with your deployed
    /// <c>wss://...workers.dev</c> URL from <c>relay/</c> before building the
    /// shipped executable (or override via <c>config.json</c> at runtime).
    /// </summary>
    public const string DefaultRelayUrl = "wss://gph-relay.example.workers.dev";

    public static AppConfig Defaults { get; } = new(
        HpCalibration: null,
        NicknameRegion: null,
        Nickname: "Player",
        Role: Role.Utility,
        HudPosition: new HudPosition(100, 100, 0),
        HudLocked: true,
        LastPartyId: null,
        PollIntervalMs: 3000,
        RelayUrl: DefaultRelayUrl);
}

public sealed record HudPosition(double X, double Y, int Monitor);
```

- [ ] **Step 2: Update `App.xaml.cs` — replace the old network construction**

In `src/GamePartyHud/App.xaml.cs`, replace the block in `JoinOrCreateAsync` that looks like:

```csharp
        // 20 random bytes rendered as 40-char lower-case hex. On the tracker wire
        // BitTorrentSignaling re-encodes this as 20 raw Latin-1 bytes (WebTorrent
        // peer_id convention); internally everywhere else we carry the hex form.
        var selfPeerBytes = new byte[20];
        RandomNumberGenerator.Fill(selfPeerBytes);
        var selfPeer = Convert.ToHexString(selfPeerBytes).ToLowerInvariant();
        var signaling = new BitTorrentSignaling();
        var turn = _config.CustomTurnUrl is { Length: > 0 } url
            ? new PeerNetwork.TurnCreds(url, _config.CustomTurnUsername, _config.CustomTurnCredential)
            : null;
        if (turn is not null) Log.Info($"Using custom TURN URL: {turn.Url}");

        var net = new PeerNetwork(selfPeer, signaling, turn);
        net.OnPeerConnected    += id => { Log.Info($"Peer connected: {id}"); PartyStateChanged?.Invoke(); };
        net.OnPeerDisconnected += id => { Log.Info($"Peer disconnected: {id}"); PartyStateChanged?.Invoke(); };

        try
        {
            await signaling.JoinAsync(partyId, selfPeer, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error($"Signaling join failed for party '{partyId}'.", ex);
            MessageBox.Show(
                "Could not connect to party — your network may be blocking P2P connections. " +
                "See README.md / docs/requirements.md for workarounds " +
                "(UPnP / open NAT, gaming VPN, or a custom TURN URL in the config file).",
                "Game Party HUD", MessageBoxButton.OK, MessageBoxImage.Warning);
            await net.DisposeAsync();
            return;
        }
```

with:

```csharp
        // 20 random bytes rendered as 40-char lower-case hex. The relay uses
        // this string opaquely as the peer's identity on the wire; the rest of
        // the app uses it as a stable id throughout the party's lifetime.
        var selfPeerBytes = new byte[20];
        RandomNumberGenerator.Fill(selfPeerBytes);
        var selfPeer = Convert.ToHexString(selfPeerBytes).ToLowerInvariant();

        var relayUri = new Uri($"{_config.RelayUrl.TrimEnd('/')}/party/{Uri.EscapeDataString(partyId)}");
        var net = new RelayClient(selfPeer, relayUri);
        net.OnPeerConnected    += id => { Log.Info($"Peer connected: {id}"); PartyStateChanged?.Invoke(); };
        net.OnPeerDisconnected += id => { Log.Info($"Peer disconnected: {id}"); PartyStateChanged?.Invoke(); };
        // PartyOrchestrator's ctor subscribes to OnMessage below — don't double-subscribe here.

        try
        {
            await net.JoinAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error($"Relay join failed for party '{partyId}'.", ex);
            MessageBox.Show(
                $"Could not connect to party '{partyId}' — relay at {_config.RelayUrl} is unreachable. "
                + "Check your internet connection; if the problem persists, ask the person who built "
                + "this copy of Game Party HUD whether the relay URL in config.json is still correct.",
                "Game Party HUD", MessageBoxButton.OK, MessageBoxImage.Warning);
            await net.DisposeAsync();
            return;
        }
```

- [ ] **Step 3: Build to verify it compiles**

```bash
dotnet build 2>&1 | tail -10
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

If there are errors complaining about missing `CustomTurnUrl` etc. elsewhere, grep for them and delete those references:

```bash
grep -rn "CustomTurn" src/ tests/ | head
```

Any remaining references: delete the entire lines.

- [ ] **Step 4: Handle legacy `config.json` files on user machines**

Open `src/GamePartyHud/Config/ConfigStore.cs`. Locate the JSON deserialization path. If it doesn't already handle missing fields via a default-to-`AppConfig.Defaults`-fallback pattern, edit the deserialization so that a `config.json` written by a pre-rewrite build (missing `RelayUrl`, containing now-unknown `CustomTurnUrl` fields) produces a valid `AppConfig` with `RelayUrl = AppConfig.DefaultRelayUrl` and unknown fields ignored. System.Text.Json ignores unknown properties by default; the only risk is a thrown exception on `RelayUrl` being absent when the record's positional constructor requires it.

The safest shape is:

```csharp
// In ConfigStore.Load (or equivalent), after the base deserialize:
return raw with
{
    RelayUrl = string.IsNullOrWhiteSpace(raw.RelayUrl) ? AppConfig.DefaultRelayUrl : raw.RelayUrl,
};
```

If your existing `ConfigStore.Load` doesn't let you do this directly (e.g. it uses `JsonSerializer.Deserialize<AppConfig>` straight), wrap the call in a try/catch and fall back to `AppConfig.Defaults` on any JsonException, then `Save` a fresh config so the next run is clean.

Test this manually by copying your current `%AppData%\GamePartyHud\config.json` aside, running the app, and verifying it starts successfully.

- [ ] **Step 5: Run tests**

```bash
dotnet test --nologo
```

Expected: all tests pass. Some legacy tests (TwoPeerDiscoveryTests, SdpCandidateSummaryTests, BitTorrentSignalingWireFormatTests) still pass because PeerNetwork and BitTorrentSignaling are still in the tree — they're deleted in Task 18.

- [ ] **Step 6: Commit**

```bash
git add src/GamePartyHud/App.xaml.cs src/GamePartyHud/Config/AppConfig.cs src/GamePartyHud/Party/PartyOrchestrator.cs
git commit -m "feat(network): wire RelayClient into the composition root

PartyOrchestrator now takes RelayClient instead of PeerNetwork. AppConfig
drops the three CustomTurn* fields, gains a single RelayUrl defaulted to
the placeholder that the maintainer overwrites after the first wrangler
deploy. App.xaml.cs constructs RelayClient with wss://<host>/party/<id>."
```

---

## Phase 6 — Cleanup

### Task 18: Delete old Network files and tests

**Files:**
- Delete: `src/GamePartyHud/Network/BitTorrentSignaling.cs`
- Delete: `src/GamePartyHud/Network/ISignalingProvider.cs`
- Delete: `src/GamePartyHud/Network/PeerNetwork.cs`
- Delete: `tests/GamePartyHud.Tests/Network/BitTorrentSignalingWireFormatTests.cs`
- Delete: `tests/GamePartyHud.Tests/Network/LoopbackSignaling.cs`
- Delete: `tests/GamePartyHud.Tests/Network/SdpCandidateSummaryTests.cs`
- Delete: `tests/GamePartyHud.Tests/Network/TwoPeerDiscoveryTests.cs`

- [ ] **Step 1: Delete the files**

```bash
git rm src/GamePartyHud/Network/BitTorrentSignaling.cs \
       src/GamePartyHud/Network/ISignalingProvider.cs \
       src/GamePartyHud/Network/PeerNetwork.cs \
       tests/GamePartyHud.Tests/Network/BitTorrentSignalingWireFormatTests.cs \
       tests/GamePartyHud.Tests/Network/LoopbackSignaling.cs \
       tests/GamePartyHud.Tests/Network/SdpCandidateSummaryTests.cs \
       tests/GamePartyHud.Tests/Network/TwoPeerDiscoveryTests.cs
```

- [ ] **Step 2: Build — expect compile errors from any lingering references**

```bash
dotnet build 2>&1 | tail -20
```

Expected: either `Build succeeded` (ideal) or compile errors naming `PeerNetwork` / `BitTorrentSignaling` / `ISignalingProvider` / `RTCIceServer`. If any, fix:

```bash
grep -rnE "PeerNetwork|BitTorrentSignaling|ISignalingProvider|PreGeneratedOffer" src/ tests/
```

Any hits: delete those lines / files (they're dead code left over from the old stack).

- [ ] **Step 3: Run tests**

```bash
dotnet test --nologo
```

Expected: all remaining tests pass.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "refactor: remove WebRTC mesh + BitTorrent tracker signaling

Replaced by RelayClient against the Cloudflare WebSocket relay in
relay/. Deletes ~1200 LOC of client networking code and 6 test files."
```

### Task 19: Drop SIPSorcery NuGet dependency

**Files:**
- Modify: `src/GamePartyHud/GamePartyHud.csproj`

- [ ] **Step 1: Remove the package reference**

Open `src/GamePartyHud/GamePartyHud.csproj`. Delete the line:

```xml
    <PackageReference Include="SIPSorcery" Version="8.0.9" />
```

- [ ] **Step 2: Build**

```bash
dotnet restore
dotnet build
```

Expected: `Build succeeded. 0 Warning(s). 0 Error(s).`

If there's a lingering `using SIPSorcery.Net;` or similar anywhere, grep and remove:

```bash
grep -rn "SIPSorcery" src/ tests/
```

- [ ] **Step 3: Tests**

```bash
dotnet test --nologo
```

Expected: all tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/GamePartyHud/GamePartyHud.csproj
git commit -m "chore: drop SIPSorcery — no longer needed without WebRTC"
```

---

## Phase 7 — Docs

### Task 20: Update requirements, CLAUDE.md, README

**Files:**
- Modify: `docs/requirements.md`
- Modify: `CLAUDE.md`
- Modify: `README.md` (if present — if not, create a stub)

- [ ] **Step 1: Update `docs/requirements.md`**

In `docs/requirements.md`, replace the single bullet that reads:

```markdown
5. There is **no centralized server** holding party data. Players' apps communicate directly with each other.
```

with:

```markdown
5. There is **no centralized server storing party data**. A stateless WebSocket relay (free-tier Cloudflare Worker) routes messages between players; nothing about the party is persisted server-side. The relay is stateless between reconnects — closing the last member's connection evicts the in-memory party.
```

- [ ] **Step 2: Update `CLAUDE.md` constraint #2**

In `CLAUDE.md`, replace:

```markdown
2. **Zero hosting cost.** Do not introduce dependencies on paid services or servers. Signaling uses public BitTorrent trackers and PeerJS public cloud. If you think we need a hosted backend, stop and raise the design question first.
```

with:

```markdown
2. **Free-tier hosting only.** The message relay is a Cloudflare Worker in `relay/` deployed to a free Cloudflare account; no paid services. If you think we need a paid tier or a long-term background service, stop and raise the design question first.
```

- [ ] **Step 3: Create or update `README.md`** (top level)

If `README.md` does not exist, create it with:

File: `README.md`
````markdown
# Game Party HUD

Windows-only WPF tray app that shows a party-style HUD of teammates' HP bars
by reading pixels from each teammate's own screen and exchanging them via a
stateless WebSocket relay.

See [requirements](docs/requirements.md) and the
[design spec](docs/superpowers/specs/2026-04-16-game-party-hud-design.md).

## Build

```bash
dotnet build
dotnet test
```

Release publish:

```bash
dotnet publish src/GamePartyHud -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Relay

Party messages are routed through a Cloudflare Worker in `relay/`. To deploy
your own:

```bash
cd relay
npm install
npx wrangler login
npx wrangler deploy
```

Copy the deployed URL (e.g. `https://gph-relay.you.workers.dev`) and update
`AppConfig.DefaultRelayUrl` in `src/GamePartyHud/Config/AppConfig.cs` (or the
`RelayUrl` field in the per-user `config.json`), replacing `https://` with
`wss://`.

Costs: well within the Cloudflare free tier for hobbyist usage. See
[relay/README.md](relay/README.md).
````

If `README.md` exists already, add the **Relay** section above.

- [ ] **Step 4: Deprecate the obsolete sections of the old design spec and plan**

The original design spec and implementation plan describe the WebRTC mesh in detail. Don't rewrite them — append a prominent deprecation banner to each so future readers know to follow the newer docs.

Prepend to `docs/superpowers/specs/2026-04-16-game-party-hud-design.md` (immediately after the front-matter / first heading):

```markdown
> **⚠ Architecture superseded.** The WebRTC-mesh + BitTorrent-tracker signaling described in this spec was replaced on 2026-04-22 by a WebSocket relay. See [the reliability review](2026-04-22-reliability-scalability-review.md) for reasoning and [the rewrite plan](../plans/2026-04-22-websocket-relay-rewrite.md) for the new architecture. Sections below that refer to PeerNetwork, SIPSorcery, ISignalingProvider, or the tracker protocol no longer reflect the code.
```

Prepend the same banner (adjusted paths) to `docs/superpowers/plans/2026-04-16-game-party-hud-plan.md`:

```markdown
> **⚠ Superseded for networking.** Tasks and type references in this plan for the `Network/` folder — PeerNetwork, BitTorrentSignaling, SIPSorcery, TurnCreds — are no longer current. See [the rewrite plan](2026-04-22-websocket-relay-rewrite.md). Non-network sections (capture, party, HUD, config, tray) are still accurate.
```

- [ ] **Step 5: Commit**

```bash
git add docs/requirements.md CLAUDE.md README.md docs/superpowers/specs/2026-04-16-game-party-hud-design.md docs/superpowers/plans/2026-04-16-game-party-hud-plan.md
git commit -m "docs: reflect switch from P2P to stateless relay"
```

---

## Phase 8 — Manual smoke test

### Task 21: Two-peer smoke test across NATs

This task is manual — it's the acceptance gate before merging the branch.

- [ ] **Step 1: Deploy the relay (if not done in Task 9)**

```bash
cd relay
npx wrangler deploy
```

Record the URL. Confirm `AppConfig.DefaultRelayUrl` points to it.

- [ ] **Step 2: Build two release copies**

```bash
dotnet publish src/GamePartyHud -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/win-x64
```

Ship `publish/win-x64/GamePartyHud.exe` to two users on different networks.

- [ ] **Step 3: Observed behaviour checklist**

Each user should see, in their `%AppData%\GamePartyHud\app.log`:

- `RelayClient: connecting to wss://...`
- `BitTorrentSignaling:` **does NOT appear** (confirms old code is gone)
- `PeerNetwork[...]:` **does NOT appear**
- `RelayClient: welcome received with N existing member(s).`
- After the other peer joins: `Peer connected: <other peer id>`
- Periodic `PartyOrchestrator: ← state from <peer>…` every ~3 s

Each user should see, in the HUD:

- A card for themselves AND a card for the other peer.
- The other peer's HP bar animates when theirs in-game changes.

- [ ] **Step 4: Chaos check — kill one peer mid-party**

- User A closes the app.
- User B should see within ~60 s: HUD card for A greys out, then disappears.
- B's log: `Peer disconnected: <A's peer id>` and `PartyOrchestrator:` continues broadcasting self-state without errors.
- User A restarts the app and rejoins. B sees A reappear.

- [ ] **Step 5: 20-peer stress test** (optional if 2-peer passes)

If you have access to a test pool: 20 peers all join the same party. Confirm
every peer sees all 19 others. Log should show 19 `Peer connected` entries
within the first couple of seconds after join.

- [ ] **Step 6: Open PR to main**

```bash
git push -u origin feat/websocket-relay
gh pr create --base main --head feat/websocket-relay --title "Replace WebRTC mesh with WebSocket relay (Option D)" --body "Implements docs/superpowers/plans/2026-04-22-websocket-relay-rewrite.md. See docs/superpowers/specs/2026-04-22-reliability-scalability-review.md for context."
```

- [ ] **Step 7: After merge — remove the worktree**

```bash
cd ..
git worktree remove game-party-hud-relay
```

---

## Type reference

All public types introduced by this plan, in alphabetical order, with exact signatures. Use this as the cross-check for task consistency.

### C# — `GamePartyHud.Network`

```csharp
public sealed class RelayClient : IAsyncDisposable
{
    public RelayClient(string selfPeerId, Uri relayWsUri);
    public bool IsJoined { get; }
    public string SelfPeerId { get; }
    public event Action<string>? OnPeerConnected;         // peerId
    public event Action<string>? OnPeerDisconnected;      // peerId
    public event Action<string, string>? OnMessage;      // (fromPeerId, payload)
    public Task JoinAsync(CancellationToken ct);
    public Task BroadcastAsync(string json);
    public ValueTask DisposeAsync();
}

public static class RelayProtocol
{
    public abstract record ServerMessage;
    public sealed record Welcome(string PeerId, IReadOnlyList<string> Members) : ServerMessage;
    public sealed record PeerJoined(string PeerId)                             : ServerMessage;
    public sealed record PeerLeft(string PeerId)                               : ServerMessage;
    public sealed record Message(string FromPeerId, string Payload)            : ServerMessage;
    public sealed record ErrorMessage(string Reason)                           : ServerMessage;

    public static string EncodeJoin(string peerId);
    public static string EncodeBroadcast(string payload);
    public static ServerMessage? DecodeServerMessage(string json);
}
```

### TypeScript — `relay/src/protocol.ts`

```typescript
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
  | "party-full" | "invalid-join" | "duplicate-peer"
  | "rate-limit" | "message-too-large" | "protocol-error";

export function decodeClientMessage(raw: string): ClientMessage | null;
export function encodeServerMessage(msg: ServerMessage): string;
```

### TypeScript — `relay/src/room.ts`

```typescript
export class PartyRoom implements DurableObject {
  constructor(state: DurableObjectState, env: unknown);
  async fetch(request: Request): Promise<Response>;
}
```

---

## Verification summary

After Task 21 the repository should:

- Have **zero** references to `SIPSorcery`, `PeerNetwork`, `BitTorrentSignaling`, `ISignalingProvider`, `RTCIce*`, `TurnCreds`, `PreGeneratedOffer` in the source tree.
- `dotnet build` reports 0 warnings, 0 errors.
- `dotnet test --nologo` reports all tests pass (exact count depends on removals, but includes every test in `tests/GamePartyHud.Tests/Network/RelayProtocolTests.cs` and `RelayClientTests.cs`).
- `relay/npm test` reports all Vitest suites pass.
- Two manually-tested peers on different networks can join the same party, see each other, and exchange HP updates for at least 5 minutes without disconnect.
