# Party widget redesign — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Re-skin the HUD party widget: nickname overlaid on a taller HP bar, 30%-smaller role icon, outer border hugs actual content, and an 11th member overflows into a second column (max 2 × 10).

**Architecture:** Pure WPF / XAML change plus one tiny code-behind addition. The view-model layer (`HudMember`, `PartyState`, `HudViewModelSync`) is untouched. Two-column layout is implemented with a `UniformGrid` whose `Columns` property is bound to a new `ColumnCount` property on `HudWindow`, recomputed whenever `MemberList.CollectionChanged` fires.

**Tech Stack:** WPF (.NET 8, `net8.0-windows10.0.19041.0`), XAML, C# 12.

**Spec:** [docs/superpowers/specs/2026-04-20-party-widget-redesign.md](../specs/2026-04-20-party-widget-redesign.md)

**Testing philosophy (from `CLAUDE.md`):** UI is manually verified — no flaky UI automation. No new unit tests are added in this plan. The existing unit-test suite (`dotnet test`) must stay green, and the `HudSmokeHarness` is extended so every sizing breakpoint can be eyeballed quickly.

---

## File map

| File | Change | Purpose |
|---|---|---|
| `src/GamePartyHud/Hud/HudSmokeHarness.cs` | Modify | Accept a count argument to seed 1 / 5 / 10 / 11 / 15 / 20 fake members for visual breakpoint tests. |
| `src/GamePartyHud/Hud/MemberCard.xaml` | Rewrite body | New 200×24 card: smaller role tile, taller bar, nickname overlaid. |
| `src/GamePartyHud/Hud/HudWindow.xaml.cs` | Modify | Add `INotifyPropertyChanged` + `ColumnCount` property, subscribe to `MemberList.CollectionChanged`. |
| `src/GamePartyHud/Hud/HudWindow.xaml` | Modify | Wrap members in a `UniformGrid` (`Rows=10`, `Columns` bound to `ColumnCount`). |
| `src/GamePartyHud/App.xaml.cs` | Modify (tiny) | Parse smoke-harness count argument. |

No files created. No files deleted. No test files touched.

---

## Task 1: Extend `HudSmokeHarness` to seed N fake members

**Why first:** every subsequent task needs a way to eyeball 1 / 10 / 11 / 20 member states. Doing this first means tasks 2–4 can be manually verified as they land.

**Files:**
- Modify: `src/GamePartyHud/Hud/HudSmokeHarness.cs`
- Modify: `src/GamePartyHud/App.xaml.cs` (argument parsing — tiny)

- [ ] **Step 1: Read the current smoke harness**

