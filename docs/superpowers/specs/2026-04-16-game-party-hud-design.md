# Game Party HUD — Design Spec

**Date:** 2026-04-16
**Status:** Approved — ready for implementation planning

---

## 1. Overview

A lightweight Windows desktop utility that provides a party HUD (heads-up display) for games that lack a built-in party UI with HP bars. Each player runs the app, calibrates a region of the screen where their own HP bar is rendered, picks a role, and joins a shared party by ID. The app shows an always-on-top overlay listing all connected party members with their nicknames, role icons, and live HP bars that update every 3 seconds.

The app is peer-to-peer with zero server hosting cost. There is no backend, no account system, and no persistent identity.

### Inspiration and reference

- HP bar example: generic in-game HP/resource bar from the player's target game.
- Party HUD reference: Albion Online's party UI (vertical stack of member cards with role icons, HP bars, and a lock button in the top-right).

### Target user

A gamer who plays games without a native party UI and wants to see teammates' HP at a glance. Willing to install a small utility, do a one-time region calibration, and share a short party ID over voice/Discord.

---

## 2. Goals & Non-Goals

### Goals

- Show live party HP bars as an always-on-top overlay while gaming.
- Work over the internet for friends in different locations, with zero hosting cost.
- Add negligible CPU/GPU overhead (target <1% of each).
- Be game-agnostic: work with any game that has a horizontal HP bar on screen.
- Be anti-cheat friendly: read only the screen pixels; no process injection, no game memory access, no DirectX hooking.
- Simple setup: install, calibrate once, create/join a party by ID.

### Non-Goals (explicit scope cuts)

- Voice or text chat.
- Stats, history, logging of party events.
- Numeric HP readout; bar-only display.
- Secondary resource bars (mana, stamina, shield).
- Vertical or radial HP bars.
- Mobile or web companion apps.
- Cross-platform (macOS, Linux).
- Accounts, cloud sync, leaderboards.
- Game-specific integrations (memory reading, game APIs).
- Auto-updater (v1 ships with manual updates only).
- Localization (English only in v1).
- Theming / custom skins.
- TURN relay for users behind symmetric NATs (documented limitation).

---

## 3. Architecture

Single C#/.NET 8 WPF application running as a Windows tray process. Internally modular so each subsystem is independently testable.

```
┌──────────────────────────────────────────────────────────┐
│                    GamePartyHud.exe                      │
│                                                          │
│  ┌────────────┐   ┌─────────────┐   ┌────────────────┐   │
│  │ Tray UI    │   │ HUD Overlay │   │ Config Store   │   │
│  │ (NotifyIcon│   │ (WPF window │   │ (JSON in       │   │
│  │  + menu)   │   │  transparent│   │  %AppData%)    │   │
│  │            │   │  always-on- │   │                │   │
│  │            │   │  top)       │   │                │   │
│  └─────┬──────┘   └──────┬──────┘   └──────┬─────────┘   │
│        │                 │                 │             │
│        └─────────┬───────┴─────────┬───────┘             │
│                  │                 │                     │
│        ┌─────────▼─────────┐  ┌────▼──────────────┐      │
│        │ Party State       │  │ Screen Poller      │      │
│        │ (roster, leader,  │  │ (3s tick, bitmap   │      │
│        │  self HP/role)    │  │  capture + HP calc)│      │
│        └─────────┬─────────┘  └────────────────────┘      │
│                  │                                        │
│        ┌─────────▼─────────┐                              │
│        │ Peer Network      │                              │
│        │ (WebRTC via       │                              │
│        │  SIPSorcery)      │                              │
│        └─────────┬─────────┘                              │
│                  │                                        │
└──────────────────┼────────────────────────────────────────┘
                   │
          ┌────────▼────────┐
          │ Signaling layer │
          │ (public BT      │
          │  trackers or    │
          │  PeerJS)        │
          └─────────────────┘
```

### Component responsibilities

