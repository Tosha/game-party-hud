# Game Party HUD — Requirements

A plain-language overview of what this app does, who it's for, and what it must do. For engineering details, see the [design spec](superpowers/specs/2026-04-16-game-party-hud-design.md).

---

## Problem

Some games don't include a party UI that shows teammates' health bars. When you're healing a group or coordinating a fight, you have no way to see how each teammate is doing without squinting at individual characters on screen.

Existing party overlay tools either require a paid server, work only on a local network, target a specific game, or are buried inside heavier gaming suites. There's room for a small, free, zero-setup tool that just shows your party's HP bars on top of any game.

## Goal

A small Windows tray app that every player runs. Each player calibrates a region of their own screen where their HP bar is shown, picks a role, and joins a shared party by ID. All players in the same party see a stacked HUD of each other's HP bars, nicknames, and role icons, updating every few seconds.

## Who it's for

Gamers who play together in small-to-medium groups (up to 20 people) in games that lack a built-in party UI, and who already use voice chat (Discord etc.) to share a short party code.

---

## User stories

- **As a healer**, I want to see my four teammates' HP bars on screen at all times, so I know who to heal next without clicking each character.
- **As a party leader**, I want to create a party, share a short code with my friends, and have everyone's HP show up automatically — no server setup, no accounts.
- **As a player**, I want to move the HUD out of the way of my game's UI, lock it in place once I like its position, and forget about it.
- **As a player on mid-range hardware**, I want the HUD to have no noticeable impact on my FPS.

---

## Functional requirements

### Party

1. The app supports **multiple simultaneous parties**. Each party is identified by a short shareable code (6 characters).
2. Players join a party by entering the code — no account, no password, no invite link.
3. Up to **20 players** can be in one party.
4. The first player to create a party is the **leader**. The leader can kick members; leadership transfers automatically if the leader leaves.
5. There is **no centralized server** holding party data. Players' apps communicate directly with each other.
6. Party IDs do not need to be remembered — the app can rejoin the last party in one click after a restart, and users can copy the current ID to clipboard from the tray menu.

### Individual setup (per player)

7. Each player runs the app on their own machine. It lives in the **system tray**.
8. Through a short wizard, each player calibrates:
   - The **screen region where their HP bar appears**, by dragging a box around it.
   - The **screen region where their character name appears**, by dragging a box around it. The app reads the name via OCR and pre-fills it in a text field, which the player can edit.
   - Their **role**, chosen from a fixed list: Tank, Healer, Support, Melee DPS, Ranged DPS, Utility.
9. Calibration is saved. The player can re-run the wizard anytime from the tray menu.

### HUD display

10. While a party is active, an **always-on-top overlay** is visible on top of the game. It works with games running in windowed or borderless-windowed mode.
11. The HUD is a **vertical stack of member cards**. Each card shows: role icon, nickname, HP bar (always red).
12. HP bars **decrease when a teammate takes damage** and **increase when they're healed**, mirroring each player's real in-game HP. Updates arrive roughly every 3 seconds.
13. A player whose app stops responding (disconnect, crash) appears **greyed out** for up to 60 seconds and is then removed from the HUD. If they reconnect within that window, their card returns to normal.
14. The HUD has a **lock button** in the top-right corner.
    - **Locked (default):** the HUD is visual only. Clicks pass through to the game. You cannot accidentally interact with the HUD during combat.
    - **Unlocked:** the HUD becomes interactive. You can drag it to move the whole block, or drag one member's card onto another to swap their positions.
15. Members cannot be separated — the HUD is a single block, and members can only be reordered within it.
16. The HUD's position is saved. The next time the app starts, it appears where you left it.

### Settings

17. The update interval is configurable (default 3 seconds, range 1–10 seconds).
18. The nickname text can be edited at any time from the tray menu.
19. The role can be changed at any time from the tray menu.

---

## Non-functional requirements

### Performance

- CPU usage: **less than 1%** while a game is running.
- Memory usage: **less than 100 MB**.
- No measurable impact on game FPS.
- No reading of game memory, no injection into the game process. The app only looks at the screen, like a recording tool.

### Cost

- **Zero hosting cost.** Players' apps connect directly to each other using free public infrastructure. No paid servers.
- **Free to use.** No accounts, no subscriptions.

### Safety & compatibility

- Works with anti-cheat systems (the app is an overlay that reads screen pixels — same category as OBS or Discord overlay; does not touch the game process).
- Works in borderless-windowed game mode. Exclusive-fullscreen mode may hide the HUD (a known limitation of all overlay tools).

### Simplicity

- Single installer, no external services to set up.
- Joining a party should take under 30 seconds: open the app, paste the code, see the HUD.
- Calibration should take under 2 minutes the first time, zero seconds every time after.

---

## Explicit non-requirements

To keep the scope small and the app focused, the following are **not** part of this project:

- Voice chat or text chat.
- Mana, stamina, or shield bars (HP only).
- Numeric HP values on the HUD (just the bar).
- Vertical or radial HP bars (horizontal only).
- Stats, history, logs, or post-fight analysis.
- macOS, Linux, or mobile versions.
- Game-specific features or integrations.
- Themes, skins, or custom layouts.
- Accounts, profiles, or cloud sync.

---

## Known limitations

- Roughly 10–15% of players behind certain home-router configurations (symmetric NAT) cannot connect peer-to-peer without a paid relay server. Those players will see a "cannot connect" message. This is a trade-off of keeping hosting cost at zero. **Workarounds for affected players:**
    - Enable **UPnP** or "open NAT" in the router settings (often resolves it).
    - Use a gaming VPN such as **ZeroTier** or **Radmin VPN** to create a virtual local network that bypasses NAT entirely.
    - Advanced users can point the app at their own relay server (a self-hosted TURN server, e.g. `coturn`) via an optional setting in the config file.
- Games with animated, gradient, or textured HP bars may give inaccurate readings. Re-calibrating with a full HP bar usually resolves it.
- The app reads the screen. Moving your game window means re-calibrating to the new position.
