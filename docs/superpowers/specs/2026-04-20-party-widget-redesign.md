# Party widget redesign

**Date:** 2026-04-20
**Scope:** `src/GamePartyHud/Hud/MemberCard.xaml` + `HudWindow.xaml`. No behavioural / non-UI changes.

## Goals

Four user-visible improvements to the party HUD:

1. Remove the dead space on the right of each card (HP bar fills the card width). Outer border grows/shrinks to match actual content.
2. Reduce the role-icon tile by ~30% to better match the Albion reference density.
3. Replace the stacked "nickname above / HP bar below" layout with a single, taller HP bar that has the nickname written on top of it — Albion-party style.
4. Support up to two columns of 10 members each: member 11 and beyond overflow into a second column; the outer border / window resize accordingly.

## Non-goals

- No changes to capture, HP analysis, party state, messaging, signalling, or tray. UI-only.
- No unit-test changes. Pure-logic code paths are untouched.
- No new per-member data or message-protocol fields.

## Reference

- User-supplied mockup: single card with nickname drawn over a red HP bar.
- User-supplied Albion Online party screenshot: two columns of 10 members, each row a horizontal bar with nickname overlaid.

## Design

### 1. `MemberCard.xaml` — card layout

| Property          | Current | New                    |
|-------------------|---------|------------------------|
| Card width        | 240     | **200**                |
| Card height       | 40      | **24**                 |
| Role-icon tile    | 26×26   | **18×18** (≈30% smaller) |
| HP bar height     | 8       | **22**                 |
| HP bar width      | 90 (fixed) | **174** (fills remaining width after role icon + gaps) |
| Nickname position | Above bar, separate TextBlock | **Overlaid on bar**, vertically centered, left-padded 6px |

The `UserControl` root declarations change from `Width="240" Height="40"` → `Width="200" Height="24"`.

Internal structure (single `Grid` with two columns: Role | Bar):

