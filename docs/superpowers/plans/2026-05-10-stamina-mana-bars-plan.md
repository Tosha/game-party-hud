# Stamina and Mana bar tracking — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add optional Stamina and Mana bar tracking on top of the existing colour-agnostic HP pipeline, with per-sender card layouts (1–3 stacked bars with hairline dividers) and wire-/config-compatibility with peers running pre-Stam/Mana builds.

**Architecture:** Three independent capture+analyze passes per tick (gated by per-bar `BarCalibration?`), three independent smoothers, OR-ed broadcast threshold, two new nullable floats on the wire `StateMessage`, two new nullable fields on `MemberState`, two new optional `BarCalibration?` fields on `AppConfig`, and a `MemberCard.xaml` rewrite that renders a vertical stack of bars sized 1/N of the bar area's height (N = number of non-null bar percents on that sender's `MemberState`).

**Tech Stack:** .NET 8, C# 12, WPF, xUnit. No new dependencies.

**Driving spec:** [docs/superpowers/specs/2026-05-10-stamina-mana-bars-design.md](../specs/2026-05-10-stamina-mana-bars-design.md)

**Phases (each leaves a green-build, testable repo state):**
- **Phase A — Data shapes** (Tasks 1–3): TDD-driven extension of `StateMessage`/`MessageJson`, `MemberState`/`PartyState`, and `AppConfig`. After each task, build green + tests pass.
- **Phase B — Capture pipeline** (Tasks 4–5): add `BarType` enum; refactor `PartyOrchestrator` to capture, smooth, and broadcast all three bars.
- **Phase C — HUD** (Tasks 6–8): extend `HudMember`, `HudViewModelSync`, rewrite `MemberCard.xaml`, and extend the smoke harness.
- **Phase D — MainWindow UI** (Task 9): "Optional bars" section with two checkboxes + per-bar pick buttons.
- **Phase E — Manual verification** (Task 10): live-game smoke test gate.

---

## File Structure

Files touched by this plan (paths relative to repo root). Almost everything is additive — only `MemberCard.xaml` and `PartyOrchestrator.cs` get substantial rewrites.

```
src/GamePartyHud/
├── Capture/
│   └── BarType.cs                       NEW — enum BarType { Hp, Stamina, Mana }   (Task 4)
├── Party/
│   ├── PartyMessage.cs                  modify StateMessage              (Task 1)
│   ├── MessageJson.cs                   modify Encode/Decode             (Task 1)
│   ├── MemberState.cs                   add Stamina/Mana percent fields  (Task 2)
│   ├── PartyState.cs                    update Apply()                   (Task 2)
│   └── PartyOrchestrator.cs             multi-bar capture + broadcast    (Task 5)
├── Config/
│   └── AppConfig.cs                     add Stamina/Mana calibration     (Task 3)
├── Hud/
│   ├── HudMember.cs                     add Stamina/Mana percent + flags (Task 6)
│   ├── HudViewModelSync.cs              push the new fields              (Task 7)
│   ├── MemberCard.xaml                  vertical 1-3-bar layout          (Task 8)
│   ├── MemberCard.xaml.cs               (unchanged)
│   └── HudSmokeHarness.cs               new layout-coverage scenario     (Task 8)
├── MainWindow.xaml                      "Optional bars" section          (Task 9)
└── MainWindow.xaml.cs                   handler generalised on BarType   (Task 9)

tests/GamePartyHud.Tests/
├── Party/
│   ├── MessageJsonTests.cs              update existing + 3 new cases    (Task 1)
│   └── PartyStateTests.cs               update existing + 1 new case     (Task 2)
├── Config/
│   └── ConfigStoreTests.cs              update RoundTrip + new mig case  (Task 3)
└── Hud/
    └── HudMemberTests.cs                NEW — clamping + flag flips      (Task 6)
```

---

## Phase A — Data shapes

Each task in this phase is TDD: write the failing test that exercises the new behaviour, run it, add the implementation, run again, commit.

### Task 1: Extend `StateMessage` and `MessageJson` for stamina/mana

**Files:**
- Modify: `src/GamePartyHud/Party/PartyMessage.cs`
- Modify: `src/GamePartyHud/Party/MessageJson.cs`
- Modify: `tests/GamePartyHud.Tests/Party/MessageJsonTests.cs`

- [ ] **Step 1: Add the failing tests**

In `tests/GamePartyHud.Tests/Party/MessageJsonTests.cs`, **replace the existing `RoundTrip_State` and `State_NullHp_RoundTripsAsNull` tests** (the constructor signature changes) with the following block, and **append** three new tests at the end. The full revised file should look like:

```csharp
using GamePartyHud.Party;
using Xunit;

namespace GamePartyHud.Tests.Party;

public class MessageJsonTests
{
    [Fact]
    public void RoundTrip_State_AllBars()
    {
        var msg = new StateMessage("peer-1", "Yia", Role.Tank, 0.72f, 0.55f, 0.41f, 1713200000);
        var json = MessageJson.Encode(msg);
        var decoded = MessageJson.Decode(json);
        Assert.Equal(msg, decoded);
    }

    [Fact]
    public void RoundTrip_State_HpOnly_StaminaAndManaNull()
    {
        var msg = new StateMessage("peer-1", "Yia", Role.Tank, 0.72f, null, null, 1713200000);
        var json = MessageJson.Encode(msg);
        Assert.Contains("\"stamina\":null", json);
        Assert.Contains("\"mana\":null", json);
        var decoded = (StateMessage?)MessageJson.Decode(json);
        Assert.NotNull(decoded);
        Assert.Equal(0.72f, decoded!.Hp);
        Assert.Null(decoded.Stamina);
        Assert.Null(decoded.Mana);
    }

    [Fact]
    public void Decode_OldShapeJson_MissingStaminaAndMana_ParsesAsNulls()
    {
        // Wire-back-compat: a peer running a pre-Stam/Mana build emits JSON
        // without "stamina" and "mana" keys. Decoder must default them to null
        // rather than throw.
        var json = """
        {
          "type": "state",
          "peerId": "old-peer",
          "nick": "Old",
          "role": "Tank",
          "hp": 0.5,
          "t": 1234
        }
        """;
        var decoded = (StateMessage?)MessageJson.Decode(json);
        Assert.NotNull(decoded);
        Assert.Equal(0.5f, decoded!.Hp);
        Assert.Null(decoded.Stamina);
        Assert.Null(decoded.Mana);
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
    public void State_NullHp_RoundTripsAsNull()
    {
        var msg = new StateMessage("p1", "n", Role.Healer, null, null, null, 42);
        var json = MessageJson.Encode(msg);
        Assert.Contains("\"hp\":null", json);
        var decoded = (StateMessage?)MessageJson.Decode(json);
        Assert.NotNull(decoded);
        Assert.Null(decoded!.Hp);
        Assert.Null(decoded.Stamina);
        Assert.Null(decoded.Mana);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail to compile**

```bash
dotnet test --filter "FullyQualifiedName~MessageJsonTests"
```

Expected: build error — the `StateMessage` constructor no longer matches the calls (extra args). The new behaviour tests cannot run yet.

- [ ] **Step 3: Update `StateMessage`**

Replace the contents of `src/GamePartyHud/Party/PartyMessage.cs` with:

```csharp
namespace GamePartyHud.Party;

public abstract record PartyMessage;

public sealed record StateMessage(
    string PeerId,
    string Nick,
    Role Role,
    float? Hp,
    float? Stamina,
    float? Mana,
    long T) : PartyMessage;
public sealed record ByeMessage(string PeerId) : PartyMessage;
public sealed record KickMessage(string Target) : PartyMessage;
```

- [ ] **Step 4: Update `MessageJson.Encode` and `MessageJson.Decode`**

In `src/GamePartyHud/Party/MessageJson.cs`, replace the `StateMessage` arm of `Encode` (around line 21) with:

```csharp
StateMessage s => JsonSerializer.Serialize(new
{
    type = "state",
    peerId = s.PeerId,
    nick = s.Nick,
    role = s.Role,
    hp = s.Hp,
    stamina = s.Stamina,
    mana = s.Mana,
    t = s.T
}, Opts),
```

Replace the `"state"` arm of `Decode` (around line 46) with:

```csharp
"state" => new StateMessage(
    root.GetProperty("peerId").GetString() ?? "",
    root.GetProperty("nick").GetString() ?? "",
    ParseRole(root.GetProperty("role")),
    ParseNullableFloat(root.GetProperty("hp")),
    ParseOptionalNullableFloat(root, "stamina"),
    ParseOptionalNullableFloat(root, "mana"),
    root.GetProperty("t").GetInt64()),
```

Then **add** this helper at the bottom of the class, just before the closing brace:

```csharp
private static float? ParseOptionalNullableFloat(JsonElement root, string name) =>
    root.TryGetProperty(name, out var e) ? ParseNullableFloat(e) : null;
