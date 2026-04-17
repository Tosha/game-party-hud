# Changelog

All notable changes to this project are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.0] — 2026-04-17

First public release.

### Added

- **Windows tray app** (`GamePartyHud.exe`) targeting Windows 10 1903+ and Windows 11. Self-contained single-file binary, no installer, no runtime dependencies.
- **Calibration wizard** — three-step flow: drag a single box around your character name + HP bar, pick a role, confirm the OCR'd nickname. The app auto-detects where the HP bar sits inside the selection via `HpBarDetector` (first row-run of saturated pixels) and splits the region accordingly.
- **Peer-to-peer party sync** — WebRTC data channels via [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery). Signaling is tiered and zero-cost: public BitTorrent WSS trackers as primary (`tracker.openwebtorrent.com`, `tracker.btorrent.xyz`, `tracker.webtorrent.io`), [PeerJS public cloud](https://peerjs.com) as a fallback.
- **6-character shareable party IDs** from an unambiguous alphabet (drops 0/O/1/I). No accounts, no servers, no logins.
- **Always-on-top HUD overlay** — transparent WPF window with per-pixel click-through via `WM_NCHITTEST` (`HTTRANSPARENT`), so when locked the HUD is purely visual and clicks pass straight to the game.
- **HUD interactions (unlocked mode):** drag the whole HUD to move it, drag a member card onto another to swap their positions. Position persists between runs.
- **Tray menu:** Calibrate character, Change nickname, Change role, Create party, Join party, Copy party ID, Quit.
- **Right-click context menu** on a member card: Kick from party (leader-only; broadcast as a `KickMessage` that every peer enforces locally).
- **Disconnect handling:** a peer's card greys out after 6 s of silence, is removed after 60 s. Reconnection within that window restores the card seamlessly.
- **Deterministic leader election** — earliest `JoinedAtUnix`, tie-broken by lexicographic `PeerId`. No leader handoff messages; every peer independently recomputes.
- **Three-second polling with configurable interval** (1–10 s in `config.json`).
- **Settings persisted** at `%AppData%\GamePartyHud\config.json`: HP region + full-HP colour, nickname region, nickname, role, HUD position, last-joined party ID, poll interval, and optional custom TURN URL.
- **Optional custom TURN URL** in the config file (`CustomTurnUrl` / `CustomTurnUsername` / `CustomTurnCredential`) as an escape hatch for users behind symmetric NAT.
- **Lepo WPF-UI dark theme** on dialogs (calibration wizard, rename, role picker, join party) with Mica backdrop. HUD has a dark-grey gradient chrome with a vertical red HP-bar gradient.
- **Six fixed roles** with inline glyphs: Tank (◆), Healer (✚), Support (⚙), Melee DPS (⚔), Ranged DPS (➚), Utility (★).
- **Anti-cheat safety** — the app only reads screen pixels and draws its own overlay window, same as OBS, Discord Overlay, or Nvidia ShadowPlay. No game memory reads, no DLL injection, no DirectX/Vulkan hooking, no file or registry access beyond `%AppData%\GamePartyHud\`.
- **GitHub Actions CI/CD** — every PR runs build + 53 unit/integration tests; pushing a `v*.*.*` tag produces a self-contained `win-x64` single-file `.exe`, zips it, and publishes a GitHub Release.
- **Documentation:** end-user `README.md` with configuration walkthrough and anti-cheat compatibility statement; technical design spec at `docs/superpowers/specs/`; implementation plan at `docs/superpowers/plans/`; `SECURITY.md` for private vulnerability reporting; `docs/github-setup-checklist.md` for the one-time repo hardening steps.

### Known limitations

- **Windows only** (10 1903+ or 11). No macOS/Linux port planned.
- **Borderless-windowed games only.** Exclusive-fullscreen DirectX games can defeat the GDI-based screen capture (black rectangle) and hide the overlay. Switch the game to borderless windowed mode.
- **Horizontal HP bars only.** No vertical or radial bar support.
- **Symmetric-NAT users cannot connect** without configuring a custom TURN URL. ~10–15% of home internet setups are affected. Workarounds documented in README (UPnP / open NAT, gaming VPN, self-hosted coturn).
- **Games with heavily animated or textured HP bars** may need periodic re-calibration.
- **Anti-cheat safety is technical, not legal.** Always check your specific game's Terms of Service; some publishers prohibit any third-party overlay regardless of how it works.

[Unreleased]: https://github.com/Tosha/game-party-hud/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/Tosha/game-party-hud/releases/tag/v0.1.0