| Component | Responsibility |
|---|---|
| **Tray UI** | Entry point. `NotifyIcon` with menu: Calibrate character, Create party, Join party, Copy party ID, Settings, Quit. |
| **HUD Overlay** | Borderless transparent always-on-top WPF window. Renders member cards. Handles lock toggle, block drag, member swap drag. |
| **Config Store** | Reads/writes JSON to `%AppData%\GamePartyHud\config.json`. Persists calibration, nickname, role, HUD position, last-joined party ID, poll interval. |
| **Screen Poller** | Timer-driven (3s default). Captures calibrated HP region, computes HP percent, emits event. |
| **Party State** | Single source of truth for the roster. In-memory dictionary keyed by peer ID. Emits change events. |
| **Peer Network** | WebRTC peer-connection manager. Connect/disconnect, broadcast self-state, receive peer states. |
| **Signaling layer** | Interface-based abstraction over BT trackers / PeerJS / custom URL. Used only during connection setup. |

### Platform & stack

- **OS:** Windows 10 1903+ (required for `Windows.Graphics.Capture`).
- **Runtime:** .NET 8, self-contained single-file publish. Target binary size ~30MB.
- **UI framework:** WPF with per-monitor DPI awareness (`PerMonitorV2`).
- **Primary NuGet dependencies:**
  - `SIPSorcery` — pure-C# WebRTC.
  - `System.Text.Json` — built-in, used for config and wire messages.
- **WinRT APIs** (`Windows.Graphics.Capture`, `Windows.Media.Ocr`) are accessed via the Windows SDK projection enabled by targeting `net8.0-windows10.0.19041.0` (or newer) in the project file — no additional NuGet package required.

---

## 4. Networking & P2P

### Topology

**Full mesh** up to 20 peers. Every peer connects to every other peer. HP updates are tiny (~80 bytes per peer per 3s), so aggregate bandwidth peaks at ~5 KB/s per peer in a full party. No relay, no host bottleneck. If any one peer leaves, others stay connected.

### Transport

