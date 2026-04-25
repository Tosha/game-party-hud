# Game Party HUD Implementation Plan

> **⚠ Superseded for networking.** Tasks and type references in this plan for the `Network/` folder — PeerNetwork, BitTorrentSignaling, SIPSorcery, TurnCreds — are no longer current. See [the rewrite plan](2026-04-22-websocket-relay-rewrite.md). Non-network sections (capture, party, HUD, config, tray) are still accurate.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a Windows tray app that shows a peer-to-peer party HUD with live HP bars read from the screen, with zero backend cost, shippable as a signed single-file release.

**Architecture:** Single WPF/.NET 8 application. Separate non-UI logic (capture, HP analysis, party state, signaling, networking) into testable modules; UI layer (tray, HUD window, calibration wizard) consumes them via interfaces. Peer-to-peer over WebRTC (SIPSorcery), signaling via BitTorrent trackers with PeerJS fallback. Full-mesh topology up to 20 peers.

**Tech Stack:**
- .NET 8, WPF, C# 12, Windows-only (`net8.0-windows10.0.19041.0`)
- `SIPSorcery` (WebRTC), `SIPSorceryMedia.Abstractions`
- `Windows.Graphics.Capture`, `Windows.Media.Ocr` (via WinRT projection)
- `System.Text.Json` (config + wire messages)
- xUnit + Moq for tests
- GitHub Actions for CI + release

**Milestones (each produces working, testable software):**
- M0: Foundation — solution, test project, CLAUDE.md, editor config, first green CI
- M1: CI/CD — PR build/test, tag-triggered release with self-contained publish
- M2: HP bar reading — HSV matching, pixel analysis, screen capture (standalone testable)
- M3: HUD overlay — transparent always-on-top window, click-through via NCHITTEST, member cards
- M4: Configuration + calibration wizard — region selector, OCR, 4-step wizard, tray menu
- M5: P2P networking & signaling — wire messages, party state, BT tracker + PeerJS, peer manager
- M6: Full integration — wire everything together, drag/swap, disconnect/reconnect
- M7: First release — publish profile, signed build, v0.1.0 tag

---

## File Structure

Single WPF project with folders by responsibility. Tests in a separate library project.

```
GamePartyHud.sln
├── .github/workflows/
│   ├── ci.yml                          # PR: restore/build/test
│   └── release.yml                     # tag: publish + GitHub release
├── src/GamePartyHud/
│   ├── GamePartyHud.csproj             # WPF, WinExe, net8.0-windows10.0.19041.0
│   ├── App.xaml, App.xaml.cs           # application bootstrap, composition root
│   ├── app.manifest                    # DPI awareness PerMonitorV2
│   ├── Capture/
│   │   ├── HpRegion.cs                 # record: monitor, x, y, w, h
│   │   ├── Hsv.cs                      # Hsv record + RgbToHsv helper
│   │   ├── HsvTolerance.cs             # record: h, s, v
│   │   ├── FillDirection.cs            # enum LTR/RTL
│   │   ├── HpCalibration.cs            # aggregate of the above
│   │   ├── HpBarAnalyzer.cs            # pure: bitmap + calibration → percent
│   │   ├── IScreenCapture.cs           # interface
│   │   └── WindowsScreenCapture.cs     # Windows.Graphics.Capture impl
│   ├── Party/
│   │   ├── Role.cs                     # enum Tank/Healer/Support/MeleeDps/RangedDps/Utility
│   │   ├── MemberState.cs              # record
│   │   ├── PartyMessage.cs             # abstract + State/Bye/Kick records
│   │   ├── PartyState.cs               # roster + leader election + disconnect ticks
│   │   └── MessageJson.cs              # encode/decode wire JSON
│   ├── Network/
│   │   ├── ISignalingProvider.cs
│   │   ├── BitTorrentSignaling.cs      # wss://tracker.openwebtorrent.com, etc.
│   │   ├── PeerJsSignaling.cs          # 0.peerjs.com fallback
│   │   ├── CompositeSignaling.cs       # primary + fallback logic
│   │   └── PeerNetwork.cs              # SIPSorcery peer-connection manager
│   ├── Config/
│   │   ├── AppConfig.cs                # full persisted model
│   │   └── ConfigStore.cs              # JSON read/write, %AppData%
│   ├── Calibration/
│   │   ├── RegionSelectorWindow.xaml   # drag-to-select overlay
│   │   ├── RegionSelectorWindow.xaml.cs
│   │   ├── OcrService.cs               # Windows.Media.Ocr wrapper
│   │   ├── CalibrationWizard.xaml      # 4 steps
│   │   └── CalibrationWizard.xaml.cs
│   ├── Hud/
│   │   ├── HudWindow.xaml              # transparent, topmost
│   │   ├── HudWindow.xaml.cs           # NCHITTEST, drag-to-swap
│   │   ├── MemberCard.xaml             # user control
│   │   ├── MemberCard.xaml.cs
│   │   └── HitTestInterop.cs           # Win32 P/Invoke
│   ├── Tray/
│   │   ├── TrayIcon.cs                 # NotifyIcon bootstrap
│   │   └── TrayMenu.cs                 # menu item wiring
│   └── Assets/
│       └── roles/                      # 6 PNG icons, 24x24
├── tests/GamePartyHud.Tests/
│   ├── GamePartyHud.Tests.csproj       # net8.0-windows10.0.19041.0, xUnit
│   ├── Capture/
│   │   ├── HsvTests.cs
│   │   ├── HpBarAnalyzerTests.cs
│   │   └── SyntheticBitmap.cs          # helper to build BGRA byte[] fixtures
│   ├── Party/
│   │   ├── PartyStateTests.cs
│   │   ├── LeaderElectionTests.cs
│   │   └── MessageJsonTests.cs
│   ├── Network/
│   │   └── CompositeSignalingTests.cs  # mock providers
│   └── Config/
│       └── ConfigStoreTests.cs         # round-trip JSON
├── .editorconfig
├── .gitattributes                      # force LF in source, CRLF for .sln
├── CLAUDE.md                           # conventions for future sessions
├── LICENSE                             # already in repo
├── README.md                           # project overview
└── GamePartyHud.sln
```

**Key conventions enforced by this structure:**
- Non-UI logic lives in folders that depend only on BCL + small interfaces. Easy to unit-test.
- UI code (`Hud`, `Calibration`, `Tray`) depends on non-UI logic, never vice versa.
- `App.xaml.cs` is the composition root — it's the only place where concrete implementations of interfaces are instantiated. Tests use fakes/mocks.

---

## Type reference (kept consistent across tasks)

These types are defined in M2/M5; all later tasks use these exact names and signatures. If you find yourself about to write a different signature, check here first.

```csharp
// Capture/HpRegion.cs
public sealed record HpRegion(int Monitor, int X, int Y, int W, int H);

// Capture/Hsv.cs
public readonly record struct Hsv(float H, float S, float V);

// Capture/HsvTolerance.cs
public sealed record HsvTolerance(float H, float S, float V);

// Capture/FillDirection.cs
public enum FillDirection { LTR, RTL }

// Capture/HpCalibration.cs
public sealed record HpCalibration(
    HpRegion Region,
    Hsv FullColor,
    HsvTolerance Tolerance,
    FillDirection Direction);

// Capture/IScreenCapture.cs
public interface IScreenCapture
{
    ValueTask<byte[]> CaptureBgraAsync(HpRegion region, CancellationToken ct = default);
}

// Capture/HpBarAnalyzer.cs
public sealed class HpBarAnalyzer
{
    public float Analyze(ReadOnlySpan<byte> bgra, int width, int height, HpCalibration cal);
}

// Party/Role.cs
public enum Role { Tank, Healer, Support, MeleeDps, RangedDps, Utility }

// Party/MemberState.cs
public sealed record MemberState(
    string PeerId,
    string Nickname,
    Role Role,
    float? HpPercent,
    long JoinedAtUnix,
    long LastUpdateUnix);

// Party/PartyMessage.cs
public abstract record PartyMessage;
public sealed record StateMessage(string PeerId, string Nick, Role Role, float? Hp, long T) : PartyMessage;
public sealed record ByeMessage(string PeerId) : PartyMessage;
public sealed record KickMessage(string Target) : PartyMessage;

// Party/PartyState.cs
public sealed class PartyState
{
    public IReadOnlyDictionary<string, MemberState> Members { get; }
    public string? LeaderPeerId { get; }
    public event Action? Changed;
    public void Apply(PartyMessage msg, long nowUnix);
    public void Tick(long nowUnix); // transitions live→stale→removed
    public bool IsKicked(string peerId);
}

// Network/ISignalingProvider.cs
public interface ISignalingProvider : IAsyncDisposable
{
    Task JoinAsync(string partyId, string selfPeerId, CancellationToken ct);
    event Func<string, string, Task> OnOffer;    // (fromPeerId, sdp)
    event Func<string, string, Task> OnAnswer;   // (fromPeerId, sdp)
    event Func<string, string, Task> OnIce;      // (fromPeerId, candidateJson)
    Task SendOfferAsync(string toPeerId, string sdp, CancellationToken ct);
    Task SendAnswerAsync(string toPeerId, string sdp, CancellationToken ct);
    Task SendIceAsync(string toPeerId, string candidateJson, CancellationToken ct);
}
```

---

## Milestone 0 — Foundation

**Outcome:** Solution builds locally and under CI; `dotnet test` runs an empty passing test; CLAUDE.md documents conventions; commits follow a consistent style.

### Task 0.1: Create the .NET solution and WPF project

**Files:**
- Create: `GamePartyHud.sln`
- Create: `src/GamePartyHud/GamePartyHud.csproj`
- Create: `src/GamePartyHud/App.xaml`
- Create: `src/GamePartyHud/App.xaml.cs`
- Create: `src/GamePartyHud/app.manifest`

- [ ] **Step 1: Create the solution file**

Run from repo root:
```bash
dotnet new sln -n GamePartyHud
```
Expected: `GamePartyHud.sln` appears.

- [ ] **Step 2: Create the WPF project**

```bash
dotnet new wpf -o src/GamePartyHud -n GamePartyHud -f net8.0-windows
```
Then edit `src/GamePartyHud/GamePartyHud.csproj` to target the specific Windows SDK TFM required for WinRT projections:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <SupportedOSPlatformVersion>10.0.19041.0</SupportedOSPlatformVersion>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <LangVersion>12</LangVersion>
    <RootNamespace>GamePartyHud</RootNamespace>
    <AssemblyName>GamePartyHud</AssemblyName>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Create `app.manifest` with per-monitor DPI**

`src/GamePartyHud/app.manifest`:
```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="GamePartyHud.app"/>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
      <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
    </windowsSettings>
  </application>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/> <!-- Windows 10 -->
    </application>
  </compatibility>
</assembly>
```

- [ ] **Step 4: Replace App.xaml contents (no StartupUri; tray app)**

`src/GamePartyHud/App.xaml`:
```xml
<Application x:Class="GamePartyHud.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown">
    <Application.Resources/>
</Application>
```

`src/GamePartyHud/App.xaml.cs`:
```csharp
using System.Windows;

namespace GamePartyHud;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Composition root wired in later milestones.
    }
}
```

Delete the template's `MainWindow.xaml` / `MainWindow.xaml.cs` — we don't have a main window. The default WPF template creates those; remove them and any references.

- [ ] **Step 5: Add the project to the solution**

```bash
dotnet sln GamePartyHud.sln add src/GamePartyHud/GamePartyHud.csproj
```

- [ ] **Step 6: Verify build**

```bash
dotnet build GamePartyHud.sln -c Debug
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 7: Commit**

```bash
git add GamePartyHud.sln src/GamePartyHud/
git commit -m "chore: scaffold WPF project targeting net8.0-windows10.0.19041.0"
```

---

### Task 0.2: Create the test project

**Files:**
- Create: `tests/GamePartyHud.Tests/GamePartyHud.Tests.csproj`
- Create: `tests/GamePartyHud.Tests/SmokeTest.cs`

- [ ] **Step 1: Scaffold the test project**

```bash
dotnet new xunit -o tests/GamePartyHud.Tests -n GamePartyHud.Tests -f net8.0
```

- [ ] **Step 2: Update TFM and settings**

Replace `tests/GamePartyHud.Tests/GamePartyHud.Tests.csproj` with:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <SupportedOSPlatformVersion>10.0.19041.0</SupportedOSPlatformVersion>
    <IsPackable>false</IsPackable>
    <Nullable>enable</Nullable>
    <LangVersion>12</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="Moq" Version="4.20.72" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\GamePartyHud\GamePartyHud.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Replace the template test with a minimal smoke test**

Delete `tests/GamePartyHud.Tests/UnitTest1.cs` if present. Create `tests/GamePartyHud.Tests/SmokeTest.cs`:
```csharp
using Xunit;

namespace GamePartyHud.Tests;

public class SmokeTest
{
    [Fact]
    public void Sanity()
    {
        Assert.Equal(4, 2 + 2);
    }
}
```

- [ ] **Step 4: Add test project to solution**

```bash
dotnet sln GamePartyHud.sln add tests/GamePartyHud.Tests/GamePartyHud.Tests.csproj
```

- [ ] **Step 5: Run tests**

```bash
dotnet test GamePartyHud.sln -c Debug --nologo
```
Expected output contains: `Passed!  - Failed: 0, Passed: 1`.

- [ ] **Step 6: Commit**

```bash
git add tests/ GamePartyHud.sln
git commit -m "chore: add xUnit test project with smoke test"
```

---

### Task 0.3: Add `.editorconfig` and `.gitattributes`

**Files:**
- Create: `.editorconfig`
- Create: `.gitattributes`

- [ ] **Step 1: Create `.editorconfig`**

```ini
root = true

[*]
charset = utf-8
end_of_line = lf
insert_final_newline = true
trim_trailing_whitespace = true
indent_style = space
indent_size = 4

[*.{sln,csproj,props,targets}]
indent_size = 2

[*.{yml,yaml,json,md}]
indent_size = 2

[*.cs]
dotnet_diagnostic.CA1416.severity = none  # platform-specific code is expected
dotnet_sort_system_directives_first = true
csharp_new_line_before_open_brace = all
csharp_prefer_braces = true:warning
```

- [ ] **Step 2: Create `.gitattributes`**

```gitattributes
* text=auto eol=lf
*.sln       text eol=crlf
*.csproj    text eol=crlf
*.props     text eol=crlf
*.xaml      text eol=lf
*.cs        text eol=lf
*.md        text eol=lf
*.yml       text eol=lf
*.yaml      text eol=lf
*.json      text eol=lf
*.png       binary
*.jpg       binary
*.ico       binary
```

- [ ] **Step 3: Commit**

```bash
git add .editorconfig .gitattributes
git commit -m "chore: add editorconfig and gitattributes for consistent line endings"
```

---

### Task 0.4: Create `CLAUDE.md` with project conventions

**Files:**
- Create: `CLAUDE.md`

- [ ] **Step 1: Write `CLAUDE.md`**

```markdown
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
```

- [ ] **Step 2: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: add CLAUDE.md with project conventions"
```

---

### Task 0.5: Create `README.md`

**Files:**
- Create: `README.md`

- [ ] **Step 1: Write a minimal `README.md`**

```markdown
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
```

- [ ] **Step 2: Commit and push the foundation**

```bash
git add README.md
git commit -m "docs: add README"
git push
```

Expected: changes land on GitHub `main`.

---

## Milestone 1 — CI/CD

**Outcome:** Every PR runs build + tests via GitHub Actions; tagging `v<semver>` produces a self-contained single-file `.exe` and publishes a GitHub Release.

### Task 1.1: CI workflow — build and test on PRs

**Files:**
- Create: `.github/workflows/ci.yml`

- [ ] **Step 1: Create the CI workflow file**

```yaml
name: ci

on:
  push:
    branches: [main]
  pull_request:
    branches: [main]

jobs:
  build-test:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Restore
        run: dotnet restore GamePartyHud.sln

      - name: Build
        run: dotnet build GamePartyHud.sln -c Release --no-restore

      - name: Test
        run: dotnet test GamePartyHud.sln -c Release --no-build --logger "trx;LogFileName=test-results.trx"

      - name: Upload test results
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: test-results
          path: '**/test-results.trx'
          if-no-files-found: ignore
```

- [ ] **Step 2: Commit and push; verify workflow runs green**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: add build-and-test workflow for PRs and main pushes"
git push
```

Then watch the run: `gh run watch` (or open the repo Actions tab). Expected: the `ci` workflow completes successfully. If it fails, fix before moving on.

---

### Task 1.2: Release workflow — tag-triggered publish

**Files:**
- Create: `.github/workflows/release.yml`

- [ ] **Step 1: Decide the `RuntimeIdentifier`**

Target `win-x64` for v0.1.0. `win-arm64` can be added later. Document this by writing nothing in the project file — set it on the `dotnet publish` command line.

- [ ] **Step 2: Create the release workflow**

```yaml
name: release

on:
  push:
    tags:
      - 'v*.*.*'

permissions:
  contents: write  # required to upload release assets

jobs:
  publish:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0  # needed for git-derived version if ever used

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Derive version from tag
        id: ver
        shell: pwsh
        run: |
          $tag = "${{ github.ref_name }}"
          $v = $tag.TrimStart('v')
          echo "version=$v" >> $env:GITHUB_OUTPUT

      - name: Restore
        run: dotnet restore GamePartyHud.sln

      - name: Test
        run: dotnet test GamePartyHud.sln -c Release --logger "trx;LogFileName=test-results.trx"

      - name: Publish self-contained single-file
        run: >
          dotnet publish src/GamePartyHud/GamePartyHud.csproj
          -c Release
          -r win-x64
          --self-contained true
          -p:PublishSingleFile=true
          -p:IncludeNativeLibrariesForSelfExtract=true
          -p:Version=${{ steps.ver.outputs.version }}
          -p:FileVersion=${{ steps.ver.outputs.version }}.0
          -p:AssemblyVersion=${{ steps.ver.outputs.version }}.0
          -o publish/win-x64

      - name: Archive release binary
        shell: pwsh
        run: |
          Compress-Archive -Path publish/win-x64/GamePartyHud.exe -DestinationPath publish/GamePartyHud-${{ steps.ver.outputs.version }}-win-x64.zip

      - name: Create GitHub Release
        uses: softprops/action-gh-release@v2
        with:
          tag_name: ${{ github.ref_name }}
          name: GamePartyHud ${{ steps.ver.outputs.version }}
          draft: false
          prerelease: ${{ contains(github.ref_name, '-') }}
          generate_release_notes: true
          files: |
            publish/GamePartyHud-${{ steps.ver.outputs.version }}-win-x64.zip
```

- [ ] **Step 3: Add version props to the csproj (so CLI override works)**

Edit `src/GamePartyHud/GamePartyHud.csproj` — add these inside the first `<PropertyGroup>` (after `<AssemblyName>`):

```xml
    <Version>0.0.1</Version>
    <FileVersion>0.0.1.0</FileVersion>
    <AssemblyVersion>0.0.1.0</AssemblyVersion>
    <AssemblyTitle>Game Party HUD</AssemblyTitle>
    <Product>Game Party HUD</Product>
    <Company>Game Party HUD contributors</Company>
    <Copyright>See LICENSE</Copyright>
