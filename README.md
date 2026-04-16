# Game Party HUD

A small Windows tray app that shows a peer-to-peer party HUD with live HP bars read from the screen, for games that lack a built-in party UI.

- **Platform:** Windows 10 (1903+) / Windows 11
- **Cost:** zero hosting, zero accounts
- **How it works:** each player calibrates a region where their own HP bar is drawn, picks a role, and joins a shared party by a 6-character ID. HP bars are read from the screen every few seconds and shared directly with other party members via WebRTC.

## Documentation

- [Requirements (non-technical)](docs/requirements.md)
- [Design spec (technical)](docs/superpowers/specs/2026-04-16-game-party-hud-design.md)
- [Implementation plan](docs/superpowers/plans/2026-04-16-game-party-hud-plan.md)

## Building from source

```bash
dotnet build GamePartyHud.sln -c Release
dotnet test GamePartyHud.sln
```

## Status

Early development. See the implementation plan for milestones.

## License

See [LICENSE](LICENSE).