```

The existing `ParseNullableFloat(JsonElement)` already handles null vs. number; the new helper just adds "missing key → null" on top.

- [ ] **Step 5: Run the tests**

```bash
dotnet test --filter "FullyQualifiedName~MessageJsonTests"
```

Expected: all 8 tests pass.

- [ ] **Step 6: Run the full test suite to confirm no other call site broke**

```bash
dotnet test
```

Expected: there will be **failures** in `PartyStateTests` and `PartyOrchestrator` test fixtures (if any) because they construct `StateMessage` with the old 5-arg signature. **That's expected.** Note the failure list — Task 2 (`PartyState`) and Task 5 (`PartyOrchestrator`) update those construction sites. Don't try to fix them in this task.

- [ ] **Step 7: Commit**

```bash
git add src/GamePartyHud/Party/PartyMessage.cs \
        src/GamePartyHud/Party/MessageJson.cs \
        tests/GamePartyHud.Tests/Party/MessageJsonTests.cs
git commit -m "feat(wire): extend StateMessage with optional Stamina and Mana

Adds two nullable float fields (Stamina, Mana) to the wire-state message
and to MessageJson.Encode/Decode. Wire-compatible with peers running
pre-Stam/Mana builds: missing JSON keys decode as null, extra keys are
silently ignored by System.Text.Json defaults.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
"
```

---

### Task 2: Extend `MemberState` and `PartyState.Apply`

**Files:**
- Modify: `src/GamePartyHud/Party/MemberState.cs`
- Modify: `src/GamePartyHud/Party/PartyState.cs:48-72` (the `StateMessage` arm of `Apply`)
- Modify: `tests/GamePartyHud.Tests/Party/PartyStateTests.cs`

- [ ] **Step 1: Update existing tests + add the new behaviour test**

In `tests/GamePartyHud.Tests/Party/PartyStateTests.cs`, every existing `new StateMessage(peer, nick, role, hp, t)` call site has the wrong arity. **Update each** to insert two `null` arguments (Stamina, Mana) between `hp` and `t`. Then **append** this new test at the bottom of the class:

```csharp
[Fact]
public void Apply_State_PopulatesAllThreeBarFields()
{
    var s = new PartyState();
    s.Apply(new StateMessage("p1", "Yia", Role.Tank, 0.72f, 0.55f, 0.41f, 100), 100);
    var m = s.Members["p1"];
    Assert.Equal(0.72f, m.HpPercent);
    Assert.Equal(0.55f, m.StaminaPercent);
    Assert.Equal(0.41f, m.ManaPercent);
}

[Fact]
public void Apply_StateAgain_UpdatesAllThreeBars()
{
    var s = new PartyState();
    s.Apply(new StateMessage("p1", "n", Role.Tank, 0.9f, 0.8f, 0.7f, 100), 100);
    s.Apply(new StateMessage("p1", "n", Role.Tank, 0.4f, 0.3f, null, 200), 200);
    Assert.Equal(0.4f, s.Members["p1"].HpPercent);
    Assert.Equal(0.3f, s.Members["p1"].StaminaPercent);
    Assert.Null(s.Members["p1"].ManaPercent);
}
```

For example, the existing line 11:
```csharp
s.Apply(new StateMessage("p1", "n", Role.Tank, 0.9f, 100), 100);
```
becomes:
```csharp
s.Apply(new StateMessage("p1", "n", Role.Tank, 0.9f, null, null, 100), 100);
```

Apply this update pattern to every `new StateMessage(...)` site in `PartyStateTests.cs`.

- [ ] **Step 2: Run the tests to verify failure**

```bash
dotnet test --filter "FullyQualifiedName~PartyStateTests"
```

Expected: build error — `MemberState` doesn't have `StaminaPercent` / `ManaPercent` properties yet. The new behaviour test references them.

- [ ] **Step 3: Update `MemberState`**

Replace the contents of `src/GamePartyHud/Party/MemberState.cs` with:

```csharp
namespace GamePartyHud.Party;

public sealed record MemberState(
    string PeerId,
    string Nickname,
    Role Role,
    float? HpPercent,
    float? StaminaPercent,
    float? ManaPercent,
    long JoinedAtUnix,
    long LastUpdateUnix);
```

- [ ] **Step 4: Update `PartyState.Apply`**

In `src/GamePartyHud/Party/PartyState.cs`, replace the `StateMessage` arm of `Apply` (lines 53-72 of the current file) with:

```csharp
case StateMessage s:
    if (_kicked.Contains(s.PeerId)) break;
    if (_members.TryGetValue(s.PeerId, out var prev))
    {
        _members[s.PeerId] = prev with
        {
            Nickname = s.Nick,
            Role = s.Role,
            HpPercent = s.Hp,
            StaminaPercent = s.Stamina,
            ManaPercent = s.Mana,
            LastUpdateUnix = nowUnix
        };
    }
    else
    {
        _members[s.PeerId] = new MemberState(
            s.PeerId, s.Nick, s.Role, s.Hp, s.Stamina, s.Mana, nowUnix, nowUnix);
    }
    _staleSet.Remove(s.PeerId);
    changed = true;
    break;
```

The `ByeMessage` and `KickMessage` arms are unchanged.

- [ ] **Step 5: Run the tests**

```bash
dotnet test --filter "FullyQualifiedName~PartyStateTests"
```

Expected: all `PartyStateTests` pass (existing 7 plus 2 new).

- [ ] **Step 6: Run the full suite — note remaining call-site breakage**

```bash
dotnet test
```

Expected: `PartyOrchestrator` test fixtures (if any), `ConfigStoreTests`, and `MainWindow.xaml.cs` compilation may still fail because they construct `MemberState` or `StateMessage` with the old arity. The orchestrator construction in `App.xaml.cs:260` constructs `_state.Apply(new StateMessage(...))` — that's covered by Task 5. The smoke harness still works because it only sets `HpPercent` (the new fields are non-required `init`-able auto-properties through the record's constructor, default `null`). **Confirmed via Step 7.**

Wait — `MemberState` is a positional record. The smoke harness uses `new HudMember(...) { Nickname=..., HpPercent=... }` — those are `HudMember` (a class), not `MemberState`. So the smoke harness compiles fine. Confirmed.

- [ ] **Step 7: Verify build**

```bash
dotnet build
```

Expected: 0 errors. (Warnings in `MainWindow.xaml.cs` about the existing handler are also tolerable — those get fixed in Task 9.)

If `dotnet build` produces errors elsewhere, list them and STOP — those callers (probably `App.xaml.cs`'s `new StateMessage(...)` self-state call, line 150 of `PartyOrchestrator.cs`) need to be updated. The plan handles them in Task 5; if Task 2 leaves the build broken, you can either:
- Add the two `null` arguments to those callers as part of Task 2 (defer the actual logic to Task 5 — just satisfy the compiler), OR
- Skip ahead in spirit and report the issue.

Your call as the implementer. The cleanest path is to add `null, null` placeholders in `PartyOrchestrator.cs:150` and `PartyOrchestrator.cs:165` (the two `new StateMessage(...)` sites) so the build stays green between Task 2 and Task 5. Task 5 then replaces those nulls with real captured values.

- [ ] **Step 8: Commit**

```bash
git add src/GamePartyHud/Party/MemberState.cs \
        src/GamePartyHud/Party/PartyState.cs \
        src/GamePartyHud/Party/PartyOrchestrator.cs \
        tests/GamePartyHud.Tests/Party/PartyStateTests.cs
git commit -m "feat(party): extend MemberState and PartyState.Apply for stamina/mana