```

These are the default version values; the release workflow overrides them with `-p:Version=...` when tagging.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/release.yml src/GamePartyHud/GamePartyHud.csproj
git commit -m "ci: add tag-triggered release workflow and default assembly version"
git push
```

- [ ] **Step 5: Smoke-test the workflow with a pre-release tag**

```bash
git tag v0.0.1-preflight
git push origin v0.0.1-preflight
gh run watch
```

Expected: the `release` workflow completes and creates a draft release with `GamePartyHud-0.0.1-preflight-win-x64.zip` attached.

If successful, **delete the test release and tag** (the real first release happens in M7):
```bash
gh release delete v0.0.1-preflight --yes --cleanup-tag
```

If the workflow fails, fix and retry before proceeding.

---

### Task 1.3: PR template and issue template (lightweight)

**Files:**
- Create: `.github/pull_request_template.md`
- Create: `.github/ISSUE_TEMPLATE/bug_report.md`
- Create: `.github/ISSUE_TEMPLATE/feature_request.md`

- [ ] **Step 1: PR template**

`.github/pull_request_template.md`:
```markdown
## Summary

<!-- What does this change do? 1–3 bullets. -->

## Test plan

<!-- What did you verify? -->
- [ ] `dotnet build` clean
- [ ] `dotnet test` green
- [ ] Manual verification: <describe>

## Related

<!-- Link to the plan milestone/task or issue, e.g. "Milestone 2, Task 2.3" -->
```

- [ ] **Step 2: Issue templates**

`.github/ISSUE_TEMPLATE/bug_report.md`:
```markdown
---
name: Bug report
about: Report a problem with Game Party HUD
labels: bug
---

**Describe the bug**

**Steps to reproduce**
1.
2.
3.

**Expected behavior**

**Screenshots / video** (if applicable)

**Environment**
- Windows version:
- GPU / display scale:
- Game / HP bar being read:
```

`.github/ISSUE_TEMPLATE/feature_request.md`:
```markdown
---
name: Feature request
about: Suggest an idea
labels: enhancement
---

**Problem**
<!-- What's annoying or missing? -->

**Proposed solution**

**Alternatives considered**
```

- [ ] **Step 3: Commit**

```bash
git add .github/
git commit -m "chore: add PR and issue templates"
git push
```

---

## Milestone 2 — HP bar reading

**Outcome:** Given a calibrated region and a captured bitmap, we produce an HP percent in [0, 1]. All pure logic is TDD'd; the Windows capture implementation is wired separately and verified manually.

### Task 2.1: Introduce the capture value types

**Files:**
- Create: `src/GamePartyHud/Capture/HpRegion.cs`
- Create: `src/GamePartyHud/Capture/Hsv.cs`
- Create: `src/GamePartyHud/Capture/HsvTolerance.cs`
- Create: `src/GamePartyHud/Capture/FillDirection.cs`
- Create: `src/GamePartyHud/Capture/HpCalibration.cs`

- [ ] **Step 1: Create `HpRegion`**

```csharp
namespace GamePartyHud.Capture;

public sealed record HpRegion(int Monitor, int X, int Y, int W, int H);
```

- [ ] **Step 2: Create `Hsv` and `HsvTolerance`**

`Hsv.cs`:
```csharp
namespace GamePartyHud.Capture;

public readonly record struct Hsv(float H, float S, float V)
{
    /// <summary>Convert 8-bit BGRA to HSV. H is in degrees [0, 360), S and V in [0, 1].</summary>
    public static Hsv FromBgra(byte b, byte g, byte r)
    {
        float rf = r / 255f, gf = g / 255f, bf = b / 255f;
        float max = MathF.Max(rf, MathF.Max(gf, bf));
        float min = MathF.Min(rf, MathF.Min(gf, bf));
        float v = max;
        float delta = max - min;
        float s = max == 0f ? 0f : delta / max;
        float h;
        if (delta == 0f) h = 0f;
        else if (max == rf) h = 60f * (((gf - bf) / delta) % 6f);
        else if (max == gf) h = 60f * (((bf - rf) / delta) + 2f);
        else h = 60f * (((rf - gf) / delta) + 4f);
        if (h < 0f) h += 360f;
        return new Hsv(h, s, v);
    }
}
```

`HsvTolerance.cs`:
```csharp
namespace GamePartyHud.Capture;

public sealed record HsvTolerance(float H, float S, float V)
{
    public static HsvTolerance Default { get; } = new(15f, 0.25f, 0.25f);

    public bool Matches(Hsv reference, Hsv sample)
    {
        float dh = HueDistance(reference.H, sample.H);
        return dh <= H
            && MathF.Abs(reference.S - sample.S) <= S
            && MathF.Abs(reference.V - sample.V) <= V;
    }

    private static float HueDistance(float a, float b)
    {
        float d = MathF.Abs(a - b);
        return d > 180f ? 360f - d : d;
    }
}
```

- [ ] **Step 3: Create `FillDirection` and `HpCalibration`**

`FillDirection.cs`:
```csharp
namespace GamePartyHud.Capture;

public enum FillDirection { LTR, RTL }
```

`HpCalibration.cs`:
```csharp
namespace GamePartyHud.Capture;

public sealed record HpCalibration(
    HpRegion Region,
    Hsv FullColor,
    HsvTolerance Tolerance,
    FillDirection Direction);
```

- [ ] **Step 4: Build**

```bash
dotnet build GamePartyHud.sln -c Debug
```
Expected: clean build.

- [ ] **Step 5: Commit**

```bash
git add src/GamePartyHud/Capture/
git commit -m "feat(capture): introduce HpRegion, Hsv, FillDirection, HpCalibration value types"
```

---

### Task 2.2: HSV conversion and tolerance tests

**Files:**
- Create: `tests/GamePartyHud.Tests/Capture/HsvTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using GamePartyHud.Capture;
using Xunit;

namespace GamePartyHud.Tests.Capture;

public class HsvTests
{
    [Fact]
    public void FromBgra_PureRed_ReturnsHueZero()
    {
        var hsv = Hsv.FromBgra(b: 0, g: 0, r: 255);
        Assert.Equal(0f, hsv.H);
        Assert.Equal(1f, hsv.S, 3);
        Assert.Equal(1f, hsv.V, 3);
    }

    [Fact]
    public void FromBgra_PureGreen_ReturnsHue120()
    {
        var hsv = Hsv.FromBgra(b: 0, g: 255, r: 0);
        Assert.Equal(120f, hsv.H, 3);
    }

    [Fact]
    public void FromBgra_PureBlue_ReturnsHue240()
    {
        var hsv = Hsv.FromBgra(b: 255, g: 0, r: 0);
        Assert.Equal(240f, hsv.H, 3);
    }

    [Fact]
    public void FromBgra_Black_ReturnsZeroValue()
    {
        var hsv = Hsv.FromBgra(0, 0, 0);
        Assert.Equal(0f, hsv.V);
        Assert.Equal(0f, hsv.S);
    }

    [Theory]
    [InlineData(355f, 5f, true)]   // wraps through 0 within 15 deg
    [InlineData(0f, 10f, true)]
    [InlineData(0f, 20f, false)]
    [InlineData(90f, 120f, false)]
    public void Tolerance_HueWrapAround_IsSymmetric(float reference, float sample, bool expected)
    {
        var tol = new HsvTolerance(15f, 1f, 1f);
        var a = new Hsv(reference, 0.5f, 0.5f);
        var b = new Hsv(sample, 0.5f, 0.5f);
        Assert.Equal(expected, tol.Matches(a, b));
    }

    [Fact]
    public void Tolerance_SaturationAndValueDifferencesAreRespected()
    {
        var tol = new HsvTolerance(360f, 0.1f, 0.1f);
        var reference = new Hsv(0f, 0.8f, 0.8f);
        Assert.True(tol.Matches(reference, new Hsv(0f, 0.85f, 0.82f)));
        Assert.False(tol.Matches(reference, new Hsv(0f, 0.5f, 0.8f)));
        Assert.False(tol.Matches(reference, new Hsv(0f, 0.8f, 0.5f)));
    }
}
```

- [ ] **Step 2: Run the tests**

```bash
dotnet test tests/GamePartyHud.Tests/ --filter "FullyQualifiedName~HsvTests"
```

Expected: all pass (the types already do the work — this is a verification milestone, not a red/green loop for these tests).

- [ ] **Step 3: Commit**

```bash
git add tests/GamePartyHud.Tests/Capture/HsvTests.cs
git commit -m "test(capture): cover HSV conversion and tolerance with hue wrap-around"
```

---

### Task 2.3: `HpBarAnalyzer` — TDD

**Files:**
- Create: `tests/GamePartyHud.Tests/Capture/SyntheticBitmap.cs`
- Create: `tests/GamePartyHud.Tests/Capture/HpBarAnalyzerTests.cs`
- Create: `src/GamePartyHud/Capture/HpBarAnalyzer.cs`

- [ ] **Step 1: Add a helper to build synthetic BGRA bitmaps**

`tests/GamePartyHud.Tests/Capture/SyntheticBitmap.cs`:
```csharp
using System;

namespace GamePartyHud.Tests.Capture;

/// <summary>
/// Builds a flat-color horizontal bar: left <fillRatio> fraction is <fillBgr>,
/// right remainder is <emptyBgr>. Stride = width * 4. Alpha = 255 throughout.
/// </summary>
internal static class SyntheticBitmap
{
    public static byte[] HorizontalBar(int width, int height, float fillRatio,
        (byte b, byte g, byte r) fillBgr, (byte b, byte g, byte r) emptyBgr)
    {
        if (fillRatio < 0f) fillRatio = 0f;
        if (fillRatio > 1f) fillRatio = 1f;
        var buf = new byte[width * height * 4];
        int split = (int)MathF.Round(width * fillRatio);
        for (int y = 0; y < height; y++)
        {
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                var c = x < split ? fillBgr : emptyBgr;
                int i = row + x * 4;
                buf[i + 0] = c.b;
                buf[i + 1] = c.g;
                buf[i + 2] = c.r;
                buf[i + 3] = 255;
            }
        }
        return buf;
    }
}
```

- [ ] **Step 2: Write the failing analyzer tests**

`tests/GamePartyHud.Tests/Capture/HpBarAnalyzerTests.cs`:
```csharp
using GamePartyHud.Capture;
using Xunit;

namespace GamePartyHud.Tests.Capture;

public class HpBarAnalyzerTests
{
    private static readonly HpCalibration RedLtr = new(
        Region: new HpRegion(0, 0, 0, 200, 10),
        FullColor: Hsv.FromBgra(b: 0, g: 0, r: 255),
        Tolerance: HsvTolerance.Default,
        Direction: FillDirection.LTR);

    private static byte[] Bar(float ratio) => SyntheticBitmap.HorizontalBar(
        width: 200, height: 10, fillRatio: ratio,
        fillBgr: (0, 0, 255),     // red
        emptyBgr: (40, 40, 40));  // dark grey

    [Fact]
    public void Analyze_FullBar_Returns1()
    {
        var buf = Bar(1.0f);
        var pct = new HpBarAnalyzer().Analyze(buf, 200, 10, RedLtr);
        Assert.InRange(pct, 0.98f, 1.0f);
    }

    [Fact]
    public void Analyze_EmptyBar_Returns0()
    {
        var buf = Bar(0.0f);
        var pct = new HpBarAnalyzer().Analyze(buf, 200, 10, RedLtr);
        Assert.InRange(pct, 0.0f, 0.02f);
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.50f)]
    [InlineData(0.72f)]
    [InlineData(0.90f)]
    public void Analyze_PartialBar_WithinTwoPercent(float ratio)
    {
        var buf = Bar(ratio);
        var pct = new HpBarAnalyzer().Analyze(buf, 200, 10, RedLtr);
        Assert.InRange(pct, ratio - 0.02f, ratio + 0.02f);
    }

    [Fact]
    public void Analyze_Rtl_InvertsReading()
    {
        // fill on the LEFT but the bar is configured as RTL → reads as empty
        var buf = SyntheticBitmap.HorizontalBar(200, 10, 0.7f, (0, 0, 255), (40, 40, 40));
        var cal = RedLtr with { Direction = FillDirection.RTL };
        var pct = new HpBarAnalyzer().Analyze(buf, 200, 10, cal);
        // RTL: "full color" is expected on the right edge. Left-filled bar reads ~0.3 (1 - 0.7).
        Assert.InRange(pct, 0.28f, 0.32f);
    }

    [Fact]
    public void Analyze_NoMatchingPixels_Returns0()
    {
        // bar is entirely empty color
        var buf = SyntheticBitmap.HorizontalBar(200, 10, 0f, (40, 40, 40), (40, 40, 40));
        var pct = new HpBarAnalyzer().Analyze(buf, 200, 10, RedLtr);
        Assert.Equal(0f, pct);
    }

    [Fact]
    public void Analyze_Clamps_PercentToClosedUnit()
    {
        // bar has some noisy pixels at the very edge that match the fill color,
        // but the main fill is at 50%.
        var buf = SyntheticBitmap.HorizontalBar(200, 10, 0.5f, (0, 0, 255), (40, 40, 40));
        // Poke a stray "full" pixel in the middle of the empty region (simulates anti-alias).
        buf[(5 * 200 * 4) + 150 * 4 + 0] = 0;
        buf[(5 * 200 * 4) + 150 * 4 + 1] = 0;
        buf[(5 * 200 * 4) + 150 * 4 + 2] = 255;
        var pct = new HpBarAnalyzer().Analyze(buf, 200, 10, RedLtr);
        Assert.InRange(pct, 0.48f, 0.52f);   // single stray pixel shouldn't drag the reading
    }
}
```

- [ ] **Step 3: Run the tests — expect compile errors**

```bash
dotnet test tests/GamePartyHud.Tests/ --filter "FullyQualifiedName~HpBarAnalyzerTests"
```
Expected: build fails because `HpBarAnalyzer` does not exist yet.

- [ ] **Step 4: Implement `HpBarAnalyzer`**

`src/GamePartyHud/Capture/HpBarAnalyzer.cs`:
```csharp
using System;

namespace GamePartyHud.Capture;

/// <summary>
/// Reads the HP bar fill percentage from a captured BGRA bitmap.
/// Scans the middle horizontal strip for the transition point between "full HP color"
/// pixels and non-matching pixels; requires ≥3 consecutive non-match pixels to declare
/// end of fill (tolerant of anti-alias noise).
/// </summary>
public sealed class HpBarAnalyzer
{
    private const int RunRequiredToStop = 3;

    public float Analyze(ReadOnlySpan<byte> bgra, int width, int height, HpCalibration cal)
    {
        if (width <= 0 || height <= 0) return 0f;
        if (bgra.Length < width * height * 4) throw new ArgumentException("bgra too small", nameof(bgra));

        // Sample the middle strip — use a 3-row band around the vertical center for robustness.
        int centerY = height / 2;
        int y0 = Math.Max(0, centerY - 1);
        int y1 = Math.Min(height - 1, centerY + 1);

        bool ltr = cal.Direction == FillDirection.LTR;

        // Count per-column match votes across the band and require majority.
        int matchedColumns = 0;
        int nonMatchRun = 0;
        int transition = -1;

        for (int i = 0; i < width; i++)
        {
            int col = ltr ? i : (width - 1 - i);
            int matches = 0;
            int total = 0;
            for (int y = y0; y <= y1; y++)
            {
                int idx = (y * width + col) * 4;
                var hsv = Hsv.FromBgra(bgra[idx], bgra[idx + 1], bgra[idx + 2]);
                if (cal.Tolerance.Matches(cal.FullColor, hsv)) matches++;
                total++;
            }
            bool colMatches = matches * 2 > total;   // > 50% of band rows match
            if (colMatches)
            {
                matchedColumns++;
                nonMatchRun = 0;
            }
            else
            {
                nonMatchRun++;
                if (matchedColumns > 0 && nonMatchRun >= RunRequiredToStop)
                {
                    transition = i - nonMatchRun + 1;
                    break;
                }
            }
        }

        if (matchedColumns == 0) return 0f;
        if (transition < 0) transition = width; // reached end of bar without a run of non-matches

        float pct = (float)transition / width;
        return Math.Clamp(pct, 0f, 1f);
    }
}
```

- [ ] **Step 5: Run the tests — expect all pass**

```bash
dotnet test tests/GamePartyHud.Tests/ --filter "FullyQualifiedName~HpBarAnalyzerTests"
```
Expected: all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/GamePartyHud/Capture/HpBarAnalyzer.cs tests/GamePartyHud.Tests/Capture/
git commit -m "feat(capture): implement HpBarAnalyzer with middle-strip HSV matching"
```

---

### Task 2.4: Exponential moving average for poll smoothing

**Files:**
- Create: `src/GamePartyHud/Capture/HpSmoother.cs`
- Create: `tests/GamePartyHud.Tests/Capture/HpSmootherTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
using GamePartyHud.Capture;
using Xunit;

namespace GamePartyHud.Tests.Capture;

public class HpSmootherTests
{
    [Fact]
    public void FirstSample_ReturnsRaw()
    {
        var s = new HpSmoother(alpha: 0.5f);
        Assert.Equal(0.7f, s.Push(0.7f));
    }

    [Fact]
    public void SecondSample_WeightsHalfAndHalf_WhenAlphaIsHalf()
    {
        var s = new HpSmoother(alpha: 0.5f);
        s.Push(0.8f);
        Assert.Equal(0.6f, s.Push(0.4f), 3);
    }

    [Fact]
    public void Reset_DropsPriorState()
    {
        var s = new HpSmoother(alpha: 0.5f);
        s.Push(0.8f);
        s.Reset();
        Assert.Equal(0.3f, s.Push(0.3f));
    }

    [Fact]
    public void AlphaOne_PassesThroughRawValues()
    {
        var s = new HpSmoother(alpha: 1.0f);
        Assert.Equal(0.2f, s.Push(0.2f));
        Assert.Equal(0.9f, s.Push(0.9f));
    }
}
```

- [ ] **Step 2: Run — expect compile failure**

```bash
dotnet test tests/GamePartyHud.Tests/ --filter "FullyQualifiedName~HpSmootherTests"
```

- [ ] **Step 3: Implement `HpSmoother`**

```csharp
namespace GamePartyHud.Capture;

/// <summary>Exponential moving average filter: current = alpha*x + (1-alpha)*previous.</summary>
public sealed class HpSmoother
{
    private readonly float _alpha;
    private float? _state;

    public HpSmoother(float alpha = 0.5f)
    {
        if (alpha <= 0f || alpha > 1f) throw new ArgumentOutOfRangeException(nameof(alpha));
        _alpha = alpha;
    }

    public float Push(float sample)
    {
        _state = _state is null ? sample : _alpha * sample + (1f - _alpha) * _state.Value;
        return _state.Value;
    }

    public void Reset() => _state = null;
}
```

- [ ] **Step 4: Run — expect all pass**

```bash
dotnet test tests/GamePartyHud.Tests/ --filter "FullyQualifiedName~HpSmootherTests"
```

- [ ] **Step 5: Commit**

```bash
git add src/GamePartyHud/Capture/HpSmoother.cs tests/GamePartyHud.Tests/Capture/HpSmootherTests.cs
git commit -m "feat(capture): add HpSmoother EMA filter"
```

---

### Task 2.5: `IScreenCapture` interface and Windows implementation

**Files:**
- Create: `src/GamePartyHud/Capture/IScreenCapture.cs`
- Create: `src/GamePartyHud/Capture/WindowsScreenCapture.cs`

**Note:** The Windows.Graphics.Capture API is verified manually (no unit tests for this). The interface keeps downstream code testable.

- [ ] **Step 1: Create the interface**

```csharp
using System.Threading;
using System.Threading.Tasks;

