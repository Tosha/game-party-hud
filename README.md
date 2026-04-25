# Game Party HUD

[![CI](https://github.com/Tosha/game-party-hud/actions/workflows/ci.yml/badge.svg)](https://github.com/Tosha/game-party-hud/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/Tosha/game-party-hud?display_name=tag&sort=semver&label=release)](https://github.com/Tosha/game-party-hud/releases/latest)
[![License: MIT](https://img.shields.io/github/license/Tosha/game-party-hud?color=blue)](LICENSE)
[![Downloads](https://img.shields.io/github/downloads/Tosha/game-party-hud/total?color=brightgreen)](https://github.com/Tosha/game-party-hud/releases)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2B-0078D6?logo=windows&logoColor=white)](#)

A tiny, free Windows overlay that shows your party's HP bars on top of any game that doesn't have a built-in party UI — share a 6-character code, no accounts, no launchers to install. Party state flows through a tiny WebSocket relay (free-tier Cloudflare Worker) that doesn't store anything about you.

<p align="center">
  <a href="https://github.com/Tosha/game-party-hud/releases/latest">
    <img src="https://img.shields.io/badge/%E2%AC%87%EF%B8%8E%20Download%20latest%20release-238636?style=for-the-badge&logo=windows&logoColor=white&labelColor=1F6FEB" alt="Download latest release for Windows">
  </a>
  <br/>
  <sub>Single self-contained <code>.exe</code> for Windows 10 (1903+) / 11 &nbsp;·&nbsp; <a href="#getting-started">Setup guide ↓</a></sub>
</p>

- 📦 **Single 180 MB `.exe`** — self-contained, nothing else to install
- 🪪 **No accounts** — share a 6-character party code
- 💰 **Free forever** — routes through a stateless WebSocket relay on the Cloudflare Workers free tier (`relay/` in this repo); no accounts to pay for, nothing persisted server-side
- 🖥️ **Windows 10 (1903+) / Windows 11**
- 🛡️ **Anti-cheat safe** — see below

---

## 🛡️ Anti-cheat / EasyAntiCheat compatibility

**Game Party HUD is safe to run alongside any game that uses EasyAntiCheat, BattlEye, Vanguard, or similar anti-cheat systems.**

It works exactly the same way OBS, Discord Overlay, Steam Overlay, and Nvidia ShadowPlay do:

| What Game Party HUD does | What Game Party HUD *does not* do |
|---|---|
| Reads a small rectangle of **screen pixels** (like a screenshot) | ❌ Read the game's memory |
| Draws its own **transparent overlay window** on top of your screen | ❌ Inject code into the game process |
| Sends your own screen-derived HP percentage to party members over a **WebSocket** to a stateless relay | ❌ Hook DirectX, Vulkan, or any game rendering API |
| | ❌ Modify game files, registry, or network traffic |
| | ❌ Give you any in-game advantage (it's the same info you already see on your own screen, just shared with teammates) |

If your game allows OBS or Discord to run, it allows Game Party HUD to run. That said, **always check your specific game's Terms of Service** — some publishers prohibit *any* third-party overlay regardless of technical safety. Use at your own responsibility.

No permissions are requested, no UAC prompt, no driver installs. The binary is plain user-mode code.

---

## Getting started

### 1. Download

Grab the latest `GamePartyHud-X.Y.Z-win-x64.zip` from the [Releases page](https://github.com/Tosha/game-party-hud/releases). Unzip it anywhere — everything is in one `.exe`.

### 2. First launch

Double-click `GamePartyHud.exe`. It starts silent in the system tray (bottom-right corner next to the clock). Right-click the tray icon for the menu.

### 3. Calibrate once

You need to tell the app where on screen your HP bar is drawn, what colour it is when full, what your character's name is, and what role you play. Do this once per game/resolution combination.

1. Make sure you're **in-game with HP full**.
2. Right-click the tray icon → **Calibrate character…**
3. **Step 1 — HP bar.** Click **Pick HP bar region**. Your screen dims; drag a tight rectangle around **only the HP bar**. Do **not** include the character name, other bars (mana, stamina, shield…), or any frame around the bar.

   ```
    ┌────────────────────┐
    │  ████████░░░░       │   ← HP bar, fill visible
    └────────────────────┘   ← top and bottom edges hug the bar
   ```

   The tighter your box matches the coloured fill, the more accurate the reading. If the bar has numeric text like `246/246` overlaid on it, include the text — the app samples colour from the top and bottom of the bar where text doesn't reach.

4. **Step 2 — Nickname.** Type your character's name as you want it to appear on teammates' HUDs.
5. **Step 3 — Role.** Pick your role (Tank / Healer / Support / Melee DPS / Ranged DPS / Utility). Click **Save**.

The config is persisted at `%AppData%\GamePartyHud\config.json`, so you only do this once.

### Re-calibrate when things change

If the app reads your HP incorrectly (bar jumps around, or shows empty when it shouldn't), re-run **Calibrate character…** from the tray with a tighter selection around the coloured fill. If it still reads wrong, grab `%AppData%\GamePartyHud\app.log` and attach it to a bug report.

### 4. Play together

- **Create a party:** Tray → **Create party**. A 6-character code appears on the tray tooltip. **Copy party ID** copies it to your clipboard; share it with your friends (voice chat, Discord, wherever).
- **Join a party:** Tray → **Join party…** → paste the code.

Within a couple of seconds you should see each other's HP bars show up on the HUD. They update every 3 seconds.

### 5. Move the HUD where you want it

The HUD starts **locked** — it's purely visual, your mouse clicks pass straight through to the game. To reposition it:

1. Click the 🔒 lock button in the top-right of the HUD → it becomes 🔓.
2. Drag the HUD anywhere by clicking in the empty area. Drag a member's card onto another to swap their positions in the list.
3. Click 🔓 to re-lock when you're happy. Position is remembered across sessions.

### 6. Optional tweaks

The tray menu also has:
- **Change nickname…** — edit your displayed name without re-running calibration.
- **Change role…** — swap your role icon.
- **Copy party ID** — handy for re-sharing mid-session.
- **Quit** — closes cleanly; party members see you leave immediately.

---

## If you can't connect to the party

If you get **"Could not connect to party — relay at &lt;url&gt; is unreachable":**

1. **Check your internet connection.** The relay is a WebSocket over outbound `wss://` (TCP 443). Most networks allow this without any extra setup.
2. **Confirm the relay is up.** The repo's `RelayUrl` default is a placeholder; the person who built your copy of the app should have replaced it with their own deployed `wss://<their-worker>.<their-subdomain>.workers.dev`. If they haven't, the app can't connect anywhere. See **Relay** below.
3. **Override the URL locally.** Edit `%AppData%\GamePartyHud\config.json` and set `"RelayUrl": "wss://your-relay.example.com"` to point at a different deployment.

---

## Relay

Party messages are routed through a Cloudflare Worker in [`relay/`](relay/). To
deploy your own (one-time, by the repo maintainer):

```bash
cd relay
npm install
npx wrangler login
npx wrangler deploy --name <your-worker-name>
```

The `--name` flag overrides the placeholder in `wrangler.toml`; the live
worker name stays out of public source. Save your chosen name (something
with a random suffix is harder for bots to probe) in your password manager.

Copy the deployed URL (e.g. `https://<your-worker-name>.<you>.workers.dev`),
swap `https://` for `wss://`, and store it as the **`GPH_RELAY_URL`** repo
secret in GitHub Actions. The next tagged release will bake that URL into
the published `.exe` automatically — no source-code change needed.

Costs: well within the Cloudflare free tier for hobbyist usage. See
[`relay/README.md`](relay/README.md) for setup details and
[`docs/superpowers/specs/2026-04-22-reliability-scalability-review.md`](docs/superpowers/specs/2026-04-22-reliability-scalability-review.md)
for the design rationale.

---

## How it works

Each player runs an identical copy of the app. On a 3-second timer, it:

1. Grabs a small screenshot of the calibrated HP-bar region.
2. Measures the red-fill percentage using HSV color matching.
3. Sends `{ nickname, role, hp% }` over a single WebSocket to a Cloudflare Worker. The Worker routes the frame to a Durable Object pinned to the party id, which fans it out to every other connected member.

The relay holds **no party state between sessions** — when the last member disconnects, the Durable Object evicts itself and the party id becomes free again. The wire protocol is documented in [`relay/src/protocol.ts`](relay/src/protocol.ts) and pinned by parity tests on both sides.

```
   peer A                   peer B
     │                        │
     │  WebSocket (wss://)    │
     ▼                        ▼
  ┌──────────────────────────────┐
  │  Cloudflare Worker (gph-relay) │
  │  ┌─────────────────────────┐ │
  │  │ Durable Object: party A2K9XV │  ← one DO per party id, in-memory only
  │  │  roster: { A, B }       │ │
  │  └─────────────────────────┘ │
  └──────────────────────────────┘
```

Architecture docs:
- Current: [reliability review](docs/superpowers/specs/2026-04-22-reliability-scalability-review.md), [WebSocket relay plan](docs/superpowers/plans/2026-04-22-websocket-relay-rewrite.md)
- Original (P2P design, superseded): [game-party-hud-design.md](docs/superpowers/specs/2026-04-16-game-party-hud-design.md)
- Product requirements: [`docs/requirements.md`](docs/requirements.md)

---

## Tech stack

**Client — `src/GamePartyHud/`**

- .NET 8 / C# 12 / WPF (`net8.0-windows10.0.19041.0`)
- `System.Net.WebSockets.ClientWebSocket` (BCL — no third-party network deps)
- `System.Text.Json` for wire encoding and `config.json`
- `Windows.Graphics.Capture` + GDI BitBlt for screen pixels
- xUnit for tests

**Server — `relay/`**

- Cloudflare Workers + Durable Objects, free tier ($0 for hobbyist usage)
- TypeScript, strict mode
- WebSocket Hibernation API (Durable Objects doze between messages, cutting billed CPU time)
- SQLite-backed DOs (no persistent storage actually used; SQLite is required for free-plan DO classes)
- Vitest + `@cloudflare/vitest-pool-workers` (Miniflare) for tests
- Per-peer leaky-bucket rate limit (capacity 20, refill 10/s)

**CI / release**

- GitHub Actions (`.github/workflows/ci.yml` for builds + tests, `release.yml` for tagged releases)
- `release.yml` triggers on `v<semver>` tags and publishes a self-contained single-file `.exe`

---

## Performance

- **CPU:** under 1% on modern hardware. Measurable floor is the 3-second screen capture (~0.3 ms) + HP calculation (~0.2 ms).
- **RAM:** ~50–80 MB.
- **GPU:** effectively zero. The overlay only redraws when state actually changes.
- **Network:** ~0.5 KB/s per peer pair. State frames are ~120 bytes each; a 5-peer party generates ~85 k requests/day at typical session length, well inside the Cloudflare Workers free quota of 100 k/day. See [`relay/README.md`](relay/README.md#costs) for the scaling math.

---

## Limitations

- **Windows only** (10 1903+ or 11). No macOS/Linux port planned.
- **Borderless-windowed games only.** Exclusive-fullscreen games can hide the overlay — switch your game to borderless windowed mode.
- **Horizontal HP bars only.** No vertical or radial support.
- **No voice chat, no text chat, no stats.** This is a single-purpose utility.
- **Relay availability gates everything.** If the WebSocket relay (the URL baked into your `.exe` build) is offline, parties can't form. The relay is stateless and free to redeploy in seconds, but it's a dependency the original P2P design avoided.

---

## Building from source

### Client (the WPF app)

Requirements: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and Windows 10 1903+.

```bash
git clone https://github.com/Tosha/game-party-hud.git
cd game-party-hud
dotnet build GamePartyHud.sln -c Release
dotnet test GamePartyHud.sln
```

To produce the same single-file `.exe` that ships in releases:

```bash
dotnet publish src/GamePartyHud/GamePartyHud.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish/
```

### Relay (the Cloudflare Worker)

Requirements: [Node.js](https://nodejs.org/) ≥ 20 and a free [Cloudflare account](https://dash.cloudflare.com/sign-up).

```bash
cd relay
npm install
npm test                                       # vitest + Miniflare, runs the full server suite locally
npm run dev                                    # wrangler dev — local server on http://localhost:8787
npx wrangler deploy --name <your-worker-name>  # publish to your Cloudflare account
```

After your first deploy, store the `wss://<your-worker-name>.<your-subdomain>.workers.dev` URL as the `GPH_RELAY_URL` repo secret in GitHub Actions. The next tagged release will bake it into the published `.exe` automatically. Full walkthrough — including Cloudflare account creation — is in [`relay/README.md`](relay/README.md).

---

## Contributing

Small PRs welcome. Please:

1. Fork and create a feature branch from `main`.
2. Make your changes.
3. Ensure both test suites are green:
   - `dotnet build -c Release && dotnet test` for the client.
   - `cd relay && npm test` for the server.
4. Open a PR against `main`. Describe **what** changed and **why** in the body.

See [`CLAUDE.md`](CLAUDE.md) for the project's conventions (Conventional Commits, testing philosophy, architecture rules).

---

## License

See [LICENSE](LICENSE).

## Security

If you find a security issue (credential leak, RCE, etc.), please see [SECURITY.md](SECURITY.md) for how to report it privately.