MemberState gains StaminaPercent and ManaPercent (both float?) and
PartyState.Apply propagates them when a StateMessage arrives. Also
threads two null placeholders through PartyOrchestrator's two
new StateMessage(...) construction sites so the build stays green;
Task 5 replaces those nulls with real captured values.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
"
```

---

### Task 3: Extend `AppConfig` schema with Stamina/Mana calibrations

**Files:**
- Modify: `src/GamePartyHud/Config/AppConfig.cs`
- Modify: `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs`

- [ ] **Step 1: Write the failing tests**

In `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs`:

**(a)** Replace the existing `RoundTrip_PreservesEverythingExceptRelayUrl` test body with one that populates all three calibrations:

```csharp
[Fact]
public void RoundTrip_PreservesEverythingExceptRelayUrl()
{
    var store = new ConfigStore(_tmp);
    var cfg = AppConfig.Defaults with
    {
        HpCalibration = new BarCalibration(
            new CaptureRegion(0, 10, 20, 300, 18),
            FillDirection.LTR),
        StaminaCalibration = new BarCalibration(
            new CaptureRegion(0, 10, 40, 300, 18),
            FillDirection.LTR),
        ManaCalibration = new BarCalibration(
            new CaptureRegion(0, 10, 60, 300, 18),
            FillDirection.LTR),
        NicknameRegion = new CaptureRegion(0, 10, 0, 300, 20),
        Nickname = "Yiawahuye",
        Role = Role.Tank,
        HudPosition = new HudPosition(500, 400, 1),
        HudLocked = false,
        LastPartyId = "X7K2P9",
        PollIntervalMs = 2500,
    };
    store.Save(cfg);
    Assert.Equal(cfg, store.Load());
}
```

**(b)** Append this new test at the bottom of the class, before its closing brace:

```csharp
[Fact]
public void Load_OldShapeConfig_MissingStaminaAndManaCalibrations_ParseAsNull()
{
    // A config.json saved before stamina/mana support contains only
    // hpCalibration. The new optional fields default to null on load
    // (System.Text.Json default unknown-field handling). Round-trip then
    // re-serialises with the two new keys present (as null) — that's
    // expected.
    File.WriteAllText(_tmp, """
{
  "hpCalibration": {
    "region": { "monitor": 0, "x": 10, "y": 20, "w": 300, "h": 18 },
    "direction": "LTR"
  },
  "nicknameRegion": null,
  "nickname": "Test",
  "role": "Tank",
  "hudPosition": { "x": 0, "y": 0, "monitor": 0 },
  "hudLocked": true,
  "lastPartyId": null,
  "pollIntervalMs": 2000,
  "relayUrl": ""
}
""");

    var loaded = new ConfigStore(_tmp).Load();
    Assert.NotNull(loaded.HpCalibration);
    Assert.Null(loaded.StaminaCalibration);
    Assert.Null(loaded.ManaCalibration);
}
```

- [ ] **Step 2: Run the tests to confirm they fail to compile**

```bash
dotnet test --filter "FullyQualifiedName~ConfigStoreTests"
```

Expected: build error — `AppConfig` doesn't have `StaminaCalibration` or `ManaCalibration` properties.

- [ ] **Step 3: Update `AppConfig`**

In `src/GamePartyHud/Config/AppConfig.cs`, modify the record signature (around line 8):

Before:
```csharp
public sealed record AppConfig(
    BarCalibration? HpCalibration,
    CaptureRegion? NicknameRegion,
    string Nickname,
    Role Role,
    HudPosition HudPosition,
    bool HudLocked,
    string? LastPartyId,
    int PollIntervalMs,
    string RelayUrl)
```

After:
```csharp
public sealed record AppConfig(
    BarCalibration? HpCalibration,
    BarCalibration? StaminaCalibration,
    BarCalibration? ManaCalibration,
    CaptureRegion? NicknameRegion,
    string Nickname,
    Role Role,
    HudPosition HudPosition,
    bool HudLocked,
    string? LastPartyId,
    int PollIntervalMs,
    string RelayUrl)
```

Update `AppConfig.Defaults` (around line 39):

```csharp
public static AppConfig Defaults { get; } = new(
    HpCalibration: null,
    StaminaCalibration: null,
    ManaCalibration: null,
    NicknameRegion: null,
    Nickname: "Player",
    Role: Role.Utility,
    HudPosition: new HudPosition(100, 100, 0),
    HudLocked: true,
    LastPartyId: null,
    PollIntervalMs: 2000,
    RelayUrl: DefaultRelayUrl);
```

- [ ] **Step 4: Run the tests**

```bash
dotnet test --filter "FullyQualifiedName~ConfigStoreTests"
```

Expected: all `ConfigStoreTests` pass (existing 5 plus 1 new).

- [ ] **Step 5: Run the full suite**

```bash
dotnet test
```

Expected: build clean (or at most the same `PartyOrchestrator`-related issues left over from Task 2; if Task 2 was committed cleanly they should be gone).

- [ ] **Step 6: Commit**

```bash
git add src/GamePartyHud/Config/AppConfig.cs \
        tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs
git commit -m "feat(config): add optional Stamina and Mana BarCalibration fields

AppConfig gains two nullable BarCalibration fields. Old configs missing
the new keys deserialize cleanly (System.Text.Json defaults unknown
fields to null). Round-trip test extended to cover all three.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
"
```

---

## Phase B — Capture pipeline

### Task 4: Add `BarType` enum

A small standalone task because `BarType` is referenced from both `PartyOrchestrator` (Task 5) and `MainWindow.xaml.cs` (Task 9). Landing it first lets both depend on a stable type.

**Files:**
- Create: `src/GamePartyHud/Capture/BarType.cs`

- [ ] **Step 1: Create the file**

Write `src/GamePartyHud/Capture/BarType.cs`:

```csharp
namespace GamePartyHud.Capture;

/// <summary>
/// The three bar types the app can track. <see cref="Hp"/> is required for joining
/// a party; <see cref="Stamina"/> and <see cref="Mana"/> are optional. Used by the
/// calibration wizard (per-bar prompt and config field selection) and by
/// PartyOrchestrator's diagnostic logging.
/// </summary>
public enum BarType { Hp, Stamina, Mana }
```

- [ ] **Step 2: Build**

```bash
dotnet build
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 3: Commit**

```bash
git add src/GamePartyHud/Capture/BarType.cs
git commit -m "feat(capture): introduce BarType enum (Hp, Stamina, Mana)

Used by the calibration wizard handler (Task 9) to select which
BarCalibration field to write, and by PartyOrchestrator's diagnostic
logging (Task 5) to label per-bar reads.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
"
```

---

### Task 5: Refactor `PartyOrchestrator` for multi-bar capture and broadcast

This is the largest single task in the plan. The shape of the change is:
- Three independent smoothers (one per bar type), gated on each `BarCalibration` being non-null.
- Three captures + analyzes per tick (sequential, cheap).
- Renamed constant `HpChangeThreshold` → `BarChangeThreshold`.
- Three "last broadcast" tracking fields.
- OR-ed change detection across all three bars.
- `LogTick` extended to a per-bar loop.

**Files:**
- Modify: `src/GamePartyHud/Party/PartyOrchestrator.cs`

This task has no new unit tests — `PartyOrchestrator` is the integration loop. Its inputs and outputs are tested by the surrounding pieces (Tasks 1-3 cover wire/state/config; Task 8 smoke harness covers visual output). Manual verification (Task 10) is the end-to-end gate.

- [ ] **Step 1: Replace the field declarations**

In `src/GamePartyHud/Party/PartyOrchestrator.cs`, find the field block (around lines 24-52) and replace it with:

```csharp
// Bar changes smaller than this don't justify a network broadcast — the
// visual delta on a 170-px HUD bar is sub-pixel. Receivers learn about
// the new value at the next ≥ 1 % move on any bar or the next heartbeat,
// whichever comes first.
private const float BarChangeThreshold = 0.01f;

// Maximum gap between broadcasts during steady state (HP/role/nickname
// unchanged). Must stay shorter than PartyState.StaleAfterSec or
// recipients will mark live peers stale during quiet periods.
private static readonly TimeSpan BroadcastHeartbeat = TimeSpan.FromSeconds(15);

private readonly IScreenCapture _capture;
private readonly BarAnalyzer _analyzer = new();
private readonly BarSmoother _hpSmoother = new(windowSize: 3);
private readonly BarSmoother _staminaSmoother = new(windowSize: 3);
private readonly BarSmoother _manaSmoother = new(windowSize: 3);
private readonly PartyState _state;
private readonly RelayClient _net;
// _cfg is mutable so that nickname / role / poll-interval / calibration
// changes from the UI propagate into the broadcast loop without
// recreating the orchestrator. Updated via <see cref="UpdateConfig"/>.
private AppConfig _cfg;
private readonly string _selfPeerId;
private readonly long _joinedAt;
private CancellationTokenSource? _loopCts;

private int _tickCounter;

// Last-broadcast snapshot, for delta detection. _lastBroadcastAtUnix
// starts at 0 so the very first tick is always heartbeat-due, ensuring
// peers learn we exist as soon as we join.
private float? _lastBroadcastHp;
private float? _lastBroadcastStamina;
private float? _lastBroadcastMana;
private string _lastBroadcastNick = "";
private Role _lastBroadcastRole = default;
private long _lastBroadcastAtUnix;
```

- [ ] **Step 2: Replace `PollAndBroadcastLoopAsync`**

Find the method `PollAndBroadcastLoopAsync` (around lines 126-184) and replace its body with:

```csharp
private async Task PollAndBroadcastLoopAsync(CancellationToken ct)
{
    // Deterministic per-peer jitter (0–250 ms) so 20 peers don't all broadcast on the same boundary.
    int jitter = Math.Abs(_selfPeerId.GetHashCode()) % 250;

    while (!ct.IsCancellationRequested)
    {
        try
        {
            float? hp = await ReadBarAsync(_cfg.HpCalibration, _hpSmoother, ct).ConfigureAwait(false);
            float? stamina = await ReadBarAsync(_cfg.StaminaCalibration, _staminaSmoother, ct).ConfigureAwait(false);
            float? mana = await ReadBarAsync(_cfg.ManaCalibration, _manaSmoother, ct).ConfigureAwait(false);

            LogTick(hp, stamina, mana);

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // Apply our own state locally so our card shows up on our HUD too.
            // This always runs, even when we suppress the network broadcast —
            // local applies are free and keep the self card refreshed.
            _state.Apply(new StateMessage(_selfPeerId, _cfg.Nickname, _cfg.Role, hp, stamina, mana, now), now);

            // Decide whether to actually broadcast to peers. Each WebSocket
            // message costs one relay request inbound here PLUS one per
            // recipient on the fan-out side, so suppressing no-op
            // broadcasts (all bars unchanged within threshold, role/nick same
            // as last sent) compounds with party size. A heartbeat enforces a
            // floor so receivers don't mark us stale during quiet stretches.
            bool barChanged =
                   !ApproxEqual(hp,      _lastBroadcastHp,      BarChangeThreshold)
                || !ApproxEqual(stamina, _lastBroadcastStamina, BarChangeThreshold)
                || !ApproxEqual(mana,    _lastBroadcastMana,    BarChangeThreshold);
            bool nickChanged = _cfg.Nickname != _lastBroadcastNick;
            bool roleChanged = _cfg.Role != _lastBroadcastRole;
            bool heartbeatDue = (now - _lastBroadcastAtUnix) >= (long)BroadcastHeartbeat.TotalSeconds;

            if (barChanged || nickChanged || roleChanged || heartbeatDue)
            {
                var json = MessageJson.Encode(new StateMessage(_selfPeerId, _cfg.Nickname, _cfg.Role, hp, stamina, mana, now));
                await _net.BroadcastAsync(json).ConfigureAwait(false);
                _lastBroadcastHp = hp;
                _lastBroadcastStamina = stamina;
                _lastBroadcastMana = mana;
                _lastBroadcastNick = _cfg.Nickname;
                _lastBroadcastRole = _cfg.Role;
                _lastBroadcastAtUnix = now;
            }
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex)
        {
            // Keep looping — transient errors during capture/broadcast shouldn't kill the party,
            // but they should show up in the log so we can diagnose.
            Log.Error("PartyOrchestrator: capture/broadcast tick failed; continuing.", ex);
        }

        try { await Task.Delay(_cfg.PollIntervalMs + jitter, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { break; }
    }
}

/// <summary>
/// Capture, analyze, and smooth a single bar. Returns null if no calibration is
/// set for this bar (the caller broadcasts null in that field, which receivers
/// render as "this peer doesn't track that bar").
/// </summary>
private async Task<float?> ReadBarAsync(BarCalibration? cal, BarSmoother smoother, CancellationToken ct)
{
    if (cal is null) return null;
    var bgra = await _capture.CaptureBgraAsync(cal.Region, ct).ConfigureAwait(false);
    float raw = _analyzer.Analyze(bgra, cal.Region.W, cal.Region.H, cal);
    return smoother.Push(raw);
}
```

- [ ] **Step 3: Replace `LogTick`**

Find the existing `LogTick` method (around lines 186-220) and replace it with:

```csharp
private void LogTick(float? hp, float? stamina, float? mana)
{
    _tickCounter++;
    Log.Info(
        $"PartyOrchestrator tick#{_tickCounter}: " +
        $"hp={FormatBar(hp)} stamina={FormatBar(stamina)} mana={FormatBar(mana)}");
}

private static string FormatBar(float? value) =>
    value is { } v ? v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "n/a";
```

The previous version's per-column histogram (`bar/partial/missing` counts) and mid-HSV diagnostic are dropped. They were useful when validating the missing-pixel algorithm against unfamiliar real captures; with the algorithm now landed and validated against three different bar colours, a per-tick smoothed-value summary is sufficient. Removing the heavy diagnostic also simplifies the multi-bar log.

If you want to keep the deeper diagnostic for future regressions, leave a single static helper (`LogPerColumnHistogram(BarCalibration, byte[])`) commented out at the bottom of the file with a `// TODO: re-enable for capture debugging` note. Optional.

- [ ] **Step 4: Verify the imports at the top of the file are still correct**