namespace GamePartyHud.Capture;

public interface IScreenCapture
{
    /// <summary>Capture the given region and return a BGRA byte buffer (stride = width*4, alpha=255).</summary>
    ValueTask<byte[]> CaptureBgraAsync(HpRegion region, CancellationToken ct = default);
}
```

- [ ] **Step 2: Implement using `Windows.Graphics.Capture` and D3D11**

```csharp
using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace GamePartyHud.Capture;

/// <summary>
/// Captures a screen region using Windows.Graphics.Capture.
/// We capture the full monitor then crop; this is cheaper than creating a new capture
/// session on every poll and avoids per-region setup cost.
/// </summary>
public sealed class WindowsScreenCapture : IScreenCapture, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GraphicsCaptureItem? _monitorItem;
    private int _monitorIndex = -1;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private IDirect3DDevice? _device;
    private SizeInt32 _monitorSize;

    public async ValueTask<byte[]> CaptureBgraAsync(HpRegion region, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureMonitorSessionAsync(region.Monitor).ConfigureAwait(false);

            using var frame = await WaitForFrameAsync(ct).ConfigureAwait(false);
            if (frame is null) return Array.Empty<byte>();

            return CropAndCopy(frame, region);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ValueTask EnsureMonitorSessionAsync(int monitorIndex)
    {
        if (_session is not null && _monitorIndex == monitorIndex) return ValueTask.CompletedTask;
        Teardown();
        _monitorIndex = monitorIndex;
        _monitorItem = MonitorCaptureItem.ForMonitor(monitorIndex);
        _monitorSize = _monitorItem.Size;
        _device = Direct3D11Helper.CreateDevice();
        _framePool = Direct3D11CaptureFramePool.Create(
            _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, _monitorSize);
        _session = _framePool.CreateCaptureSession(_monitorItem);
        _session.IsCursorCaptureEnabled = false;
        _session.StartCapture();
        return ValueTask.CompletedTask;
    }

    private async Task<Direct3D11CaptureFrame?> WaitForFrameAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<Direct3D11CaptureFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(Direct3D11CaptureFramePool sender, object _)
        {
            var f = sender.TryGetNextFrame();
            tcs.TrySetResult(f);
        }
        _framePool!.FrameArrived += Handler;
        try
        {
            using (ct.Register(() => tcs.TrySetResult(null)))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _framePool.FrameArrived -= Handler;
        }
    }

    private static byte[] CropAndCopy(Direct3D11CaptureFrame frame, HpRegion r)
    {
        // The frame's surface is a D3D11 texture. Copy to a staging texture, map, then crop.
        // Direct3D11Helper.CopyToByteArray handles the GPU→CPU copy for BGRA format.
        var (bytes, stride, width, height) = Direct3D11Helper.CopyToByteArray(frame.Surface);

        int cropW = Math.Clamp(r.W, 0, width - r.X);
        int cropH = Math.Clamp(r.H, 0, height - r.Y);
        var crop = new byte[cropW * cropH * 4];

        for (int y = 0; y < cropH; y++)
        {
            int srcOffset = (r.Y + y) * stride + r.X * 4;
            int dstOffset = y * cropW * 4;
            Buffer.BlockCopy(bytes, srcOffset, crop, dstOffset, cropW * 4);
        }
        return crop;
    }

    private void Teardown()
    {
        _session?.Dispose(); _session = null;
        _framePool?.Dispose(); _framePool = null;
        _monitorItem = null;
    }

    public ValueTask DisposeAsync()
    {
        Teardown();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 3: Add interop helper stubs**

The helpers `MonitorCaptureItem.ForMonitor(int)` and `Direct3D11Helper` wrap WinRT interop that isn't directly exposed to C#. Add them:

`src/GamePartyHud/Capture/MonitorCaptureItem.cs`:
```csharp
using System;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace GamePartyHud.Capture;

internal static class MonitorCaptureItem
{
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [System.Security.SuppressUnmanagedCodeSecurity]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    public static GraphicsCaptureItem ForMonitor(int monitorIndex)
    {
        // Enumerate monitors by index.
        IntPtr hmonitor = MonitorEnumerator.GetMonitorHandle(monitorIndex);
        if (hmonitor == IntPtr.Zero)
            throw new ArgumentException($"Monitor index {monitorIndex} not found.");

        var factory = WinRT.CastExtensions.As<IGraphicsCaptureItemInterop>(
            WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem"));
        var iid = typeof(GraphicsCaptureItem).GUID;
        var ptr = factory.CreateForMonitor(hmonitor, ref iid);
        return GraphicsCaptureItem.FromAbi(ptr);
    }
}
```

`src/GamePartyHud/Capture/MonitorEnumerator.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GamePartyHud.Capture;

internal static class MonitorEnumerator
{
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    public static IntPtr GetMonitorHandle(int index)
    {
        var handles = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (h, _, _, _) => { handles.Add(h); return true; },
            IntPtr.Zero);
        return index >= 0 && index < handles.Count ? handles[index] : IntPtr.Zero;
    }
}
```

`src/GamePartyHud/Capture/Direct3D11Helper.cs`:
```csharp
using System;
using System.Runtime.InteropServices;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GamePartyHud.Capture;

/// <summary>D3D11 device creation + GPU→CPU copy helpers for Windows.Graphics.Capture.</summary>
internal static class Direct3D11Helper
{
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice",
        SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true,
        PreserveSig = false)]
    private static extern IDirect3DDevice CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice);

    public static IDirect3DDevice CreateDevice()
    {
        ID3D11Device device = D3D11.D3D11CreateDevice(
            adapter: null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
            out _).Device!;
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        return CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer);
    }

    public static (byte[] Bytes, int Stride, int Width, int Height) CopyToByteArray(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var srcTexture = access.GetInterface<ID3D11Texture2D>();
        var desc = srcTexture.Description;
        var device = srcTexture.Device;
        using var staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = desc.Width,
            Height = desc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = desc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });
        device.ImmediateContext.CopyResource(staging, srcTexture);
        var map = device.ImmediateContext.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        int stride = (int)map.RowPitch;
        int width = (int)desc.Width;
        int height = (int)desc.Height;
        var bytes = new byte[stride * height];
        Marshal.Copy(map.DataPointer, bytes, 0, bytes.Length);
        device.ImmediateContext.Unmap(staging, 0);
        return (bytes, stride, width, height);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }
}
```

- [ ] **Step 4: Add `Vortice.Direct3D11` NuGet reference**

Edit `src/GamePartyHud/GamePartyHud.csproj`, add inside a new `<ItemGroup>`:
```xml
  <ItemGroup>
    <PackageReference Include="Vortice.Direct3D11" Version="3.6.2" />
    <PackageReference Include="Vortice.DXGI" Version="3.6.2" />
  </ItemGroup>
```

- [ ] **Step 5: Build**

```bash
dotnet build GamePartyHud.sln -c Debug
```
Expected: clean build. Note: if the Vortice API surface has shifted for the pinned version, adjust the imports to match. The only thing this file is required to provide is: `IDirect3DDevice CreateDevice()` and `(bytes, stride, width, height) CopyToByteArray(IDirect3DSurface)`.

- [ ] **Step 6: Manual verification harness**

Create a one-off test window in `src/GamePartyHud/Calibration/CaptureSmokeHarness.cs` that, on a hotkey, captures a hardcoded region and saves it to `%TEMP%\gph_capture.png`. Keep it `#if DEBUG`-guarded so it doesn't ship. Use it to eyeball the captured region on multiple monitors / DPI scales during development. Delete once M4 calibration UI exists.

```csharp
#if DEBUG
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using GamePartyHud.Capture;

namespace GamePartyHud.Calibration;

internal static class CaptureSmokeHarness
{
    public static async Task SaveAsync(HpRegion region, string path)
    {
        await using var cap = new WindowsScreenCapture();
        var bgra = await cap.CaptureBgraAsync(region);
        var bmp = BitmapSource.Create(region.W, region.H, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, bgra, region.W * 4);
        using var fs = File.OpenWrite(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        encoder.Save(fs);
    }
}
#endif
```

- [ ] **Step 7: Commit**

```bash
git add src/GamePartyHud/Capture/ src/GamePartyHud/Calibration/CaptureSmokeHarness.cs src/GamePartyHud/GamePartyHud.csproj
git commit -m "feat(capture): Windows.Graphics.Capture screen capture implementation"
```

---

## Milestone 3 — HUD overlay

**Outcome:** A transparent always-on-top window that renders a vertical stack of member cards with role icon, nickname, and red HP bar. Lock button toggles per-pixel click-through via `WM_NCHITTEST`. Drag-to-move (unlocked) and drag-to-swap are implemented. Manual smoke test with three fake members passes.

### Task 3.1: Role enum, role icons, and a view-model member record

**Files:**
- Create: `src/GamePartyHud/Party/Role.cs`
- Create: `src/GamePartyHud/Hud/HudMember.cs`
- Create: `src/GamePartyHud/Assets/roles/*.png` (6 placeholder PNGs)

- [ ] **Step 1: Define `Role`**

```csharp
namespace GamePartyHud.Party;

public enum Role { Tank, Healer, Support, MeleeDps, RangedDps, Utility }
```

- [ ] **Step 2: Define a HUD view-model type (pure data for binding)**

`src/GamePartyHud/Hud/HudMember.cs`:
```csharp
using GamePartyHud.Party;

namespace GamePartyHud.Hud;

public sealed class HudMember : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string PeerId { get; }
    public HudMember(string peerId) { PeerId = peerId; }

    private string _nickname = "";
    public string Nickname
    {
        get => _nickname;
        set { if (_nickname != value) { _nickname = value; Raise(nameof(Nickname)); } }
    }

    private Role _role;
    public Role Role
    {
        get => _role;
        set { if (_role != value) { _role = value; Raise(nameof(Role)); Raise(nameof(RoleIconPath)); } }
    }

    public string RoleIconPath => $"pack://application:,,,/Assets/roles/{_role.ToString().ToLowerInvariant()}.png";

    private float _hpPercent;
    public float HpPercent
    {
        get => _hpPercent;
        set
        {
            float clamped = System.Math.Clamp(value, 0f, 1f);
            if (_hpPercent != clamped) { _hpPercent = clamped; Raise(nameof(HpPercent)); }
        }
    }

    private bool _isStale;
    public bool IsStale
    {
        get => _isStale;
        set { if (_isStale != value) { _isStale = value; Raise(nameof(IsStale)); Raise(nameof(Opacity)); } }
    }

    public double Opacity => _isStale ? 0.4 : 1.0;

    private void Raise(string p) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(p));
}
```

- [ ] **Step 3: Provide placeholder role icons**

Create six 24×24 PNGs in `src/GamePartyHud/Assets/roles/`: `tank.png`, `healer.png`, `support.png`, `meleedps.png`, `rangeddps.png`, `utility.png`. Simple solid-color shapes are fine for v0.1.0 — they're placeholders for proper art later. Mark them as Resource in the csproj:

Add to `src/GamePartyHud/GamePartyHud.csproj`:
```xml
  <ItemGroup>
    <Resource Include="Assets\roles\*.png" />
  </ItemGroup>
```

- [ ] **Step 4: Commit**

```bash
git add src/GamePartyHud/Party/Role.cs src/GamePartyHud/Hud/HudMember.cs src/GamePartyHud/Assets/ src/GamePartyHud/GamePartyHud.csproj
git commit -m "feat(hud): role enum, HudMember view-model, placeholder role icons"
```

---

### Task 3.2: `MemberCard` user control

**Files:**
- Create: `src/GamePartyHud/Hud/MemberCard.xaml`
- Create: `src/GamePartyHud/Hud/MemberCard.xaml.cs`

- [ ] **Step 1: Create the XAML**

```xml
<UserControl x:Class="GamePartyHud.Hud.MemberCard"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Width="240" Height="36"
             Background="Transparent">
    <Border Padding="6,4" Background="#99000000" CornerRadius="4"
            Opacity="{Binding Opacity}">
        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>

            <Image Grid.Column="0" Width="24" Height="24" Margin="0,0,8,0"
                   Source="{Binding RoleIconPath}"/>

            <StackPanel Grid.Column="1" VerticalAlignment="Center">
                <TextBlock Text="{Binding Nickname}" Foreground="White"
                           FontSize="11" FontWeight="SemiBold"
                           TextTrimming="CharacterEllipsis"/>
                <Grid Height="8" Margin="0,3,0,0">
                    <Border Background="#33000000" CornerRadius="1"/>
                    <Border Background="#FFE12A2A" CornerRadius="1"
                            HorizontalAlignment="Left"
                            Width="{Binding HpPercent, Converter={StaticResource HpWidthConverter}, ConverterParameter=180}"/>
                    <Border BorderBrush="#55000000" BorderThickness="1" CornerRadius="1"/>
                </Grid>
            </StackPanel>
        </Grid>
    </Border>
</UserControl>
```

- [ ] **Step 2: Create the converter**

`src/GamePartyHud/Hud/HpWidthConverter.cs`:
```csharp
using System;
using System.Globalization;
using System.Windows.Data;

namespace GamePartyHud.Hud;

public sealed class HpWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        float pct = value is float f ? f : 0f;
        double max = parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 180;
        return Math.Clamp(pct, 0f, 1f) * max;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
```

Register it in `App.xaml`:
```xml
<Application x:Class="GamePartyHud.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:hud="clr-namespace:GamePartyHud.Hud"
             ShutdownMode="OnExplicitShutdown">
    <Application.Resources>
        <hud:HpWidthConverter x:Key="HpWidthConverter"/>
    </Application.Resources>
</Application>
```

- [ ] **Step 3: Create the code-behind**

`src/GamePartyHud/Hud/MemberCard.xaml.cs`:
```csharp
using System.Windows.Controls;

namespace GamePartyHud.Hud;

public partial class MemberCard : UserControl
{
    public MemberCard() { InitializeComponent(); }
}
```

- [ ] **Step 4: Build**

```bash
dotnet build GamePartyHud.sln -c Debug
```

- [ ] **Step 5: Commit**

```bash
git add src/GamePartyHud/Hud/ src/GamePartyHud/App.xaml
git commit -m "feat(hud): MemberCard user control with role icon, nickname, red HP bar"
```

---

### Task 3.3: `HudWindow` — transparent always-on-top, per-pixel click-through

**Files:**
- Create: `src/GamePartyHud/Hud/HudWindow.xaml`
- Create: `src/GamePartyHud/Hud/HudWindow.xaml.cs`
- Create: `src/GamePartyHud/Hud/HitTestInterop.cs`

- [ ] **Step 1: Win32 interop for extended styles and hit testing**

```csharp
using System;
using System.Runtime.InteropServices;

namespace GamePartyHud.Hud;

internal static class HitTestInterop
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;

    public const int WM_NCHITTEST = 0x0084;
    public const int HTCLIENT = 1;
    public const int HTTRANSPARENT = -1;
    public const int HTCAPTION = 2;

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public static void ApplyExtendedStyles(IntPtr hwnd)
    {
        long current = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        long applied = current | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(applied));
    }

    public static short LoWord(IntPtr l) => (short)(l.ToInt64() & 0xFFFF);
    public static short HiWord(IntPtr l) => (short)((l.ToInt64() >> 16) & 0xFFFF);
}
```

- [ ] **Step 2: `HudWindow.xaml`**

```xml
<Window x:Class="GamePartyHud.Hud.HudWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:hud="clr-namespace:GamePartyHud.Hud"
        WindowStyle="None" AllowsTransparency="True"
        Background="Transparent" Topmost="True"
        ShowInTaskbar="False" ResizeMode="NoResize"
        SizeToContent="WidthAndHeight"
        Title="GamePartyHUD">
    <Border Padding="6" Background="#CC000000" CornerRadius="6"
            BorderBrush="#66FFFFFF" BorderThickness="{Binding BorderThickness}">
        <StackPanel>
            <!-- Lock button -->
            <Grid HorizontalAlignment="Right" Margin="0,0,0,4">
                <Button x:Name="LockButton" Width="20" Height="20"
                        Background="Transparent" BorderThickness="0"
                        Click="OnLockButtonClick"
                        Padding="0">
                    <TextBlock x:Name="LockGlyph" Text="🔒" FontSize="12"
                               Foreground="White"/>
                </Button>
            </Grid>
            <!-- Members -->
            <ItemsControl x:Name="Members">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <hud:MemberCard Margin="0,2"/>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </StackPanel>
    </Border>
</Window>
```

- [ ] **Step 3: `HudWindow.xaml.cs` — hook `WM_NCHITTEST`, lock toggle, bind members**

```csharp
using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;

namespace GamePartyHud.Hud;

public partial class HudWindow : Window
{
    public ObservableCollection<HudMember> MemberList { get; } = new();
    private bool _isLocked = true;

    public HudWindow()
    {
        InitializeComponent();
        Members.ItemsSource = MemberList;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HitTestInterop.ApplyExtendedStyles(hwnd);
            HwndSource.FromHwnd(hwnd)!.AddHook(WndProc);
        };
        Loaded += (_, _) => UpdateLockVisual();
    }

    public Thickness BorderThickness => _isLocked ? new Thickness(0) : new Thickness(1);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == HitTestInterop.WM_NCHITTEST)
        {
            handled = true;
            if (!_isLocked) return new IntPtr(HitTestInterop.HTCLIENT);
            int sx = HitTestInterop.LoWord(lParam);
            int sy = HitTestInterop.HiWord(lParam);
            var clientPt = PointFromScreen(new Point(sx, sy));
            if (IsOverLockButton(clientPt)) return new IntPtr(HitTestInterop.HTCLIENT);
            return new IntPtr(HitTestInterop.HTTRANSPARENT);
        }
        return IntPtr.Zero;
    }

    private bool IsOverLockButton(Point clientPt)
    {
        var btnOrigin = LockButton.TranslatePoint(new Point(0, 0), this);
        var r = new Rect(btnOrigin, new Size(LockButton.ActualWidth, LockButton.ActualHeight));
        return r.Contains(clientPt);
    }

    private void OnLockButtonClick(object sender, RoutedEventArgs e)
    {
        _isLocked = !_isLocked;
        UpdateLockVisual();
    }

    private void UpdateLockVisual()
    {
        LockGlyph.Text = _isLocked ? "🔒" : "🔓";
        // Re-evaluate BorderThickness binding
        GetBindingExpression(BorderThicknessProperty)?.UpdateTarget();
        InvalidateVisual();
    }

    /// <summary>Block-drag in unlocked mode. Called from left-mouse-down on the background.</summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_isLocked) return;
        if (e.OriginalSource == LockButton) return;
        // If the click landed on a MemberCard, the card's drag logic handles it (Task 3.5).
        // Otherwise drag the whole window.
        if (!IsOverMemberCard(e.OriginalSource))
        {
            DragMove();
        }
    }

    private static bool IsOverMemberCard(object source)
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (d is MemberCard) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }
}
```

- [ ] **Step 4: Build**

```bash
dotnet build GamePartyHud.sln -c Debug
```

- [ ] **Step 5: Commit**

```bash
git add src/GamePartyHud/Hud/HudWindow.xaml src/GamePartyHud/Hud/HudWindow.xaml.cs src/GamePartyHud/Hud/HitTestInterop.cs
git commit -m "feat(hud): HudWindow with per-pixel NCHITTEST click-through and lock toggle"
```

---

### Task 3.4: Manual smoke-test the HUD with three fake members

**Files:**
- Modify: `src/GamePartyHud/App.xaml.cs` (temporary dev harness)

- [ ] **Step 1: Wire a dev harness in `App.OnStartup` under `#if DEBUG`**

```csharp
using System.Windows;
using GamePartyHud.Hud;
using GamePartyHud.Party;

namespace GamePartyHud;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
#if DEBUG
        var hud = new HudWindow();
        hud.MemberList.Add(new HudMember("p1") { Nickname = "Yiawahuye", Role = Role.Tank,       HpPercent = 0.72f });
        hud.MemberList.Add(new HudMember("p2") { Nickname = "Kyrele",    Role = Role.Healer,     HpPercent = 1.00f });
        hud.MemberList.Add(new HudMember("p3") { Nickname = "Arakh",     Role = Role.MeleeDps,   HpPercent = 0.30f, IsStale = true });
        hud.Show();
#endif
    }
}
```

- [ ] **Step 2: Run the app**

```bash
dotnet run --project src/GamePartyHud/GamePartyHud.csproj -c Debug
```

- [ ] **Step 3: Verify manually**

- HUD appears on top of other windows.
- Locked (default): click through the HUD body to the window below; only the lock icon is clickable.
- Click the lock — glyph changes to open padlock, accent border appears.
- Now click-drag the HUD body: it moves.
- Close the app (Alt+F4 while focused, or kill the process) — expected, no crash.

If any of the above fail, fix before continuing. **Do not commit the dev harness.** Revert `App.xaml.cs` to the minimal version before the next task. Keep a local note in a scratch file only.

- [ ] **Step 4: Revert the harness**

Restore `App.xaml.cs` to:
```csharp
using System.Windows;

namespace GamePartyHud;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
    }
}
```

- [ ] **Step 5: No commit for the reverted harness** (nothing to commit)

```bash
git status
```
Expected: clean tree.

---

### Task 3.5: Drag-to-swap between member cards

**Files:**
- Modify: `src/GamePartyHud/Hud/HudWindow.xaml.cs`

- [ ] **Step 1: Extend `HudWindow` with drag-swap logic**

Add these fields and methods to `HudWindow.xaml.cs`:

```csharp
private HudMember? _dragSource;
private Point _dragStart;
private const double DragThreshold = 4;

protected override void OnMouseMove(MouseEventArgs e)
{
    base.OnMouseMove(e);
    if (_isLocked || e.LeftButton != MouseButtonState.Pressed) return;
    if (_dragSource is null) return;
    if ((e.GetPosition(this) - _dragStart).Length < DragThreshold) return;

    var target = MemberCardUnder(e.GetPosition(this));
    if (target is not null && target != _dragSource)
    {
        int si = MemberList.IndexOf(_dragSource);
        int ti = MemberList.IndexOf(target);
        if (si >= 0 && ti >= 0) MemberList.Move(si, ti);
    }
}

protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
{
    base.OnMouseLeftButtonUp(e);
    _dragSource = null;
}

private HudMember? MemberCardUnder(Point p)
{
    var hit = VisualTreeHelper.HitTest(this, p)?.VisualHit;
    while (hit is not null)
    {
        if (hit is FrameworkElement fe && fe.DataContext is HudMember m) return m;
        hit = VisualTreeHelper.GetParent(hit);
    }
    return null;
}
```

Modify `OnMouseLeftButtonDown` to initialize the drag:

```csharp
protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
{
    base.OnMouseLeftButtonDown(e);
    if (_isLocked) return;
    if (e.OriginalSource == LockButton) return;

    _dragStart = e.GetPosition(this);
    _dragSource = MemberCardUnder(_dragStart);

    if (_dragSource is null)
    {
        // Click on empty area → block drag
        DragMove();
    }
}
```

- [ ] **Step 2: Smoke test (repeat harness)**

Temporarily re-add the 3-member harness from Task 3.4. Unlock the HUD. Drag member 1 onto member 3 — expect the list to reorder. Re-lock. Close. Revert harness. Commit.

- [ ] **Step 3: Commit**

```bash
git add src/GamePartyHud/Hud/HudWindow.xaml.cs
git commit -m "feat(hud): drag-to-swap member cards in unlocked mode"
```

---

## Milestone 4 — Configuration and calibration wizard

**Outcome:** A user can run a 4-step wizard from the tray menu to pick HP region, nickname region (with OCR pre-fill), role, and confirm nickname. The result persists to `%AppData%\GamePartyHud\config.json` and round-trips faithfully.

### Task 4.1: `AppConfig` and `ConfigStore`

**Files:**
- Create: `src/GamePartyHud/Config/AppConfig.cs`
- Create: `src/GamePartyHud/Config/ConfigStore.cs`
- Create: `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs`

- [ ] **Step 1: Define `AppConfig`**

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
    string? CustomTurnUrl,
    string? CustomTurnUsername,
    string? CustomTurnCredential)
{
    public static AppConfig Defaults { get; } = new(
        HpCalibration: null,
        NicknameRegion: null,
        Nickname: "Player",
        Role: Role.Utility,
        HudPosition: new HudPosition(100, 100, 0),
        HudLocked: true,
        LastPartyId: null,
        PollIntervalMs: 3000,
        CustomTurnUrl: null,
        CustomTurnUsername: null,
        CustomTurnCredential: null);
}

