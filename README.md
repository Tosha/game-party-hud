# Game Party HUD

A tiny, free Windows overlay that shows your party's HP bars on top of any game that doesn't have a built-in party UI — fully peer-to-peer, no accounts, no servers to pay for, no launchers to install.

- 📦 **Single 180 MB `.exe`** — self-contained, nothing else to install
- 🪪 **No accounts** — share a 6-character party code
- 💰 **Free forever** — built on free public infrastructure (BitTorrent trackers + PeerJS) so nobody pays for hosting
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
| Sends your own screen-derived HP percentage to party members over **WebRTC** | ❌ Hook DirectX, Vulkan, or any game rendering API |
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

You need to tell the app where on screen your character's name and HP bar are drawn. Do this once per game/resolution combination.

1. Make sure you're **in-game with HP full**.
2. Right-click the tray icon → **Calibrate character…**
3. In the wizard, click **Pick character region**. Your screen dims; drag a single rectangle that covers:
   - Your character name (top)
   - Your HP bar (directly below the name)
   - Not other bars (mana, shield, etc.) — just name + HP bar.

   ```
    ┌─────────────────────┐
    │     YourName        │  ← name (top of selection)
    │   ████████░░░░      │  ← HP bar
    └─────────────────────┘  ← bottom of selection, just below the bar
   ```

4. The app auto-detects which part is the name and which is the HP bar. It shows you the colour it detected and OCR's the nickname. **Double-check** the nickname — if OCR misread it, you'll fix it in step 4 of the wizard.
5. Pick your **Role** (Tank / Healer / Support / Melee DPS / Ranged DPS / Utility).
6. Confirm the nickname. **Save**.

The config is persisted at `%AppData%\GamePartyHud\config.json`, so you only do this once.

### 4. Play together

- **Create a party:** Tray → **Create party**. A 6-character code appears on the tray tooltip. **Copy party ID** copies it to your clipboard; share it with your friends (voice chat, Discord, wherever).
- **Join a party:** Tray → **Join party…** → paste the code.

Within about 30 seconds you should see each other's HP bars show up on the HUD. They update every 3 seconds.

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

About 10–15% of home internet setups (symmetric NAT, carrier-grade NAT on some ISPs, very restrictive routers) can't form direct P2P connections without a paid relay server. We don't run one — it would break the "zero hosting cost" design.

If you get **"Could not connect to party — your network may be blocking P2P connections":**

1. **Enable UPnP** on your router (sometimes labelled "Open NAT"). Most consumer routers have this in the web admin under *Advanced → UPnP*. This resolves the majority of cases.
2. **Use a gaming VPN** like [ZeroTier](https://www.zerotier.com) or [Radmin VPN](https://www.radmin-vpn.com). Each player installs it and joins the same virtual network, which sidesteps NAT entirely.
3. **Use your own TURN server** (advanced). Edit `%AppData%\GamePartyHud\config.json` and add:
   ```json
   "CustomTurnUrl": "turn:your-server.example.com:3478",
   "CustomTurnUsername": "user",
   "CustomTurnCredential": "pass"
   ```
   You can self-host [coturn](https://github.com/coturn/coturn) on a cheap VPS.

---

## How it works

Each player runs an identical copy of the app. On a 3-second timer, it:

1. Grabs a small screenshot of the calibrated HP-bar region.
2. Measures the red-fill percentage using HSV color matching.
3. Broadcasts `{ nickname, role, hp% }` to every other party member via a WebRTC data channel.

No centralized server holds any party data. Players find each other through free, pre-existing signaling infrastructure (public BitTorrent trackers as the primary rendezvous, PeerJS public cloud as a fallback) — those services only help the initial handshake and never see the HP data itself.

Detailed design: [`docs/superpowers/specs/`](docs/superpowers/specs/)
Product requirements: [`docs/requirements.md`](docs/requirements.md)

---

## Performance

- **CPU:** under 1% on modern hardware. Measurable floor is the 3-second screen capture (~0.3 ms) + HP calculation (~0.2 ms).
- **RAM:** ~50–80 MB.
- **GPU:** effectively zero. The overlay only redraws when state actually changes.
- **Network:** ~0.5 KB/s per peer pair (HP updates are tiny).

---

## Limitations (v0.1.0)

- **Windows only** (10 1903+ or 11). No macOS/Linux port planned.
- **Borderless-windowed games only.** Exclusive-fullscreen games can hide the overlay — switch your game to borderless windowed mode.
- **Horizontal HP bars only.** No vertical or radial support.
- **No voice chat, no text chat, no stats.** This is a single-purpose utility.

---

## Building from source

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

---

## Contributing

Small PRs welcome. Please:

1. Fork and create a feature branch from `main`.
2. Make your changes.
3. Ensure `dotnet build -c Release` is clean and `dotnet test` is green.
4. Open a PR against `main`. Describe **what** changed and **why** in the body.

See [`CLAUDE.md`](CLAUDE.md) for the project's conventions (Conventional Commits, testing philosophy, architecture rules).

---

## License

See [LICENSE](LICENSE).

## Security

If you find a security issue (credential leak, RCE, etc.), please see [SECURITY.md](SECURITY.md) for how to report it privately.