- **Role glyph tile** — same red-gradient `Border` as today, shrunk to 18×18. `FontSize` of the glyph drops from 14 → 10 to fit. Margin-right 4 (was 8).
- **HP bar** — a single `Grid` containing three z-stacked elements:
  1. Empty track `Border` (dark gradient, CornerRadius 2, full bar width)
  2. Red fill `Border`, left-aligned, `Width = HpPercent × barWidth` via existing `HpWidthConverter` (the `ConverterParameter` is updated from `90` → the new bar width; the existing inner-highlight binding's parameter is updated proportionally). The inner white highlight stripe at the top of the fill is kept; its height stays 2px.
  3. Nickname `TextBlock` — left-padded 6, `VerticalAlignment=Center`, `FontSize=11`, `FontWeight=SemiBold`, `Foreground=#FFF2F2F2`, with a black 1px `DropShadowEffect` (`BlurRadius=2, ShadowDepth=0, Opacity=0.85`) so text stays legible over both the filled red and the empty dark portions of the bar.

Opacity binding (`{Binding Opacity}`) stays on the root Border — stale-member dimming still works.

The card's own outer `Border` stays (1px `#33FFFFFF`) but its padding shrinks to `2,1` so the bar can be the dominant visual. Cards inside the window get `Margin="0,1"`.

### 2. `HudWindow.xaml` — multi-column layout

Replace the single vertical `StackPanel` hosting `ItemsControl` with a layout that can switch between 1 and 2 columns based on member count:

- The `ItemsControl`'s `ItemsPanel` is set to a `ColumnMajorUniformGrid` with `Rows=10`. `Columns` is bound to a derived property on the view model (`ColumnCount`) that returns `1` when `Members.Count <= 10` and `2` otherwise. Members beyond 20 are clamped (out-of-scope bug but must not throw).
- `ColumnMajorUniformGrid` (a custom `Panel` subclass) fills **column-major**: items 1–10 populate the first column top-to-bottom, items 11–20 populate the second column. This matches the user's expectation ("11th player appears on the second column"). WPF's built-in `UniformGrid` is row-major and was replaced by this custom panel during implementation.
- Column gap: 4px. Implementation: each `MemberCard` gets `Margin="2,1"`. `ColumnMajorUniformGrid` cells end up sized to the card + its margins, so cards are separated by 4px horizontally (2 right + 2 left) and 2px vertically. The extra 2px outside the leftmost/rightmost cells is absorbed by the root Border's existing `Padding="6,4"` (or reduce root padding slightly if the total feels too wide in practice).
- Row gap: the `Margin="2,1"` above also gives a 2px vertical gap, matching today's spacing.

### 3. Window sizing

- `HudWindow` keeps `SizeToContent="WidthAndHeight"` and `WindowStyle="None" AllowsTransparency="True"`.
- Root `Border` keeps its translucent gradient + `CornerRadius=3` + `Padding="6,4"`.
- **Single-column width** (1–10 members): inner content ≈ 200 → window ≈ 212 wide.
- **Two-column width** (11–20 members): inner content ≈ 200 + 4 + 200 = 404 → window ≈ 416 wide.
- **Heights:**
  - 1-col: `N × 24 + (N−1) × 2` + lock-button row + root padding.
  - 2-col: always `10 × 24 + 9 × 2 = 258` + lock-button row + root padding (shorter column's empty cells are not rendered by `ItemsControl`, but the grid still reserves their height slots — desired, so the window shape stays stable while the party is in 2-col mode).
- Lock-button row: unchanged (right-aligned, 20×20).

### 4. Binding plumbing

The `HudMember` view model is unchanged. A single new computed read-only property is added to whichever view-model hosts the `Members` collection (currently `HudWindow` owns the `ItemsControl` directly; `HudViewModelSync` maintains the collection):

- `ColumnCount { get; }` — returns 1 or 2 based on `Members.Count`, raises `PropertyChanged` whenever the collection changes.

If the current code binds `ItemsControl.ItemsSource` directly to an `ObservableCollection<HudMember>`, we wrap it in a tiny `HudWindowViewModel` that exposes both `Members` and `ColumnCount` and subscribes to `CollectionChanged` to re-raise `ColumnCount` when the size crosses the 10/11 boundary. This is the minimal new plumbing.

### 5. Visual details preserved

- Red-gradient role tile colour and glyph font family.
- Dark translucent card / window background.
- Stale-member 0.4 opacity.
- Red HP-bar gradient + inner highlight stripe.
- Lock button, "Kick from party" context menu, window drag behaviour.

## Testing

Per `CLAUDE.md`: pure logic is unit-tested, UI is manually verified.

- **No new unit tests.** `HudMember`, `HpWidthConverter`, party state, and message encoding are untouched.
- **All existing unit tests must stay green** — the pre-existing `dotnet test` suite runs unchanged.
- **`HudSmokeHarness`** is extended with a way to seed 5 / 10 / 11 / 15 / 20 fake members (toggleable), so all sizing breakpoints are easy to eyeball.
- **Manual verification checklist** (run before merging):
  1. 1 member — window tight around one 200-wide card, no right-side dead space.
  2. 10 members — single column of 10 cards.
  3. 11 members — snaps to 2 columns; second column shows exactly 1 card.
  4. 20 members — two full columns of 10.
  5. Live 10 ↔ 11 ↔ 10 ↔ 11 transitions (add/remove members) — window resizes smoothly, no clipping or flicker.
  6. Nickname legibility at 0%, ~50%, and 100% HP — text readable in all three states thanks to the drop shadow.
  7. Stale-member dimming (opacity 0.4) still applies to the new overlaid layout.
  8. Lock button toggle + "Kick from party" context menu still work.
  9. Screenshot-capture and HP-read overlay are visually unchanged (no code change in capture pipeline).
  10. CPU / GPU / RAM under an 8-hour run remain within the perf budget from `CLAUDE.md` (spot-check, not a full re-run).

## Risks & mitigations

- **Text legibility over red fill** — the black drop shadow should handle it. If it doesn't look good in practice, we fall back to a semi-transparent dark horizontal strip under the text (inside the bar), still overlaying the fill. Decide during manual verification.
- **`UniformGrid` fill order** — WPF `UniformGrid` is row-major by default. We hit this in practice: fixing `Rows=10` and varying `Columns` did **not** produce the desired column-major fill. We replaced `UniformGrid` with a small custom `Panel` subclass, `ColumnMajorUniformGrid`, which lays out items column-major directly. The two-`ItemsControl` fallback was not needed.
- **Window-width flicker at the 10 ↔ 11 boundary** — `SizeToContent` plus a re-bound `Columns` should be atomic within a single layout pass. If flicker appears, briefly hide the root Border during the transition (1 frame) — a last-resort hack we'd only reach for if we actually observe a problem.

## Out of scope / future ideas

- Horizontal row layout (like a top-of-screen raid bar) — different design, not requested.
- Configurable column count / wrap threshold in settings — YAGNI; the 10/11 rule is fine.
- Numeric HP readout on the bar — user did not ask for it; Albion reference doesn't show one.