public sealed record HudPosition(double X, double Y, int Monitor);
```

- [ ] **Step 2: Implement `ConfigStore`**

```csharp
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GamePartyHud.Config;

public sealed class ConfigStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _opts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ConfigStore(string? overridePath = null)
    {
        _path = overridePath ?? DefaultPath();
    }

    public static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GamePartyHud");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "config.json");
    }

    public AppConfig Load()
    {
        if (!File.Exists(_path)) return AppConfig.Defaults;
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppConfig>(json, _opts) ?? AppConfig.Defaults;
        }
        catch (Exception)
        {
            // Corrupted file — back it up and return defaults, never crash the app.
            try { File.Move(_path, _path + ".bad-" + DateTime.UtcNow.Ticks, overwrite: true); } catch { }
            return AppConfig.Defaults;
        }
    }

    public void Save(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, _opts);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }
}
```

- [ ] **Step 3: Tests**

```csharp
using System;
using System.IO;
using GamePartyHud.Capture;
using GamePartyHud.Config;
using GamePartyHud.Party;
using Xunit;

namespace GamePartyHud.Tests.Config;

public class ConfigStoreTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "gph_" + Guid.NewGuid() + ".json");

    public void Dispose()
    {
        if (File.Exists(_tmp)) File.Delete(_tmp);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var store = new ConfigStore(_tmp);
        var cfg = store.Load();
        Assert.Equal(AppConfig.Defaults, cfg);
    }

    [Fact]
    public void RoundTrip_PreservesEverything()
    {
        var store = new ConfigStore(_tmp);
        var cfg = AppConfig.Defaults with
        {
            HpCalibration = new HpCalibration(
                new HpRegion(0, 10, 20, 300, 18),
                new Hsv(5, 0.9f, 0.7f),
                new HsvTolerance(15, 0.25f, 0.25f),
                FillDirection.LTR),
            NicknameRegion = new HpRegion(0, 10, 0, 300, 20),
            Nickname = "Yiawahuye",
            Role = Role.Tank,
            HudPosition = new HudPosition(500, 400, 1),
            HudLocked = false,
            LastPartyId = "X7K2P9",
            PollIntervalMs = 2500,
            CustomTurnUrl = "turn:example.com:3478",
            CustomTurnUsername = "user",
            CustomTurnCredential = "pass"
        };
        store.Save(cfg);
        Assert.Equal(cfg, store.Load());
    }

    [Fact]
    public void Load_CorruptFile_BacksUpAndReturnsDefaults()
    {
        File.WriteAllText(_tmp, "{not-json");
        var cfg = new ConfigStore(_tmp).Load();
        Assert.Equal(AppConfig.Defaults, cfg);
        Assert.False(File.Exists(_tmp)); // moved to .bad-*
        var backups = Directory.GetFiles(Path.GetDirectoryName(_tmp)!, Path.GetFileName(_tmp) + ".bad-*");
        Assert.NotEmpty(backups);
        foreach (var b in backups) File.Delete(b);
    }
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/GamePartyHud.Tests/ --filter "FullyQualifiedName~ConfigStoreTests"
```
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/GamePartyHud/Config/ tests/GamePartyHud.Tests/Config/
git commit -m "feat(config): AppConfig + ConfigStore with atomic write and corruption recovery"
```

---

### Task 4.2: Region selector overlay

**Files:**
- Create: `src/GamePartyHud/Calibration/RegionSelectorWindow.xaml`
- Create: `src/GamePartyHud/Calibration/RegionSelectorWindow.xaml.cs`

- [ ] **Step 1: XAML — fullscreen transparent dimmer with drag rectangle**

```xml
<Window x:Class="GamePartyHud.Calibration.RegionSelectorWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None" AllowsTransparency="True"
        Background="#66000000" Topmost="True"
        ShowInTaskbar="False" ResizeMode="NoResize"
        WindowState="Maximized"
        Cursor="Cross"
        Title="Select region">
    <Grid x:Name="Root">
        <TextBlock x:Name="Prompt" Foreground="White" FontSize="18"
                   HorizontalAlignment="Center" VerticalAlignment="Top"
                   Margin="0,32,0,0"/>
        <Canvas x:Name="Canvas" Background="Transparent">
            <Rectangle x:Name="Selection"
                       Stroke="#FFE12A2A" StrokeThickness="2"
                       Fill="#33E12A2A" Visibility="Collapsed"/>
        </Canvas>
    </Grid>
</Window>
```

- [ ] **Step 2: Code-behind — emit a chosen `HpRegion`**

```csharp
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using GamePartyHud.Capture;

namespace GamePartyHud.Calibration;

public partial class RegionSelectorWindow : Window
{
    public HpRegion? Result { get; private set; }
    private Point _start;
    private bool _dragging;

    public RegionSelectorWindow(string prompt)
    {
        InitializeComponent();
        Prompt.Text = prompt;
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void OnDown(object s, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(Canvas);
        Canvas.SetLeft(Selection, _start.X);
        Canvas.SetTop(Selection, _start.Y);
        Selection.Width = 0; Selection.Height = 0;
        Selection.Visibility = Visibility.Visible;
        _dragging = true;
        CaptureMouse();
    }

    private void OnMove(object s, MouseEventArgs e)
    {
        if (!_dragging) return;
        var cur = e.GetPosition(Canvas);
        double x = Math.Min(_start.X, cur.X);
        double y = Math.Min(_start.Y, cur.Y);
        Canvas.SetLeft(Selection, x);
        Canvas.SetTop(Selection, y);
        Selection.Width = Math.Abs(cur.X - _start.X);
        Selection.Height = Math.Abs(cur.Y - _start.Y);
    }

    private void OnUp(object s, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        // Convert to screen coordinates on the current monitor.
        var topLeftScreen = PointToScreen(new Point(Canvas.GetLeft(Selection), Canvas.GetTop(Selection)));
        int monitorIndex = PrimaryMonitorIndexFor(topLeftScreen);
        Result = new HpRegion(
            monitorIndex,
            (int)topLeftScreen.X,
            (int)topLeftScreen.Y,
            (int)Selection.Width,
            (int)Selection.Height);
        Close();
    }

    private static int PrimaryMonitorIndexFor(Point screenPt)
    {
        // For v0.1.0 always return 0 (single-monitor assumption for capture).
        // Multi-monitor support: enumerate and match by bounds in a later milestone.
        return 0;
    }
}
```

- [ ] **Step 3: Build**

```bash
dotnet build GamePartyHud.sln -c Debug
```

- [ ] **Step 4: Commit**

```bash
git add src/GamePartyHud/Calibration/RegionSelectorWindow.xaml src/GamePartyHud/Calibration/RegionSelectorWindow.xaml.cs
git commit -m "feat(calibration): region selector overlay window"
```

---

### Task 4.3: `OcrService`

**Files:**
- Create: `src/GamePartyHud/Calibration/OcrService.cs`

**Note:** Windows.Media.Ocr returns async via WinRT. Wrap it in a small async service.

- [ ] **Step 1: Implement the service**

```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace GamePartyHud.Calibration;

public sealed class OcrService
{
    private readonly OcrEngine _engine;

    public OcrService()
    {
        var lang = new Language("en-US");
        _engine = OcrEngine.TryCreateFromLanguage(lang)
            ?? OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("No OCR engine available on this system.");
    }

    /// <summary>Run OCR on a BGRA byte buffer and return the concatenated recognized text.</summary>
    public async Task<string> RecognizeAsync(byte[] bgra, int width, int height)
    {
        var bmp = SoftwareBitmap.CreateCopyFromBuffer(
            ToIBuffer(bgra),
            BitmapPixelFormat.Bgra8,
            width, height,
            BitmapAlphaMode.Premultiplied);
        var result = await _engine.RecognizeAsync(bmp);
        return string.Join(" ", result.Lines.Select(l => l.Text)).Trim();
    }

    private static IBuffer ToIBuffer(byte[] data)
    {
        var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        writer.WriteBytes(data);
        writer.StoreAsync().AsTask().GetAwaiter().GetResult();
        return writer.DetachBuffer();
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build GamePartyHud.sln -c Debug
```

- [ ] **Step 3: Commit**

```bash
git add src/GamePartyHud/Calibration/OcrService.cs
git commit -m "feat(calibration): OcrService wrapping Windows.Media.Ocr"
```

---

### Task 4.4: Calibration wizard (4 steps)

**Files:**
- Create: `src/GamePartyHud/Calibration/CalibrationWizard.xaml`
- Create: `src/GamePartyHud/Calibration/CalibrationWizard.xaml.cs`

- [ ] **Step 1: XAML — stacked pages with Next/Back**

```xml
<Window x:Class="GamePartyHud.Calibration.CalibrationWizard"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Calibrate character"
        Width="460" Height="320"
        WindowStartupLocation="CenterScreen"
        ResizeMode="NoResize">
    <Grid Margin="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <TabControl x:Name="Steps" Grid.Row="0">
            <TabItem Header="1. HP bar" IsEnabled="False">
                <StackPanel Margin="8">
                    <TextBlock Text="Make sure your HP is full, then click the button and drag a box around your HP bar." TextWrapping="Wrap" Margin="0,0,0,12"/>
                    <Button x:Name="PickHpButton" Content="Pick HP bar region" Padding="10,6" HorizontalAlignment="Left" Click="OnPickHp"/>
                    <TextBlock x:Name="HpStatus" Margin="0,12,0,0" Foreground="DarkGreen"/>
                </StackPanel>
            </TabItem>
            <TabItem Header="2. Nickname" IsEnabled="False">
                <StackPanel Margin="8">
                    <TextBlock Text="Drag a box around your character's name text." TextWrapping="Wrap" Margin="0,0,0,12"/>
                    <Button x:Name="PickNickButton" Content="Pick nickname region" Padding="10,6" HorizontalAlignment="Left" Click="OnPickNick"/>
                    <TextBlock x:Name="NickStatus" Margin="0,12,0,0" Foreground="DarkGreen"/>
                </StackPanel>
            </TabItem>
            <TabItem Header="3. Role" IsEnabled="False">
                <StackPanel Margin="8">
                    <TextBlock Text="Pick your role." Margin="0,0,0,12"/>
                    <ComboBox x:Name="RoleCombo" Width="200" HorizontalAlignment="Left"/>
                </StackPanel>
            </TabItem>
            <TabItem Header="4. Confirm" IsEnabled="False">
                <StackPanel Margin="8">
                    <TextBlock Text="Your nickname (edit if needed):"/>
                    <TextBox x:Name="NickText" Margin="0,4,0,0" Width="260" HorizontalAlignment="Left"/>
                </StackPanel>
            </TabItem>
        </TabControl>

        <StackPanel Grid.Row="1" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,12,0,0">
            <Button x:Name="BackBtn" Content="Back" Padding="12,6" Margin="0,0,8,0" Click="OnBack"/>
            <Button x:Name="NextBtn" Content="Next" Padding="12,6" Click="OnNext"/>
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Step 2: Code-behind**

```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using GamePartyHud.Capture;
using GamePartyHud.Config;
using GamePartyHud.Party;

namespace GamePartyHud.Calibration;

public partial class CalibrationWizard : Window
{
    public AppConfig? Result { get; private set; }
    private readonly AppConfig _initial;
    private readonly IScreenCapture _capture;
    private readonly OcrService _ocr;
    private HpCalibration? _hpCal;
    private HpRegion? _nickRegion;
    private string _ocrText = "";

    public CalibrationWizard(AppConfig initial, IScreenCapture capture, OcrService ocr)
    {
        InitializeComponent();
        _initial = initial; _capture = capture; _ocr = ocr;
        RoleCombo.ItemsSource = Enum.GetValues<Role>();
        RoleCombo.SelectedItem = initial.Role;
        NickText.Text = initial.Nickname;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        BackBtn.IsEnabled = Steps.SelectedIndex > 0;
        NextBtn.Content = Steps.SelectedIndex == 3 ? "Save" : "Next";
    }

    private async void OnPickHp(object s, RoutedEventArgs e)
    {
        Hide();
        var picker = new RegionSelectorWindow("Full HP — drag around your HP bar");
        picker.ShowDialog();
        Show();
        if (picker.Result is not { } region) return;

        var bgra = await _capture.CaptureBgraAsync(region);
        var color = SampleFullColor(bgra, region.W, region.H);
        _hpCal = new HpCalibration(region, color, HsvTolerance.Default, FillDirection.LTR);
        HpStatus.Text = $"Captured {region.W}×{region.H} px, full HP color: H={color.H:F0}°, S={color.S:F2}, V={color.V:F2}";
    }

    private static Hsv SampleFullColor(byte[] bgra, int w, int h)
    {
        // Median-ish: average the middle strip to avoid border pixels.
        int y0 = h / 2 - 1, y1 = h / 2 + 1;
        double sr = 0, sg = 0, sb = 0; int n = 0;
        for (int y = Math.Max(0, y0); y <= Math.Min(h - 1, y1); y++)
        for (int x = w / 4; x < w * 3 / 4; x++)
        {
            int i = (y * w + x) * 4;
            sb += bgra[i]; sg += bgra[i + 1]; sr += bgra[i + 2];
            n++;
        }
        return Hsv.FromBgra((byte)(sb / n), (byte)(sg / n), (byte)(sr / n));
    }

    private async void OnPickNick(object s, RoutedEventArgs e)
    {
        Hide();
        var picker = new RegionSelectorWindow("Drag around your character name");
        picker.ShowDialog();
        Show();
        if (picker.Result is not { } region) return;

        _nickRegion = region;
        var bgra = await _capture.CaptureBgraAsync(region);
        try { _ocrText = await _ocr.RecognizeAsync(bgra, region.W, region.H); }
        catch (Exception) { _ocrText = ""; }

        NickStatus.Text = string.IsNullOrWhiteSpace(_ocrText)
            ? "Captured. OCR didn't read any text — you can type the name in step 4."
            : $"Captured. OCR read: \"{_ocrText}\"";
        if (!string.IsNullOrWhiteSpace(_ocrText)) NickText.Text = _ocrText;
    }

