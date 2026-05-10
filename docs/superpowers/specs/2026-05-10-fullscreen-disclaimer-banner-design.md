# Fullscreen disclaimer banner — design

**Status:** Approved (brainstorming complete)
**Date:** 2026-05-10
**Author:** Anton Zemskov

---

## TL;DR

Add a yellow disclaimer banner to the top of the settings screen (`MainWindow.xaml`) explaining that the party HUD may not be visible when the game runs in exclusive fullscreen, and offering two workarounds (drag HUD to a second monitor, or switch to borderless / windowed fullscreen). The banner is dismissible: clicking X stores a flag in config and the banner never reappears.

Independent of the heavier detection-based fullscreen status surface described in `2026-05-01-fullscreen-capture-design.md` — this is a one-time onboarding hint, not a live status indicator.

---

## Why now

Several users on borderless / windowed-fullscreen setups assumed the HUD would render above their game on the first launch, then assumed the app was broken when they switched to exclusive fullscreen and the HUD vanished. The HUD's invisibility above an exclusive-fullscreen DXGI swap chain is a hard architectural limit (DLL injection / DXGI hooking is banned by [CLAUDE.md](../../../CLAUDE.md) for anti-cheat-friendliness). The fix is honest communication up front: tell the user about the limit on first launch, point them at the two real workarounds, and never bug them again.

This is a strictly smaller change than the 2026-05-01 fullscreen-capture-design proposes — that one detects exclusive-fullscreen at runtime and surfaces persistent + tray-balloon UX. This banner is detection-free and self-disposing.

---

## Non-goals

- Detection of when the game enters / exits exclusive fullscreen (that's the 2026-05-01 design).
- A persistent status row showing live fullscreen state.
- A re-show mechanism (the banner is gone forever once dismissed; no "show banner again" menu entry).
- Tray balloon notification.
- Any change to the HUD overlay itself, capture pipeline, or relay protocol.

---

## 1. Config schema

`AppConfig` gains one boolean with a default value of `false`. Following the existing pattern set by `RelayFallbackUrl = ""` in PR #67, the new field is appended to the record with a default initializer so existing call sites don't break:

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
    string RelayUrl,
    string RelayFallbackUrl = "",
    bool FullscreenDisclaimerDismissed = false);
```

`AppConfig.Defaults` adds the field as `false`.

### Migration

Old configs on disk that don't contain the new key deserialize cleanly with `FullscreenDisclaimerDismissed = false` (System.Text.Json's default-value handling for missing fields plus `JsonSerializerDefaults.Web`). Existing users see the banner once on their next launch and dismiss it. Same pattern as the Stamina/Mana migration (PR #68 Task 3).

---

## 2. UI

### 2.1 Placement

Inside the existing `<ScrollViewer>` → `<StackPanel>` block in `MainWindow.xaml`, at the very top of the scrollable settings content, immediately before the `<TextBlock Text="Your settings"/>` heading. Scrolls with the rest of the settings; not pinned.

### 2.2 Component

`Wpf.Ui`'s `<ui:InfoBar>` — same component already used for the "Party status" InfoBar at the bottom of this window. Yellow styling comes from `Severity="Warning"`. Built-in close (X) button comes from `IsClosable="True"`.

```xml
<ui:InfoBar x:Name="FullscreenDisclaimer"
            Severity="Warning"
            IsClosable="True"
            Margin="0,0,0,16"
            Title="If your game runs in fullscreen, the HUD may not be visible."
            Message="Workaround: use borderless / windowed-fullscreen mode, or drag the HUD onto a second monitor."
            Closed="OnFullscreenDisclaimerClosed"/>
```

### 2.3 Behaviour

On `MainWindow.PopulateFromConfig` (already runs at construction and on `UpdateConfig`):

```csharp
FullscreenDisclaimer.IsOpen = !cfg.FullscreenDisclaimerDismissed;
```

The `_populating` guard already in place prevents the `Closed` handler from firing during population.

On `Closed`:

```csharp
private void OnFullscreenDisclaimerClosed(InfoBar sender, InfoBarClosedEventArgs args)
{
    if (_populating) return;
    _ctl.UpdateConfig(_ctl.Config with { FullscreenDisclaimerDismissed = true });
    Log.Info("MainWindow: fullscreen disclaimer dismissed.");
}
```

`UpdateConfig` already persists the new config to disk via `ConfigStore.Save`, so the dismissal is durable immediately. Restart shows the banner only if the user manually edits config.json (out of scope to support).

---

## 3. Testing

### 3.1 Unit / migration

- `ConfigStoreTests.RoundTrip_PreservesEverythingExceptRelayUrl` is extended to set `FullscreenDisclaimerDismissed = true` and verify it survives save → load.
- A new test case: legacy JSON on disk without the `fullscreenDisclaimerDismissed` key → `loaded.FullscreenDisclaimerDismissed == false` (banner-shown default). Mirrors the Stam/Mana migration test pattern.

### 3.2 Manual

Per [CLAUDE.md](../../../CLAUDE.md), UI is manually tested. Verification steps:

1. Delete `%AppData%\GamePartyHud\config.json` (or start from a fresh user account). Launch the app.
2. The banner appears at the top of the settings screen, above "Your settings", in yellow Wpf.Ui styling with the title + workaround message and a close (X) button.
3. Click the X. The banner disappears.
4. Close the app, reopen. The banner does NOT reappear.
5. Open `config.json`, confirm `"fullscreenDisclaimerDismissed": true` is present.

### 3.3 Existing-user upgrade verification

A config saved by the previous build (no `fullscreenDisclaimerDismissed` key on disk) is loaded by the new build. Expected: banner appears (treated as dismissed = false on a missing key). Dismissing it persists. Locked in by the unit migration test in §3.1.

---

## 4. Relationship to the 2026-05-01 fullscreen-capture-design

The 2026-05-01 spec describes:
- A `WgcScreenCapture` engine swap (capture works inside fullscreen).
- A *detection*-driven fullscreen-state surface: persistent status row in MainWindow + one-time tray balloon when an exclusive-fullscreen app is observed after a party is joined.

This banner is independent of both:
- It doesn't read or react to live capture / fullscreen state.
- It doesn't replace the persistent status row from the 2026-05-01 design; that surface (when implemented) is dynamic per-tick and reflects *current* state. This banner is a one-time onboarding nudge.

When the 2026-05-01 design lands, both can coexist: the disclaimer is shown until dismissed regardless of detection state; the dynamic status row toggles per-tick. If the team later decides one supersedes the other, that's a follow-up decision out of scope here.

---

## 5. Risks and mitigations

| Risk | Mitigation |
|---|---|
| User dismisses banner accidentally, wants it back. | Out of scope. The banner is intentionally one-shot. Power users can edit `config.json` (`fullscreenDisclaimerDismissed: false`) to re-show. |
| Banner clutters the settings screen for returning users. | It's only visible on first launch (or after `config.json` deletion); existing users see it once during upgrade then never again. Worst case: one extra dismiss per existing user. |
| Banner wording becomes stale once the 2026-05-01 detection-based status lands. | The 2026-05-01 design owns the per-tick status row. The banner stays as a one-time intro; if the team decides to retire it later, that's a one-line XAML deletion. |

---

## 6. Out-of-scope follow-ups

- Adding a "Show first-launch tips again" button in some help / about menu (the current app has no such menu and YAGNI).
- Detection-based fullscreen-state surface (already specified in 2026-05-01-fullscreen-capture-design.md).
- Tray balloon notifications.
- Translating the banner text (the app is English-only today).