The previous version used `CaptureDiagnostic.AverageHsv(...)`. After Step 3, that call is gone, but the `using GamePartyHud.Diagnostics;` import (around line 5) may now be unused. **Leave the using directive in place** — `Log.Info`/`Log.Error`/`Log.Warn` calls still depend on it (they're under `GamePartyHud.Diagnostics`).

- [ ] **Step 5: Build**

```bash
dotnet build
```

Expected: 0 errors, 0 warnings. If there are warnings about unused usings, remove them.

- [ ] **Step 6: Run the full test suite**

```bash
dotnet test
```

Expected: 0 failures. The orchestrator change is exercised end-to-end through `PartyState`, `MessageJson`, and `BarAnalyzer` — all already tested at the unit level.

- [ ] **Step 7: Commit**

```bash
git add src/GamePartyHud/Party/PartyOrchestrator.cs
git commit -m "feat(party): orchestrator captures hp/stamina/mana per tick

Three independent BarSmoother instances, three sequential capture+analyze
passes per tick (each gated on its calibration being non-null), and
broadcast triggered on any bar moving >= 1% (or nick/role/heartbeat).
Renamed HpChangeThreshold to BarChangeThreshold. LogTick collapsed to
a per-bar smoothed-value summary; the per-column missing/partial/bar
histogram from the redesign is dropped now that the algorithm is
validated.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
"
```

---

## Phase C — HUD

### Task 6: Extend `HudMember` with Stamina, Mana, and visibility flags

**Files:**
- Modify: `src/GamePartyHud/Hud/HudMember.cs`
- Create: `tests/GamePartyHud.Tests/Hud/HudMemberTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/GamePartyHud.Tests/Hud/HudMemberTests.cs`:

```csharp
using System.ComponentModel;
using System.Collections.Generic;
using GamePartyHud.Hud;
using Xunit;

namespace GamePartyHud.Tests.Hud;

public class HudMemberTests
{
    [Fact]
    public void StaminaPercent_DefaultsToNull_AndHasStaminaIsFalse()
    {
        var m = new HudMember("p1");
        Assert.Null(m.StaminaPercent);
        Assert.False(m.HasStamina);
    }

    [Fact]
    public void ManaPercent_DefaultsToNull_AndHasManaIsFalse()
    {
        var m = new HudMember("p1");
        Assert.Null(m.ManaPercent);
        Assert.False(m.HasMana);
    }

    [Fact]
    public void SettingStaminaPercent_RaisesPropertyChangedAndFlipsHasStamina()
    {
        var m = new HudMember("p1");
        var changes = new List<string>();
        m.PropertyChanged += (_, e) => changes.Add(e.PropertyName ?? "");

        m.StaminaPercent = 0.5f;

        Assert.Equal(0.5f, m.StaminaPercent);
        Assert.True(m.HasStamina);
        Assert.Contains(nameof(HudMember.StaminaPercent), changes);
        Assert.Contains(nameof(HudMember.HasStamina), changes);
    }

    [Fact]
    public void ClearingStaminaPercent_RaisesPropertyChangedAndFlipsHasStamina()
    {
        var m = new HudMember("p1") { StaminaPercent = 0.5f };
        var changes = new List<string>();
        m.PropertyChanged += (_, e) => changes.Add(e.PropertyName ?? "");

        m.StaminaPercent = null;

        Assert.Null(m.StaminaPercent);
        Assert.False(m.HasStamina);
        Assert.Contains(nameof(HudMember.StaminaPercent), changes);
        Assert.Contains(nameof(HudMember.HasStamina), changes);
    }

    [Fact]
    public void SettingManaPercent_RaisesPropertyChangedAndFlipsHasMana()
    {
        var m = new HudMember("p1");
        var changes = new List<string>();
        m.PropertyChanged += (_, e) => changes.Add(e.PropertyName ?? "");

        m.ManaPercent = 0.5f;

        Assert.Equal(0.5f, m.ManaPercent);
        Assert.True(m.HasMana);
        Assert.Contains(nameof(HudMember.ManaPercent), changes);
        Assert.Contains(nameof(HudMember.HasMana), changes);
    }

    [Theory]
    [InlineData(-0.1f, 0f)]
    [InlineData(0f, 0f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(1f, 1f)]
    [InlineData(1.5f, 1f)]
    public void StaminaPercent_ClampsToZeroOne(float input, float expected)
    {
        var m = new HudMember("p1") { StaminaPercent = input };
        Assert.Equal(expected, m.StaminaPercent);
    }

    [Theory]
    [InlineData(-0.1f, 0f)]
    [InlineData(0f, 0f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(1f, 1f)]
    [InlineData(1.5f, 1f)]
    public void ManaPercent_ClampsToZeroOne(float input, float expected)
    {
        var m = new HudMember("p1") { ManaPercent = input };
        Assert.Equal(expected, m.ManaPercent);
    }
}
```

- [ ] **Step 2: Run the tests to verify failure**

```bash
dotnet test --filter "FullyQualifiedName~HudMemberTests"
```

Expected: build error — `HudMember` doesn't have `StaminaPercent`, `ManaPercent`, `HasStamina`, or `HasMana`.

- [ ] **Step 3: Update `HudMember`**

In `src/GamePartyHud/Hud/HudMember.cs`, add the following just below the existing `HpPercent` property block (after line 59):

```csharp
private float? _staminaPercent;
public float? StaminaPercent
{
    get => _staminaPercent;
    set
    {
        float? clamped = value is { } v ? Math.Clamp(v, 0f, 1f) : (float?)null;
        if (_staminaPercent != clamped)
        {
            bool hadValue = _staminaPercent.HasValue;
            _staminaPercent = clamped;
            Raise(nameof(StaminaPercent));
            if (hadValue != clamped.HasValue) Raise(nameof(HasStamina));
        }
    }
}
public bool HasStamina => _staminaPercent.HasValue;

private float? _manaPercent;
public float? ManaPercent
{
    get => _manaPercent;
    set
    {
        float? clamped = value is { } v ? Math.Clamp(v, 0f, 1f) : (float?)null;
        if (_manaPercent != clamped)
        {
            bool hadValue = _manaPercent.HasValue;
            _manaPercent = clamped;
            Raise(nameof(ManaPercent));
            if (hadValue != clamped.HasValue) Raise(nameof(HasMana));
        }
    }
}
public bool HasMana => _manaPercent.HasValue;
```

- [ ] **Step 4: Run the tests**

```bash
dotnet test --filter "FullyQualifiedName~HudMemberTests"
```

Expected: all 9 tests pass.

- [ ] **Step 5: Run full suite**

```bash
dotnet test
```

Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add src/GamePartyHud/Hud/HudMember.cs \
        tests/GamePartyHud.Tests/Hud/HudMemberTests.cs
git commit -m "feat(hud): add Stamina/Mana percent and visibility flags to HudMember

Two new nullable float properties (StaminaPercent, ManaPercent) with
[0..1] clamping (mirroring HpPercent). HasStamina / HasMana derived
boolean flags raise PropertyChanged when their underlying value
transitions in or out of null, so the bar rows in MemberCard.xaml can
bind their Visibility directly.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
"
```

---

### Task 7: Update `HudViewModelSync` to push the new fields

**Files:**
- Modify: `src/GamePartyHud/Hud/HudViewModelSync.cs:53-56`

This is a small additive change — testing it cleanly requires either a running WPF dispatcher or a refactor to expose the private `Sync()` method. Skipping unit tests; visual verification via the smoke harness (Task 8) and manual game session (Task 10).

- [ ] **Step 1: Add the new lines**

In `src/GamePartyHud/Hud/HudViewModelSync.cs`, find the inner foreach loop (around lines 44-57). The existing block:

```csharp
existing.Nickname = m.Nickname;
existing.Role = m.Role;
existing.HpPercent = m.HpPercent ?? 0f;
existing.IsStale = _state.IsStale(m, now);
```

Becomes:

```csharp
existing.Nickname = m.Nickname;
existing.Role = m.Role;
existing.HpPercent = m.HpPercent ?? 0f;
existing.StaminaPercent = m.StaminaPercent;
existing.ManaPercent = m.ManaPercent;
existing.IsStale = _state.IsStale(m, now);
```

Note: `HpPercent` collapses null → `0f` because the existing renderer always shows the HP bar; `StaminaPercent` and `ManaPercent` pass null through so the rows can collapse via `HasStamina` / `HasMana`.

- [ ] **Step 2: Build**

```bash
dotnet build
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 3: Run full suite**

```bash
dotnet test
```

Expected: all green.

- [ ] **Step 4: Commit**

```bash
git add src/GamePartyHud/Hud/HudViewModelSync.cs
git commit -m "feat(hud): sync StaminaPercent and ManaPercent into HudMember

HudViewModelSync.Sync() now copies the two new MemberState fields
through to the HudMember view-model so the bar rows in MemberCard.xaml
can render and toggle visibility based on the sender's data.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
"
```

---

### Task 8: Rewrite `MemberCard.xaml` for the 1–3 bar layout + extend the smoke harness

This is the visual heart of the feature. The existing single-bar XAML becomes a vertical 3-row Grid with hairline dividers; rows 2 and 3 collapse based on `HasStamina` / `HasMana`. The smoke harness gains a layout-coverage scenario.

**Files:**
- Modify: `src/GamePartyHud/Hud/MemberCard.xaml`
- Modify: `src/GamePartyHud/Hud/HudSmokeHarness.cs`

- [ ] **Step 1: Rewrite `MemberCard.xaml`**

Replace the contents of `src/GamePartyHud/Hud/MemberCard.xaml` with:

```xml
<UserControl x:Class="GamePartyHud.Hud.MemberCard"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Width="200" Height="24"
             Background="Transparent"
             FontFamily="Segoe UI Variable, Segoe UI">
    <UserControl.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis"/>
    </UserControl.Resources>
    <Border Padding="2,1" CornerRadius="2" Opacity="{Binding Opacity}"
            BorderBrush="#33FFFFFF" BorderThickness="1">
        <Border.Background>
            <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
                <GradientStop Color="#66262629" Offset="0"/>
                <GradientStop Color="#661C1C20" Offset="1"/>
            </LinearGradientBrush>
        </Border.Background>
        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>

            <!-- Role glyph tile -->
            <Border Grid.Column="0" Width="18" Height="18" Margin="0,0,4,0"
                    VerticalAlignment="Center"
                    CornerRadius="2"
                    BorderBrush="{Binding RoleBorderBrush}" BorderThickness="1"
                    Background="{Binding RoleBackgroundBrush}">
                <TextBlock Text="{Binding RoleGlyph}"
                           Foreground="#FFF2F2F2"
                           FontSize="10"
                           FontWeight="SemiBold"
                           HorizontalAlignment="Center"
                           VerticalAlignment="Center"/>
            </Border>

            <!-- Bar block: 1-3 stacked bars + hairline dividers + nickname overlay -->
            <Grid Grid.Column="1" Width="174" Height="22" VerticalAlignment="Center">
                <!-- Outer empty track + border (hosts all bars under one rounded frame) -->
                <Border CornerRadius="2"
                        BorderBrush="#44000000" BorderThickness="1">
                    <Border.Background>
                        <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
                            <GradientStop Color="#661A1A1C" Offset="0"/>
                            <GradientStop Color="#66111113" Offset="1"/>
                        </LinearGradientBrush>
                    </Border.Background>
                </Border>

                <!-- Stacked rows. RowDefinitions all use Height="*" so visible
                     rows split the area equally; collapsed rows contribute 0. -->
                <Grid>
                    <Grid.RowDefinitions>
                        <RowDefinition Height="*"/>
                        <RowDefinition Height="*"/>
                        <RowDefinition Height="*"/>
                    </Grid.RowDefinitions>

                    <!-- Row 0: HP (always visible) -->
                    <Grid Grid.Row="0">
                        <Border HorizontalAlignment="Left"
                                Width="{Binding HpPercent, Converter={StaticResource HpWidthConverter}, ConverterParameter=174}">
                            <Border.Background>
                                <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
                                    <GradientStop Color="#FFB91C1C" Offset="0"/>
                                    <GradientStop Color="#FF7F1D1D" Offset="1"/>
                                </LinearGradientBrush>
                            </Border.Background>
                        </Border>
                        <Border HorizontalAlignment="Left"
                                Height="2" VerticalAlignment="Top"
                                Margin="1,1,0,0"
                                Background="#33FFFFFF"
                                Width="{Binding HpPercent, Converter={StaticResource HpWidthConverter}, ConverterParameter=172}"/>
                    </Grid>

                    <!-- Row 1: Stamina (collapsed when HasStamina = false) -->
                    <Grid Grid.Row="1" Visibility="{Binding HasStamina, Converter={StaticResource BoolToVis}}">
                        <!-- Hairline divider on top edge -->
                        <Border Height="1" VerticalAlignment="Top" Background="#88000000"/>
                        <Border HorizontalAlignment="Left"
                                Margin="0,1,0,0"
                                Width="{Binding StaminaPercent, Converter={StaticResource HpWidthConverter}, ConverterParameter=174}">
                            <Border.Background>
                                <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
                                    <GradientStop Color="#FFFCD34D" Offset="0"/>
                                    <GradientStop Color="#FFCA8A04" Offset="1"/>
                                </LinearGradientBrush>
                            </Border.Background>
                        </Border>
                    </Grid>

                    <!-- Row 2: Mana (collapsed when HasMana = false) -->
                    <Grid Grid.Row="2" Visibility="{Binding HasMana, Converter={StaticResource BoolToVis}}">
                        <Border Height="1" VerticalAlignment="Top" Background="#88000000"/>
                        <Border HorizontalAlignment="Left"
                                Margin="0,1,0,0"
                                Width="{Binding ManaPercent, Converter={StaticResource HpWidthConverter}, ConverterParameter=174}">
                            <Border.Background>
                                <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
                                    <GradientStop Color="#FF2563EB" Offset="0"/>
                                    <GradientStop Color="#FF1E3A8A" Offset="1"/>
                                </LinearGradientBrush>
                            </Border.Background>
                        </Border>
                    </Grid>
                </Grid>

                <!-- Nickname overlay spans the entire bar block; drop-shadow keeps it legible
                     across all colour bands. -->
                <TextBlock Text="{Binding Nickname}"
                           Foreground="#FFF2F2F2"
                           FontSize="11" FontWeight="SemiBold"
                           VerticalAlignment="Center"
                           HorizontalAlignment="Left"
                           Margin="6,0,6,0"
                           TextTrimming="CharacterEllipsis">
                    <TextBlock.Effect>
                        <DropShadowEffect Color="Black" BlurRadius="3"
                                          ShadowDepth="0" Opacity="1.0"/>
                    </TextBlock.Effect>
                </TextBlock>
            </Grid>
        </Grid>
    </Border>
</UserControl>
```

The `HpWidthConverter` is reused for all three bars (`{StaticResource HpWidthConverter}` is defined globally — verify the resource is in `App.xaml`; if not, add a local resource declaration in `MemberCard.xaml`'s `<UserControl.Resources>` block).

- [ ] **Step 2: Verify `HpWidthConverter` is reachable as a static resource**

Search the codebase for the existing `HpWidthConverter` resource declaration:

```
Grep pattern: HpWidthConverter, glob: src/**/*.xaml
```

If you find a `<local:HpWidthConverter x:Key="HpWidthConverter"/>` declaration in `App.xaml` or a parent XAML, you're done. If you can't find one, add it to the `<UserControl.Resources>` block of `MemberCard.xaml`:

```xml
<UserControl.Resources>
    <hud:HpWidthConverter x:Key="HpWidthConverter"/>
    <BooleanToVisibilityConverter x:Key="BoolToVis"/>
</UserControl.Resources>
```

(Add `xmlns:hud="clr-namespace:GamePartyHud.Hud"` to the `<UserControl>` element if it isn't there yet.)

- [ ] **Step 3: Update `HudSmokeHarness.cs`**

Replace the contents of `src/GamePartyHud/Hud/HudSmokeHarness.cs` with:

```csharp
#if DEBUG
using System.Windows;
using GamePartyHud.Party;

namespace GamePartyHud.Hud;

/// <summary>
/// Debug-only manual smoke harness for the HUD window. Invoked from <see cref="App"/>
/// when the process is launched with <c>--hud-smoke</c> (optionally
/// <c>--hud-smoke=N</c> to seed N members). Removed from Release builds by the
/// <c>#if DEBUG</c> guard; not part of the shipped app.
/// </summary>
internal static class HudSmokeHarness
{
    public const string CliFlag = "--hud-smoke";

    // Each seed describes a member's nick, role, and bar values. Stamina/Mana
    // are nullable so we can mock all four card-layout configurations:
    //   HP only            (Stam = null, Mana = null)
    //   HP + Stamina       (Mana = null)
    //   HP + Mana          (Stam = null)
    //   HP + Stamina + Mana
    //
    // First six entries cover the six roles. Layout-coverage entries 7-10
    // exercise each card configuration so a launch with `--hud-smoke=10`
    // shows all four layouts on a single screen.
    private static readonly (string Nick, Role Role, float Hp, float? Stamina, float? Mana, bool Stale)[] Seeds =
    {
        ("Yiawahuye",    Role.Tank,      0.72f, null,  null,  false),
        ("Kyrele",       Role.Healer,    1.00f, null,  null,  false),
        ("Stelis",       Role.Support,   0.66f, null,  null,  false),
        ("Arakh",        Role.MeleeDps,  0.30f, null,  null,  true),
        ("Thal",         Role.RangedDps, 0.85f, null,  null,  false),
        ("Riven",        Role.Utility,   0.50f, null,  null,  false),
        // Layout coverage:
        ("HpOnly",       Role.Tank,      0.55f, null,  null,  false),
        ("HpStam",       Role.MeleeDps,  0.80f, 0.40f, null,  false),
        ("HpMana",       Role.Healer,    0.90f, null,  0.60f, false),
        ("HpStamMana",   Role.Support,   0.65f, 0.30f, 0.45f, false),
        // Filler (HP-only, kept for backwards-compat with --hud-smoke=20+):
        ("StupidBeast",  Role.Tank,      0.55f, null,  null,  false),
        ("Barrakh",      Role.MeleeDps,  0.10f, null,  null,  false),
        ("ShalfeyHealz", Role.Healer,    0.95f, null,  null,  false),
        ("AboutFeeder",  Role.RangedDps, 0.68f, null,  null,  false),
        ("MinSu",        Role.MeleeDps,  0.40f, null,  null,  false),
        ("Gosling",      Role.RangedDps, 0.78f, null,  null,  false),
        ("YaGood",       Role.Tank,      1.00f, null,  null,  false),
        ("TyZok",        Role.MeleeDps,  0.22f, null,  null,  false),
        ("Aggressor",    Role.MeleeDps,  0.50f, null,  null,  false),
        ("Mir",          Role.Healer,    0.88f, null,  null,  false),
        ("Tomodo",       Role.RangedDps, 0.15f, null,  null,  true),
        ("GLIST",        Role.Tank,      0.61f, null,  null,  false),
    };

    public static void Run(Application app, int count = 4)
    {
        int n = System.Math.Clamp(count, 1, Seeds.Length);
        var hud = new HudWindow();
        for (int i = 0; i < n; i++)
        {
            var (nick, role, hp, stamina, mana, stale) = Seeds[i];
            hud.MemberList.Add(new HudMember($"p{i + 1}")
            {
                Nickname = nick,
                Role = role,
                HpPercent = hp,
                StaminaPercent = stamina,
                ManaPercent = mana,
                IsStale = stale,
            });
        }
        hud.Closed += (_, _) => app.Shutdown();
        hud.Show();
    }
}
#endif
```

- [ ] **Step 4: Build**

```bash
dotnet build
```

Expected: 0 errors. (Warnings about XAML resource references that can't be resolved at compile-time would point to a misconfigured `HpWidthConverter` — fix per Step 2.)

- [ ] **Step 5: Run full test suite**

```bash
dotnet test
```

Expected: all green.

- [ ] **Step 6: Visual smoke verification**

This is the first task that requires a visible HUD render. Run:

```bash
dotnet run --project src/GamePartyHud -c Debug -- --hud-smoke=10
```

Expected behavior:
- A small translucent window appears top-left with 10 member cards stacked.
- Cards 1–6 show one bar each (the existing HP-only look).
- Card 7 (`HpOnly`): one HP bar.
- Card 8 (`HpStam`): two bars — HP top, Stamina bottom — with a visible 1-px hairline between them. Stamina row uses the amber gradient.
- Card 9 (`HpMana`): two bars — HP top, Mana bottom (Mana stays in the bottom slot). Mana row uses the blue gradient.
- Card 10 (`HpStamMana`): three bars — HP, Stamina, Mana, top-to-bottom. Two hairlines.
- Nicknames remain legible across all colour bands.

Close the HUD window to exit (the harness wires `Closed → Shutdown`).

If something looks wrong (missing bar, divider in the wrong place, colour off, nickname unreadable on the amber band), fix the XAML before committing.

- [ ] **Step 7: Commit**

```bash
git add src/GamePartyHud/Hud/MemberCard.xaml \
        src/GamePartyHud/Hud/HudSmokeHarness.cs
git commit -m "feat(hud): MemberCard renders 1-3 stacked bars per sender

Vertical 3-row Grid where rows 2 (Stamina) and 3 (Mana) collapse via
HasStamina / HasMana. Hairline divider (1px, semi-transparent black)
between adjacent visible rows. Colour palette: HP red-700/900 (existing),
Stamina amber-300/600, Mana blue-600/900. Nickname overlay drop-shadow
keeps text legible across all bands.

HudSmokeHarness gains four layout-coverage entries so launching with
--hud-smoke=10 visually exercises all four card configurations
(HP-only, HP+Sta, HP+Mana, HP+Sta+Mana).

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
"
```

---

## Phase D — MainWindow UI

### Task 9: "Optional bars" section + handler generalised on BarType

The existing HP-region UI (lines 52-78 of `MainWindow.xaml`) and its handler (`OnPickRegion`, lines ~173-212 of `MainWindow.xaml.cs`) are unchanged. We add a new section directly below the HP region, before the "Party" section, with two checkboxes that gate per-bar pick buttons.

**Files:**
- Modify: `src/GamePartyHud/MainWindow.xaml`
- Modify: `src/GamePartyHud/MainWindow.xaml.cs`

This task has no automated tests — XAML rendering and event handlers are manually verified via Task 10 (live game session).

- [ ] **Step 1: Add the "Optional bars" XAML section**

In `src/GamePartyHud/MainWindow.xaml`, find the line with `<!-- Party ─────────...` separator (around line 80). **Insert** the following block immediately above that comment:

```xml
                <!-- Optional bars ───────────────────────────────────────── -->
                <Separator Margin="0,28,0,0" Opacity="0.3"/>
                <TextBlock Text="Optional bars"
                           FontSize="18" FontWeight="SemiBold" Margin="0,20,0,12"/>
                <TextBlock Opacity="0.75" Margin="0,0,0,10" TextWrapping="Wrap">
                    Track stamina and mana too — picked up the same way, sent to your
                    teammates alongside HP, and shown as additional bars on each card.
                </TextBlock>

                <CheckBox x:Name="IncludeStaminaCheck"
                          Content="Include stamina"
                          Margin="0,0,0,4"
                          Checked="OnIncludeStaminaChecked"
                          Unchecked="OnIncludeStaminaUnchecked"/>
                <StackPanel x:Name="StaminaPickRow"
                            Orientation="Horizontal"
                            Margin="20,0,0,12"
                            Visibility="Collapsed">
                    <ui:Button x:Name="PickStaminaRegionButton"
                               Appearance="Secondary"
                               Icon="Target24"
                               Content="Pick stamina bar region"
                               Tag="Stamina"
                               Click="OnPickRegion"/>
                    <Border x:Name="StaminaStatusChip"
                            VerticalAlignment="Center" Margin="12,0,0,0"
                            Padding="10,4"
                            CornerRadius="10"
                            Background="#22FFFFFF"
                            BorderBrush="#33FFFFFF" BorderThickness="1">
                        <StackPanel Orientation="Horizontal">
                            <TextBlock x:Name="StaminaStatusIcon"
                                       Text="○" FontSize="12" FontWeight="Bold"
                                       VerticalAlignment="Center"/>
                            <TextBlock x:Name="StaminaStatus"
                                       VerticalAlignment="Center" Margin="6,0,0,0"
                                       TextWrapping="Wrap" MaxWidth="320"/>
                        </StackPanel>
                    </Border>
                </StackPanel>

                <CheckBox x:Name="IncludeManaCheck"
                          Content="Include mana"
                          Margin="0,0,0,4"
                          Checked="OnIncludeManaChecked"
                          Unchecked="OnIncludeManaUnchecked"/>
                <StackPanel x:Name="ManaPickRow"
                            Orientation="Horizontal"
                            Margin="20,0,0,12"
                            Visibility="Collapsed">
                    <ui:Button x:Name="PickManaRegionButton"
                               Appearance="Secondary"
                               Icon="Target24"
                               Content="Pick mana bar region"
                               Tag="Mana"
                               Click="OnPickRegion"/>
                    <Border x:Name="ManaStatusChip"
                            VerticalAlignment="Center" Margin="12,0,0,0"
                            Padding="10,4"
                            CornerRadius="10"
                            Background="#22FFFFFF"
                            BorderBrush="#33FFFFFF" BorderThickness="1">
                        <StackPanel Orientation="Horizontal">
                            <TextBlock x:Name="ManaStatusIcon"
                                       Text="○" FontSize="12" FontWeight="Bold"
                                       VerticalAlignment="Center"/>
                            <TextBlock x:Name="ManaStatus"
                                       VerticalAlignment="Center" Margin="6,0,0,0"
                                       TextWrapping="Wrap" MaxWidth="320"/>
                        </StackPanel>
                    </Border>
                </StackPanel>
```

Also **add a `Tag="Hp"` attribute** to the existing HP pick button (around line 60), so the generalised handler can dispatch on tag:

Before:
```xml
<ui:Button x:Name="PickRegionButton"
           Appearance="Primary"
           Icon="Target24"
           Content="Pick HP bar region"
           Click="OnPickRegion"/>
```

After:
```xml
<ui:Button x:Name="PickRegionButton"
           Appearance="Primary"
           Icon="Target24"
           Content="Pick HP bar region"
           Tag="Hp"
           Click="OnPickRegion"/>
```

- [ ] **Step 2: Update `MainWindow.xaml.cs`**

In `src/GamePartyHud/MainWindow.xaml.cs`:

**(a)** Add `using GamePartyHud.Capture;` if it isn't already imported (it should be — already used for `BarCalibration`).

**(b)** Replace the existing `OnPickRegion` handler (around lines 173-212) and the `RegionStatusState` enum / `SetRegionStatus` helper. Keep the broad shape (Opacity=0 trick, try/finally), but generalise to take a `BarType` from the sender's `Tag`. The replacement:

```csharp
private void OnPickRegion(object sender, RoutedEventArgs e)
{
    var bar = ParseBarType(sender);
    Log.Info($"MainWindow: Pick-{bar}-region button clicked.");
    Opacity = 0;
    try
    {
        var picker = new RegionSelectorWindow(PromptFor(bar));
        picker.ShowDialog();
        if (picker.Result is not { } region)
        {
            Log.Info($"MainWindow: {bar} region selection cancelled.");
            return;
        }

        var cal = new BarCalibration(region, FillDirection.LTR);

        var newConfig = bar switch
        {
            BarType.Hp      => _ctl.Config with { HpCalibration      = cal, NicknameRegion = null },
            BarType.Stamina => _ctl.Config with { StaminaCalibration = cal },
            BarType.Mana    => _ctl.Config with { ManaCalibration    = cal },
            _ => _ctl.Config
        };
        _ctl.UpdateConfig(newConfig);

        SetRegionStatus(bar, RegionStatusState.Ok,
            $"Captured {region.W}×{region.H} at ({region.X}, {region.Y}).");
        Log.Info($"MainWindow: {bar} region calibrated {region.W}x{region.H}@({region.X},{region.Y}).");
    }
    catch (Exception ex)
    {
        Log.Error($"MainWindow: OnPickRegion ({ParseBarType(sender)}) failed.", ex);
        SetRegionStatus(ParseBarType(sender), RegionStatusState.Error, "Error: " + ex.Message);
    }
    finally
    {
        Opacity = 1;
        Activate();
    }
}

private static BarType ParseBarType(object sender) =>
    sender is FrameworkElement fe && Enum.TryParse<BarType>(fe.Tag as string, out var t) ? t : BarType.Hp;

private static string PromptFor(BarType bar) => bar switch
{
    BarType.Hp      => "Drag a tight box around your HP bar ONLY (no nickname, no other bars)",
    BarType.Stamina => "Drag a tight box around your STAMINA bar ONLY (no nickname, no other bars)",
    BarType.Mana    => "Drag a tight box around your MANA bar ONLY (no nickname, no other bars)",
    _ => ""
};
```

Note: `OnPickRegion` is no longer `async`. The `Capture.CaptureBgraAsync(...)` call was removed in the bar-detection redesign already, so there's no `await` to drop here.

**(c)** Generalise `SetRegionStatus` to target a specific bar's chip. Replace the existing `SetRegionStatus(RegionStatusState state, string text)` method (around lines 436-450) with:

```csharp
private void SetRegionStatus(BarType bar, RegionStatusState state, string text)
{
    var (textBlock, iconBlock, chip) = bar switch
    {
        BarType.Hp      => (RegionStatus,        RegionStatusIcon,        RegionStatusChip),
        BarType.Stamina => (StaminaStatus,       StaminaStatusIcon,       StaminaStatusChip),
        BarType.Mana    => (ManaStatus,          ManaStatusIcon,          ManaStatusChip),
        _ => (RegionStatus, RegionStatusIcon, RegionStatusChip)
    };

    textBlock.Text = text;
    (string icon, string bg, string border, string fg) = state switch
    {
        RegionStatusState.Ok    => ("✓", "#333E8E3E", "#664CAF50", "#FFAEE6AE"),
        RegionStatusState.Error => ("✗", "#33C62828", "#66EF5350", "#FFFFB4B4"),
        _                       => ("○", "#22FFFFFF", "#33FFFFFF", "#CCCCCCCC"),
    };
    iconBlock.Text = icon;
    iconBlock.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(fg)!;
    textBlock.Foreground = iconBlock.Foreground;
    chip.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(bg)!;
    chip.BorderBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(border)!;
}
```

**(d)** Find the existing call sites of `SetRegionStatus(...)` (without a `BarType` parameter) inside `OnPickRegion`'s old body and `PopulateFromConfig`. After (b), the `OnPickRegion` body already passes the `bar` argument. For `PopulateFromConfig` (around lines 90-110), update the calls to specify HP:

Before:
```csharp
if (cfg.HpCalibration is { } cal)
{
    SetRegionStatus(RegionStatusState.Ok,
        $"Saved {cal.Region.W}×{cal.Region.H} at ({cal.Region.X}, {cal.Region.Y}).");
}
else
{
    SetRegionStatus(RegionStatusState.NotSet, "Not set yet.");
}
```

After:
```csharp
if (cfg.HpCalibration is { } cal)
{
    SetRegionStatus(BarType.Hp, RegionStatusState.Ok,
        $"Saved {cal.Region.W}×{cal.Region.H} at ({cal.Region.X}, {cal.Region.Y}).");
}
else
{
    SetRegionStatus(BarType.Hp, RegionStatusState.NotSet, "Not set yet.");
}

if (cfg.StaminaCalibration is { } sCal)
{
    IncludeStaminaCheck.IsChecked = true;
    StaminaPickRow.Visibility = Visibility.Visible;
    SetRegionStatus(BarType.Stamina, RegionStatusState.Ok,
        $"Saved {sCal.Region.W}×{sCal.Region.H} at ({sCal.Region.X}, {sCal.Region.Y}).");
}
else
{
    IncludeStaminaCheck.IsChecked = false;
    StaminaPickRow.Visibility = Visibility.Collapsed;
    SetRegionStatus(BarType.Stamina, RegionStatusState.NotSet, "Not set yet.");
}

if (cfg.ManaCalibration is { } mCal)
{
    IncludeManaCheck.IsChecked = true;
    ManaPickRow.Visibility = Visibility.Visible;
    SetRegionStatus(BarType.Mana, RegionStatusState.Ok,
        $"Saved {mCal.Region.W}×{mCal.Region.H} at ({mCal.Region.X}, {mCal.Region.Y}).");
}
else
{
    IncludeManaCheck.IsChecked = false;
    ManaPickRow.Visibility = Visibility.Collapsed;
    SetRegionStatus(BarType.Mana, RegionStatusState.NotSet, "Not set yet.");
}
```

**(e)** Add the four checkbox event handlers. Place them at the end of the class, just before the closing brace (before `ShowAndActivate()`):

```csharp
private void OnIncludeStaminaChecked(object sender, RoutedEventArgs e)
{
    if (_populating) return;
    StaminaPickRow.Visibility = Visibility.Visible;
    Log.Info("MainWindow: Include-stamina checkbox ticked.");
    // No config write here; user must explicitly pick a region.
}

private void OnIncludeStaminaUnchecked(object sender, RoutedEventArgs e)
{
    if (_populating) return;
    StaminaPickRow.Visibility = Visibility.Collapsed;
    _ctl.UpdateConfig(_ctl.Config with { StaminaCalibration = null });
    SetRegionStatus(BarType.Stamina, RegionStatusState.NotSet, "Not set yet.");
    Log.Info("MainWindow: Include-stamina checkbox unticked; calibration cleared.");
}

private void OnIncludeManaChecked(object sender, RoutedEventArgs e)
{
    if (_populating) return;
    ManaPickRow.Visibility = Visibility.Visible;
    Log.Info("MainWindow: Include-mana checkbox ticked.");
}

private void OnIncludeManaUnchecked(object sender, RoutedEventArgs e)
{
    if (_populating) return;
    ManaPickRow.Visibility = Visibility.Collapsed;
    _ctl.UpdateConfig(_ctl.Config with { ManaCalibration = null });
    SetRegionStatus(BarType.Mana, RegionStatusState.NotSet, "Not set yet.");
    Log.Info("MainWindow: Include-mana checkbox unticked; calibration cleared.");
}
```

The `_populating` flag (already a member of `MainWindow`, line 55) suppresses these handlers when `PopulateFromConfig` is setting `IsChecked` programmatically — same pattern as the existing nickname/role handlers.

- [ ] **Step 3: Build**

```bash
dotnet build
```

Expected: 0 errors, 0 warnings. (Watch for unresolved XAML references — typos in `x:Name` between XAML and `.xaml.cs`.)

- [ ] **Step 4: Run full test suite**

```bash
dotnet test
```

Expected: all green (97 + 9 new HudMember tests + the new MessageJson/PartyState/ConfigStore cases ≈ ~115).

- [ ] **Step 5: Visual smoke check**

```bash
dotnet run --project src/GamePartyHud -c Debug
```

Open the main window. Verify:
- HP bar region section is unchanged.
- "Optional bars" section appears between HP and Party.
- Description blurb is visible.
- Two checkboxes ("Include stamina", "Include mana") with neither pick row visible initially.
- Ticking either checkbox makes the corresponding pick button + status chip appear ("Not set yet").
- Clicking a pick button opens `RegionSelectorWindow` with a bar-specific prompt.
- After picking a region, the chip updates to "Captured WxH at (X,Y)".
- Restarting the app preserves the saved calibration: checkbox is ticked, chip shows "Saved WxH at (X,Y)".
- Unticking a previously-saved checkbox immediately collapses the row and clears the calibration. Restarting the app shows the checkbox unticked.

If any of these fail, fix before committing.

- [ ] **Step 6: Commit**

```bash
git add src/GamePartyHud/MainWindow.xaml \
        src/GamePartyHud/MainWindow.xaml.cs
git commit -m "feat(ui): Optional bars section with stamina/mana checkboxes

New 'Optional bars' section in MainWindow lives between HP-region and
Party. Two checkboxes ('Include stamina', 'Include mana') gate
per-bar pick buttons + status chips. The existing OnPickRegion handler
is generalised on a BarType (parsed from the button's Tag) and dispatches
to the right BarCalibration field. Status chips are mapped per-bar via
SetRegionStatus(BarType, ...). Unticking a checkbox clears the
corresponding calibration immediately so the next broadcast tick sends
null for that bar.

Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>
"
```

---

## Phase E — Manual verification

### Task 10: Live game session smoke test

Per [CLAUDE.md](../../../CLAUDE.md), runtime correctness is verified manually. This task is mandatory before declaring the feature complete.

- [ ] **Step 1: Build a release-style binary**

```bash
dotnet publish src/GamePartyHud/GamePartyHud.csproj -c Release
```

- [ ] **Step 2: Calibrate all three bars in-game**

Launch the published exe. Open the main window. Calibrate HP (existing flow). Tick "Include stamina", click pick button, drag a box around your stamina bar in-game. Confirm the chip updates. Same for mana.

- [ ] **Step 3: Watch the HUD for a full HP/Stamina/Mana cycle**

Stay in-game for a fight or training session. On your own HUD card, confirm:
- Three bars visible: HP top (red), Stamina middle (amber), Mana bottom (blue).
- Hairlines visible between bars.
- HP, Stamina, Mana track in real time as you take damage / sprint / cast spells.
- Bar values are smooth (no spikes); transitions look continuous.
- Nickname stays legible across all three colour bands.

- [ ] **Step 4: Multi-peer test**

If a teammate is available, have them join your party. Check both perspectives:
- **You see a teammate with HP-only:** their card on your HUD shows one bar.
- **You see a teammate with HP+Stamina+Mana:** their card on your HUD shows three bars.
- **A teammate sees you with all three:** they should see your three-bar card.
- **Pre-Stam/Mana peer (running an older build):** ask them to update or test with a build of `main` from before this feature. Confirm:
  - Their card on your HUD shows their HP-only.
  - Your card on their HUD shows your HP-only (they don't see your Stam/Mana — expected).
  - No crashes, no decode errors in either side's `app.log`.

- [ ] **Step 5: Toggle off / on**

Untick "Include mana" in your settings. Within ~2 sec (next broadcast), your card on a teammate's HUD should drop from 3 bars to 2 (HP + Stamina). Re-tick and re-pick — the bar should reappear after the next broadcast.

- [ ] **Step 6: Performance check**

Run the app for an 8-hour session in-game (per CLAUDE.md performance budget validation). Watch:
- Task Manager: CPU stays <1%, GPU stays <1%, RAM stays <100 MB.
- `%AppData%\GamePartyHud\app.log`: no flood of error messages, log size stays reasonable.

- [ ] **Step 7: Threshold revalidation (only if needed)**

If during Step 3 you observed systematic mis-readings on Stamina or Mana that don't appear on HP — for example, the stamina bar reads as 50% when it's clearly 100% — file a follow-up issue with screenshots and per-tick log excerpts. The shared `MissingMaxSaturation` / `MissingMaxValue` thresholds may need per-bar overrides on `BarCalibration`. Don't fix it in this branch unless the regression is severe enough to block merge.

- [ ] **Step 8: No code change to commit (verification-only)**

If everything looks correct, the task is done. If you found a real issue, branch back to the appropriate earlier task and fix it.

---

## Self-review

(performed against [the spec](../specs/2026-05-10-stamina-mana-bars-design.md))

**Spec coverage:**
- §1 (wire protocol): Task 1 ✓
- §2 (config schema): Task 3 ✓
- §3 (capture pipeline): Tasks 4, 5 ✓
- §4 (party state): Task 2 ✓
- §5 (HUD member card): Tasks 6, 7, 8 ✓
- §6 (MainWindow UI): Task 9 ✓
- §7.1 (unit tests): Tasks 1 (MessageJson), 2 (PartyState), 3 (ConfigStore), 6 (HudMember) ✓
- §7.2 (manual verification): Task 10 ✓
- §7.3 (threshold revalidation): Task 10 step 7 ✓

**Type consistency:**
- `StateMessage(string PeerId, string Nick, Role Role, float? Hp, float? Stamina, float? Mana, long T)` — same signature in Task 1 (definition) and Tasks 2, 5 (call sites).
- `MemberState(string PeerId, string Nickname, Role Role, float? HpPercent, float? StaminaPercent, float? ManaPercent, long JoinedAtUnix, long LastUpdateUnix)` — Task 2 definition matches Task 7 read sites.
- `BarType { Hp, Stamina, Mana }` — Task 4 definition matches Task 9 use sites.
- `BarCalibration` (existing from 2026-05-08 redesign) is referenced in Tasks 3, 5, 9 with the same `(CaptureRegion Region, FillDirection Direction)` shape.

**Placeholder scan:** No "TBD", "TODO", or vague "handle edge cases" instructions. Every code step shows the literal code. Every command step shows the exact command and expected output.

No issues found. The plan is ready.