Read `src/GamePartyHud/Hud/HudSmokeHarness.cs` in full (it's ~27 lines). Confirm it currently seeds exactly 4 members and is invoked from `App.xaml.cs` on CLI flag `--hud-smoke`.

- [ ] **Step 2: Read how `App.xaml.cs` dispatches the flag**

Read `src/GamePartyHud/App.xaml.cs`. Find the block that detects `HudSmokeHarness.CliFlag` and calls `HudSmokeHarness.Run(app)`. We'll extend this to accept an optional count suffix, e.g. `--hud-smoke=15`.

- [ ] **Step 3: Replace `HudSmokeHarness.Run` to accept a count**

Full new file contents for `src/GamePartyHud/Hud/HudSmokeHarness.cs`:

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

    private static readonly (string Nick, Role Role, float Hp, bool Stale)[] Seeds =
    {
        ("Yiawahuye",    Role.Tank,      0.72f, false),
        ("Kyrele",       Role.Healer,    1.00f, false),
        ("Arakh",        Role.MeleeDps,  0.30f, true),
        ("Thal",         Role.RangedDps, 0.85f, false),
        ("StupidBeast",  Role.Tank,      0.55f, false),
        ("Barrakh",      Role.MeleeDps,  0.10f, false),
        ("ShalfeyHealz", Role.Healer,    0.95f, false),
        ("AboutFeeder",  Role.RangedDps, 0.68f, false),
        ("MinSu",        Role.MeleeDps,  0.40f, false),
        ("Gosling",      Role.RangedDps, 0.78f, false),
        ("YaGood",       Role.Tank,      1.00f, false),
        ("TyZok",        Role.MeleeDps,  0.22f, false),
        ("Aggressor",    Role.MeleeDps,  0.50f, false),
        ("Mir",          Role.Healer,    0.88f, false),
        ("Tomodo",       Role.RangedDps, 0.15f, true),
        ("GLIST",        Role.Tank,      0.61f, false),
        ("TinMiraqle",   Role.Healer,    0.33f, false),
        ("Zalbeng",      Role.MeleeDps,  0.80f, false),
        ("DoraLany",     Role.RangedDps, 0.47f, false),
        ("Feng",         Role.Tank,      0.92f, false),
    };

    public static void Run(Application app, int count = 4)
    {
        int n = System.Math.Clamp(count, 1, Seeds.Length);
        var hud = new HudWindow();
        for (int i = 0; i < n; i++)
        {
            var (nick, role, hp, stale) = Seeds[i];
            hud.MemberList.Add(new HudMember($"p{i + 1}")
            {
                Nickname = nick,
                Role = role,
                HpPercent = hp,
                IsStale = stale,
            });
        }
        hud.Closed += (_, _) => app.Shutdown();
        hud.Show();
    }
}
#endif
```

- [ ] **Step 4: Teach `App.xaml.cs` to parse the count suffix**

In `App.xaml.cs`, find this existing block inside `OnStartup`:

```csharp
#if DEBUG
        foreach (var arg in e.Args)
        {
            if (string.Equals(arg, GamePartyHud.Hud.HudSmokeHarness.CliFlag, StringComparison.Ordinal))
            {
                Log.Info("DEBUG smoke harness flag detected; showing HUD with fake members.");
                GamePartyHud.Hud.HudSmokeHarness.Run(this);
                return;
            }
        }
#endif
```

Replace it with the count-aware version below. The new logic accepts both `--hud-smoke` (default 4) and `--hud-smoke=N`:

```csharp
#if DEBUG
        foreach (var arg in e.Args)
        {
            bool isBare = string.Equals(arg, GamePartyHud.Hud.HudSmokeHarness.CliFlag, StringComparison.Ordinal);
            bool hasCount = arg.StartsWith(GamePartyHud.Hud.HudSmokeHarness.CliFlag + "=", StringComparison.Ordinal);
            if (!isBare && !hasCount) continue;

            int count = 4;
            if (hasCount)
            {
                int eq = arg.IndexOf('=');
                if (eq > 0 && int.TryParse(arg.AsSpan(eq + 1), out var parsed))
                {
                    count = parsed;
                }
            }
            Log.Info($"DEBUG smoke harness flag detected; showing HUD with {count} fake members.");
            GamePartyHud.Hud.HudSmokeHarness.Run(this, count);
            return;
        }
