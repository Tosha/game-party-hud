# Stamina and Mana bar tracking — design

**Status:** Approved (brainstorming complete)
**Date:** 2026-05-10
**Author:** Anton Zemskov

---

## TL;DR

Add optional Stamina and Mana bar tracking on top of the existing HP pipeline. The bar analyzer is already colour-agnostic (per the 2026-05-08 redesign), so adding more bars is purely additive: two more `BarCalibration` regions in config, two more pixel reads per tick, two more nullable floats on the wire-state message, and a HUD member card that grows from one bar into a stack of 1–3 bars sized **per sender** with hairline dividers. HP stays the gate to join a party; Stamina and Mana stay optional throughout.

The change is wire-compatible with peers running pre-Stam/Mana builds: the existing `hp` JSON key is unchanged, and the two new keys default to absent → null on the receiver. Old configs deserialize without migration code, same as the previous redesign.

---

## Why now

The bar analyzer redesign (2026-05-08) was specifically scoped to "HP-only at runtime, but generalised so future stamina/mana support is additive". That additive moment is now: the colour-agnostic predicate (`IsMissingPixel`) already classifies any desaturated grey as the empty portion of any bar regardless of fill colour, so adding stamina (yellow fill) and mana (blue fill) requires zero algorithm changes — only data-flow plumbing and UI rendering.

For Mortal Online 2 specifically, stamina and mana are first-class resources that teammates regularly need to coordinate around (mana for healers, stamina for melee/ranged DPS). HP-only signalling was always a v0.1.0 simplification; this is the natural follow-up.

---

## Non-goals

- Auto-detection of bar regions. The user still drags a box per bar.
- Per-bar threshold tuning. The shared `MissingMaxSaturation` / `MissingMinValue` / `MissingMaxValue` constants from the 2026-05-08 redesign are reused for all three bars. If real-world stamina/mana captures show systematic misclassification during manual verification, threshold tuning becomes a follow-up.
- Multi-bar capture parallelism. Captures run sequentially within a tick (each is small; the budget is unaffected).
- Preserving Stam/Mana calibrations when the user un-ticks their checkbox. Un-checking sets the relevant `BarCalibration` to `null`. Re-ticking re-shows the pick button at "Not set."
- Per-bar staleness. A peer either is stale or isn't; bars don't have independent freshness.
- New bar types beyond Stamina and Mana. The wire protocol does not generalise to a `Bars` map; we add the two specific fields the spec calls for.

---

## 1. Wire protocol

### 1.1 `StateMessage` shape

```csharp
public sealed record StateMessage(
    string PeerId,
    string Nick,
    Role Role,
    float? Hp,
    float? Stamina,
    float? Mana,
    long T) : PartyMessage;
```

### 1.2 JSON encoding

```json
{
  "type": "state",
  "peerId": "abc...",
  "nick": "Tosha",
  "role": "Tank",
  "hp": 0.72,
  "stamina": 0.41,
  "mana": null,
  "t": 1715180400
}
```

`MessageJson.Encode` adds two object properties (`stamina`, `mana`) with the `float?` values as-is.

### 1.3 Decoding

`MessageJson.Decode` reads the two new keys via `TryGetProperty` (defaulting to `null` when absent), to support both:
- **Old peer → new peer** (legacy JSON missing the two keys): `StateMessage(... Hp=value, Stamina=null, Mana=null, ...)`. The receiver renders an HP-only card.
- **New peer → old peer** (current JSON with the two extra keys): `System.Text.Json` ignores unknown JSON properties by default, so the existing `GetProperty("hp")` call path still works on legacy clients. They simply don't see Stam/Mana.

### 1.4 Existing helpers

`ParseNullableFloat(JsonElement)` (the same one used for `Hp` today) is reused for `Stamina` and `Mana` — null-aware, no new code.

---

## 2. Config schema

### 2.1 `AppConfig` shape

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
    string RelayUrl);