    private void OnBack(object s, RoutedEventArgs e)
    {
        if (Steps.SelectedIndex > 0) Steps.SelectedIndex--;
        UpdateButtons();
    }

    private void OnNext(object s, RoutedEventArgs e)
    {
        if (Steps.SelectedIndex < 3)
        {
            Steps.SelectedIndex++;
            UpdateButtons();
            return;
        }
        // Finish
        var role = (Role)RoleCombo.SelectedItem;
        Result = _initial with
        {
            HpCalibration = _hpCal ?? _initial.HpCalibration,
            NicknameRegion = _nickRegion ?? _initial.NicknameRegion,
            Nickname = string.IsNullOrWhiteSpace(NickText.Text) ? _initial.Nickname : NickText.Text.Trim(),
            Role = role
        };
        DialogResult = true;
        Close();
    }
}
```

- [ ] **Step 3: Build**

```bash
dotnet build GamePartyHud.sln -c Debug
```

- [ ] **Step 4: Commit**

```bash
git add src/GamePartyHud/Calibration/CalibrationWizard.xaml src/GamePartyHud/Calibration/CalibrationWizard.xaml.cs
git commit -m "feat(calibration): 4-step wizard (HP region, nickname region + OCR, role, confirm)"
```

---

### Task 4.5: Tray icon and menu

**Files:**
- Create: `src/GamePartyHud/Tray/TrayIcon.cs`
- Modify: `src/GamePartyHud/GamePartyHud.csproj` (add `System.Drawing.Common`)
- Modify: `src/GamePartyHud/App.xaml.cs`

- [ ] **Step 1: Add NuGet**

In `src/GamePartyHud/GamePartyHud.csproj`, add:
```xml
  <ItemGroup>
    <PackageReference Include="System.Drawing.Common" Version="8.0.10" />
  </ItemGroup>
```

Note: WPF has no built-in tray icon. We use `System.Windows.Forms.NotifyIcon` via a reference. Add to the csproj:
```xml
  <ItemGroup>
    <FrameworkReference Include="Microsoft.WindowsDesktop.App.WindowsForms" />
  </ItemGroup>
<PropertyGroup>
    <UseWindowsForms>true</UseWindowsForms>
  </PropertyGroup>
```

(WPF + WinForms in one project is supported; we use WinForms only for the tray icon.)

- [ ] **Step 2: Implement `TrayIcon`**

```csharp
using System;
using System.Drawing;
using System.Windows.Forms;

namespace GamePartyHud.Tray;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public event Action? CalibrateRequested;
    public event Action? CreatePartyRequested;
    public event Action? JoinPartyRequested;
    public event Action? CopyPartyIdRequested;
    public event Action? QuitRequested;

    public TrayIcon()
    {
        _icon = new NotifyIcon
        {
            Text = "Game Party HUD",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
    }

    public void SetPartyId(string? id)
    {
        _icon.Text = id is null
            ? "Game Party HUD"
            : $"Game Party HUD — party {id}";
    }

    private ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add("Calibrate character…", null, (_, _) => CalibrateRequested?.Invoke());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Create party", null, (_, _) => CreatePartyRequested?.Invoke());
        m.Items.Add("Join party…", null, (_, _) => JoinPartyRequested?.Invoke());
        m.Items.Add("Copy party ID", null, (_, _) => CopyPartyIdRequested?.Invoke());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Quit", null, (_, _) => QuitRequested?.Invoke());
        return m;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
```

- [ ] **Step 3: Wire tray in `App.xaml.cs`**

```csharp
using System.Windows;
using GamePartyHud.Calibration;
using GamePartyHud.Capture;
using GamePartyHud.Config;
using GamePartyHud.Tray;

namespace GamePartyHud;

public partial class App : Application
{
    private TrayIcon? _tray;
    private ConfigStore? _store;
    private AppConfig _config = AppConfig.Defaults;
    private IScreenCapture? _capture;
    private OcrService? _ocr;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _store = new ConfigStore();
        _config = _store.Load();
        _capture = new WindowsScreenCapture();
        _ocr = new OcrService();

        _tray = new TrayIcon();
        _tray.CalibrateRequested += RunCalibration;
        _tray.QuitRequested += Shutdown;
        // Party-related menu items wired in M6.
    }

    private async void RunCalibration()
    {
        var wiz = new CalibrationWizard(_config, _capture!, _ocr!);
        if (wiz.ShowDialog() == true && wiz.Result is { } updated)
        {
            _config = updated;
            _store!.Save(_config);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
```

- [ ] **Step 4: Build and run manually**

```bash
dotnet build GamePartyHud.sln -c Debug
dotnet run --project src/GamePartyHud/GamePartyHud.csproj -c Debug
```

Expected: system tray icon appears. Right-click → menu with "Calibrate character…", "Create party", "Join party…", "Copy party ID", "Quit". Clicking "Calibrate character…" opens the wizard. Run through all 4 steps and save; verify `%AppData%\GamePartyHud\config.json` contains your selections. Quit via the tray menu.

- [ ] **Step 5: Commit**

```bash
git add src/GamePartyHud/Tray/ src/GamePartyHud/App.xaml.cs src/GamePartyHud/GamePartyHud.csproj
git commit -m "feat(tray): NotifyIcon tray menu wired to calibration wizard and config store"
```

---

## Milestone 5 — P2P networking and signaling

**Outcome:** Party state with deterministic leader election, wire message encoding, BT-tracker signaling (primary) with PeerJS fallback, and a SIPSorcery-based peer manager that establishes WebRTC data channels. In-process multi-peer integration test passes.

### Task 5.1: `PartyMessage` types and JSON codec

**Files:**
- Create: `src/GamePartyHud/Party/PartyMessage.cs`
- Create: `src/GamePartyHud/Party/MessageJson.cs`
- Create: `tests/GamePartyHud.Tests/Party/MessageJsonTests.cs`

- [ ] **Step 1: Failing tests first**

```csharp
using GamePartyHud.Party;
using Xunit;

namespace GamePartyHud.Tests.Party;

public class MessageJsonTests
{
    [Fact]
    public void RoundTrip_State()
    {
        var msg = new StateMessage("peer-1", "Yia", Role.Tank, 0.72f, 1713200000);
        var json = MessageJson.Encode(msg);
        var decoded = MessageJson.Decode(json);
        Assert.Equal(msg, decoded);
    }

    [Fact]
    public void RoundTrip_Bye()
    {
        var msg = new ByeMessage("peer-2");
        Assert.Equal(msg, MessageJson.Decode(MessageJson.Encode(msg)));
    }

    [Fact]
    public void RoundTrip_Kick()
    {
        var msg = new KickMessage("peer-3");
        Assert.Equal(msg, MessageJson.Decode(MessageJson.Encode(msg)));
    }

    [Fact]
    public void Decode_UnknownType_ReturnsNull()
    {
        Assert.Null(MessageJson.Decode("""{"type":"nope"}"""));
    }

    [Fact]
    public void Decode_MalformedJson_ReturnsNull()
    {
        Assert.Null(MessageJson.Decode("{not-json"));
    }

    [Fact]
    public void State_NullHp_IsEncodedAsNull()
    {
        var msg = new StateMessage("p1", "n", Role.Healer, null, 42);
        var json = MessageJson.Encode(msg);
        Assert.Contains("\"hp\":null", json);
    }
}
```

- [ ] **Step 2: Define the messages**

```csharp
namespace GamePartyHud.Party;

public abstract record PartyMessage;

public sealed record StateMessage(string PeerId, string Nick, Role Role, float? Hp, long T) : PartyMessage;
public sealed record ByeMessage(string PeerId) : PartyMessage;
public sealed record KickMessage(string Target) : PartyMessage;
```

- [ ] **Step 3: Implement the codec**

```csharp
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GamePartyHud.Party;

public static class MessageJson
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Encode(PartyMessage msg) => msg switch
    {
        StateMessage s => JsonSerializer.Serialize(new
        {
            type = "state", peerId = s.PeerId, nick = s.Nick, role = s.Role, hp = s.Hp, t = s.T
        }, Opts),
        ByeMessage b => JsonSerializer.Serialize(new { type = "bye", peerId = b.PeerId }, Opts),
        KickMessage k => JsonSerializer.Serialize(new { type = "kick", target = k.Target }, Opts),
        _ => throw new ArgumentException("Unknown message type", nameof(msg))
    };

    public static PartyMessage? Decode(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            return type switch
            {
                "state" => new StateMessage(
                    root.GetProperty("peerId").GetString() ?? "",
                    root.GetProperty("nick").GetString() ?? "",
                    JsonSerializer.Deserialize<Role>(root.GetProperty("role").GetRawText(), Opts),
                    root.GetProperty("hp").ValueKind == JsonValueKind.Null ? null : root.GetProperty("hp").GetSingle(),
                    root.GetProperty("t").GetInt64()),
                "bye"  => new ByeMessage(root.GetProperty("peerId").GetString() ?? ""),
                "kick" => new KickMessage(root.GetProperty("target").GetString() ?? ""),
                _ => null
            };
        }
        catch (Exception)
        {
            return null;
        }
    }
}
```

- [ ] **Step 4: Run tests — all pass**

```bash
dotnet test tests/GamePartyHud.Tests/ --filter "FullyQualifiedName~MessageJsonTests"
```

- [ ] **Step 5: Commit**

```bash
git add src/GamePartyHud/Party/PartyMessage.cs src/GamePartyHud/Party/MessageJson.cs tests/GamePartyHud.Tests/Party/MessageJsonTests.cs
git commit -m "feat(party): PartyMessage records and JSON codec"
```

---

### Task 5.2: `MemberState` and `PartyState` with leader election

**Files:**
- Create: `src/GamePartyHud/Party/MemberState.cs`
- Create: `src/GamePartyHud/Party/PartyState.cs`
- Create: `tests/GamePartyHud.Tests/Party/PartyStateTests.cs`
- Create: `tests/GamePartyHud.Tests/Party/LeaderElectionTests.cs`

- [ ] **Step 1: Write failing state tests**

`PartyStateTests.cs`:
```csharp
using GamePartyHud.Party;
using Xunit;

namespace GamePartyHud.Tests.Party;

public class PartyStateTests
{
    [Fact]
    public void Apply_State_AddsMember()
    {
        var s = new PartyState();
        s.Apply(new StateMessage("p1", "n", Role.Tank, 0.9f, 100), 100);
        Assert.True(s.Members.ContainsKey("p1"));
        Assert.Equal(0.9f, s.Members["p1"].HpPercent);
        Assert.Equal(100, s.Members["p1"].JoinedAtUnix);
    }

    [Fact]
    public void Apply_StateAgain_UpdatesHpButKeepsJoinTime()
    {
        var s = new PartyState();
        s.Apply(new StateMessage("p1", "n", Role.Tank, 0.9f, 100), 100);
        s.Apply(new StateMessage("p1", "n", Role.Tank, 0.4f, 200), 200);
        Assert.Equal(0.4f, s.Members["p1"].HpPercent);
        Assert.Equal(100, s.Members["p1"].JoinedAtUnix); // unchanged
        Assert.Equal(200, s.Members["p1"].LastUpdateUnix);
    }

    [Fact]
    public void Apply_Bye_RemovesMember()
    {
        var s = new PartyState();
        s.Apply(new StateMessage("p1", "n", Role.Tank, 0.9f, 100), 100);
        s.Apply(new ByeMessage("p1"), 150);
        Assert.False(s.Members.ContainsKey("p1"));
    }

    [Fact]
    public void Apply_Kick_FlagsPeer_AndIgnoresSubsequentState()
    {
        var s = new PartyState();
        s.Apply(new StateMessage("p1", "n", Role.Tank, 0.9f, 100), 100);
        s.Apply(new KickMessage("p1"), 150);
        Assert.True(s.IsKicked("p1"));
        Assert.False(s.Members.ContainsKey("p1"));

        // Rejoining with same peer id is ignored.
        s.Apply(new StateMessage("p1", "n", Role.Tank, 0.9f, 200), 200);
        Assert.False(s.Members.ContainsKey("p1"));
    }

    [Fact]
    public void Tick_MarksStaleAfter6s_RemovesAfter60s()
    {
        var s = new PartyState();
        s.Apply(new StateMessage("p1", "n", Role.Tank, 1f, 100), 100);
        s.Tick(105);
        Assert.False(IsStale(s, "p1"));

        s.Tick(107); // 7s since last update
        Assert.True(IsStale(s, "p1"));

        s.Tick(170); // 70s since last update
        Assert.False(s.Members.ContainsKey("p1"));
    }

    private static bool IsStale(PartyState s, string id) =>
        s.Members.TryGetValue(id, out var m) && s.IsStale(m, s.LastTickUnix);

    [Fact]
    public void Changed_FiresOnApplyAndTickTransition()
    {
        var s = new PartyState();
        int count = 0;
        s.Changed += () => count++;
        s.Apply(new StateMessage("p1", "n", Role.Tank, 1f, 100), 100);
        Assert.Equal(1, count);
        s.Tick(107); // transition to stale
        Assert.Equal(2, count);
    }
}
```

`LeaderElectionTests.cs`:
```csharp
using GamePartyHud.Party;
using Xunit;

namespace GamePartyHud.Tests.Party;

public class LeaderElectionTests
{
    [Fact]
    public void EarliestJoiner_IsLeader()
    {
        var s = new PartyState();
        s.Apply(new StateMessage("p3", "n", Role.Tank, 1f, 300), 300);
        s.Apply(new StateMessage("p1", "n", Role.Tank, 1f, 100), 300);
        s.Apply(new StateMessage("p2", "n", Role.Tank, 1f, 200), 300);
        Assert.Equal("p1", s.LeaderPeerId);
    }

    [Fact]
    public void TieBreaker_IsLexicographicPeerId()
    {
        var s = new PartyState();
        s.Apply(new StateMessage("zeta", "n", Role.Tank, 1f, 100), 300);
        s.Apply(new StateMessage("alpha", "n", Role.Tank, 1f, 100), 300);
        s.Apply(new StateMessage("mike", "n", Role.Tank, 1f, 100), 300);
        Assert.Equal("alpha", s.LeaderPeerId);
    }

    [Fact]
    public void LeaderLeaves_NextEarliestBecomesLeader()
    {
        var s = new PartyState();
        s.Apply(new StateMessage("p1", "n", Role.Tank, 1f, 100), 100);
        s.Apply(new StateMessage("p2", "n", Role.Tank, 1f, 200), 200);
        Assert.Equal("p1", s.LeaderPeerId);
        s.Apply(new ByeMessage("p1"), 210);
        Assert.Equal("p2", s.LeaderPeerId);
    }

    [Fact]
    public void EmptyParty_LeaderIsNull()
    {
        Assert.Null(new PartyState().LeaderPeerId);
    }
}
```

- [ ] **Step 2: Implement the types**

`MemberState.cs`:
```csharp
namespace GamePartyHud.Party;

public sealed record MemberState(
    string PeerId,
    string Nickname,
    Role Role,
    float? HpPercent,
    long JoinedAtUnix,
    long LastUpdateUnix);
```

`PartyState.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace GamePartyHud.Party;

public sealed class PartyState
{
    private const int StaleAfterSec = 6;
    private const int RemoveAfterSec = 60;

    private readonly Dictionary<string, MemberState> _members = new();
    private readonly HashSet<string> _kicked = new();
    private readonly HashSet<string> _staleSet = new();
    public long LastTickUnix { get; private set; }

    public IReadOnlyDictionary<string, MemberState> Members => _members;
    public event Action? Changed;

    public string? LeaderPeerId
    {
        get
        {
            if (_members.Count == 0) return null;
            return _members.Values
                .OrderBy(m => m.JoinedAtUnix)
                .ThenBy(m => m.PeerId, StringComparer.Ordinal)
                .First().PeerId;
        }
    }

    public bool IsKicked(string peerId) => _kicked.Contains(peerId);

    public bool IsStale(MemberState m, long nowUnix) =>
        nowUnix - m.LastUpdateUnix >= StaleAfterSec;

    public void Apply(PartyMessage msg, long nowUnix)
    {
        bool changed = false;
        switch (msg)
        {
            case StateMessage s:
                if (_kicked.Contains(s.PeerId)) break;
                if (_members.TryGetValue(s.PeerId, out var prev))
                {
                    _members[s.PeerId] = prev with
                    {
                        Nickname = s.Nick,
                        Role = s.Role,
                        HpPercent = s.Hp,
                        LastUpdateUnix = nowUnix
                    };
                }
                else
                {
                    _members[s.PeerId] = new MemberState(
                        s.PeerId, s.Nick, s.Role, s.Hp, nowUnix, nowUnix);
                }
                _staleSet.Remove(s.PeerId); // fresh data → not stale
                changed = true;
                break;

            case ByeMessage b:
                if (_members.Remove(b.PeerId)) { _staleSet.Remove(b.PeerId); changed = true; }
                break;

            case KickMessage k:
                _kicked.Add(k.Target);
                if (_members.Remove(k.Target)) { _staleSet.Remove(k.Target); changed = true; }
                break;
        }
        LastTickUnix = nowUnix;
        if (changed) Changed?.Invoke();
    }

    public void Tick(long nowUnix)
    {
        LastTickUnix = nowUnix;
        var toRemove = new List<string>();
        bool changed = false;

        foreach (var m in _members.Values.ToList())
        {
            long age = nowUnix - m.LastUpdateUnix;
            if (age >= RemoveAfterSec)
            {
                toRemove.Add(m.PeerId);
                continue;
            }
            bool isNowStale = age >= StaleAfterSec;
            bool wasStale = _staleSet.Contains(m.PeerId);
            if (isNowStale && !wasStale)  { _staleSet.Add(m.PeerId);   changed = true; }
            else if (!isNowStale && wasStale) { _staleSet.Remove(m.PeerId); changed = true; }
        }

        foreach (var id in toRemove)
        {
            _members.Remove(id);
            _staleSet.Remove(id);
            changed = true;
        }

        if (changed) Changed?.Invoke();
    }
}
```

- [ ] **Step 3: Run tests — all pass**

```bash
dotnet test tests/GamePartyHud.Tests/ --filter "FullyQualifiedName~PartyStateTests|FullyQualifiedName~LeaderElectionTests"
```

- [ ] **Step 4: Commit**

```bash
git add src/GamePartyHud/Party/MemberState.cs src/GamePartyHud/Party/PartyState.cs tests/GamePartyHud.Tests/Party/
git commit -m "feat(party): MemberState + PartyState with deterministic leader election and stale/remove ticks"
```

---

### Task 5.3: `ISignalingProvider` interface and composite

**Files:**
- Create: `src/GamePartyHud/Network/ISignalingProvider.cs`
- Create: `src/GamePartyHud/Network/CompositeSignaling.cs`
- Create: `tests/GamePartyHud.Tests/Network/CompositeSignalingTests.cs`

- [ ] **Step 1: Interface**

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace GamePartyHud.Network;

public interface ISignalingProvider : IAsyncDisposable
{
    bool IsJoined { get; }
    Task JoinAsync(string partyId, string selfPeerId, CancellationToken ct);
    event Func<string, string, Task>? OnOffer;
    event Func<string, string, Task>? OnAnswer;
    event Func<string, string, Task>? OnIce;
    Task SendOfferAsync(string toPeerId, string sdp, CancellationToken ct);
    Task SendAnswerAsync(string toPeerId, string sdp, CancellationToken ct);
    Task SendIceAsync(string toPeerId, string candidateJson, CancellationToken ct);
}
```

- [ ] **Step 2: Composite with primary + fallback**

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace GamePartyHud.Network;

/// <summary>
/// Tries the primary provider first; if JoinAsync fails or doesn't complete within <see cref="JoinTimeout"/>,
/// falls back to the secondary. Inbound events from both providers are forwarded.
/// Outbound sends go to whichever successfully joined.
/// </summary>
public sealed class CompositeSignaling : ISignalingProvider
{
    public static TimeSpan JoinTimeout { get; set; } = TimeSpan.FromSeconds(8);

    private readonly ISignalingProvider _primary;
    private readonly ISignalingProvider _secondary;
    private ISignalingProvider? _active;

    public bool IsJoined => _active?.IsJoined == true;

    public event Func<string, string, Task>? OnOffer;
    public event Func<string, string, Task>? OnAnswer;
    public event Func<string, string, Task>? OnIce;

    public CompositeSignaling(ISignalingProvider primary, ISignalingProvider secondary)
    {
        _primary = primary; _secondary = secondary;
        Wire(_primary);
        Wire(_secondary);
    }

    private void Wire(ISignalingProvider p)
    {
        p.OnOffer  += (from, sdp) => OnOffer?.Invoke(from, sdp) ?? Task.CompletedTask;
        p.OnAnswer += (from, sdp) => OnAnswer?.Invoke(from, sdp) ?? Task.CompletedTask;
        p.OnIce    += (from, ice) => OnIce?.Invoke(from, ice) ?? Task.CompletedTask;
    }

    public async Task JoinAsync(string partyId, string selfPeerId, CancellationToken ct)
    {
        if (await TryJoinAsync(_primary, partyId, selfPeerId, ct))
        {
            _active = _primary;
            return;
        }
        if (await TryJoinAsync(_secondary, partyId, selfPeerId, ct))
        {
            _active = _secondary;
            return;
        }
        throw new InvalidOperationException("Signaling join failed on all providers.");
    }

    private static async Task<bool> TryJoinAsync(ISignalingProvider p, string id, string pid, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(JoinTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await p.JoinAsync(id, pid, linked.Token);
            return p.IsJoined;
        }
        catch (Exception) { return false; }
    }

    public Task SendOfferAsync(string to, string sdp, CancellationToken ct) =>
        _active?.SendOfferAsync(to, sdp, ct) ?? throw new InvalidOperationException("Not joined.");
    public Task SendAnswerAsync(string to, string sdp, CancellationToken ct) =>
        _active?.SendAnswerAsync(to, sdp, ct) ?? throw new InvalidOperationException("Not joined.");
    public Task SendIceAsync(string to, string ice, CancellationToken ct) =>
        _active?.SendIceAsync(to, ice, ct) ?? throw new InvalidOperationException("Not joined.");

    public async ValueTask DisposeAsync()
    {
        await _primary.DisposeAsync();
        await _secondary.DisposeAsync();
    }
}
```

- [ ] **Step 3: Tests with fakes**

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using GamePartyHud.Network;
using Xunit;

namespace GamePartyHud.Tests.Network;

public class CompositeSignalingTests
{
    private sealed class FakeProvider : ISignalingProvider
    {
        public bool IsJoined { get; private set; }
        public bool ShouldFail { get; set; }
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        public event Func<string, string, Task>? OnOffer;
        public event Func<string, string, Task>? OnAnswer;
        public event Func<string, string, Task>? OnIce;

        public async Task JoinAsync(string partyId, string selfPeerId, CancellationToken ct)
        {
            await Task.Delay(Delay, ct);
            if (ShouldFail) throw new InvalidOperationException("boom");
            IsJoined = true;
        }
        public Task SendOfferAsync(string t, string s, CancellationToken c) => Task.CompletedTask;
        public Task SendAnswerAsync(string t, string s, CancellationToken c) => Task.CompletedTask;
        public Task SendIceAsync(string t, string s, CancellationToken c) => Task.CompletedTask;

        public Task RaiseOfferAsync(string from, string sdp) => OnOffer?.Invoke(from, sdp) ?? Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Join_UsesPrimary_WhenPrimarySucceeds()
    {
        var a = new FakeProvider();
        var b = new FakeProvider();
        var c = new CompositeSignaling(a, b);
        await c.JoinAsync("X7K2P9", "me", CancellationToken.None);
        Assert.True(a.IsJoined);
        Assert.False(b.IsJoined);
    }

    [Fact]
    public async Task Join_FallsBackToSecondary_WhenPrimaryFails()
    {
        var a = new FakeProvider { ShouldFail = true };
        var b = new FakeProvider();
        var c = new CompositeSignaling(a, b);
        await c.JoinAsync("X7K2P9", "me", CancellationToken.None);
        Assert.False(a.IsJoined);
        Assert.True(b.IsJoined);
    }

    [Fact]
    public async Task Join_Throws_WhenBothFail()
    {
        var a = new FakeProvider { ShouldFail = true };
        var b = new FakeProvider { ShouldFail = true };
        var c = new CompositeSignaling(a, b);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => c.JoinAsync("X", "me", CancellationToken.None));
    }

    [Fact]
    public async Task OnOffer_FromEitherProvider_IsForwarded()
    {
        var a = new FakeProvider();
        var b = new FakeProvider();
        var c = new CompositeSignaling(a, b);
        string? seenFrom = null;
        c.OnOffer += (f, _) => { seenFrom = f; return Task.CompletedTask; };
        await a.RaiseOfferAsync("peerX", "sdp");
        Assert.Equal("peerX", seenFrom);
        await b.RaiseOfferAsync("peerY", "sdp");
        Assert.Equal("peerY", seenFrom);
    }
}
```

- [ ] **Step 4: Run tests**

```bash
dotnet test tests/GamePartyHud.Tests/ --filter "FullyQualifiedName~CompositeSignalingTests"
```
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/GamePartyHud/Network/ISignalingProvider.cs src/GamePartyHud/Network/CompositeSignaling.cs tests/GamePartyHud.Tests/Network/
git commit -m "feat(network): ISignalingProvider + CompositeSignaling with primary/fallback"
```

---

### Task 5.4: BitTorrent tracker signaling (primary)

**Files:**
- Create: `src/GamePartyHud/Network/BitTorrentSignaling.cs`

**Background:** WebTorrent-style trackers accept WebSocket connections and speak a small JSON protocol: `{action: "announce", info_hash, peer_id, offer/answer}` etc. We implement just enough to (1) announce ourselves under the party ID, (2) receive other peers' offers, (3) send our own offers/answers.

- [ ] **Step 1: Add `System.Net.WebSockets.Client` (built-in to .NET 8 — no package needed) and implement**

```csharp
using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GamePartyHud.Network;

/// <summary>
/// Signaling via public BitTorrent WSS trackers (WebTorrent-compatible).
/// The party ID is hashed to a 20-byte "infohash" so peers announcing the
/// same party ID find each other via the tracker's peer list.
/// </summary>
public sealed class BitTorrentSignaling : ISignalingProvider
{
    private static readonly string[] DefaultTrackers =
    {
        "wss://tracker.openwebtorrent.com",
        "wss://tracker.btorrent.xyz",
        "wss://tracker.webtorrent.io"
    };

    private readonly string[] _trackers;
    private readonly ClientWebSocket[] _sockets;
    private CancellationTokenSource? _readLoopCts;
    private string _partyHash = "";
    private string _selfPeer = "";
    public bool IsJoined { get; private set; }

    public event Func<string, string, Task>? OnOffer;
    public event Func<string, string, Task>? OnAnswer;
    public event Func<string, string, Task>? OnIce;

    public BitTorrentSignaling(string[]? trackers = null)
    {
        _trackers = trackers ?? DefaultTrackers;
        _sockets = new ClientWebSocket[_trackers.Length];
    }

    public async Task JoinAsync(string partyId, string selfPeerId, CancellationToken ct)
    {
        _partyHash = PartyIdToInfohash(partyId);
        _selfPeer = selfPeerId;

        int opened = 0;
        for (int i = 0; i < _trackers.Length; i++)
        {
            try
            {
                var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(_trackers[i]), ct);
                _sockets[i] = ws;
                opened++;
            }
            catch { /* continue trying others */ }
        }
        if (opened == 0) throw new InvalidOperationException("No trackers reachable.");

        _readLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        for (int i = 0; i < _sockets.Length; i++)
        {
            if (_sockets[i] is not { } s) continue;
            int idx = i;
            _ = Task.Run(() => ReadLoopAsync(idx, s, _readLoopCts.Token));
            await AnnounceAsync(s, _readLoopCts.Token);
        }
        IsJoined = true;
    }

    private async Task AnnounceAsync(ClientWebSocket s, CancellationToken ct)
    {
        var msg = JsonSerializer.Serialize(new
        {
            action = "announce",
            info_hash = _partyHash,
            peer_id = _selfPeer,
            numwant = 20,
            uploaded = 0,
            downloaded = 0,
            left = 0,
            offers = Array.Empty<object>() // we'll send offers reactively via SendOfferAsync
        });
        await s.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, ct);
    }

    private async Task ReadLoopAsync(int idx, ClientWebSocket s, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!ct.IsCancellationRequested && s.State == WebSocketState.Open)
            {
                var total = 0;
                WebSocketReceiveResult r;
                do
                {
                    r = await s.ReceiveAsync(buffer.AsMemory(total), ct);
                    total += r.Count;
                    if (total >= buffer.Length) break;
                } while (!r.EndOfMessage);

                if (r.MessageType == WebSocketMessageType.Close) break;
                var text = Encoding.UTF8.GetString(buffer, 0, total);
                await HandleMessageAsync(text);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* tracker dropped — other trackers continue */ }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task HandleMessageAsync(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("info_hash", out var ih) || ih.GetString() != _partyHash) return;
            var from = root.TryGetProperty("peer_id", out var pid) ? pid.GetString() ?? "" : "";
            if (from == _selfPeer || from.Length == 0) return;

            if (root.TryGetProperty("offer", out var offer))
            {
                var sdp = offer.GetProperty("sdp").GetString() ?? "";
                if (OnOffer is { } h) await h.Invoke(from, sdp);
            }
            else if (root.TryGetProperty("answer", out var answer))
            {
                var sdp = answer.GetProperty("sdp").GetString() ?? "";
                if (OnAnswer is { } h) await h.Invoke(from, sdp);
            }
            // Note: WebTorrent trackers do not forward ICE candidates separately;
            // the SDP carries ICE info. The OnIce event is a no-op for this provider.
        }
        catch (Exception) { /* ignore malformed */ }
    }

    public async Task SendOfferAsync(string toPeerId, string sdp, CancellationToken ct)
    {
        var msg = JsonSerializer.Serialize(new
        {
            action = "announce",
            info_hash = _partyHash,
            peer_id = _selfPeer,
            to_peer_id = toPeerId,
            offer = new { type = "offer", sdp },
            offer_id = Guid.NewGuid().ToString("N").Substring(0, 20)
        });
        await BroadcastAsync(msg, ct);
    }

    public async Task SendAnswerAsync(string toPeerId, string sdp, CancellationToken ct)
    {
        var msg = JsonSerializer.Serialize(new
        {
            action = "announce",
            info_hash = _partyHash,
            peer_id = _selfPeer,
            to_peer_id = toPeerId,
            answer = new { type = "answer", sdp }
        });
        await BroadcastAsync(msg, ct);
    }

    public Task SendIceAsync(string toPeerId, string candidateJson, CancellationToken ct) =>
        Task.CompletedTask; // WebTorrent tracker protocol bundles ICE in SDP.

    private async Task BroadcastAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        foreach (var s in _sockets)
        {
            if (s is null || s.State != WebSocketState.Open) continue;
            try { await s.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
            catch { /* one-tracker failure is fine */ }
        }
    }

    private static string PartyIdToInfohash(string partyId)
    {
        // 20-byte infohash (SHA-1 of party id, hex-encoded lower case).
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes("gph:" + partyId));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        _readLoopCts?.Cancel();
        foreach (var s in _sockets)
        {
            if (s is null) continue;
            try { await s.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            s.Dispose();
        }
    }
}
```

**Note on protocol:** real WebTorrent tracker announces include a list of *local* WebRTC offers; the tracker forwards each to a matched peer. The implementation above simplifies this by sending one offer per `SendOfferAsync`. If a tracker rejects this pattern, fall back to batching in the initial announce. This is verified in manual testing (no unit test dependency on live trackers).

- [ ] **Step 2: Build**

```bash
dotnet build GamePartyHud.sln -c Debug
```

- [ ] **Step 3: Commit**

```bash
git add src/GamePartyHud/Network/BitTorrentSignaling.cs
git commit -m "feat(network): BitTorrentSignaling via public WSS trackers"
```

---

### Task 5.5: PeerJS fallback signaling

**Files:**
- Create: `src/GamePartyHud/Network/PeerJsSignaling.cs`

- [ ] **Step 1: Implement**

```csharp
using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GamePartyHud.Network;