#endif
```

All referenced APIs (`string.Equals`, `StringComparison.Ordinal`, `string.AsSpan`, `int.TryParse`) come from `System`, which is already `using`'d at the top of the file. No new imports needed.

- [ ] **Step 5: Build**

Run: `dotnet build`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 6: Smoke-run with 20 members to confirm the plumbing works**

Run: `dotnet run --project src/GamePartyHud -- --hud-smoke=20`
Expected: HUD window appears with 20 fake cards in the current (pre-redesign) layout. Close the window. This confirms the harness extension works before we touch the visuals.

- [ ] **Step 7: Commit**

```bash
git add src/GamePartyHud/Hud/HudSmokeHarness.cs src/GamePartyHud/App.xaml.cs
git commit -m "test(hud): smoke harness seeds N fake members via --hud-smoke=N"
```

---

## Task 2: Rewrite `MemberCard.xaml` to the new layout

**Files:**
- Modify: `src/GamePartyHud/Hud/MemberCard.xaml`

New dimensions (from the spec):
- Card: 200 W × 24 H (was 240 × 40)
- Role-icon tile: 18 × 18 (was 26 × 26; glyph font drops 14 → 10)
- HP bar: 174 W × 22 H (was 90 × 8), fills remaining card width
- Nickname: overlaid on bar, vertically centered, 11px SemiBold, drop-shadow for legibility

Arithmetic: `200 − 2 (left pad) − 18 (role) − 4 (role right-margin) − 2 (right pad) = 174`.

- [ ] **Step 1: Replace the file with the new layout**

Full new contents of `src/GamePartyHud/Hud/MemberCard.xaml`:

```xml
<UserControl x:Class="GamePartyHud.Hud.MemberCard"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Width="200" Height="24"
             Background="Transparent"
             FontFamily="Segoe UI Variable, Segoe UI">
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

            <!-- Role glyph tile (30% smaller than the previous 26px tile) -->
            <Border Grid.Column="0" Width="18" Height="18" Margin="0,0,4,0"
                    VerticalAlignment="Center"
                    CornerRadius="2"
                    BorderBrush="#55E12A2A" BorderThickness="1">
                <Border.Background>
                    <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
                        <GradientStop Color="#44E12A2A" Offset="0"/>
                        <GradientStop Color="#332A1515" Offset="1"/>
                    </LinearGradientBrush>
                </Border.Background>
                <TextBlock Text="{Binding RoleGlyph}"
                           Foreground="#FFF2F2F2"
                           FontSize="10"
                           FontWeight="SemiBold"
                           HorizontalAlignment="Center"
                           VerticalAlignment="Center"/>
            </Border>

            <!-- HP bar (fills remaining width). Nickname is overlaid on the bar. -->
            <Grid Grid.Column="1" Width="174" Height="22" VerticalAlignment="Center">
                <!-- Empty track -->
                <Border CornerRadius="2"
                        BorderBrush="#44000000" BorderThickness="1">
                    <Border.Background>
                        <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
                            <GradientStop Color="#661A1A1C" Offset="0"/>
                            <GradientStop Color="#66111113" Offset="1"/>
                        </LinearGradientBrush>
                    </Border.Background>
                </Border>
                <!-- Red fill -->
                <Border CornerRadius="2"
                        HorizontalAlignment="Left"
                        Width="{Binding HpPercent, Converter={StaticResource HpWidthConverter}, ConverterParameter=174}">
                    <Border.Background>
                        <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
                            <GradientStop Color="#FFFF3B3B" Offset="0"/>
                            <GradientStop Color="#FFC81919" Offset="1"/>
                        </LinearGradientBrush>
                    </Border.Background>
                </Border>
                <!-- Inner white highlight stripe on the filled portion -->
                <Border CornerRadius="2"
                        HorizontalAlignment="Left"
                        Height="2"
                        VerticalAlignment="Top"
                        Margin="1,1,0,0"
                        Background="#33FFFFFF"
                        Width="{Binding HpPercent, Converter={StaticResource HpWidthConverter}, ConverterParameter=172}"/>
                <!-- Nickname overlay, drop-shadow keeps it legible over both fill and empty track -->
                <TextBlock Text="{Binding Nickname}"
                           Foreground="#FFF2F2F2"
                           FontSize="11" FontWeight="SemiBold"
                           VerticalAlignment="Center"
                           HorizontalAlignment="Left"
                           Margin="6,0,6,0"
                           TextTrimming="CharacterEllipsis">
                    <TextBlock.Effect>
                        <DropShadowEffect Color="Black" BlurRadius="2"
                                          ShadowDepth="0" Opacity="0.85"/>
                    </TextBlock.Effect>
                </TextBlock>
            </Grid>
        </Grid>
    </Border>
</UserControl>
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 3: Manual visual check — single card layout**

