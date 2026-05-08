# Self-hosted relay support — design

**Status:** Approved (brainstorming complete)
**Date:** 2026-04-28
**Author:** Anton Zemskov
**Supersedes:** Phase 9 of [`2026-04-22-websocket-relay-rewrite.md`](../plans/2026-04-22-websocket-relay-rewrite.md), which became obsolete when the relay was relocated to its own repo.

---

## TL;DR

Make the existing relay's wire protocol available as a self-hosted Node.js service that power users can run on their own hardware, while keeping the Cloudflare-Workers deploy as the default. The C# client gains a small UI for pointing at a self-hosted instance and supplying an optional auth token.

The work spans two repos:

- **`game-party-hud-server`** (the relay, recently relocated) — extract a transport-agnostic `RoomCore`, add a Node.js entry point that reuses it, add `RELAY_TOKEN` auth as a thin layer in front of `RoomCore`, ship a `SELFHOST.md` operator runbook.
- **`game-party-hud`** (this repo, the client) — add `CustomRelayUrl` and `CustomRelayToken` to `AppConfig`, fix `ConfigStore` to honour the override (instead of stripping it), append `?token=` when set, add a settings dialog accessible from the main window.

Same wire protocol on both adapters — no protocol fork, no client-side branching beyond URL/token. The Cloudflare Worker keeps no-auth (its URL is unguessable and not user-visible); the Node adapter recommends setting `RELAY_TOKEN` whenever it's exposed publicly.

---

## Non-goals

