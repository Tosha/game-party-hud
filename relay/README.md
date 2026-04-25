# gph-relay — WebSocket relay for Game Party HUD

Stateless Cloudflare Worker + Durable Object that routes party-state broadcasts
between GamePartyHud clients. One Durable Object per party id; each broadcast
fans out to the other members. No storage, no party data persisted.

This README is the step-by-step guide for the **maintainer** (the person who
builds the Game Party HUD `.exe`) to stand up their own free Cloudflare Worker
that all their players' apps will connect to. Players themselves don't need to
do any of this — they just run the `.exe` you ship them.

---

## Quick map of what you're about to do

1. Create a free Cloudflare account (≈ 3 min, no credit card).
2. Authenticate `wrangler` (the Cloudflare CLI) on your dev machine (≈ 2 min, browser-based).
3. Deploy this folder as a Worker (≈ 1 min, one command).
4. Paste the resulting URL into `AppConfig.cs` (or each user's `config.json`) so
   the client knows where to connect.
5. Smoke-test it from the command line.

The whole thing is well under 10 minutes the first time. Subsequent re-deploys
are a single `npx wrangler deploy` from this folder.

---

## Step 1 — Create a Cloudflare account

1. Open <https://dash.cloudflare.com/sign-up> in a browser.
2. Enter an email + password. **No credit card is required for the Workers Free
   plan**, which is what we're using.
3. Cloudflare emails you a verification link. Click it.
4. The first time you sign in, Cloudflare may ask you to "Add a website".
   **You can skip this step** — Workers don't need a domain. If there's no skip
   button, click the Workers/Pages tile in the left nav instead and proceed.

You don't need to set up DNS, configure a domain, or install anything in the
dashboard. The deploy step in this guide creates the Worker programmatically.

---

## Step 2 — Install Node.js and Wrangler (one time, your dev machine)

You only need Node.js. Wrangler is installed transitively via `npm install` in
step 3, so you don't have to install it globally.

### Windows

1. Install Node.js LTS from <https://nodejs.org/> (the "LTS" download) — or run
   `winget install OpenJS.NodeJS.LTS` from PowerShell.
2. Open a fresh terminal and confirm:
   ```powershell
   node --version
   npm --version
   ```
   Both should print version numbers (Node ≥ 20).

### macOS / Linux

```bash
# macOS via Homebrew:
brew install node

# Debian/Ubuntu:
sudo apt install nodejs npm
```

Then `node --version` and `npm --version` should both print versions ≥ 20.

---

## Step 3 — Install this project's dependencies

From the **`relay/` folder of this repo** (the one this README is in):

```bash
npm install
```

This downloads Wrangler, the test runner, and friends into `relay/node_modules/`.
~120 MB on disk, takes 30–90 s depending on your connection. There may be
deprecation warnings about transitive deps — ignore them; they're benign.

If `npm install` fails with `EACCES` on Linux, you've installed Node as root
somewhere; fix the permissions or use `nvm` to install a user-local Node. Don't
`sudo npm install` here.

---

## Step 4 — Authenticate Wrangler with your Cloudflare account

```bash
npx wrangler login
```

What happens:

1. Wrangler prints a long URL and tries to open it in your default browser.
2. If your browser doesn't open automatically, copy-paste the URL.
3. The browser asks you to sign in to Cloudflare (use the account from Step 1).
4. Cloudflare asks "Allow Wrangler to access your account?" — click **Allow**.
5. The browser tab says "Successfully logged in"; you can close it.
6. Back in your terminal, Wrangler prints something like
   `Successfully logged in.`

Wrangler stores the auth token under `~/.wrangler/` (or `%USERPROFILE%\.wrangler\` on
Windows). You only do this once per machine; subsequent `wrangler` commands
are silent.

---

## Step 5 — Deploy

Still inside the `relay/` folder. **Pick a worker name and pass it via `--name`** so the real production name stays out of source. Pick something with a random suffix to make casual probing harder, e.g. `my-relay-7f3a`:

```bash
npx wrangler deploy --name my-relay-7f3a
```

The `name = "..."` field in `wrangler.toml` is a deliberate placeholder; the `--name` flag overrides it. Save the real name in your password manager — you'll need it for every future deploy.

What it prints (annotated):

```
 ⛅️ wrangler 3.x.x
-------------------
Total Upload: ~5 KiB / gzip: ~2 KiB
Worker Startup Time: 5 ms
Your worker has access to the following bindings:
- Durable Objects:
  - PARTY_ROOM: PartyRoom
Uploaded my-relay-7f3a (1.2 sec)
Published my-relay-7f3a (5.4 sec)
  https://my-relay-7f3a.<your-subdomain>.workers.dev    ← copy this line
Current Deployment ID: ...
```

**Copy the `https://gph-relay.<your-subdomain>.workers.dev` URL.** Your
"subdomain" is auto-generated from your Cloudflare account email the first
time you deploy any Worker; it's stable across re-deploys.

If wrangler refuses to deploy with a message about "Workers paid plan required
for Durable Objects" or similar, see the [Troubleshooting](#troubleshooting)
section below — recent Cloudflare changes made SQLite-backed Durable Objects
free, but if your account predates that change you may need a plan tweak in
the dashboard.

---

## Step 6 — Smoke-test the deployed worker

Install `wscat` (a tiny WebSocket REPL):

```bash
npm install -g wscat
```

Connect:

```bash
wscat -c wss://gph-relay.<your-subdomain>.workers.dev/party/SMOKE
```

**Note the `wss://` scheme** (not `https://`) and the `/party/SMOKE` path. Once
connected, type:

```json
{"type":"join","peerId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}
```

You should see the server reply within a second:

```json
{"type":"welcome","peerId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","members":[]}
```

That's it — the relay is live and routing. Press `Ctrl+C` in `wscat` to disconnect.

---

## Step 7 — Tell Game Party HUD where the relay lives

Two ways:

### A. Bake it into the build (recommended for the maintainer)

Edit `src/GamePartyHud/Config/AppConfig.cs` and replace the placeholder:

```csharp
public const string DefaultRelayUrl = "wss://gph-relay.example.workers.dev";
```

with your real URL — note the `wss://` scheme:

```csharp
public const string DefaultRelayUrl = "wss://gph-relay.<your-subdomain>.workers.dev";
```

Rebuild the app, ship the resulting `.exe` to your players. Their config.json
will pick up the new default automatically.

### B. Per-machine override (no rebuild needed)

Each user can edit `%AppData%\GamePartyHud\config.json` (Windows) and add:

```json
{
  "relayUrl": "wss://gph-relay.<your-subdomain>.workers.dev"
}
```

The app reads this on startup and uses it instead of the compiled-in default.
Useful for testing a new deploy without re-shipping a binary.

---

## Costs

The Game Party HUD traffic shape is well inside the Workers Free tier:

- **Workers requests:** 100,000/day free. Each party message is one request.
  A 5-person party broadcasting state every 3 seconds for 8 hours = 5 × 1200 ×
  8 = 48k requests/day, comfortably under the cap. 20-person parties are still
  fine for hobbyist use.
- **Egress:** 1 GB/day free. State messages are ~100 bytes each — a 20-person
  party would need to run continuously for years to exhaust this.
- **Durable Objects:** Cloudflare made SQLite-backed Durable Objects free on
  the Workers Free plan in late 2024. Our `PartyRoom` uses SQLite (configured
  in `wrangler.toml` as `new_sqlite_classes`), so this is free.

If you ever do hit the daily request cap, the cheapest fix is the **Workers
Paid** plan at **US$5/month flat**, which gives 10M requests/month included.
For a hobbyist relay that's a 100× headroom.

Cloudflare's current pricing: <https://developers.cloudflare.com/workers/platform/pricing/>

---

## Re-deploying after code changes

Pass the same `--name` you used at first deploy:

```bash
cd relay
npx wrangler deploy --name <your-worker-name>
```

Same URL, new code. Connected clients keep their existing connections; new
connections hit the new code. Roll back with `npx wrangler rollback --name <your-worker-name>` if something
breaks.

---

## Inspecting your relay in the Cloudflare dashboard

After deploying, visit <https://dash.cloudflare.com/?to=/:account/workers> →
your `gph-relay` Worker. The dashboard shows:

- **Logs** — real-time `console.log` output from the Worker (useful for
  diagnosing weird client behaviour). The relay doesn't log per-message by
  default (party traffic is private), but you'll see deploy events.
- **Metrics** — request volume, error rate, p50/p99 latency. Should hover near
  zero for a hobbyist deployment.
- **Durable Objects** → **PartyRoom** — list of active party DOs and their
  state. Empty parties evict themselves automatically.

There's also a **Triggers** tab where you can attach a custom domain (e.g.
`relay.your-game-clan.com`) instead of the auto-generated `*.workers.dev`
URL. Optional; the auto URL works fine forever.

---

## Local dev

For iterating on the Worker code without re-deploying:

```bash
npm run dev    # spins up wrangler dev on http://localhost:8787
npm test       # runs the vitest + miniflare test suite
```

`npm run dev` simulates the Cloudflare runtime locally, including Durable
Objects. Connect with `wscat -c ws://localhost:8787/party/LOCAL` to drive it
manually.

---

## Troubleshooting

**`wrangler login` opens the browser but the page never loads.**
Some corporate VPNs block the localhost callback. Disconnect the VPN, retry.
Worst case, run `npx wrangler login --browser=false` and copy-paste the URL by hand.

**`wrangler deploy` says "You need to register a workers.dev subdomain".**
Visit your Cloudflare dashboard → Workers & Pages → in the right sidebar,
"workers.dev". Pick a subdomain (anything you like; appears as
`https://<worker>.your-subdomain.workers.dev`). Then re-run `wrangler deploy`.

**`wrangler deploy` complains about Durable Objects requiring a paid plan.**
This was true before late 2024 but no longer is — SQLite-backed Durable
Objects are free. If you still see the message, double-check `wrangler.toml`
has `new_sqlite_classes = ["PartyRoom"]` (not `new_classes`); Cloudflare
distinguishes the two for billing.

**`wrangler deploy` says "compatibility_flags must contain nodejs_compat".**
Verify `wrangler.toml` line 4 reads:
`compatibility_flags = ["nodejs_compat"]`. The repo ships with this set, so
unless you edited it, this shouldn't happen.

**Smoke test (`wscat`) connects but never receives a welcome.**
The peerId in your `join` frame might be empty or non-string. The decoder
silently drops malformed frames. Use `"peerId":"aaaaaaaa..."` (40-char hex)
in your test.

**`wscat` connection fails with "unexpected server response: 426".**
You hit the Worker without an `Upgrade: websocket` header. `wscat` does
this for you; if you're testing with `curl`, use
`curl -H "Upgrade: websocket" -H "Connection: Upgrade" ...` or just use
`wscat`.

**HUD says "Could not connect to party — relay at wss://... is unreachable".**
The user's `RelayUrl` doesn't match a deployed Worker. Verify they're using
the URL from your `wrangler deploy` output (with the `wss://` scheme).

---

## Wire protocol

See `src/protocol.ts` and the fixtures in `test/fixtures.ts`. Those strings are
also the source of truth for the C# client's JSON parsing — `RelayProtocol.cs`
on the C# side and the parity tests on both sides keep encoder output
byte-for-byte identical. If you change the protocol, change all three.