/// <summary>
/// Signaling via the free PeerJS public cloud (https://peerjs.com/peerserver.html).
/// Connects to wss://0.peerjs.com/peerjs with the given peer id and party id.
/// Peer discovery within a party uses a prefix convention: full peer id is
/// "{partyId}-{selfPeerId}". PeerJS lets any peer contact any other by id.
/// </summary>
public sealed class PeerJsSignaling : ISignalingProvider
{
    private const string Endpoint = "wss://0.peerjs.com/peerjs?key=peerjs&id={0}&token=t&version=1.5.0";
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private string _party = "";
    private string _selfFullId = "";
    public bool IsJoined { get; private set; }

    public event Func<string, string, Task>? OnOffer;
    public event Func<string, string, Task>? OnAnswer;
    public event Func<string, string, Task>? OnIce;

    public async Task JoinAsync(string partyId, string selfPeerId, CancellationToken ct)
    {
        _party = partyId;
        _selfFullId = $"{partyId}-{selfPeerId}";
        _ws = new ClientWebSocket();
        var uri = new Uri(string.Format(Endpoint, _selfFullId));
        await _ws.ConnectAsync(uri, ct);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => ReadLoopAsync(_cts.Token));
        IsJoined = true;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        while (!ct.IsCancellationRequested && _ws!.State == WebSocketState.Open)
        {
            int total = 0;
            WebSocketReceiveResult r;
            do
            {
                r = await _ws.ReceiveAsync(buf.AsMemory(total), ct);
                total += r.Count;
            } while (!r.EndOfMessage);

            var text = Encoding.UTF8.GetString(buf, 0, total);
            try
            {
                using var doc = JsonDocument.Parse(text);
                var type = doc.RootElement.GetProperty("type").GetString();
                var src  = doc.RootElement.TryGetProperty("src",  out var s) ? s.GetString() ?? "" : "";
                var payload = doc.RootElement.TryGetProperty("payload", out var p) ? p : default;

                // Strip the party prefix to recover the raw peer id.
                string from = src.StartsWith(_party + "-") ? src[(_party.Length + 1)..] : src;

                switch (type)
                {
                    case "OFFER":
                        if (OnOffer is { } ho) await ho.Invoke(from, payload.GetProperty("sdp").GetString() ?? "");
                        break;
                    case "ANSWER":
                        if (OnAnswer is { } ha) await ha.Invoke(from, payload.GetProperty("sdp").GetString() ?? "");
                        break;
                    case "CANDIDATE":
                        if (OnIce is { } hi) await hi.Invoke(from, payload.GetRawText());
                        break;
                }
            }
            catch { /* ignore malformed */ }
        }
    }

    private Task SendSignalAsync(string type, string toPeerId, object payload, CancellationToken ct)
    {
        var msg = JsonSerializer.Serialize(new
        {
            type,
            src = _selfFullId,
            dst = $"{_party}-{toPeerId}",
            payload
        });
        return _ws!.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, ct);
    }

    public Task SendOfferAsync(string to, string sdp, CancellationToken ct) =>
        SendSignalAsync("OFFER", to, new { type = "offer", sdp }, ct);
    public Task SendAnswerAsync(string to, string sdp, CancellationToken ct) =>
        SendSignalAsync("ANSWER", to, new { type = "answer", sdp }, ct);
    public Task SendIceAsync(string to, string iceJson, CancellationToken ct) =>
        SendSignalAsync("CANDIDATE", to, JsonDocument.Parse(iceJson).RootElement, ct);

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_ws is { State: WebSocketState.Open })
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        }
        _ws?.Dispose();
    }
}
```

- [ ] **Step 2: Build and commit**

```bash
dotnet build GamePartyHud.sln -c Debug
git add src/GamePartyHud/Network/PeerJsSignaling.cs
git commit -m "feat(network): PeerJsSignaling fallback via 0.peerjs.com"
```

---

### Task 5.6: `PeerNetwork` — SIPSorcery-based WebRTC manager

**Files:**
- Modify: `src/GamePartyHud/GamePartyHud.csproj` (add `SIPSorcery`)
- Create: `src/GamePartyHud/Network/PeerNetwork.cs`

- [ ] **Step 1: Add NuGet**

```xml
  <ItemGroup>
    <PackageReference Include="SIPSorcery" Version="8.0.9" />
  </ItemGroup>