- Docker images.
- Windows tray runner for the relay.
- mDNS / LAN auto-discovery.
- Multi-tenant relays (one Node process = one operator's friend group).
- Per-message encryption beyond TLS.
- Account / invite systems.
- Automatic migration of users between cloud and self-hosted (the user manually edits the URL).

---

## Audience

Server side: **power users** — comfortable with Node 20, npm, environment variables, and Cloudflare Tunnel or a reverse proxy. The runbook is an operator's reference, not a wizard.

Client side: anyone who installed the `.exe`. The settings dialog is small and discoverable but doesn't try to walk users through standing up a server — it assumes they were given a URL by the operator.

---

## 1. Server architecture

Split today's `room.ts` into two layers, then bolt on a Node entry point that reuses the platform-agnostic layer.

### 1.1 `src/room-core.ts` (new) — pure protocol/state machine

Defines a transport-agnostic `Connection` interface and a `RoomCore` class. No knowledge of Cloudflare or `ws`.

```typescript
export interface Connection {
  send(text: string): void;
  close(code?: number, reason?: string): void;
}

export class RoomCore {
  onMessage(conn: Connection, raw: string): void;
  onDisconnect(conn: Connection): void;
}
```

`RoomCore` holds the roster (`Map<Connection, peerId>` + `Map<peerId, Member>`), applies the leaky-bucket rate limits (per-peer + per-party — both transferred verbatim from today's `room.ts`), enforces the 25-peer cap and 4 KiB frame size, and emits the existing `welcome` / `peer-joined` / `peer-left` / `message` / `error` frames. Fully unit-testable with fake `Connection`s.

### 1.2 `src/room.ts` (refactored) — Cloudflare adapter

Becomes a thin wrapper. The Durable Object still does the `WebSocketPair` accept and the hibernation `serializeAttachment` / `deserializeAttachment` for peer-id rebuild after a DO restart. Message handling and disconnect handling delegate to a `RoomCore` instance held on the DO. Existing `test/room.test.ts` keeps passing — the regression check that the refactor didn't change behaviour.

### 1.3 `src/node-server.ts` (new) — Node entry point

`http.createServer` + `ws.WebSocketServer` listening on a configurable port (default 8787). One `RoomCore` per party id, kept in a `Map<string, RoomCore>`, evicted when its last connection closes (no hibernation — Node processes don't suspend).

Routes:

- `GET /health` → `200 OK` with body `{"version": "<package.version>", "auth": "none"|"required"}`.
- `GET /party/<id>` with `Upgrade: websocket` → upgrade and dispatch to `roomFor(id).onMessage(conn, raw)` per inbound frame; call `roomFor(id).onDisconnect(conn)` on close.
- Anything else → `404 Not Found` (`/party/...` without `Upgrade` returns `426 Upgrade Required`).

Reads `RELAY_TOKEN`, `RELAY_PORT` (default `8787`), `RELAY_HOST` (default `0.0.0.0`) from `process.env` at startup. ≈120 LOC.

### 1.4 `package.json` changes

- Add runtime dependency: `ws` (`^8.x`).
- Add dev dependency: `tsx` (for `start:local`) and `@types/ws`.
- Add scripts:
  - `"start:local": "tsx src/node-server.ts"` — run from source.
  - `"build:node": "tsc -p tsconfig.node.json"` — emit `dist/node-server.js` for users who prefer a single deployable artefact.
- Existing scripts (`test`, `dev`, `deploy`, `typecheck`) untouched.

### 1.5 `tsconfig.node.json` (new)

A second tsconfig that targets Node 20 instead of the Cloudflare Workers runtime — `module: "esnext"`, `moduleResolution: "bundler"`, `target: "es2022"`, no `@cloudflare/workers-types` lib. The Worker tsconfig stays unchanged.

---

## 2. Auth model

The token lives entirely outside the wire protocol — a thin gate on the WebSocket *upgrade* request. No new frame types, no protocol versioning concerns, no impact on the Cloudflare deploy.

### 2.1 Server side (`node-server.ts`)

On startup, read `process.env.RELAY_TOKEN`:

- **Empty / unset** → no auth. Every upgrade is accepted (matches today's Cloudflare behaviour). `/health` reports `{"auth": "none"}`.
- **Non-empty** → `EXPECTED_TOKEN`. On every upgrade request, extract the candidate token in this order:
  1. `Authorization: Bearer <token>` header
  2. `?token=<token>` query parameter

  Compare with `crypto.timingSafeEqual` (after Buffer-padding to equal length) to guard against timing oracles. Mismatch or missing → respond `401 Unauthorized` and abort the upgrade — no WebSocket is opened, no `RoomCore` is touched. `/health` reports `{"auth": "required"}`.

The Cloudflare Worker (`worker.ts`) is **not** modified — its URL is the secret, and adding a token there would force every existing user to update their binary.

### 2.2 Client side (`RelayClient.cs`)

When `CustomRelayToken` is non-empty, the client appends `?token=<URL-encoded-token>` to the WebSocket URL before calling `ConnectAsync`. We support **only** the query-param form on the client to keep things simple. The server accepts both forms so `wscat -H "Authorization: Bearer …"` works for users debugging.

### 2.3 Threat model & rationale

- TLS (provided by Cloudflare Tunnel or a reverse proxy) protects the token in transit. The runbook recommends `wss://` for any internet-exposed deploy.
- The token is never logged on the server (treat it like a password).
- Token rotation = restart the Node process with a new env value + tell the user(s) the new value out-of-band.
- We don't try to support multiple tokens or revocation lists. Power-user ergonomics, single operator, single shared secret.

### 2.4 Failure UX on the client

A `401` during connect surfaces in `RelayClient.JoinAsync` as a `WebSocketException`. The existing main-window error dialog says "Could not connect…" generically; we add a one-line check: if the response status was 401, show a more specific *"Auth token rejected by relay. Open Relay Settings to fix."* and route the user back to the settings dialog. The wrong-token-with-no-auth-required case isn't possible to detect cleanly (a server with auth disabled accepts everything), so we don't try.

---

## 3. Client config model

Two new persisted fields on `AppConfig`, plus a derived getter for the resolved URL.

### 3.1 Schema changes — [`AppConfig.cs`](../../../src/GamePartyHud/Config/AppConfig.cs)

```csharp
public sealed record AppConfig(
    // ...existing fields...
    string? CustomRelayUrl,        // null/empty = use DefaultRelayUrl
    string? CustomRelayToken)      // null/empty = no auth header sent
{
    public static string DefaultRelayUrl { get; } = ResolveDefaultRelayUrl();

    /// <summary>
    /// The URL the client should actually connect to. Custom override wins
    /// when non-empty; otherwise we use whatever the binary was built with.
    /// </summary>
    public string EffectiveRelayUrl =>
        string.IsNullOrWhiteSpace(CustomRelayUrl) ? DefaultRelayUrl : CustomRelayUrl;
}
```

The existing `RelayUrl` field is **dropped** from the record. Every consumer is migrated to `EffectiveRelayUrl`. The serialized form has `customRelayUrl` and `customRelayToken` instead of `relayUrl`.

### 3.2 `ConfigStore` changes — [`ConfigStore.cs`](../../../src/GamePartyHud/Config/ConfigStore.cs)

The current `Load` overrides `RelayUrl` with the build-time default ([lines 42-48](../../../src/GamePartyHud/Config/ConfigStore.cs#L42)). That whole block is removed. New behaviour:

- `Load` reads `customRelayUrl` and `customRelayToken` straight from JSON; missing fields stay `null`. Both are persisted on `Save` (no stripping).
- A migration shim handles old `config.json` files: if a top-level `relayUrl` field exists from a previous version, it's silently ignored (not promoted to `customRelayUrl` — it's almost certainly the stale build-time URL the strip behaviour was guarding against). New `Load` simply doesn't read that field.
- On startup, `Log.Info` prints `"Relay: <effective URL> (override: yes/no, auth: yes/no)"` so support can tell from a log file whether the user is on a self-hosted relay.

### 3.3 `RelayClient` change

[`App.xaml.cs:231`](../../../src/GamePartyHud/App.xaml.cs#L231) currently builds `relayUri` from `_config.RelayUrl`:

```csharp
var relayUri = new Uri($"{_config.RelayUrl.TrimEnd('/')}/party/{Uri.EscapeDataString(partyId)}");
```

The URI-construction logic moves into a small static helper — `RelayUriBuilder.Build(string relayUrl, string partyId, string? token)` — that lives in the `Network` folder. `App.xaml.cs` calls it with `_config.EffectiveRelayUrl`, the party id, and `_config.CustomRelayToken`. The helper appends `?token=<urlencoded>` when the token is non-empty. `RelayClient` itself stays transport-pure — it still takes a fully-built `Uri` in its constructor and the reconnect loop reuses it unchanged.

Putting the builder in a static helper (not inside `RelayClient`) lets the settings dialog's "Test connection" probe reuse the exact same URL-construction code without instantiating a `RelayClient`.

### 3.4 Migration semantics

Existing users have `RelayUrl: ""` saved on disk (today's `Save` writes empty). Their first launch with the new binary: `Load` ignores the field, both `CustomRelayUrl` and `CustomRelayToken` come up `null`, `EffectiveRelayUrl` returns the build-time default — exactly what they got before. No user-visible change unless they open the new settings dialog.

---

## 4. Client UI — settings dialog

### 4.1 Entry point

A small `⚙` icon button on the main window — placed below the existing Create/Join Party buttons (or in the title-bar area, depending on where `MainWindow.xaml` has space). Clicking it opens a modal `RelaySettingsWindow`, owned by `MainWindow` so it dims the parent. This matches the existing visual language — `RegionSelectorWindow` is already a separate modal window owned by the main window.

A new top-level folder `src/GamePartyHud/Settings/` houses `RelaySettingsWindow.xaml{,.cs}`. The folder is UI-only and depends on `Config` (read/write) and `Network` (the test-connection probe) — same dependency direction the rest of the UI uses.

### 4.2 Layout (top to bottom)

1. Heading: "Relay server"
2. **Custom relay URL** — `TextBox` with placeholder *"Leave blank to use the built-in relay"*. A right-aligned "Use default" button next to it clears the field.
3. **Auth token (optional)** — `PasswordBox` with placeholder *"Leave blank if your relay has no token"*. Below it, a small "Show" toggle that swaps between `PasswordBox` and a plain `TextBox` so users can verify what they pasted.
4. **Status row** — empty by default. After "Test connection" runs, populated with a coloured outcome (green for success, red for failure).
5. **Buttons** (right-aligned): "Test connection" / "Cancel" / "Save".

If the user is currently in a party, an `InfoBar` at the top of the dialog says *"You're in a party right now. Changes apply the next time you create or join."*. Existing connections aren't dropped on save — same behaviour as editing nickname today.

### 4.3 Test-connection flow

Constructs the URL from the *entered* values (not yet saved), generates a throwaway 40-char hex peer id, opens a `ClientWebSocket` to `<url>/party/__test__?token=…`, sends `{"type":"join","peerId":…}`, awaits any frame for up to 5 seconds, then closes. Outcomes:

| Server response | Status row text |
|---|---|
| `welcome` received | ✓ Connected. Server is running. |
| `error` frame with any reason | ✓ Connected. Server is running. *(server-level error is still proof the server is up)* |
| WebSocket close with HTTP 401 | ✗ Auth token rejected |
| TCP/DNS/timeout failure | ✗ Cannot reach `<url>`: `<short message>` |
| Anything else | ✗ Unexpected response: `<text>` *(capped at ≈80 chars)* |

The probe lives in a small `RelaySettingsProbe` class in the `Network` folder so it can be unit-tested against the existing `FakeRelayServer` (already present in `tests/GamePartyHud.Tests/Network/`). The dialog's code-behind just calls `await probe.TryConnectAsync(url, token, ct)` and translates the result.

### 4.4 Save behaviour

Writes both fields verbatim to `AppConfig` (whitespace trimmed; empty string stored as `null`). `App.UpdateConfig` already persists via `ConfigStore.Save` and pushes new config into the orchestrator — no special-casing needed beyond that. The "Use default" button is just a convenience that clears the URL field; it doesn't itself trigger save.

### 4.5 Validation

URL-format validation is `Uri.TryCreate(…, UriKind.Absolute, out var u) && (u.Scheme == "ws" || u.Scheme == "wss")`. If the URL field is non-empty and fails this check, "Save" is disabled and the status row shows *"URL must start with ws:// or wss://"*. An empty URL field is always valid (means "use the built-in default") and Save is enabled. The token field is never validated — any string is allowed, including obviously-wrong ones (the server is the only thing that can authoritatively reject).

### 4.6 Out of scope for the dialog

- No in-dialog wizard for setting up a Cloudflare Tunnel.
- No "browse" file picker — URLs are typed.
- No display of the built-in default URL (keeps the URL out of screenshots, matches the existing main-window practice of not showing it).

---

## 5. Operator runbook (`SELFHOST.md` in the server repo)

A single document at `C:\Users\tosha\IdeaProjects\game-party-hud-server\SELFHOST.md` aimed at a power user who has a Linux box, a Mac, or a Windows PC with Node 20 LTS installed. It complements (does not replace) the existing `README.md`, which stays focused on the Cloudflare deploy.

**Section outline:**

1. **Prerequisites** — Node 20 LTS, ~20 MB free disk, a port to listen on, optionally `cloudflared` if the operator wants a public stable URL. Explicit "you do **not** need a domain or a TLS cert if you use Cloudflare Tunnel."

2. **Install & run** — `git clone` the server repo, `npm ci`, `npm run start:local`. Expected stdout: `gph-relay (self-hosted) listening on ws://0.0.0.0:8787/party/<id>`. Curl `/health` to confirm liveness; the response includes `{"version": "...", "auth": "none|required"}`.

3. **Setting an auth token** — `RELAY_TOKEN=<long-random-string> npm run start:local`. Recommends `openssl rand -base64 32` to generate one. A short note that the token must match exactly what's pasted into the client's settings dialog.

4. **Exposing it publicly via Cloudflare Tunnel** — the canonical recipe. Install `cloudflared`, `cloudflared tunnel login`, `cloudflared tunnel create gph-relay`, `cloudflared tunnel route dns gph-relay relay.your-domain.com`, then a small `~/.cloudflared/config.yml` with `service: ws://localhost:8787` and a `tunnel run` invocation. Expected URL on the client: `wss://relay.your-domain.com`. (No domain? Cloudflare's `try.cloudflare.com` quick-tunnel works for testing; document that it gives an ephemeral URL.)

5. **LAN-only / port-forward path** — for users on a stable home IP who'd rather just open a port. Recommends `wss://` via Caddy or nginx if internet-facing; explicitly says `ws://` is fine on a LAN where everyone connects to `ws://192.168.x.y:8787`. Calls out that the C# client accepts both schemes.

6. **Running as a service** — short systemd unit (Linux) and a Windows Task Scheduler one-liner with `nssm` or the built-in scheduler. ≈15 lines each. Marked optional.

7. **Upgrade & rollback** — `git pull && npm ci && pm2 restart` (or systemd equivalent). Connected clients reconnect via existing backoff. No data to migrate — relay is stateless.

8. **Diagnostics** — what `/health` returns, what to grep for in stdout (`peer joined`, `party-full`, `rate-limit`), how to tell if it's Cloudflare Tunnel breaking versus the relay itself (curl the local port directly).

9. **Migrating between Cloudflare Worker and self-hosted** — three-step recipe: spin up the new relay, update the client's settings dialog, click Test Connection. No coordination needed; the client doesn't care which deploy answered.

The runbook contains no platform-specific shell-script files — those are inline snippets only. If demand emerges, an `etc/` folder with reusable systemd / Task-Scheduler templates can be added later.

---

## 6. Testing strategy

Three layers — pure unit tests for `RoomCore`, integration tests for the Node adapter, parity tests against the existing fixtures.

### 6.1 Server tests

| Test file | New / modified | What it covers |
|---|---|---|
| `test/room-core.test.ts` | new | Pure `RoomCore` unit tests with fake `Connection`s. ≈15 cases: join, welcome contents, peer-joined fan-out, broadcast filters self, peer-left on disconnect, per-peer rate limit, per-party rate limit, 4 KiB frame cap, 25-peer cap, duplicate peer-id, malformed JSON ignored. |
| `test/room.test.ts` | unchanged | Cloudflare DO regression. Must keep passing without modification — proves the refactor didn't drift behaviour. |
| `test/node-server.test.ts` | new | Spawn the Node entry point on a random port, drive it with real `ws` clients. ≈10 cases mirroring `room.test.ts` plus `/health` response shape, non-`/party` paths return 404, non-upgrade requests return 426, **auth: none/required + present/absent/wrong/Authorization-header form**. |
| `test/protocol.test.ts` | unchanged | Existing protocol round-trip tests stay as-is. |
| `test/fixtures.ts` | unchanged | Canonical wire strings, the source of truth for both sides. |

### 6.2 Client tests

| Test file | New / modified | What it covers |
|---|---|---|
| `RelayProtocolTests.cs` | unchanged | Encoder output stays byte-for-byte identical to TS fixtures. |
| `RelayClientTests.cs` | new test added | Token-on-URL: `FakeRelayServer` configured to require a specific token, assert connect succeeds with matching `?token=`, fails with a recognisable exception when wrong. |
| `ConfigStoreTests.cs` | new tests added (file already exists) | Migration: load an old-format `config.json` with `relayUrl: ""` → `CustomRelayUrl == null`, `EffectiveRelayUrl == DefaultRelayUrl`. Round-trip: write a config with `customRelayUrl` set, reload, assert preserved. |
| `RelaySettingsProbeTests.cs` | new | Probe behaviour against `FakeRelayServer`: success path, 401 path, unreachable path. |

### 6.3 Manual smoke (not automated)

1. Build & launch the C# client unchanged → confirm Cloudflare path still works.
2. Run the new Node server locally with no token → enter `ws://localhost:8787` in settings → Test Connection passes → join a party from two instances → HP updates flow.
3. Run with `RELAY_TOKEN=foo` → wrong token → Test Connection shows "Auth rejected"; right token → succeeds.
4. Cloudflare Tunnel a localhost server, point client at the `wss://...trycloudflare.com` URL → confirm it works end-to-end.

---

## 7. File summary

### Server repo (`game-party-hud-server`)

**New:**

- `src/room-core.ts`
- `src/node-server.ts`
- `tsconfig.node.json`
- `test/room-core.test.ts`
- `test/node-server.test.ts`
- `SELFHOST.md`

**Modified:**

- `src/room.ts` — refactor to delegate to `RoomCore`
- `package.json` — add `ws` runtime dep, `tsx` + `@types/ws` dev deps, `start:local` and `build:node` scripts
- `README.md` — add a one-line pointer to `SELFHOST.md`

**Unchanged:** `src/protocol.ts`, `src/worker.ts`, `wrangler.toml`, `test/fixtures.ts`, `test/protocol.test.ts`, `test/room.test.ts`, `vitest.config.ts`, `tsconfig.json`.

### Client repo (`game-party-hud`)

**New:**

- `src/GamePartyHud/Settings/RelaySettingsWindow.xaml`
- `src/GamePartyHud/Settings/RelaySettingsWindow.xaml.cs`
- `src/GamePartyHud/Network/RelaySettingsProbe.cs`
- `tests/GamePartyHud.Tests/Settings/RelaySettingsProbeTests.cs`

**Modified:**

- `src/GamePartyHud/Config/AppConfig.cs` — drop `RelayUrl`, add `CustomRelayUrl`, `CustomRelayToken`, `EffectiveRelayUrl` getter
- `src/GamePartyHud/Config/ConfigStore.cs` — drop the strip block, persist new fields, ignore old `relayUrl` key
- `src/GamePartyHud/Network/RelayClient.cs` — append `?token=` when set; surface 401 distinctly
- `src/GamePartyHud/App.xaml.cs` — use `EffectiveRelayUrl`; show specific error on 401
- `src/GamePartyHud/MainWindow.xaml{,.cs}` — add the `⚙` settings button
- `tests/GamePartyHud.Tests/Network/RelayClientTests.cs` — token-on-URL test
- `tests/GamePartyHud.Tests/Network/FakeRelayServer.cs` — accept an optional expected-token configuration
- (existing or new) `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs` — migration + round-trip tests

**Unchanged:** Everything in `Capture/`, `Hud/`, `Calibration/`, `Tray/`, `Party/`, `Diagnostics/`, plus `RelayProtocol.cs`, `RelayProtocolTests.cs`.

---

## 8. Decision log

For traceability — these were settled during brainstorming:

1. **Operator persona = power users only.** Drove the choice of bare-Node + runbook over a Windows tray runner or one-click installer.
2. **Client UI = small settings button on the main window.** Drove the new `Settings/` folder and `RelaySettingsWindow`.
3. **Stack = Node.js only.** No Docker / no precompiled exe. Cloudflare Tunnel is the recommended public-exposure path.
4. **Auth = optional pre-shared token via `RELAY_TOKEN` env.** Off by default. Cloudflare Worker is unchanged (URL is the secret).
5. **Token transport = query parameter on the client; query parameter or `Authorization: Bearer` on the server** (server accepts both for `wscat -H` debuggability; client uses query-param only).
6. **Settings dialog: no built-in default URL displayed.** Empty field with a placeholder + "Use default" button is enough.
7. **Code reuse: extract `RoomCore`.** Both adapters share all roster / broadcast / caps logic; the existing `room.test.ts` is the regression check.

---

## 9. Implementation plans

This spec is split into two implementation plans, one per repo:

- `docs/superpowers/plans/2026-04-28-self-hosted-relay-server-plan.md` — server-side work (`game-party-hud-server` repo).
- `docs/superpowers/plans/2026-04-28-self-hosted-relay-client-plan.md` — client-side work (`game-party-hud` repo).

The plans can be executed independently. The server plan is a hard prerequisite for end-to-end manual smoke; the client plan can be merged first with no behaviour change for users who don't open the new settings dialog.