WebRTC data channels via **SIPSorcery** (pure-C# WebRTC implementation, no native dependencies).

### Signaling — tiered fallback, zero-hosting

We never run a signaling server ourselves. Priority order:

1. **Public BitTorrent trackers (primary)** — Register the party ID as a torrent infohash on well-known public trackers (e.g. `tracker.openwebtorrent.com`, `tracker.btorrent.xyz`). Peers announcing the same infohash discover each other and exchange WebRTC SDP offers/answers via the tracker's WebSocket. Same approach as Trystero / WebTorrent.
2. **PeerJS public cloud (fallback)** — If trackers fail or are blocked, fall back to `0.peerjs.com` free signaling.
3. **Custom signaling URL (power user, in settings)** — User can paste their own signaling endpoint. Not exposed in main UI.

Signaling is abstracted behind an `ISignalingProvider` interface for testability and future swapping.

### Party ID

- Format: **6 uppercase alphanumeric characters** (A–Z, 2–9; skip confusable `0/O/1/I`). Example: `X7K2P9`.
- Generated locally with a CSPRNG by whoever creates the party.
- Used as-is as the infohash on trackers. Collision risk negligible at realistic user counts.
- Tray menu → "Create party" generates and copies to clipboard. "Join party" opens a dialog with a text input.

### Party security

ID-only. Whoever has the ID can join. If it leaks, create a new party. Matches the Discord invite-link trust model.

### Connection lifecycle

1. **Create party:** Generate ID → register on signaling → wait for peers.
2. **Join party:** Enter ID → register on signaling → receive offers from existing peers → establish WebRTC data channels with each.
3. **Steady state:** Each peer broadcasts self-state every 3s on a shared `"party"` data channel. All peers maintain a local roster keyed by peer ID.
4. **Leave/disconnect:** Graceful leave sends a `"bye"` message. Ungraceful disconnect detected via SIPSorcery ICE failure or missed updates.

### Wire protocol (JSON, compact)

```json
// Broadcast every 3s by each peer
{ "type": "state", "peerId": "<uuid>", "nick": "Yiawahuye", "role": "Tank", "hp": 0.72, "t": 1713200000 }

// Leader-only — roster changes
{ "type": "kick", "target": "<peerId>" }

// Graceful leave
{ "type": "bye", "peerId": "<uuid>" }
```

Peer IDs are ephemeral UUIDs generated per session. No persistent identity.

### Leader election

**Deterministic, no voting.** Every peer computes: `leader = peer in roster with smallest JoinedAtUnix, ties broken by lexicographic PeerId`. All peers see the same roster, so all peers agree on the leader without network traffic.

If the leader disconnects, the next-earliest joiner becomes leader automatically.

Leader's only privilege: **kick**. The leader sends `{ type: "kick", target: peerId }` to all peers; receiving peers ignore all future messages from that peer ID. The kicked peer also receives the message, shows "You were kicked", and disconnects.

Limitation: kicking is advisory/local-enforced, not cryptographically enforced. A kicked peer could rejoin with a fresh `PeerId`. Accepted trade-off; matches Discord invite-link trust model.

### NAT traversal

WebRTC uses ICE + STUN (Google's public STUN `stun.l.google.com:19302`). This handles ~85% of home internet setups.

Symmetric NATs (~10-15% of users) cannot hole-punch without a TURN relay. **No TURN is bundled** — running one costs bandwidth money and breaks the zero-cost guarantee.

**Escape hatch for affected users:** the config file supports an optional `customTurnUrl` field (plus optional `customTurnUsername` / `customTurnCredential`). When set, the URL is added to the ICE configuration and used as the last-resort relay. Not exposed in the main UI — it's a power-user setting for people who self-host `coturn`, have TURN access from another source, or want to share a TURN endpoint within a group. Default: empty.

Users without a custom TURN URL who are behind a symmetric NAT will see "Could not connect to party — your network may be blocking P2P connections."

---

## 5. Screen Capture & HP Reading

### Capture API

**Windows.Graphics.Capture (WinRT).** Modern Windows 10+ capture API. Uses DWM compositor directly — no GDI overhead, works with hardware-accelerated games and borderless-windowed mode. Captures by monitor + rectangle (absolute screen coordinates). Per-capture cost: ~0.3ms for a ~300×20 region.

### HP bar reading algorithm

**Pixel-based, HSV color matching.**

**On calibration** (user has full HP):
1. Capture the HP bar region as a bitmap.
2. Compute median color of the middle horizontal strip → this is the "full HP color".
3. Store color in HSV space with a tolerance window (default: H ±15°, S ±0.25, V ±0.25). HSV is chosen over RGB for robustness to brightness variation.
4. Determine fill direction by scanning from both ends of the bar. Default: **left-to-right fill**.
5. Save calibration.

**On each poll:**
1. Capture the same region.
2. Scan the middle horizontal strip pixel-by-pixel in the fill direction.
3. Find the transition point where pixels stop matching the "full HP color" (within tolerance) for ≥3 consecutive pixels.
4. `hpPercent = transitionX / regionWidth`, clamped to [0.0, 1.0].
5. Apply exponential moving average (α=0.5) to smooth anti-aliasing and pulse flicker.

**Edge cases handled:**
- Pulsing "low HP" animation → HSV tolerance absorbs the pulse.
- Temporarily obscured bar (UI panel in front) → if no match anywhere, keep last known value for up to 3 polls (9s), then mark `hp` as `unknown`.
- HP = 0 → transition at x=0, reads 0%.

**Edge cases explicitly not handled:**
- Vertical/radial bars.
- Games with complex gradient/texture fills — user re-calibrates if colors drift.
- Buff/debuff icons overlapping the bar — user re-calibrates to a different bar instance.

### Nickname reading (OCR)

- **Windows.Media.Ocr** (built into Windows 10+, free).
- Runs **only once during calibration**. Nickname is never re-OCR'd on polls.
- User selects a separate region for the nickname text. OCR result pre-fills a text box that the user can edit before saving.
- If OCR fails, text box is empty; user types the nickname manually.

### Calibration flow (wizard in tray menu)

Tray → "Calibrate character" opens a 4-step wizard:

1. **HP bar region.** Screen dims with instruction: *"Make sure your HP is full. Drag a box around your HP bar."* Drag rectangle. Preview shows captured pixels magnified. Buttons: Redo / Next.
2. **Nickname region.** *"Drag a box around your character name."* Same drag flow. OCR runs and shows detected text.
3. **Role.** Dropdown with 6 fixed roles (Tank / Healer / Support / Melee DPS / Ranged DPS / Utility) + icons. Pick one.
4. **Confirm nickname.** Text field pre-filled from OCR. User can edit. Save.

### Calibration data schema

```json
{
  "hpRegion": { "monitor": 0, "x": 1520, "y": 40, "w": 300, "h": 20 },
  "hpFullColorHSV": [0, 0.85, 0.6],
  "hpColorTolerance": { "h": 15, "s": 0.25, "v": 0.25 },
  "hpFillDirection": "LTR",
  "nicknameRegion": { "monitor": 0, "x": 1500, "y": 10, "w": 340, "h": 25 },
  "nickname": "Yiawahuye",
  "role": "Tank"
}
```

### Poll interval

Default 3 seconds. Configurable in settings (range 1–10s). Same timer drives both screen capture and network broadcast.

---

## 6. HUD Overlay & Interaction

### Window configuration

Single borderless transparent WPF window, always on top, no taskbar entry.

WPF XAML:
```xml
WindowStyle="None"
AllowsTransparency="True"
Background="Transparent"
Topmost="True"
ShowInTaskbar="False"
ResizeMode="NoResize"
```

Win32 extended styles applied via P/Invoke on window load:
- `WS_EX_LAYERED` — required for compositing with games.
- `WS_EX_TOOLWINDOW` — hides from Alt-Tab.
- `WS_EX_NOACTIVATE` — keeps game window focused; clicking the HUD never steals keyboard focus.

Note: we deliberately do **not** use `WS_EX_TRANSPARENT`, because it's an all-or-nothing flag that would make the lock button unclickable too. Per-pixel click-through is achieved via `WM_NCHITTEST` handling (see Lock/unlock section below).

Per-monitor DPI awareness via `app.manifest`.

### Visual layout

Vertical stack of identical member cards:

```
┌──────────────────────────────────────────────┐
│                                        [🔒]  │ ← lock button (top-right)
│  ┌─┐  Yiawahuye                              │
│  │⚔│  ████████████████░░░░░░                 │ ← role icon + nickname + HP bar
│  └─┘                                          │
│  ┌─┐  OtherPlayer                             │
│  │❤│  ██████████████████████                 │
│  └─┘                                          │
│  ┌─┐  TankDude                                │
│  │🛡│  ███████░░░░░░░░░░░░░░░ [grey/stale]   │
│  └─┘                                          │
└──────────────────────────────────────────────┘
```

Card anatomy:
- **Role icon** (24×24, left): distinct per role, ships with built-in icon set.
- **Nickname** (top): truncates with ellipsis at ~16 characters.
- **HP bar** (bottom): ~180px wide, 10px tall. **Fill color is always red.** Thin dark border.
- **State overlays:** stale = 40% opacity + "⚠" badge; kicked = hidden.

Styling: dark semi-transparent background (`#CC000000`), rounded corners, subtle 1px border. Deliberately minimal — utility, not a skin.

### Lock / unlock

Single small button in the top-right corner (padlock icon).

Click-through behavior is implemented by handling the `WM_NCHITTEST` window message in a custom WndProc:

- **Locked (default):** WndProc returns `HTTRANSPARENT` (-1) for every cursor position **except** the lock button's rectangle, where it returns `HTCLIENT`. Windows passes non-lock-button clicks through to the window behind (the game), while the lock button stays clickable.
- **Unlocked:** WndProc returns `HTCLIENT` everywhere. The entire HUD captures clicks for block drag, member swap, and right-click menus.

This per-pixel hit-testing approach keeps a single window for the whole HUD (simpler state, lighter than two-window solutions) while still allowing the lock button to remain clickable in locked mode.

Visual feedback: closed padlock when locked, open padlock when unlocked. A 1px accent border around the HUD appears when unlocked to make edit mode obvious.

No global keyboard hotkeys. Lock toggle is click-only.

### Dragging rules (unlocked mode only)

Two distinct drag behaviors:

1. **Block drag** — Mouse down on a non-card area (background around cards, lock button strip) → drags the whole HUD. New position saved to config on release.
2. **Member swap** — Mouse down on a card → a ghost of that card follows the cursor. Cards cannot detach from the block. As cursor passes over another card, cards visually reorder with an insertion indicator between cards. On release, dragged member takes the drop position; others shift to fill the gap.

Edge cases:
- Drag outside the block → card snaps back, no change.
- Drag onto self → no-op.
- Dragging own card is allowed (reorders own slot in the list).
- **Member order is local-only** — each peer maintains their own visual ordering. Not synced across peers.

### Right-click menus (unlocked)

On a card:
- **Kick** (only if I'm leader and target isn't me).
- **Mute updates** (hides card locally without affecting others).
- **Copy nickname.**

On empty block area:
- **Lock HUD.**
- **Re-anchor position to center.**
- **Close party.**

### Resize

No manual resize. Card width is fixed; overall height grows with member count.

---

## 7. State Sync, Disconnect Handling, Leader

### Self-state model

```csharp
record MemberState(
    string PeerId,       // ephemeral UUID per session
    string Nickname,     // from calibration, user-editable
    Role Role,           // enum: Tank/Healer/Support/MeleeDps/RangedDps/Utility
    float? HpPercent,    // 0.0–1.0, or null if unknown
    long JoinedAtUnix,   // for deterministic leader election
    long LastUpdateUnix  // local timestamp of last received update
);
```

Local roster is `Dictionary<string, MemberState>` keyed by `PeerId`. Always includes self.

### Broadcast loop

Every 3 seconds (same timer as screen polling, with ±250ms jitter per peer to avoid thundering-herd sync):
1. Screen poller updates `self.HpPercent`.
2. `PartyState` serializes self-state to JSON.
3. Sends over the shared `"party"` data channel to all peers.
4. Received state messages update the local roster, raising an event the HUD subscribes to.

### Disconnect detection

Background tick every 1s evaluates `LastUpdateUnix` per member:

| Time since last update | State | Visual |
|---|---|---|
| 0–6s | **Live** | Normal opacity |
| 6–60s | **Stale** | 40% opacity, ⚠ badge, "reconnecting…" after 6s |
| >60s | **Removed** | Card disappears from roster |

Plus: SIPSorcery's `RTCPeerConnection.connectionState` events accelerate detection:
- `disconnected` → mark stale immediately (don't wait 6s).
- `failed` → initiate reconnection attempt.

### Reconnection

- A dropped peer attempts to re-signal (re-register on the BT tracker) every 10s for up to 5 minutes.
- If reconnection succeeds within the 60s stale window, the card returns to live state with the same `PeerId`.
- After 60s, the peer is removed on others' views. If they later reconnect, they appear as a new member (new `PeerId`) at the bottom of the list.

### Graceful shutdown

On app close or "Close party":
- Broadcast `{ type: "bye", peerId: self }`.
- Close WebRTC connections cleanly.
- Peers remove the card immediately on receiving `"bye"`.

### Persistence (`%AppData%\GamePartyHud\config.json`)

- Calibration data (HP region, nickname region, full-HP color, nickname text, role).
- HUD position (`x`, `y`, monitor index).
- Lock state.
- Last-joined party ID (one-click rejoin after app restart).
- Poll interval preference.
- Optional `customTurnUrl` / `customTurnUsername` / `customTurnCredential` (power-user escape hatch for symmetric-NAT users; empty by default).

**Not persisted:** live roster, peer IDs, anything about other members. Roster is ephemeral per session.

---

## 8. Performance Budget

**Target: <1% CPU, <1% GPU, <100MB RAM while a game is running.**

| Operation | Frequency | Cost |
|---|---|---|
| Screen capture (small region) | 1 per 3s | ~0.3ms CPU |
| HP pixel analysis | 1 per 3s | ~0.2ms CPU |
| State broadcast (serialize + send) | 1 per 3s | ~0.5ms CPU |
| Receive state from N peers | N per 3s | ~0.1ms × N CPU |
| HUD redraw | On state change only | ~1ms CPU (WPF retained-mode) |
| Disconnect tick | 1 per 1s | <0.1ms |

Worst case (20 peers): ~5ms CPU per 3s window = <0.2% on any modern CPU.

Memory:
- SIPSorcery baseline: ~15MB.
- WPF window + tray: ~30MB.
- Small bitmap buffers: negligible.
- Expected total: 50–80MB.

GPU: WPF only redraws on state change. `Windows.Graphics.Capture` uses the compositor, zero game-side GPU hit.

**No DirectX/Vulkan hooking, no game memory reading, no process injection.** Identical footprint to OBS/Discord overlays in terms of what it touches — safe from anti-cheat flagging, robust to game updates.

---

## 9. Testing Strategy

### Unit tests (xUnit)

- **HP percent calculation:** feed synthetic bitmaps (full, half, empty, pulsing low-HP mockup, obscured bar) → verify result within ±2%.
- **Roster reducers:** given a sequence of `state`/`bye`/`kick` messages with timestamps → assert roster evolves correctly.
- **Leader election:** property-based test — any roster ordering yields the same leader across all peers.
- **HSV color matching:** edge cases around hue wrap-around near 0°/360° (common for red HP bars).

### Integration tests

- **Multi-peer localhost simulation:** 3–5 in-process peers connected via a mock signaling provider. Verify HP updates propagate and roster converges after joins, leaves, kicks.
- **Signaling fallback:** mock a failing primary provider, verify fallback activates.

### Manual pre-release checklist

- Verified working on one reference game with a horizontal HP bar.
- 3 real players on different home internets — verify real NAT traversal.
- Resolution / DPI change → re-calibrate and verify.
- Game alt-tabbed → HP reads stop updating gracefully (no crash).
- App running 8 hours in background — no memory leak, CPU stays flat.

### Not automated

- Cross-game validation (too many games). Manually verify on 1–2 reference games.
- Network chaos testing (packet loss, jitter). Relies on WebRTC's own resilience.

---

## 10. Risks & Open Questions

1. **Game-specific pixel variance.** HSV matching assumes reasonably consistent fill color. Games with heavily animated bars may need per-game tuning. Mitigation: user re-calibrates when HP is full; if persistently unreliable, it's a limitation for that game.
2. **BT tracker reliability.** Public trackers occasionally go down. PeerJS fallback covers this. Both paths must be verified during testing.
3. **Symmetric NAT users.** ~10-15% of users cannot connect without TURN. Documented limitation in v1.

---

## 11. Dependencies

Planned NuGet packages:
- `SIPSorcery` — WebRTC.
- `System.Text.Json` — built-in, config and wire messages.

WinRT APIs (`Windows.Graphics.Capture`, `Windows.Media.Ocr`) are accessed via the Windows SDK projection enabled by targeting `net8.0-windows10.0.19041.0` or newer in the `.csproj` — no additional NuGet required.

Build target: **.NET 8, Windows-only, self-contained single-file publish, `win-x64` RID.** Binary size target: ~30MB.

Minimum OS: **Windows 10 1903** (build 18362; required for `Windows.Graphics.Capture`).