Run: `dotnet run --project src/GamePartyHud -- --hud-smoke=4`
Confirm visually:
- Cards are noticeably shorter than before.
- Role icon is smaller (18×18).
- Nickname sits on top of the red bar (not above it).
- Nickname stays readable over both filled (red) and unfilled (dark) portions.
- Stale member ("Arakh") is dimmed.
- Right edge of the card is flush with the HP bar — no dead space.
- Close the window.

If any of these look wrong, stop and fix before continuing.

- [ ] **Step 4: Manual visual check — single-column window (still pre-column-wrap)**

Run: `dotnet run --project src/GamePartyHud -- --hud-smoke=10`
Confirm visually: 10 cards stacked vertically, window hugs the 200-wide card with no right-side dead space. Close the window.

(Task 3/4 will introduce the two-column overflow; don't expect it yet.)

- [ ] **Step 5: Run unit tests**

Run: `dotnet test`
Expected: all existing tests pass (HP analysis, party state, message encoding, config, etc.).

- [ ] **Step 6: Commit**

```bash
git add src/GamePartyHud/Hud/MemberCard.xaml
git commit -m "feat(hud): nickname-on-bar member card, 30% smaller role tile"
```

---

## Task 3: Add `ColumnCount` to `HudWindow.xaml.cs`

**Files:**
- Modify: `src/GamePartyHud/Hud/HudWindow.xaml.cs`

We add `INotifyPropertyChanged` and a single `ColumnCount` property that returns `1` when `MemberList.Count <= 10`, else `2`. `CollectionChanged` on `MemberList` recomputes it.

- [ ] **Step 1: Update the class declaration and usings**

Open `src/GamePartyHud/Hud/HudWindow.xaml.cs`.

Add two using statements near the top (next to existing ones):

```csharp
using System.Collections.Specialized;
using System.ComponentModel;
```

Change the class signature from:

```csharp
public partial class HudWindow : Window
```

to:

```csharp
public partial class HudWindow : Window, INotifyPropertyChanged
```

- [ ] **Step 2: Add the `ColumnCount` property and plumbing**

Inside `HudWindow`, locate the existing `MemberList` declaration:

```csharp
public ObservableCollection<HudMember> MemberList { get; } = new();
```

Immediately below it, add the new property and a backing field:

```csharp
private int _columnCount = 1;

/// <summary>
/// Number of columns the HUD should render (1 for parties of ≤10, 2 for 11–20).
/// Bound by <c>HudWindow.xaml</c>'s <c>UniformGrid.Columns</c>.
/// </summary>
public int ColumnCount
{
    get => _columnCount;
    private set
    {
        if (_columnCount == value) return;
        _columnCount = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ColumnCount)));
    }
}

public event PropertyChangedEventHandler? PropertyChanged;

private void RecomputeColumnCount()
{
    ColumnCount = MemberList.Count > 10 ? 2 : 1;
}
```

- [ ] **Step 3: Subscribe to `CollectionChanged` in the constructor**

Find the existing constructor:

```csharp
public HudWindow()
{
    InitializeComponent();
    Members.ItemsSource = MemberList;
    SourceInitialized += OnSourceInitialized;
    Loaded += (_, _) => UpdateLockVisual();
}
```

Change it to also wire up column recomputation:

```csharp
public HudWindow()
{
    InitializeComponent();
    Members.ItemsSource = MemberList;
    MemberList.CollectionChanged += OnMemberListChanged;
    SourceInitialized += OnSourceInitialized;
    Loaded += (_, _) => UpdateLockVisual();
}

private void OnMemberListChanged(object? sender, NotifyCollectionChangedEventArgs e)
{
    RecomputeColumnCount();
}
```

- [ ] **Step 4: Build**

Run: `dotnet build`
Expected: Build succeeded, 0 warnings, 0 errors.

Nullability is enabled and warnings are errors (per `CLAUDE.md`); if a warning surfaces here, fix it rather than suppress.

- [ ] **Step 5: Run unit tests**

Run: `dotnet test`
Expected: all existing tests pass. (No tests touch `HudWindow`, but we run the full suite anyway.)

- [ ] **Step 6: Commit**

```bash
git add src/GamePartyHud/Hud/HudWindow.xaml.cs
git commit -m "feat(hud): ColumnCount property tracks party size for 2-col wrap"
```

---

## Task 4: Switch `HudWindow.xaml` to a column-wrapping layout

**Files:**
- Modify: `src/GamePartyHud/Hud/HudWindow.xaml`

`UniformGrid` with `Rows="10"` and a bound `Columns` gives us column-major fill (items 1–10 populate column A top-to-bottom, items 11–20 populate column B).

- [ ] **Step 1: Replace the `ItemsControl` block**

Open `src/GamePartyHud/Hud/HudWindow.xaml`. Find the existing `<ItemsControl x:Name="Members">` element (currently wrapped in the outer `StackPanel`). Replace the whole `<ItemsControl>...</ItemsControl>` element with this:

```xml
<ItemsControl x:Name="Members">
    <ItemsControl.ItemsPanel>
        <ItemsPanelTemplate>
            <UniformGrid Rows="10"
                         Columns="{Binding ColumnCount,
                                   RelativeSource={RelativeSource AncestorType=Window}}"/>
        </ItemsPanelTemplate>
    </ItemsControl.ItemsPanel>
    <ItemsControl.ItemTemplate>
        <DataTemplate>
            <hud:MemberCard Margin="2,1">
                <hud:MemberCard.ContextMenu>
                    <ContextMenu>
                        <MenuItem Header="Kick from party" Click="OnKickClick"/>
                    </ContextMenu>
                </hud:MemberCard.ContextMenu>
            </hud:MemberCard>
        </DataTemplate>
    </ItemsControl.ItemTemplate>
</ItemsControl>
```

The only structural changes vs. today:
1. An `ItemsPanelTemplate` containing a `UniformGrid` replaces the default vertical `StackPanel` panel.
2. `<hud:MemberCard Margin="0,2">` becomes `<hud:MemberCard Margin="2,1">` — 2px left/right gives a 4px gap between columns; 1px top/bottom gives a 2px gap between rows (matching today's spacing).

Keep everything else in the file (`<Window>` attributes, the outer `<Border x:Name="RootBorder">`, the `<StackPanel>` holding the lock button row, and the lock-button `<Grid>`) untouched.

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 3: Manual visual check — 1 member**

Run: `dotnet run --project src/GamePartyHud -- --hud-smoke=1`
Confirm: window is a single card tall, tight around one 200-wide card, no dead space on right. Close.

- [ ] **Step 4: Manual visual check — 10 members (single-column threshold)**

Run: `dotnet run --project src/GamePartyHud -- --hud-smoke=10`
Confirm: single column of exactly 10 cards. Window width still matches one card. Close.

- [ ] **Step 5: Manual visual check — 11 members (wrap threshold)**

Run: `dotnet run --project src/GamePartyHud -- --hud-smoke=11`
Confirm:
- Layout snaps to two columns.
- Column A has 10 cards (members 1–10), column B has 1 card (member 11).
- Window width is roughly double the one-column width, plus a small gap.
- Column A's 10th card is flush with the bottom edge; column B is top-aligned (so column B cell 2–10 are empty but reserve height — desired).
Close.

- [ ] **Step 6: Manual visual check — 20 members (full capacity)**

Run: `dotnet run --project src/GamePartyHud -- --hud-smoke=20`
Confirm: two full columns of 10, equal heights, 4px gap between columns. Close.

- [ ] **Step 7: Run unit tests**

Run: `dotnet test`
Expected: all existing tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/GamePartyHud/Hud/HudWindow.xaml
git commit -m "feat(hud): 2-column overflow at >10 members via UniformGrid"
```

---

## Task 5: End-to-end verification

**Files:** none (verification only).

No code changes in this task. We run through the full manual checklist from the spec with the real app (not just the smoke harness), confirm nothing regressed, then we're done.

- [ ] **Step 1: Full test suite**

Run: `dotnet test`
Expected: all tests pass. If anything fails, stop and fix — do not proceed.

- [ ] **Step 2: Full build in Release mode (surfaces XAML issues the Debug build sometimes hides)**

Run: `dotnet build -c Release`
Expected: Build succeeded, 0 warnings, 0 errors.

- [ ] **Step 3: Live 10 ↔ 11 ↔ 10 transition**

Run: `dotnet run --project src/GamePartyHud -- --hud-smoke=10`

With the window open, we don't have a live "add a member" button in the smoke harness, so simulate the transition by successively running the harness with 10, then 11, then 10, then 11, checking:
- Window resizes smoothly each time.
- No clipping, no visible flicker at the boundary.

If you observe flicker, note it — the spec's "risks" section calls out the mitigation (briefly hide the root Border for one frame), and we'd add a follow-up task; do not inline that fix here.

- [ ] **Step 4: Nickname legibility at multiple HP values**

The smoke harness seeds HP values of `0.10`, `0.22`, `0.30`, `0.33`, `0.40`, `0.47`, `0.50`, `0.55`, `0.61`, `0.68`, `0.72`, `0.78`, `0.80`, `0.85`, `0.88`, `0.92`, `0.95`, `1.00`, plus some stale entries. Run `--hud-smoke=20` and scan the cards — at every fill level, the nickname should be fully readable.

If any card shows unreadable text (white text on red fill can get muddy), note it as a follow-up and decide whether to tweak the drop shadow; do not block this plan.

- [ ] **Step 5: Stale-member dimming still works**

Still in the `--hud-smoke=20` run, locate "Arakh" (index 3) and "Tomodo" (index 15) — both seeded as stale. They should appear at ~40% opacity. Confirm.

- [ ] **Step 6: Lock button + context menu still work**

Still in the `--hud-smoke=20` run:
- Click the 🔒 icon in the top-right. It should toggle to 🔓 and the window border should become visible. Click again to relock.
- Right-click a card. The "Kick from party" context menu should appear. (Clicking it won't do anything here — the smoke harness doesn't wire up the `KickRequested` event.)
Close the window.

- [ ] **Step 7: Real-run sanity check**

Run the app without the smoke flag:

Run: `dotnet run --project src/GamePartyHud`

Confirm that the tray appears and — if you have saved party state — the HUD shows party members with the new layout. If no party is joined, just confirm the tray icon is present and the app doesn't crash. Exit via the tray menu.

- [ ] **Step 8: Final commit (if anything was tweaked during verification)**

If any of steps 3–7 required a small fix, commit it now:

```bash
git add <files>
git commit -m "fix(hud): <specific issue found during verification>"
```

Otherwise skip this step.

- [ ] **Step 9: Self-review the diff**

Run: `git log --oneline main..HEAD`
Expected: four or five focused commits (one per task plus any verification fix), each under ~100 lines of change. If any commit is sprawling or mixes concerns, consider rebasing before opening a PR — but do not squash the history unless asked.

---

## Done criteria

- `dotnet build` and `dotnet build -c Release` both pass with 0 warnings, 0 errors.
- `dotnet test` passes with the pre-existing count (nothing regressed; no new tests added).
- All manual-verification steps in Task 5 pass.
- The widget visually matches the spec: small role icon, nickname-on-bar, tight outer border, 2-column overflow at 11+ members.

---

## Out of scope (explicit reminders)

- No changes to capture, HP analysis, party state, messaging, signalling, tray, or config.
- No new unit tests (UI is manually verified per `CLAUDE.md`).
- No configurable wrap threshold, no horizontal raid-bar variant, no numeric HP readout — the spec's "Out of scope" section lists these as potential future ideas, not this plan's work.