```

`AppConfig.Defaults` initialises the two new fields to `null`.

### 2.2 JSON keys

`staminaCalibration` and `manaCalibration` (matching `JsonSerializerDefaults.Web`'s camelCase). Each value is either a complete `BarCalibration` block (same shape as `hpCalibration`) or `null`.

### 2.3 Migration

No migration code required. The same `JsonSerializerDefaults.Web` default-ignore-unknown semantics that handled the `BarCalibration` slim in 2026-05-08 also handle the new fields' absence in old configs: `staminaCalibration` and `manaCalibration` simply default to `null` on first read.

---

## 3. Capture pipeline

### 3.1 Tick layout

`PartyOrchestrator.PollAndBroadcastLoopAsync` extends from one capture-and-analyze pass to up to three, each gated by its calibration's presence:

```text
each tick:
    if HpCalibration:      hp      = analyzer.Analyze(capture(HpCalibration.Region))   ; smoothed via _hpSmoother
    if StaminaCalibration: stamina = analyzer.Analyze(capture(StaminaCalibration.Region)) ; smoothed via _staminaSmoother
    if ManaCalibration:    mana    = analyzer.Analyze(capture(ManaCalibration.Region))    ; smoothed via _manaSmoother

    apply self-state locally (StateMessage with all three)
    if any of {hp, stamina, mana} moved ≥ 1% since last broadcast OR heartbeat-due:
        broadcast StateMessage(... hp, stamina, mana, ...)
```

### 3.2 Smoothing

Three independent `BarSmoother` instances (`_hpSmoother`, `_staminaSmoother`, `_manaSmoother`), each with the existing `windowSize: 3`. A noise spike in stamina cannot bleed into HP.

### 3.3 Broadcast suppression

The change-threshold check is OR-ed across the three bars:

```csharp
bool barChanged =
       !ApproxEqual(hp,      _lastBroadcastHp,      HpChangeThreshold)
    || !ApproxEqual(stamina, _lastBroadcastStamina, HpChangeThreshold)
    || !ApproxEqual(mana,    _lastBroadcastMana,    HpChangeThreshold);
bool nickChanged = ...;
bool roleChanged = ...;
bool heartbeatDue = ...;

if (barChanged || nickChanged || roleChanged || heartbeatDue) { broadcast(); }
```

The constant `HpChangeThreshold` is renamed to `BarChangeThreshold` since it now applies to all three bars (same value `0.01f`).

### 3.4 Local self-apply

The orchestrator's own `_state.Apply(new StateMessage(_selfPeerId, _cfg.Nickname, _cfg.Role, hp, stamina, mana, now))` keeps the self-card on the local HUD in sync with the bars the user has enabled. If a calibration is null, its corresponding value in the StateMessage is null too.

### 3.5 LogTick diagnostic

The existing per-tick diagnostic that reports `bar/partial/missing` column counts is repeated for each enabled bar:

```text
PartyOrchestrator tick#42:
  hp  raw=0.730 smoothed=0.728 region=290x13@(...) cols 215/8/67 bar/partial/missing; mid-HSV ...
  sta raw=0.412 smoothed=0.410 region=290x12@(...) cols 120/4/166 bar/partial/missing; mid-HSV ...
  man n/a (not calibrated)
```

Bars with a null calibration log a single `n/a (not calibrated)` line so the absence is observable.

### 3.6 No new performance budget

Three small captures per 2-second tick still fits inside the <1% CPU / <1% GPU / <100 MB RAM budget. The image-analysis cost scales linearly with `width × height` per region, and bar regions are typically a few hundred pixels by ~10 px. Three passes is roughly 3× a single HP pass, well under the existing slack. Manual verification (Section 7) confirms.

---

## 4. Party state

### 4.1 `MemberState` shape

```csharp
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

### 4.2 `PartyState.Apply(StateMessage)` update

When a `StateMessage` arrives, all three bar fields are written into the existing or new `MemberState` record. Existing semantics for kicked / stale / heartbeat are unchanged.

### 4.3 Staleness

Per the non-goal, staleness is per-peer, not per-bar. The existing 30-sec / 90-sec stale/remove timers continue to apply uniformly.

---

## 5. HUD member card

### 5.1 Layout strategy

Each card decides its bar layout from the non-null fields of its own `MemberState`. **Per-sender, not per-viewer:**