```

- [ ] **Step 2: Implement `PeerNetwork`**

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;

namespace GamePartyHud.Network;

/// <summary>
/// Owns WebRTC connections to every other peer in the party. Uses the injected
/// <see cref="ISignalingProvider"/> only during connection setup; all steady-state
/// traffic is direct peer-to-peer via a single "party" data channel per connection.
/// </summary>
public sealed class PeerNetwork : IAsyncDisposable
{
    public record TurnCreds(string Url, string? Username, string? Credential);

    private readonly string _selfPeerId;
    private readonly ISignalingProvider _signaling;
    private readonly IReadOnlyList<RTCIceServer> _iceServers;
    private readonly ConcurrentDictionary<string, Peer> _peers = new();

    public event Action<string, string>? OnMessage; // (fromPeerId, json)
    public event Action<string>? OnConnected;       // peerId
    public event Action<string>? OnDisconnected;    // peerId

    public PeerNetwork(string selfPeerId, ISignalingProvider signaling, TurnCreds? turn = null)
    {
        _selfPeerId = selfPeerId;
        _signaling = signaling;
        var servers = new List<RTCIceServer>
        {
            new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
        };
        if (turn is { Url: { Length: > 0 } })
        {
            servers.Add(new RTCIceServer
            {
                urls = turn.Url,
                username = turn.Username ?? "",
                credential = turn.Credential ?? ""
            });
        }
        _iceServers = servers;

        _signaling.OnOffer  += HandleOfferAsync;
        _signaling.OnAnswer += HandleAnswerAsync;
        _signaling.OnIce    += HandleIceAsync;
    }

    public async Task ConnectToAsync(string peerId, CancellationToken ct)
    {
        if (_peers.ContainsKey(peerId)) return;
        var peer = await CreatePeerAsync(peerId, isInitiator: true, ct);
        var offer = peer.Connection.createOffer();
        await peer.Connection.setLocalDescription(offer);
        await _signaling.SendOfferAsync(peerId, offer.sdp, ct);
    }

    public async Task BroadcastAsync(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        foreach (var p in _peers.Values)
        {
            if (p.Channel?.readyState == RTCDataChannelState.open)
            {
                try { p.Channel.send(bytes); } catch { }
            }
        }
        await Task.CompletedTask;
    }

    private async Task<Peer> CreatePeerAsync(string peerId, bool isInitiator, CancellationToken ct)
    {
        var config = new RTCConfiguration { iceServers = new List<RTCIceServer>(_iceServers) };
        var pc = new RTCPeerConnection(config);
        var peer = new Peer(peerId, pc);
        _peers[peerId] = peer;

        pc.onicecandidate += async c =>
        {
            if (c is null) return;
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                candidate = c.candidate,
                sdpMid = c.sdpMid,
                sdpMLineIndex = c.sdpMLineIndex
            });
            try { await _signaling.SendIceAsync(peerId, json, ct); } catch { }
        };
        pc.onconnectionstatechange += state =>
        {
            if (state == RTCPeerConnectionState.connected) OnConnected?.Invoke(peerId);
            if (state == RTCPeerConnectionState.disconnected
             || state == RTCPeerConnectionState.failed
             || state == RTCPeerConnectionState.closed)
            {
                OnDisconnected?.Invoke(peerId);
                _peers.TryRemove(peerId, out _);
                pc.Close("bye");
            }
        };

        if (isInitiator)
        {
            peer.Channel = await pc.createDataChannel("party");
            WireChannel(peer, peerId);
        }
        else
        {
            pc.ondatachannel += ch => { peer.Channel = ch; WireChannel(peer, peerId); };
        }
        return peer;
    }

    private void WireChannel(Peer peer, string peerId)
    {
        if (peer.Channel is null) return;
        peer.Channel.onmessage += (_, proto, data) =>
        {
            var text = Encoding.UTF8.GetString(data);
            OnMessage?.Invoke(peerId, text);
        };
    }

    private async Task HandleOfferAsync(string fromPeerId, string sdp)
    {
        var peer = _peers.TryGetValue(fromPeerId, out var p) ? p
            : await CreatePeerAsync(fromPeerId, isInitiator: false, CancellationToken.None);
        peer.Connection.setRemoteDescription(new RTCSessionDescriptionInit
        { type = RTCSdpType.offer, sdp = sdp });
        var answer = peer.Connection.createAnswer();
        await peer.Connection.setLocalDescription(answer);
        await _signaling.SendAnswerAsync(fromPeerId, answer.sdp, CancellationToken.None);
    }

    private Task HandleAnswerAsync(string fromPeerId, string sdp)
    {
        if (_peers.TryGetValue(fromPeerId, out var p))
        {
            p.Connection.setRemoteDescription(new RTCSessionDescriptionInit
            { type = RTCSdpType.answer, sdp = sdp });
        }
        return Task.CompletedTask;
    }

    private Task HandleIceAsync(string fromPeerId, string iceJson)
    {
        if (!_peers.TryGetValue(fromPeerId, out var p)) return Task.CompletedTask;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(iceJson);
            var e = doc.RootElement;
            var init = new RTCIceCandidateInit
            {
                candidate = e.GetProperty("candidate").GetString(),
                sdpMid = e.TryGetProperty("sdpMid", out var m) ? m.GetString() : null,
                sdpMLineIndex = e.TryGetProperty("sdpMLineIndex", out var i) && i.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? (ushort)i.GetInt32() : (ushort)0
            };
            p.Connection.addIceCandidate(init);
        }
        catch { }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _peers.Values) p.Connection.Close("dispose");
        _peers.Clear();
        await _signaling.DisposeAsync();
    }

    private sealed class Peer
    {
        public string PeerId { get; }
        public RTCPeerConnection Connection { get; }
        public RTCDataChannel? Channel { get; set; }
        public Peer(string id, RTCPeerConnection conn) { PeerId = id; Connection = conn; }
    }
}
```

- [ ] **Step 3: Build**

```bash
dotnet build GamePartyHud.sln -c Debug
```

Expected: clean build. If SIPSorcery's 8.x API differs in method casing (e.g. camelCase vs PascalCase), adjust; the canonical source is their repo `sipsorcery-org/sipsorcery`.

- [ ] **Step 4: Commit**

```bash
git add src/GamePartyHud/Network/PeerNetwork.cs src/GamePartyHud/GamePartyHud.csproj
git commit -m "feat(network): PeerNetwork wraps SIPSorcery data channels with signaling glue"
```

---

### Task 5.7: In-process multi-peer integration test

**Files:**
- Create: `tests/GamePartyHud.Tests/Network/InProcessPartyTests.cs`
- Create: `tests/GamePartyHud.Tests/Network/LoopbackSignaling.cs`

- [ ] **Step 1: In-memory signaling that lets multiple `PeerNetwork`s talk to each other**

`tests/GamePartyHud.Tests/Network/LoopbackSignaling.cs`:
```csharp
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using GamePartyHud.Network;

namespace GamePartyHud.Tests.Network;

/// <summary>A single-process signaling hub used by several LoopbackProviders.</summary>
internal sealed class LoopbackHub
{
    public ConcurrentDictionary<string, LoopbackProvider> Peers { get; } = new();
}

internal sealed class LoopbackProvider : ISignalingProvider
{
    private readonly LoopbackHub _hub;
    public bool IsJoined { get; private set; }

    public event Func<string, string, Task>? OnOffer;
    public event Func<string, string, Task>? OnAnswer;
    public event Func<string, string, Task>? OnIce;

    public string? SelfId { get; private set; }

    public LoopbackProvider(LoopbackHub hub) { _hub = hub; }

    public Task JoinAsync(string partyId, string selfPeerId, CancellationToken ct)
    {
        SelfId = selfPeerId;
        _hub.Peers[selfPeerId] = this;
        IsJoined = true;
        return Task.CompletedTask;
    }

    public Task SendOfferAsync(string to, string sdp, CancellationToken ct) =>
        _hub.Peers.TryGetValue(to, out var p) ? (p.OnOffer?.Invoke(SelfId!, sdp) ?? Task.CompletedTask) : Task.CompletedTask;
    public Task SendAnswerAsync(string to, string sdp, CancellationToken ct) =>
        _hub.Peers.TryGetValue(to, out var p) ? (p.OnAnswer?.Invoke(SelfId!, sdp) ?? Task.CompletedTask) : Task.CompletedTask;
    public Task SendIceAsync(string to, string iceJson, CancellationToken ct) =>
        _hub.Peers.TryGetValue(to, out var p) ? (p.OnIce?.Invoke(SelfId!, iceJson) ?? Task.CompletedTask) : Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        if (SelfId is not null) _hub.Peers.TryRemove(SelfId, out _);
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 2: Integration test**

`tests/GamePartyHud.Tests/Network/InProcessPartyTests.cs`:
```csharp
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GamePartyHud.Network;
using GamePartyHud.Party;
using Xunit;

namespace GamePartyHud.Tests.Network;

public class InProcessPartyTests
{
    [Fact(Timeout = 15_000)]
    public async Task TwoPeers_EstablishDataChannel_AndExchangeStateMessages()
    {
        var hub = new LoopbackHub();
        var sigA = new LoopbackProvider(hub);
        var sigB = new LoopbackProvider(hub);

        var netA = new PeerNetwork("A", sigA);
        var netB = new PeerNetwork("B", sigB);

        await sigA.JoinAsync("party", "A", default);
        await sigB.JoinAsync("party", "B", default);

        var received = new TaskCompletionSource<string>();
        netB.OnMessage += (from, text) => received.TrySetResult(text);

        await netA.ConnectToAsync("B", default);

        // Wait for connection to settle before broadcasting.
        var connected = new TaskCompletionSource();
        netA.OnConnected += _ => connected.TrySetResult();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var msg = MessageJson.Encode(new StateMessage("A", "AA", Role.Tank, 0.7f, 1));
        await netA.BroadcastAsync(msg);

        var text = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(msg, text);

        await netA.DisposeAsync();
        await netB.DisposeAsync();
    }
}
```

- [ ] **Step 3: Run tests**

```bash
dotnet test tests/GamePartyHud.Tests/ --filter "FullyQualifiedName~InProcessPartyTests"
```

Expected: test passes. Note — this depends on SIPSorcery's in-process peer-connection behavior. The test is marked `Timeout` to bail out if ICE negotiation stalls.

If the test fails due to environmental timing, the network code is the prime suspect; fix the behavior, not the test, before continuing.

- [ ] **Step 4: Commit**

```bash
git add tests/GamePartyHud.Tests/Network/
git commit -m "test(network): in-process two-peer data channel integration test"
```

---

## Milestone 6 — Full integration

**Outcome:** The tray app ties everything together: calibration produces a self-state; a 3s loop captures the HP region, analyzes it, and broadcasts; incoming peer states populate the HUD; disconnect/reconnect behaves as designed. Three real machines on different home internets can join a party and see each other's HP.

### Task 6.1: Party ID generator

**Files:**
- Create: `src/GamePartyHud/Party/PartyIdGenerator.cs`
- Create: `tests/GamePartyHud.Tests/Party/PartyIdGeneratorTests.cs`

- [ ] **Step 1: Failing tests**

```csharp
using GamePartyHud.Party;
using Xunit;

namespace GamePartyHud.Tests.Party;

public class PartyIdGeneratorTests
{
    [Fact]
    public void Generate_Returns6CharactersFromAllowedAlphabet()
    {
        for (int i = 0; i < 200; i++)
        {
            var id = PartyIdGenerator.Generate();
            Assert.Equal(6, id.Length);
            foreach (var c in id) Assert.Contains(c, PartyIdGenerator.Alphabet);
        }
    }

    [Fact]
    public void Alphabet_DoesNotContainConfusableCharacters()
    {
        Assert.DoesNotContain('0', PartyIdGenerator.Alphabet);
        Assert.DoesNotContain('O', PartyIdGenerator.Alphabet);
        Assert.DoesNotContain('1', PartyIdGenerator.Alphabet);
        Assert.DoesNotContain('I', PartyIdGenerator.Alphabet);
    }
}
```

- [ ] **Step 2: Implement**

```csharp
using System.Security.Cryptography;

namespace GamePartyHud.Party;

public static class PartyIdGenerator
{
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // skip 0/O/1/I

    public static string Generate(int length = 6)
    {
        var chars = new char[length];
        Span<byte> buf = stackalloc byte[length];
        RandomNumberGenerator.Fill(buf);
        for (int i = 0; i < length; i++)
            chars[i] = Alphabet[buf[i] % Alphabet.Length];
        return new string(chars);
    }
}
```

- [ ] **Step 3: Run tests and commit**

```bash
dotnet test tests/GamePartyHud.Tests/ --filter "FullyQualifiedName~PartyIdGeneratorTests"
git add src/GamePartyHud/Party/PartyIdGenerator.cs tests/GamePartyHud.Tests/Party/PartyIdGeneratorTests.cs
git commit -m "feat(party): 6-char party ID generator with unambiguous alphabet"
```

---

### Task 6.2: `PartyOrchestrator` — wire capture → broadcast → HUD

**Files:**
- Create: `src/GamePartyHud/Party/PartyOrchestrator.cs`

- [ ] **Step 1: Implement the orchestrator**

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using GamePartyHud.Capture;
using GamePartyHud.Config;
using GamePartyHud.Network;

namespace GamePartyHud.Party;

/// <summary>
/// Drives the 3-second cycle: capture HP region, analyze, broadcast self state,
/// apply received peer states. Owns the connection lifecycle for a joined party.
/// </summary>
public sealed class PartyOrchestrator : IAsyncDisposable
{
    private readonly IScreenCapture _capture;
    private readonly HpBarAnalyzer _analyzer;
    private readonly HpSmoother _smoother = new(alpha: 0.5f);
    private readonly PartyState _state;
    private readonly PeerNetwork _net;
    private readonly AppConfig _cfg;
    private readonly string _selfPeerId;
    private readonly long _joinedAt;
    private CancellationTokenSource? _loopCts;
    private CancellationTokenSource? _tickCts;

    public string SelfPeerId => _selfPeerId;
    public PartyState State => _state;

    public PartyOrchestrator(
        AppConfig cfg,
        IScreenCapture capture,
        PartyState state,
        PeerNetwork net,
        string selfPeerId)
    {
        _cfg = cfg; _capture = capture; _state = state; _net = net;
        _analyzer = new HpBarAnalyzer();
        _selfPeerId = selfPeerId;
        _joinedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _net.OnMessage += OnPeerMessage;
    }

    private void OnPeerMessage(string fromPeerId, string text)
    {
        var msg = MessageJson.Decode(text);
        if (msg is null) return;
        // Sender's peer id is taken from the wire message (authoritative for StateMessage),
        // but we do not trust a peer to announce a different peer's state.
        if (msg is StateMessage s && s.PeerId != fromPeerId) return;
        _state.Apply(msg, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public void StartLoops()
    {
        _loopCts = new CancellationTokenSource();
        _tickCts = new CancellationTokenSource();
        _ = Task.Run(() => PollAndBroadcastLoopAsync(_loopCts.Token));
        _ = Task.Run(() => StaleTickLoopAsync(_tickCts.Token));
    }

    private async Task PollAndBroadcastLoopAsync(CancellationToken ct)
    {
        // Add a deterministic jitter up to 250 ms per peer to avoid synchronized broadcasts.
        int jitter = Math.Abs(_selfPeerId.GetHashCode()) % 250;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                float? hp = null;
                if (_cfg.HpCalibration is { } cal)
                {
                    var bgra = await _capture.CaptureBgraAsync(cal.Region, ct);
                    float raw = _analyzer.Analyze(bgra, cal.Region.W, cal.Region.H, cal);
                    hp = _smoother.Push(raw);
                }
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // Apply our own state locally too.
                _state.Apply(new StateMessage(_selfPeerId, _cfg.Nickname, _cfg.Role, hp, now), now);

                // Broadcast.
                var json = MessageJson.Encode(new StateMessage(_selfPeerId, _cfg.Nickname, _cfg.Role, hp, now));
                await _net.BroadcastAsync(json);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { /* swallow — keep looping */ }

            try { await Task.Delay(_cfg.PollIntervalMs + jitter, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task StaleTickLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            _state.Tick(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task LeaveAsync()
    {
        try
        {
            var bye = MessageJson.Encode(new ByeMessage(_selfPeerId));
            await _net.BroadcastAsync(bye);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _loopCts?.Cancel();
        _tickCts?.Cancel();
        await LeaveAsync();
        await _net.DisposeAsync();
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build GamePartyHud.sln -c Debug
```

- [ ] **Step 3: Commit**

```bash
git add src/GamePartyHud/Party/PartyOrchestrator.cs
git commit -m "feat(party): PartyOrchestrator driving capture/broadcast/stale-tick loops"
```

---

### Task 6.3: HUD view-model sync service

**Files:**
- Create: `src/GamePartyHud/Hud/HudViewModelSync.cs`

- [ ] **Step 1: Implement a sync layer that translates `PartyState` → `ObservableCollection<HudMember>` on the UI thread**

```csharp
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using GamePartyHud.Party;

namespace GamePartyHud.Hud;

public sealed class HudViewModelSync
{
    private readonly PartyState _state;
    private readonly ObservableCollection<HudMember> _target;
    private readonly HashSet<string> _muted = new();

    public HudViewModelSync(PartyState state, ObservableCollection<HudMember> target)
    {
        _state = state; _target = target;
        _state.Changed += OnStateChanged;
    }

    /// <summary>Toggle local mute for a peer. Muted peers are hidden from the HUD until unmuted.</summary>
    public void ToggleMuted(string peerId)
    {
        if (!_muted.Add(peerId)) _muted.Remove(peerId);
        Application.Current?.Dispatcher.Invoke(Sync);
    }

    private void OnStateChanged()
    {
        Application.Current?.Dispatcher.Invoke(Sync);
    }

    private void Sync()
    {
        long now = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Compute the visible set (state members minus muted).
        var visibleIds = _state.Members.Keys.Where(id => !_muted.Contains(id)).ToHashSet();

        // Remove cards for members that are no longer visible.
        for (int i = _target.Count - 1; i >= 0; i--)
        {
            if (!visibleIds.Contains(_target[i].PeerId)) _target.RemoveAt(i);
        }
        // Add/update cards for visible members.
        foreach (var id in visibleIds)
        {
            var m = _state.Members[id];
            var existing = _target.FirstOrDefault(x => x.PeerId == id);
            if (existing is null)
            {
                existing = new HudMember(id);
                _target.Add(existing);
            }
            existing.Nickname = m.Nickname;
            existing.Role = m.Role;
            existing.HpPercent = m.HpPercent ?? 0f;
            existing.IsStale = _state.IsStale(m, now);
        }
    }
}
```

- [ ] **Step 2: Build and commit**

```bash
dotnet build GamePartyHud.sln -c Debug
git add src/GamePartyHud/Hud/HudViewModelSync.cs
git commit -m "feat(hud): HudViewModelSync bridges PartyState to ObservableCollection on UI thread"
```

---

### Task 6.4: Composition root wiring in `App.xaml.cs`

**Files:**
- Modify: `src/GamePartyHud/App.xaml.cs`

- [ ] **Step 1: Replace `App.xaml.cs` with full wiring**

