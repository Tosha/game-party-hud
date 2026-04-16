# CLAUDE.md — conventions for agents working on this repository

## What this project is

Windows-only WPF tray app that displays a peer-to-peer party HUD with live HP bars read from the screen. See `docs/requirements.md` for the product overview and `docs/superpowers/specs/2026-04-16-game-party-hud-design.md` for the technical design.

## Hard constraints

1. **Windows-only.** Target framework is `net8.0-windows10.0.19041.0`. Do not add cross-platform abstractions.
2. **Zero hosting cost.** Do not introduce dependencies on paid services or servers. Signaling uses public BitTorrent trackers and PeerJS public cloud. If you think we need a hosted backend, stop and raise the design question first.
3. **No DirectX / Vulkan hooking, no reading the game's memory, no process injection.** The app only reads screen pixels and draws on top. Anti-cheat friendliness is a hard requirement.
4. **Performance budget:** <1% CPU, <1% GPU, <100 MB RAM while a game is running. Validate with a manual 8-hour run before tagging a release.
5. **Nullability is enabled and warnings are errors.** Fix, don't suppress.

## Testing philosophy

- Pure logic (HP analysis, party state, message encoding, config) has unit tests. Write the failing test first, then the implementation.
- UI code (WPF windows, tray menu, calibration wizard) is manually tested. Do not invent flaky UI automation — verify by running the app.
- WebRTC networking is tested end-to-end with an in-process multi-peer fixture using a mock `ISignalingProvider`. Do not depend on public trackers in tests.

## Code organization

- `src/GamePartyHud/` — single app, folders by responsibility (`Capture`, `Party`, `Network`, `Config`, `Hud`, `Calibration`, `Tray`).
- `tests/GamePartyHud.Tests/` — mirrors the folder layout of `src/`.
- Non-UI folders depend only on BCL and each other via interfaces. UI folders depend on non-UI folders, never the reverse.
- `App.xaml.cs` is the only place that constructs concrete implementations of interfaces (composition root).

## Type reference

The canonical type signatures for `HpRegion`, `Hsv`, `HpCalibration`, `MemberState`, `PartyMessage`, `ISignalingProvider`, and related types are defined in `docs/superpowers/plans/2026-04-16-game-party-hud-plan.md` under "Type reference". If you need to change a signature, update that section and every task that uses it.

## Commit conventions

Use [Conventional Commits](https://www.conventionalcommits.org/):
- `feat:` user-visible feature.
- `fix:` bug fix.
- `chore:` build, CI, tooling, scaffolding.
- `refactor:` code change without behavioural change.
- `test:` tests only.
- `docs:` documentation only.

Keep commits small — one logical change per commit. Do not squash the history before review unless asked.

## Branching and releases

- `main` is the default branch. All work goes through PRs; direct pushes to `main` are avoided.
- Releases are triggered by pushing a tag `v<semver>` (e.g. `v0.1.0`). The `release.yml` workflow builds, signs (if configured), and publishes a GitHub release with the self-contained single-file `.exe`.
- Versions follow [SemVer](https://semver.org/).

## Tools the agent should use

- `dotnet build`, `dotnet test`, `dotnet publish` — standard lifecycle.
- `gh` CLI for GitHub interaction (issues, PRs, releases).
- Do not edit `.sln` files by hand — use `dotnet sln add/remove`.