| Sender shares | Bars rendered (top → bottom) | Per-bar share of the 22-px bar height |
|---|---|---|
| HP only | HP | full (22 px) |
| HP + Mana | HP, Mana | 11 px each |
| HP + Stamina | HP, Stamina | 11 px each |
| HP + Stamina + Mana | HP, Stamina, Mana | ~7 px each (with hairlines) |

HP is always present (joining a party requires HP calibration). Order is fixed: HP → Stamina → Mana. Skipping Stamina does not move Mana up into the middle slot — Mana stays in the bottom slot for muscle-memory consistency.

### 5.2 Hairline divider

Between adjacent bars, a single 1-px row of `#88000000` (semi-transparent black) sits flush on the lower bar's top edge. The outer border-radius (2 px) is on the bar block as a whole; only the top bar gets the top corners and only the bottom bar gets the bottom corners.

### 5.3 Colour palette

| Bar | Top stop | Bottom stop | Tailwind anchor |
|---|---|---|---|
| HP | `#FFB91C1C` | `#FF7F1D1D` | red-700 → red-900 (existing) |
| Stamina | `#FFFCD34D` | `#FFCA8A04` | amber-300 → amber-600 |
| Mana | `#FF2563EB` | `#FF1E3A8A` | blue-600 → blue-900 |

The white inner-highlight stripe (`#33FFFFFF`, 2 px tall, top of the filled portion) is preserved on each bar's filled region — same affordance the HP bar already uses to give the bar a glassy feel.

### 5.4 Nickname overlay

The nickname `TextBlock` with its drop-shadow stays drawn over the **entire** bar block, vertically centred, just as today. The drop-shadow keeps it legible regardless of which colour band the text crosses (HP red, Stamina amber, Mana blue). Yellow is the lowest-contrast band; the existing `DropShadowEffect` (Black, BlurRadius=3, ShadowDepth=0, Opacity=1.0) keeps the text legible on amber-300/600 (verified by manual check during implementation).

### 5.5 `HudMember` view-model

Add three properties:

```csharp
public float? StaminaPercent { get; set; }   // raises PropertyChanged + HasStamina
public float? ManaPercent    { get; set; }   // raises PropertyChanged + HasMana
public bool HasStamina => StaminaPercent.HasValue;
public bool HasMana    => ManaPercent.HasValue;
```

The existing `HpPercent` stays a `float` (not nullable) — HP is always present. Setters clamp `StaminaPercent`/`ManaPercent` to `[0, 1]` when non-null, mirroring the existing `HpPercent` clamping.

### 5.6 `HudViewModelSync.Sync()`

Each member-update path reads `m.StaminaPercent` and `m.ManaPercent` and writes them onto the corresponding `HudMember`. Setting either to `null` (e.g. when the sender un-ticked their checkbox and broadcast a state with that field null) flips `HasStamina`/`HasMana` to false → that bar's row collapses.

### 5.7 `MemberCard.xaml` layout

The current single-`Grid` bar area becomes a vertical stack:

```text
<Grid> (the existing role-glyph + bar-area Grid stays in place)
    Column 0: role-glyph tile          (unchanged, 18×18)
    Column 1: bar block                (174 wide × 22 tall, hosts 1-3 bars)

  inside Column 1:
    <Grid>
        rows: dynamic via SharedSizeGroup or hard-coded 3 RowDefinitions

        Row 0 (HP):       always Visible
            empty track + filled gradient + highlight stripe (red palette)
        Row 1 (Stamina):  Visibility bound to HasStamina
            empty track + filled gradient + highlight stripe (amber palette)
        Row 2 (Mana):     Visibility bound to HasMana
            empty track + filled gradient + highlight stripe (blue palette)

        nickname overlay covers all rows
    </Grid>
</Grid>
```

The hairline dividers are 1-px-tall `Border`s with `Background="#88000000"`, hosted in the row's negative top margin so they sit flush against the lower row's top edge without consuming row height.

The `HpWidthConverter` is reused for all three bars (same `[0..1]` → pixel-width math). The `Width` parameter (174 px today) is unchanged.