```csharp
using System;
using System.Threading;
using System.Windows;
using GamePartyHud.Calibration;
using GamePartyHud.Capture;
using GamePartyHud.Config;
using GamePartyHud.Hud;
using GamePartyHud.Network;
using GamePartyHud.Party;
using GamePartyHud.Tray;

namespace GamePartyHud;

public partial class App : Application
{
    private TrayIcon? _tray;
    private ConfigStore? _store;
    private AppConfig _config = AppConfig.Defaults;
    private WindowsScreenCapture? _capture;
    private OcrService? _ocr;
    private HudWindow? _hud;
    private PartyOrchestrator? _orch;
    private HudViewModelSync? _sync;
    private PartyState? _state;
    private string? _currentPartyId;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _store = new ConfigStore();
        _config = _store.Load();
        _capture = new WindowsScreenCapture();
        _ocr = new OcrService();

        _hud = new HudWindow();
        _state = new PartyState();
        _sync = new HudViewModelSync(_state, _hud.MemberList);
        _hud.Left = _config.HudPosition.X;
        _hud.Top = _config.HudPosition.Y;
        _hud.Show();

        _tray = new TrayIcon();
        _tray.CalibrateRequested += RunCalibration;
        _tray.CreatePartyRequested += async () => await JoinOrCreateAsync(PartyIdGenerator.Generate());
        _tray.JoinPartyRequested += PromptAndJoin;
        _tray.CopyPartyIdRequested += () =>
        {
            if (_currentPartyId is { } id) Clipboard.SetText(id);
        };
        _tray.QuitRequested += async () =>
        {
            if (_orch is { } o) await o.DisposeAsync();
            Shutdown();
        };

        // If we had a last party, offer one-click rejoin by pre-filling tray tooltip.
        if (!string.IsNullOrEmpty(_config.LastPartyId))
            _tray.SetPartyId(_config.LastPartyId);
    }

    private async void RunCalibration()
    {
        var wiz = new CalibrationWizard(_config, _capture!, _ocr!);
        if (wiz.ShowDialog() == true && wiz.Result is { } updated)
        {
            _config = updated;
            _store!.Save(_config);
        }
    }

    private async void PromptAndJoin()
    {
        var dlg = new JoinPartyDialog();
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.PartyId)) return;
        await JoinOrCreateAsync(dlg.PartyId!.ToUpperInvariant());
    }

    private async System.Threading.Tasks.Task JoinOrCreateAsync(string partyId)
    {
        if (_orch is { } old) await old.DisposeAsync();
        _state!.GetType(); // keep reference live

        var selfPeer = Guid.NewGuid().ToString("N");
        var primary = new BitTorrentSignaling();
        var fallback = new PeerJsSignaling();
        var signaling = new CompositeSignaling(primary, fallback);

        var turn = _config.CustomTurnUrl is { Length: > 0 }
            ? new PeerNetwork.TurnCreds(_config.CustomTurnUrl, _config.CustomTurnUsername, _config.CustomTurnCredential)
            : null;
        var net = new PeerNetwork(selfPeer, signaling, turn);

        try
        {
            await signaling.JoinAsync(partyId, selfPeer, CancellationToken.None);
        }
        catch (Exception)
        {
            MessageBox.Show(
                "Could not connect to party — your network may be blocking P2P connections. " +
                "See README.md for workarounds (UPnP, gaming VPN, custom TURN URL).",
                "Game Party HUD", MessageBoxButton.OK, MessageBoxImage.Warning);
            await net.DisposeAsync();
            return;
        }

        _orch = new PartyOrchestrator(_config, _capture!, _state!, net, selfPeer);
        _orch.StartLoops();
        _currentPartyId = partyId;
        _tray!.SetPartyId(partyId);

        _config = _config with { LastPartyId = partyId };
        _store!.Save(_config);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_orch is { } o) await o.DisposeAsync();
        if (_hud is { } h)
        {
            _config = _config with { HudPosition = new HudPosition(h.Left, h.Top, 0) };
            _store!.Save(_config);
        }
        _tray?.Dispose();
        if (_capture is { } c) await c.DisposeAsync();
        base.OnExit(e);
    }
}
```

- [ ] **Step 2: Create `JoinPartyDialog`**

`src/GamePartyHud/Calibration/JoinPartyDialog.xaml`:
```xml
<Window x:Class="GamePartyHud.Calibration.JoinPartyDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Join party" Width="320" Height="150"
        WindowStartupLocation="CenterScreen" ResizeMode="NoResize">
    <StackPanel Margin="16">
        <TextBlock Text="Enter the 6-character party ID:"/>
        <TextBox x:Name="Input" Margin="0,8,0,0" FontSize="16" MaxLength="6" CharacterCasing="Upper"/>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,16,0,0">
            <Button Content="Cancel" Width="70" Margin="0,0,8,0" IsCancel="True"/>
            <Button Content="Join" Width="70" IsDefault="True" Click="OnJoin"/>
        </StackPanel>
    </StackPanel>
</Window>
```

`src/GamePartyHud/Calibration/JoinPartyDialog.xaml.cs`:
```csharp
using System.Windows;

namespace GamePartyHud.Calibration;

public partial class JoinPartyDialog : Window
{
    public string? PartyId { get; private set; }
    public JoinPartyDialog() { InitializeComponent(); }
    private void OnJoin(object s, RoutedEventArgs e)
    {
        PartyId = Input.Text.Trim();
        DialogResult = true;
    }
}
```

- [ ] **Step 3: Build, manual run**

```bash
dotnet build GamePartyHud.sln -c Debug
dotnet run --project src/GamePartyHud/GamePartyHud.csproj -c Debug
```

Verify:
- HUD opens and shows (empty) stack.
- Right-click tray → "Create party" → HUD stays visible; tray tooltip updates.
- After calibration, your own card appears with live HP reading from the screen.
- Second instance on another machine: "Join party", enter the ID from the first → HUD on both machines shows both members.

- [ ] **Step 4: Commit**

```bash
git add src/GamePartyHud/App.xaml.cs src/GamePartyHud/Calibration/JoinPartyDialog.xaml src/GamePartyHud/Calibration/JoinPartyDialog.xaml.cs
git commit -m "feat(app): wire calibration, screen polling, P2P, and HUD end-to-end"
git push
```

---

### Task 6.5: HUD right-click menus (kick, mute, copy nickname)

**Files:**
- Modify: `src/GamePartyHud/Hud/HudWindow.xaml`
- Modify: `src/GamePartyHud/Hud/HudWindow.xaml.cs`

- [ ] **Step 1: Add a `ContextMenu` resource to the `MemberCard` binding**

Update `HudWindow.xaml`'s `DataTemplate` to attach a context menu to the card:

```xml
<DataTemplate>
    <hud:MemberCard Margin="0,2">
        <hud:MemberCard.ContextMenu>
            <ContextMenu>
                <MenuItem Header="Mute updates" Click="OnMuteClick"/>
                <MenuItem Header="Copy nickname" Click="OnCopyNickClick"/>
                <Separator/>
                <MenuItem Header="Kick" Click="OnKickClick"
                          IsEnabled="{Binding DataContext.IsLeaderAndNotSelf, RelativeSource={RelativeSource AncestorType=Window}}"/>
            </ContextMenu>
        </hud:MemberCard.ContextMenu>
    </hud:MemberCard>
</DataTemplate>
```

- [ ] **Step 2: Add handlers and signals**

In `HudWindow.xaml.cs`, add:

```csharp
public event System.Action<string>? KickRequested;
public event System.Action<string>? MuteToggled;

private void OnKickClick(object s, RoutedEventArgs e)
{
    if ((s as FrameworkElement)?.DataContext is HudMember m) KickRequested?.Invoke(m.PeerId);
}
private void OnMuteClick(object s, RoutedEventArgs e)
{
    if ((s as FrameworkElement)?.DataContext is HudMember m) MuteToggled?.Invoke(m.PeerId);
}
private void OnCopyNickClick(object s, RoutedEventArgs e)
{
    if ((s as FrameworkElement)?.DataContext is HudMember m) Clipboard.SetText(m.Nickname);
}
```

- [ ] **Step 3: Add `BroadcastLocalAsync` helper to `PartyOrchestrator`**

Add this method to `PartyOrchestrator` (in `src/GamePartyHud/Party/PartyOrchestrator.cs`):

```csharp
/// <summary>Apply a message locally and broadcast it to all peers in one call.</summary>
public Task BroadcastLocalAsync(PartyMessage msg)
{
    _state.Apply(msg, System.DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    return _net.BroadcastAsync(MessageJson.Encode(msg));
}
```

- [ ] **Step 4: Wire the kick flow in `App.xaml.cs`**

Add these lines after `_hud.Show();` in `OnStartup`:

```csharp
_hud.KickRequested += async target =>
{
    if (_orch is null || _state!.LeaderPeerId != _orch.SelfPeerId) return;
    await _orch.BroadcastLocalAsync(new KickMessage(target));
};
_hud.MuteToggled += peerId =>
{
    // Local-only: hide this peer's card. Unmuting flips it back. Not persisted.
    _sync!.ToggleMuted(peerId);
};
```

Add `using System.Linq;` at the top of `App.xaml.cs` if not already present.

- [ ] **Step 5: Build and commit**

```bash
dotnet build GamePartyHud.sln -c Debug
git add src/GamePartyHud/Hud/HudWindow.xaml src/GamePartyHud/Hud/HudWindow.xaml.cs src/GamePartyHud/Party/PartyOrchestrator.cs src/GamePartyHud/App.xaml.cs
git commit -m "feat(hud): right-click menus for kick/mute/copy-nickname"
```

---

### Task 6.6: Change nickname / role from the tray menu

Requirements items #18 and #19 say nickname and role can be edited at any time without re-running the whole calibration wizard. Add two small menu items.

**Files:**
- Modify: `src/GamePartyHud/Tray/TrayIcon.cs`
- Modify: `src/GamePartyHud/App.xaml.cs`
- Create: `src/GamePartyHud/Calibration/RenameDialog.xaml`
- Create: `src/GamePartyHud/Calibration/RenameDialog.xaml.cs`

- [ ] **Step 1: Add two new events + menu items to `TrayIcon`**

In `TrayIcon.cs`, add events and menu items:

```csharp
public event Action? ChangeNicknameRequested;
public event Action? ChangeRoleRequested;
```

In `BuildMenu`, insert right after "Calibrate character…":
```csharp
m.Items.Add("Change nickname…", null, (_, _) => ChangeNicknameRequested?.Invoke());
m.Items.Add("Change role…",     null, (_, _) => ChangeRoleRequested?.Invoke());
```

- [ ] **Step 2: Rename dialog**

`RenameDialog.xaml`:
```xml
<Window x:Class="GamePartyHud.Calibration.RenameDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Change nickname" Width="320" Height="140"
        WindowStartupLocation="CenterScreen" ResizeMode="NoResize">
    <StackPanel Margin="16">
        <TextBlock Text="Nickname:"/>
        <TextBox x:Name="Input" Margin="0,8,0,0" FontSize="14" MaxLength="32"/>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,12,0,0">
            <Button Content="Cancel" Width="70" Margin="0,0,8,0" IsCancel="True"/>
            <Button Content="Save" Width="70" IsDefault="True" Click="OnSave"/>
        </StackPanel>
    </StackPanel>
</Window>
```

`RenameDialog.xaml.cs`:
```csharp
using System.Windows;

namespace GamePartyHud.Calibration;

public partial class RenameDialog : Window
{
    public string? Value { get; private set; }
    public RenameDialog(string initial)
    {
        InitializeComponent();
        Input.Text = initial;
        Input.SelectAll();
        Input.Focus();
    }
    private void OnSave(object s, RoutedEventArgs e)
    {
        Value = Input.Text.Trim();
        DialogResult = !string.IsNullOrWhiteSpace(Value);
    }
}
```

- [ ] **Step 3: Role-picker reuses a single-combobox dialog**

Create a minimal `RolePickerDialog` alongside — same pattern:

`src/GamePartyHud/Calibration/RolePickerDialog.xaml`:
```xml
<Window x:Class="GamePartyHud.Calibration.RolePickerDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Change role" Width="280" Height="140"
        WindowStartupLocation="CenterScreen" ResizeMode="NoResize">
    <StackPanel Margin="16">
        <TextBlock Text="Role:"/>
        <ComboBox x:Name="Combo" Margin="0,8,0,0"/>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,12,0,0">
            <Button Content="Cancel" Width="70" Margin="0,0,8,0" IsCancel="True"/>
            <Button Content="Save" Width="70" IsDefault="True" Click="OnSave"/>
        </StackPanel>
    </StackPanel>
</Window>
```

`src/GamePartyHud/Calibration/RolePickerDialog.xaml.cs`:
```csharp
using System;
using System.Windows;
using GamePartyHud.Party;

namespace GamePartyHud.Calibration;

public partial class RolePickerDialog : Window
{
    public Role? Value { get; private set; }
    public RolePickerDialog(Role initial)
    {
        InitializeComponent();
        Combo.ItemsSource = Enum.GetValues<Role>();
        Combo.SelectedItem = initial;
    }
    private void OnSave(object s, RoutedEventArgs e)
    {
        Value = (Role?)Combo.SelectedItem;
        DialogResult = Value.HasValue;
    }
}
```

- [ ] **Step 4: Wire the handlers in `App.xaml.cs`**

```csharp
_tray.ChangeNicknameRequested += () =>
{
    var dlg = new RenameDialog(_config.Nickname);
    if (dlg.ShowDialog() == true && dlg.Value is { Length: > 0 } v)
    {
        _config = _config with { Nickname = v };
        _store!.Save(_config);
    }
};
_tray.ChangeRoleRequested += () =>
{
    var dlg = new RolePickerDialog(_config.Role);
    if (dlg.ShowDialog() == true && dlg.Value is { } role)
    {
        _config = _config with { Role = role };
        _store!.Save(_config);
    }
};
```

- [ ] **Step 5: Build and commit**

```bash
dotnet build GamePartyHud.sln -c Debug
git add src/GamePartyHud/Calibration/RenameDialog.xaml src/GamePartyHud/Calibration/RenameDialog.xaml.cs \
        src/GamePartyHud/Calibration/RolePickerDialog.xaml src/GamePartyHud/Calibration/RolePickerDialog.xaml.cs \
        src/GamePartyHud/Tray/TrayIcon.cs src/GamePartyHud/App.xaml.cs
git commit -m "feat(tray): change nickname and role directly from tray menu"
```

---

### Task 6.7: Three-machine manual verification

- [ ] **Step 1: Build a Release publish**

```bash
dotnet publish src/GamePartyHud/GamePartyHud.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/smoke
```

- [ ] **Step 2: Manual test matrix**

Run the published `.exe` on three Windows machines on different home internets. Verify:

- Machine 1 creates a party → short ID shows in tray tooltip.
- Machines 2 and 3 "Join party" with that ID → within 30 seconds, all three cards appear on every HUD.
- All three players calibrate their own HP bars. As each takes damage / heals, the corresponding card's bar updates across all three HUDs within 3–6 seconds.
- Machine 2 closes the app → card goes stale on 1 and 3 within 6s; disappears after 60s.
- Machine 2 reopens and rejoins → card returns.
- Leader (Machine 1) kicks Machine 3 → Machine 3's HUD shows it's been kicked; Machines 1 and 2 remove it from their view.

Write a short test report as `docs/smoke-tests/2026-04-16-first-three-machine.md` summarising the result. Commit it.

- [ ] **Step 3: Commit any bug fixes that surface**

If issues surface, fix them inline with commits scoped to the area (e.g. `fix(network): handle foo`). Do not advance to M7 until the smoke test passes end-to-end.

---

## Milestone 7 — First release (v0.1.0)

**Outcome:** A tagged `v0.1.0` GitHub release with a self-contained single-file `.exe` attached. `CHANGELOG.md` documents what ships.

### Task 7.1: Changelog

**Files:**
- Create: `CHANGELOG.md`

- [ ] **Step 1: Write the initial changelog**

```markdown
# Changelog

All notable changes to this project will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.0] — 2026-04-??

### Added
- Initial public release.
- Windows tray app (`GamePartyHud.exe`) targeting Windows 10 1903+.
- 4-step calibration wizard (HP bar region, nickname region with OCR pre-fill, role, nickname confirmation).
- Transparent always-on-top HUD with per-pixel click-through via `WM_NCHITTEST`; lock button in the top-right toggles interactivity.
- Drag-to-move the whole HUD; drag-to-swap members within the HUD.
- 6-character shareable party ID with no accounts.
- Peer-to-peer party sync over WebRTC (SIPSorcery); signaling via public BitTorrent WSS trackers with PeerJS fallback.
- 3-second HP polling (configurable 1–10s in `config.json`).
- Disconnect handling: stale after 6s (greyed), removed after 60s, auto-rejoin on reconnect.
- Leader election (earliest joiner); leader-only kick action.
- Six built-in roles: Tank, Healer, Support, Melee DPS, Ranged DPS, Utility.
- Optional `customTurnUrl` config field for users behind symmetric NATs.

### Known limitations
- Users behind symmetric NATs without a TURN URL configured cannot connect (see README for workarounds).
- Games with heavily animated or textured HP bars may need frequent re-calibration.
- Horizontal HP bars only.

[Unreleased]: https://github.com/Tosha/game-party-hud/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/Tosha/game-party-hud/releases/tag/v0.1.0
```

- [ ] **Step 2: Commit**

```bash
git add CHANGELOG.md
git commit -m "docs: add initial CHANGELOG for v0.1.0"
```

---

### Task 7.2: Verify clean build and clean tests

- [ ] **Step 1: Ensure `main` builds and tests green locally**

```bash
dotnet build GamePartyHud.sln -c Release
dotnet test GamePartyHud.sln -c Release --logger "console;verbosity=minimal"
```

Both must succeed. Any integration-test flakes must be addressed (not skipped).

- [ ] **Step 2: Verify publish works locally**

```bash
dotnet publish src/GamePartyHud/GamePartyHud.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:Version=0.1.0 -p:FileVersion=0.1.0.0 -p:AssemblyVersion=0.1.0.0 -o publish/v0.1.0
```

Run the resulting `publish/v0.1.0/GamePartyHud.exe` and smoke-test the tray menu one more time. Delete the `publish/` directory after (it's gitignored).

---

### Task 7.3: Tag and push `v0.1.0`

- [ ] **Step 1: Update the `<Version>` in the csproj to the default for the next iteration**

Edit `src/GamePartyHud/GamePartyHud.csproj` — set:
```xml
<Version>0.1.0</Version>
<FileVersion>0.1.0.0</FileVersion>
<AssemblyVersion>0.1.0.0</AssemblyVersion>
```

```bash
git add src/GamePartyHud/GamePartyHud.csproj
git commit -m "chore: bump version to 0.1.0"
git push
```

- [ ] **Step 2: Tag and push**

```bash
git tag v0.1.0 -m "v0.1.0"
git push origin v0.1.0
```

- [ ] **Step 3: Watch the release workflow**

```bash
gh run watch
```

Expected: `release` workflow completes successfully, a GitHub release named "GamePartyHud 0.1.0" is created with `GamePartyHud-0.1.0-win-x64.zip` attached. Release notes are auto-generated from the commit history since the last tag.

- [ ] **Step 4: Verify the released binary**

```bash
gh release download v0.1.0 -p "*.zip" -D /tmp/
```

Unzip, run the `.exe` on a clean Windows machine (VM is fine), walk through calibration, join a test party — it should all work identically to local Debug builds.

---

### Task 7.4: Update `CHANGELOG.md` dates and move `Unreleased`

**Files:**
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Fill in the release date**

Change `## [0.1.0] — 2026-04-??` to the actual date the release was published.

- [ ] **Step 2: Commit**

```bash
git add CHANGELOG.md
git commit -m "docs: finalize 0.1.0 release date in changelog"
git push
```

---

---

## Notes for the executing agent

- **Type reference at the top is authoritative.** Any drift from those signatures is a bug.
- **Every task ends with a commit.** Push after each milestone, not after every task, to keep the CI run rate reasonable.
- **When a step says "Run tests" and they fail,** the failing test is the spec. Either the implementation is wrong or the test encodes an outdated assumption from a previous task. Diagnose before touching either.
- **Dev harnesses (e.g. in Task 3.4) are not committed.** They live in your working copy during a single task.
- **If you can't get SIPSorcery's in-process data-channel test to pass reliably** (Task 5.7), fall back to unit-testing just the signaling handshake wiring and mark the data-channel behavior as manual-verified in M6. Do not weaken the integration test by adding broad `catch` blocks.
- **`customTurnUrl` is config-file only.** Do not add a UI field for it in v0.1.0.

---

## Definition of done (v0.1.0)

- [ ] `main` builds clean and all unit + integration tests pass on CI.
- [ ] `v0.1.0` release exists on GitHub with the self-contained `.exe` zip attached.
- [ ] Three real players on different home internets successfully run a party, see each other's HP, and the HUD respects lock/unlock, drag-to-move, drag-to-swap, and kick behaviors.
- [ ] Symmetric-NAT users receive the documented error message and can point the app at a custom TURN URL in `config.json`.
- [ ] 8-hour soak test: app runs in the background with no memory growth and no CPU creep above 1%.

---