For row sizing, the `RowDefinitions` use `Height="*"` so each visible row gets `1/N` of the bar area's vertical space, where N is the visible count. Collapsed rows contribute `0` to the proportion. The hairline dividers (1 px each, between adjacent visible rows) consume a tiny fraction of the bar area, so the actual fills are 21 px (1 bar), 10.5 px (2 bars), or 6.7 px (3 bars). This is fine for legibility — the dark-red HP bar at 6.7 px tall still reads as a bar.

### 5.8 `HudSmokeHarness`

The existing harness paints fake members for layout testing. Extend it so a `--hud-smoke=N` flag with `N ≥ 4` populates one of each of the four card configurations (HP-only, HP+Stamina, HP+Mana, HP+Stamina+Mana) at fixed mock percentages so all four layouts can be visually inspected in one launch.

---

## 6. MainWindow UI

### 6.1 Layout

A new section is added between the existing "HP bar region" section and the "Party" section. The HP region UI is **unchanged.**

```text
HP bar region                                 (existing, unchanged)
  Be in-game with HP full, then drag a tight
  box around just the red HP bar...
  [Pick HP bar region]    [○ Saved 290×13 at (1234,567)]

  ─────── separator ───────

Optional bars                                 (new)
  Track stamina and mana too — picked up the
  same way, sent to your teammates alongside
  HP, and shown as additional bars on each
  card.

  ☐ Include stamina
        [Pick stamina bar region]    [○ Not set yet]    (visible only when ticked)

  ☐ Include mana
        [Pick mana bar region]       [○ Not set yet]    (visible only when ticked)

  ─────── separator ───────

Party                                          (existing)
```

### 6.2 Checkbox semantics

Each checkbox is a two-way binding to "is this `BarCalibration` non-null":
- Reading: `cfg.StaminaCalibration is not null` ↔ checkbox `IsChecked = true`.
- Writing (un-ticking): set `_ctl.Config with { StaminaCalibration = null }`. The pick-button row collapses.
- Writing (ticking from unchecked): the pick-button row appears at "Not set yet"; nothing is written to `_ctl.Config` until the user actually picks a region.

### 6.3 Pick button handler

The existing `OnPickRegion` handler (currently HP-specific) is generalised to take a `BarType` (enum: `Hp / Stamina / Mana`). Three buttons in XAML each invoke the same handler with their bar type as `Tag`. The handler:
1. Hides MainWindow (the existing `Opacity = 0` trick).
2. Opens `RegionSelectorWindow` with a per-bar prompt:
   - HP: "Drag a tight box around your HP bar ONLY (no nickname, no other bars)" (existing).
   - Stamina: "Drag a tight box around your stamina bar ONLY (no nickname, no other bars)."
   - Mana: "Drag a tight box around your mana bar ONLY (no nickname, no other bars)."
3. On result, builds a `BarCalibration(region, FillDirection.LTR)` and writes it to the corresponding config field.
4. Updates the corresponding status chip.

### 6.4 Status chips

Three independent chips (`HpStatusChip`, `StaminaStatusChip`, `ManaStatusChip`), each using the existing `SetRegionStatus(state, text)` private helper generalised to take a target chip reference. State values (`NotSet`, `Ok`, `Error`) and styling are unchanged.

### 6.5 Validation

`ValidateBeforeJoiningParty` is unchanged. HP calibration is the only required gate. Stamina and Mana are optional throughout.

---

## 7. Testing

### 7.1 Unit tests

**`MessageJsonTests`** — round-trip cases for the three-field state message:
- All three bars present → JSON contains `hp/stamina/mana`; round-trip preserves all values.
- Only HP present → JSON contains `hp` plus `stamina: null, mana: null`; round-trip preserves the nulls.
- **Old-shape JSON** (containing only `hp` key) → decodes to `StateMessage(... Hp=x, Stamina=null, Mana=null, ...)`. Locks in wire-back-compat.

**`ConfigStoreTests`**:
- Round-trip with all three calibrations populated.
- Migration case: legacy JSON with only `hpCalibration` (no `staminaCalibration`, no `manaCalibration`) deserializes; the two new fields are `null`. Same pattern as the 2026-05-08 Task 10 migration test.

**`PartyStateTests`** (add if absent; otherwise extend):
- `Apply(StateMessage)` writes all three bar fields onto the `MemberState`.
- Subsequent updates to one bar field don't clobber the others.

**`HudMemberTests`** (new, small):
- Setting `StaminaPercent` from null to a value flips `HasStamina` from false to true and raises `PropertyChanged` for both.
- Same for `ManaPercent` / `HasMana`.
- Clamping: assigning `1.5f` clamps to `1.0f`; assigning `-0.1f` clamps to `0.0f`.

### 7.2 Manual verification

XAML rendering and live capture are manually tested per CLAUDE.md ("UI code is manually tested. Do not invent flaky UI automation"). The verification steps:

1. **Smoke-harness layout check:** launch the app with `--hud-smoke=4`. Confirm all four layouts (HP-only, HP+Sta, HP+Mana, HP+Sta+Mana) render correctly with hairline dividers and the right colour palette per bar.
2. **Calibration UI:** open the main window. Confirm the new "Optional bars" section is between HP and Party; ticking the stamina checkbox reveals its pick button row. Confirm picking a region writes a status chip with dimensions.
3. **Live capture:** in-game, calibrate all three bars in turn. Watch the HUD show three bars stacked correctly per-sender. Confirm:
   - HP-only teammates show one bar.
   - Stamina/Mana track in real time during gameplay.
   - Bar transitions look smooth (per-bar smoothing).
   - Un-ticking stamina drops the card back to 2-bar layout within ~2 sec of the next broadcast.
4. **Wire-compat:** if a teammate is running a pre-Stam/Mana build, confirm:
   - Their card on your HUD still shows their HP.
   - Your card on their HUD still shows your HP (they don't see your Stam/Mana, which is expected).
   - No crashes or decode errors in the relay log on either side.

### 7.3 Threshold revalidation

The shared `MissingMaxSaturation = 0.05f` and `MissingMaxValue = 0.70f` constants from the 2026-05-08 redesign were tuned against HP bar samples. Stamina (yellow) and Mana (blue) samples might have different anti-alias behaviour at the bar's edge. During manual verification, watch for systematic mis-readings on Stam/Mana that don't appear on HP. If found, file a follow-up to either:
- Add per-bar threshold overrides on `BarCalibration` (small change), or
- Re-tune the shared thresholds to a wider gap (if the failure is universal).

This is **not** part of this spec's scope — it's a follow-up triggered only if manual verification reveals a problem.

---

## 8. Risks and mitigations

| Risk | Mitigation |
|---|---|
| The 0.70 V-upper-bound threshold catches yellow text glyphs as missing on the stamina bar (yellow text on yellow fill is harder to discriminate). | Manual verification specifically watches for this; threshold-tuning follow-up if observed. The 7.3 revalidation gate. |
| Three captures per tick blow the CPU budget on slow machines. | Each capture is small (200×10-px region typical). Sequential. Manual smoke-test confirms <1% CPU during an 8-hour run. |
| User unticks stamina checkbox and expects their teammates' HUD to update. | Per Section 6.2, unticking sets the calibration to `null` immediately; the next broadcast tick (≤2 sec later) sends `stamina: null`; receivers' card layouts collapse on the next sync. |
| Stale config files on disk gain unrecognised fields after a downgrade and confuse the older binary. | `System.Text.Json` ignores unknown fields by default. The Task 10 migration test from 2026-05-08 already covers this pattern; we add the analogous test for the new keys. |
| Yellow nickname-overlay contrast at amber-600. | Manual verification checks the worst case (long nickname spanning the stamina row in a 3-bar card). Drop-shadow keeps it AA-acceptable. |

---

## 9. Out-of-scope follow-ups

- Per-bar threshold overrides on `BarCalibration` if 7.3 surfaces a real systematic mis-read.
- Auto-detection of bar regions across all three bars.
- Bar-level staleness (per-bar freshness indicators).
- Generalising the wire protocol to a `Bars` map for arbitrary new resource bars beyond Stam/Mana.
- Drag-to-reorder bars within a card (currently hard-coded HP → Stamina → Mana).
- A "compact mode" that hides Stam/Mana on the HUD even when teammates send them (per-viewer visibility filter).
