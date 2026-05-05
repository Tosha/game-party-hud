# Fullscreen capture & overlay-limitation UX — design

**Status:** Approved (brainstorming complete)
**Date:** 2026-05-01
**Author:** Anton Zemskov

---

## TL;DR

Replace the GDI BitBlt screen-capture backend with a Windows.Graphics.Capture (WGC) implementation behind the unchanged `IScreenCapture` interface, so HP-bar reading works in exclusive-fullscreen DXGI games (the current backend returns black there). At the same time, surface an honest explanation of the overlay-visibility limitation: a persistent status row in the main window plus a one-time tray balloon when an exclusive-fullscreen app is detected after a party is joined, with wording that points users to the two real workarounds (borderless windowed, or drag the HUD onto a second monitor).

The HUD overlay itself is **not** made visible above exclusive-fullscreen DXGI swap chains — that requires DLL injection or DXGI hooking, which [CLAUDE.md](../../../CLAUDE.md) hard-bans for anti-cheat-friendliness. The design accepts this and is honest about it in the UX.

The change is intentionally split:

- **Capture engine swap** (Section 2) — fixes HP capture in fullscreen so teammates see this user's HP correctly even when the user is in exclusive fullscreen.
- **Fullscreen-state surface** (Section 3) — explains the residual limitation in-app and points at workable alternatives.

`IScreenCapture`, `HpRegion`, `HpCalibration`, and the calibration UX are unchanged.

---

## Non-goals

- Drawing the HUD above an exclusive-fullscreen DXGI swap chain (DXGI hooking / DLL injection — banned by [CLAUDE.md](../../../CLAUDE.md)).
- Window-handle (HWND) capture for the game window. Considered and rejected — adds a "pick game window" step to calibration that the current drag-a-box flow doesn't need.
- Auto-moving the HUD to a non-fullscreen monitor when fullscreen is detected. Mentioned as a workaround in the message instead; auto-moving is too invasive.
- Programmatic control of Focus Assist to bypass tray-balloon suppression. The persistent in-app status row is the load-bearing surface; the balloon is best-effort.
- Multi-monitor calibration UX changes (e.g., persisting which monitor the calibration was made on). Today's behaviour preserved; the monitor is resolved per-tick from the region's center point.
- Any change to the capture / analysis / broadcast pipeline beyond swapping the backend.

---

## 1. Architecture overview

### 1.1 Scope

Two coordinated changes:

1. **Capture engine swap.** Replace `WindowsScreenCapture` (GDI BitBlt) with `WgcScreenCapture` (Windows.Graphics.Capture) behind the unchanged `IScreenCapture` interface.
2. **Fullscreen-state surface.** Detect exclusive-fullscreen DXGI apps, expose that as observable state, and render it as a persistent status row in MainWindow plus a one-time tray balloon per party-join.

### 1.2 Module map

```
Capture/
  IScreenCapture.cs            (unchanged)
  HpRegion.cs                  (unchanged)
  HpCalibration.cs             (unchanged)
  Hsv.cs / HsvTolerance.cs     (unchanged)
  HpBarAnalyzer.cs / Detector  (unchanged)
  HpSmoother.cs                (unchanged)
  WgcScreenCapture.cs          NEW — replaces WindowsScreenCapture.cs
  D3D11Interop.cs              NEW — CsWin32-generated bindings + helpers
  MonitorResolver.cs           NEW — pure: HpRegion → HMONITOR + origin
  BgraCropper.cs               NEW — pure: stride-aware sub-rect copy
  NullScreenCapture.cs         NEW — no-op fallback for WGC-init failure
  WindowsScreenCapture.cs      DELETED

Diagnostics/
  Log.cs                       (unchanged)
  CaptureDiagnostic.cs         (unchanged)
  FullscreenDetector.cs        NEW — 1 Hz polling of SHQueryUserNotificationState

Tray/
  TrayIcon.cs                  MODIFIED — add ShowBalloon(title, text)

App.xaml.cs                    MODIFIED — wire detector + balloon one-shot
MainWindow.xaml{,.cs}          MODIFIED — status row + capture-unavailable banner
GamePartyHud.csproj            MODIFIED — add CsWin32 PackageReference
NativeMethods.txt              NEW — CsWin32 input list

tests/GamePartyHud.Tests/
  Capture/MonitorResolverTests.cs       NEW
  Capture/BgraCropperTests.cs           NEW
  Diagnostics/FullscreenDetectorTests.cs NEW
```

Dependency direction (preserved from the existing layout): `Capture/` and `Diagnostics/` depend only on BCL + interop. UI (`MainWindow`, `Tray/`) depends on `Diagnostics/FullscreenDetector` and `Capture/IScreenCapture`. The composition root in `App.xaml.cs` is the only place concrete implementations are constructed.

### 1.3 Composition root changes

[`App.xaml.cs:117`](../../../src/GamePartyHud/App.xaml.cs#L117) currently does:

```csharp
_capture = new WindowsScreenCapture();
```

Becomes:

```csharp
try
{
    _capture = new WgcScreenCapture();
    Log.Info("Screen capture: WgcScreenCapture (Windows.Graphics.Capture).");
}
catch (Exception ex)
{
    Log.Error("WgcScreenCapture init failed; falling back to NullScreenCapture (HP capture disabled).", ex);
    _capture = new NullScreenCapture();
    _captureUnavailableReason = ex.Message;
}
```

`MainWindow` reads the (nullable) `_captureUnavailableReason` from the controller surface and shows a banner if non-null.

### 1.4 Threading

WGC's `FrameArrived` event fires on a background thread. Per the lazy-session-per-capture policy (§2.4), each `CaptureBgraAsync` call:

1. Allocates a fresh `Direct3D11CaptureFramePool` + `GraphicsCaptureSession` on the calling thread (the orchestrator's `Task.Run` loop, off the UI thread — see [`PartyOrchestrator.PollAndBroadcastLoopAsync`](../../../src/GamePartyHud/Party/PartyOrchestrator.cs#L126)).
2. Calls `session.StartCapture()`.
3. Awaits the first `FrameArrived` (with a 250 ms timeout) using a `TaskCompletionSource`.
4. Maps the texture, copies / crops to BGRA via `BgraCropper`, returns.
5. Disposes the session + frame pool.

The D3D11 device is constructed once in `WgcScreenCapture`'s constructor and reused across all calls.

`FullscreenDetector` polls on its own `DispatcherTimer` (1 Hz) on the UI thread — `SHQueryUserNotificationState` is a microsecond-cheap call.

---

## 2. WGC capture engine

### 2.1 Interop style — CsWin32

`Microsoft.Windows.CsWin32` is added as a build-time PackageReference (analyzer; zero runtime weight). A new `NativeMethods.txt` lists the APIs we want generated:

```
D3D11CreateDevice
ID3D11Device
ID3D11DeviceContext
ID3D11Texture2D
D3D11_TEXTURE2D_DESC
D3D11_BOX
D3D11_MAPPED_SUBRESOURCE
D3D11_USAGE
D3D11_CPU_ACCESS_FLAG
D3D11_BIND_FLAG
DXGI_FORMAT
IDXGIDevice
CreateDirect3D11DeviceFromDXGIDevice
IDirect3DDxgiInterfaceAccess
SHQueryUserNotificationState
QUERY_USER_NOTIFICATION_STATE
MonitorFromPoint
GetMonitorInfoW
MONITORINFO
```

This matches the project's hand-rolled-P/Invoke aesthetic ([`HitTestInterop.cs`](../../../src/GamePartyHud/Hud/HitTestInterop.cs) is the reference style) but moves the boilerplate to a generator. No new runtime dependency; the existing minimal NuGet footprint (`System.Drawing.Common`, `WPF-UI`) becomes (`WPF-UI`) — `System.Drawing.Common` can be dropped once `WindowsScreenCapture` is deleted, since nothing else references `Bitmap` / `Graphics`.

### 2.2 `WgcScreenCapture` lifecycle

```csharp
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WgcScreenCapture : IScreenCapture, IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;

    public WgcScreenCapture()
    {
        // D3D11CreateDevice with BGRA_SUPPORT so WGC can deliver BGRA frames.
        // Throws on failure; the App composition root catches and swaps in NullScreenCapture.
        (_device, _context) = D3D11Interop.CreateBgraDevice();
        _winrtDevice = D3D11Interop.WrapForWinRT(_device);
    }

    public ValueTask<byte[]> CaptureBgraAsync(HpRegion region, CancellationToken ct = default);

    public void Dispose() { /* release _winrtDevice, _context, _device */ }
}
```

The D3D11 device is constructed once per app lifetime, disposed by `App.OnExit`. Per Q3, the session and frame pool are *not* held here — they're constructed inside each `CaptureBgraAsync` call.

### 2.3 Capture flow per call

The whole body sits inside an outer try/catch on `Exception` that logs and returns `Array.Empty<byte>()`. Cancellation tokens propagate through unchanged (`OperationCanceledException` rethrown by the linked CTS); every other failure becomes a single-tick empty buffer per the table in §2.8. Only argument validation (`ct.ThrowIfCancellationRequested()`) and the empty-region short-circuit run *before* the try block:

```csharp
public async ValueTask<byte[]> CaptureBgraAsync(HpRegion region, CancellationToken ct)
{
    if (region.W <= 0 || region.H <= 0) return Array.Empty<byte>();
    ct.ThrowIfCancellationRequested();

    try
    {
        // 1. Resolve which monitor contains the region (pure logic over Win32 monitor enum).
        var (hmonitor, monitorOrigin) = MonitorResolver.ResolveFor(region);

        // 2. Build the WGC capture item from the monitor handle.
        var item = D3D11Interop.CreateCaptureItemForMonitor(hmonitor);

        // 3. Spin up a single-frame pool sized to the monitor.
        using var pool = Direct3D11CaptureFramePool.Create(
            _winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 2,
            size: item.Size);
        using var session = pool.CreateCaptureSession(item);

        // 4. Disable the yellow capture border on Windows 11 22H2+. Silent no-op on older builds.
        if (D3D11Interop.IsBorderRequiredSettable())
            session.IsBorderRequired = false;

        // 5. Wire FrameArrived to a TaskCompletionSource and start.
        var tcs = new TaskCompletionSource<Direct3D11CaptureFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        pool.FrameArrived += (s, _) =>
        {
            var f = s.TryGetNextFrame();
            if (f is not null) tcs.TrySetResult(f);
        };
        session.StartCapture();

        // 6. Wait up to 250 ms for the first frame.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        using var reg = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token));

        using var frame = await tcs.Task.ConfigureAwait(false);

        // 7. Crop the monitor-sized BGRA surface to the region (in monitor-local coords)
        //    via D3D11 staging texture; then BgraCropper does the byte marshaling.
        var monitorLocal = new HpRegion(
            region.Monitor,
            region.X - monitorOrigin.X,
            region.Y - monitorOrigin.Y,
            region.W, region.H);
        return D3D11Interop.CropToBgra(_context, frame.Surface, monitorLocal);
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        Log.Warn($"WgcScreenCapture: single-tick capture failure ({ex.GetType().Name}: {ex.Message}); returning empty.");
        return Array.Empty<byte>();
    }
}
```

### 2.4 Lazy session-per-capture rationale (Q3)

Continuous WGC sessions deliver frames at the display's refresh rate (60–240 fps); at our 2 s poll cadence that's >99 % wasted GPU work. Lazy session-per-capture has zero idle cost and pays a one-time ~50–250 ms setup cost per capture, which is invisible inside the 2 s poll. The persistent D3D11 device covers the only expensive constructor call.

### 2.5 Monitor resolution

`MonitorResolver` is pure logic, decoupled from D3D11, so it is unit-tested without a real monitor:

```csharp
public static class MonitorResolver
{
    /// <summary>
    /// Finds the monitor whose virtual-desktop rect contains the region's
    /// center point. Returns the HMONITOR plus that monitor's top-left in
    /// virtual-desktop coords (so we can rebase region.X/Y to monitor-local).
    ///
    /// If the region's center isn't on any monitor (unplugged display, etc.),
    /// throws InvalidOperationException — caller logs and treats it as a
    /// single-tick capture failure.
    /// </summary>
    public static (IntPtr Hmonitor, Point Origin) ResolveFor(HpRegion region);

    // Internal overload taking an injectable monitor list, for tests.
    internal static (IntPtr Hmonitor, Point Origin) ResolveFor(
        HpRegion region,
        IReadOnlyList<MonitorInfo> monitors);
}

internal readonly record struct MonitorInfo(IntPtr Hmonitor, Rectangle VirtualBounds);
```

Implementation uses `MonitorFromPoint(POINT, MONITOR_DEFAULTTONULL)` with the region's center, then `GetMonitorInfo` for the origin. Center-point (rather than top-left) is the right pick: if the user calibrated near a monitor edge, their region's top-left could be one pixel off the boundary; the center is a stable choice.

**Edge case: region spans two monitors.** With WGC monitor capture, only one monitor's pixels can be delivered per call. `MonitorResolver` returns the monitor of the region's center; `D3D11Interop.CropToBgra` clips the crop box to that monitor's bounds. The HP analyzer already tolerates short-rect inputs (it scans columns, not exact width). A `Log.Warn` fires once per region (deduplicated by `(X,Y,W,H)` tuple) so we don't spam the log.

### 2.6 Cropping — `BgraCropper`

The captured surface is an `IDirect3DSurface` wrapping an `ID3D11Texture2D` of monitor size. `D3D11Interop.CropToBgra`:

1. From the `IDirect3DSurface`, gets `IDirect3DDxgiInterfaceAccess` and pulls the underlying `ID3D11Texture2D` (the GPU-side captured frame).
2. Creates a CPU-readable staging texture sized `region.W × region.H` (`D3D11_USAGE_STAGING`, `D3D11_CPU_ACCESS_READ`).
3. `CopySubresourceRegion` from the captured texture to the staging texture, using a `D3D11_BOX` of `(monitorLocal.X, Y, +W, +H, 0, 1)`, clipped to monitor bounds.
4. `Map` the staging texture; hand the mapped pointer + `RowPitch` + region dimensions to `BgraCropper.Marshal(...)` which returns the final `byte[]`.
5. `Unmap`, dispose the staging texture, return.

`BgraCropper` is the pure bit:

```csharp
public static class BgraCropper
{
    /// <summary>
    /// Copies <paramref name="height"/> rows of <paramref name="rowBytes"/> bytes
    /// from <paramref name="src"/> (with stride <paramref name="srcRowPitch"/>) into
    /// a fresh byte[]. Forces alpha = 255 on every pixel.
    /// rowBytes must equal width * 4.
    /// </summary>
    public static byte[] Marshal(IntPtr src, int srcRowPitch, int width, int height);

    // Allocation-free overload for use in tight loops + unit tests over byte spans.
    public static byte[] Marshal(ReadOnlySpan<byte> src, int srcRowPitch, int width, int height);
}
```

The two overloads share an implementation; the `IntPtr` form is what `D3D11Interop` calls; the span form is what tests drive. Stride handling matches the existing GDI implementation ([`WindowsScreenCapture.cs:42-51`](../../../src/GamePartyHud/Capture/WindowsScreenCapture.cs#L42)) — D3D11's `RowPitch` may exceed `width * 4`, so we copy row-by-row.

### 2.7 DPI invariant

The app manifest declares PerMonitorV2 DPI awareness ([`app.manifest`](../../../src/GamePartyHud/app.manifest)). WGC delivers physical pixels on the captured monitor. `HpRegion` is documented as physical pixels on the virtual desktop. Both invariants are preserved — the only change is that virtual-desktop pixels are translated to monitor-local physical pixels before the GPU copy. No DPI-scaling math.

### 2.8 Error handling

| Failure | Behaviour |
|---|---|
| WGC not supported on this OS | Cannot occur on supported TFM (≥19041); `D3D11Interop` checks an OS-version gate at construction and throws if violated. |
| `D3D11CreateDevice` fails | Constructor throws → `App.OnStartup` catches → swap in `NullScreenCapture` + show MainWindow banner. |
| `MonitorResolver` finds no monitor | Single-tick failure: log, return empty buffer. Next tick retries. |
| `FrameArrived` doesn't fire within 250 ms | Single-tick failure: log, return empty buffer. Most likely a transient driver hiccup. |
| Region spans monitor boundary | Log warn once per region tuple; clip to the center-monitor bounds and continue (per §2.5). |
| `Map` fails on staging texture | Single-tick failure: log + return empty. Disposal still runs. |

Empty buffer (`Array.Empty<byte>()`) is the existing "no calibration" sentinel — `HpBarAnalyzer` already handles a 0-length input gracefully. Single-tick capture failures result in a momentary "no HP update," but no crash, no broadcast, no UI flash.

### 2.9 `NullScreenCapture`

```csharp
[SupportedOSPlatform("windows")]
public sealed class NullScreenCapture : IScreenCapture
{
    public ValueTask<byte[]> CaptureBgraAsync(HpRegion region, CancellationToken ct = default)
        => ValueTask.FromResult(Array.Empty<byte>());
}
```

Trivial — used only when `WgcScreenCapture` initialization fails. The orchestrator broadcasts `null` HP for this user (already handled — `StateMessage.Hp` is nullable and the analyzer is bypassed when the buffer is empty).

---

## 3. Fullscreen detection & status surface

### 3.1 Detection API — `SHQueryUserNotificationState`

This is the API Windows itself uses to decide whether to suppress notifications during gameplay (Focus Assist's "Game" automatic rule). We treat two state values as "fullscreen":

- `QUNS_RUNNING_D3D_FULL_SCREEN` — exclusive-fullscreen DXGI app foreground. Exactly the condition that defeats overlay rendering.
- `QUNS_PRESENTATION_MODE` — fullscreen presentation (PowerPoint etc.). Overlay also won't show; treat the same.

Other states (`QUNS_ACCEPTS_NOTIFICATIONS`, `QUNS_BUSY`, `QUNS_QUIET_TIME`, `QUNS_NOT_PRESENT`) → not fullscreen.

Why this over alternatives:

- **Window-rect vs. monitor-rect comparison.** Borderless-windowed games at full monitor size produce false positives — we don't want to nag a user whose HUD is working fine.
- **`IDXGIOutput::GetFullscreenState`.** Requires owning a DXGI swap chain to ask. Heavier interop, no benefit over `SHQueryUserNotificationState`.
- **`IsImmersiveProcess`.** Asks "is this a UWP-style immersive shell app," not "is this fullscreen." Wrong question.

`SHQueryUserNotificationState` returns `S_OK` and a state enum in microseconds. Available since Vista. No DPI / monitor edge cases.

### 3.2 `FullscreenDetector`

Lives in `Diagnostics/` (matches `Log.cs` placement — diagnostics shared across UI and party loop):

```csharp
public sealed class FullscreenDetector : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Func<QUERY_USER_NOTIFICATION_STATE> _stateProbe;
    private bool _isFullscreen;

    public bool IsFullscreen
    {
        get => _isFullscreen;
        private set
        {
            if (_isFullscreen == value) return;
            _isFullscreen = value;
            StateChanged?.Invoke(value);
        }
    }

    public event Action<bool>? StateChanged;

    public FullscreenDetector() : this(QueryUserNotificationState) { }

    /// <summary>Test-only constructor with an injectable state probe.</summary>
    internal FullscreenDetector(Func<QUERY_USER_NOTIFICATION_STATE> stateProbe)
    {
        _stateProbe = stateProbe;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        Poll();
    }

    private void Poll()
    {
        var s = _stateProbe();
        IsFullscreen = s == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN
                    || s == QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE;
    }

    public void Dispose() => _timer.Stop();

    /// <summary>
    /// Production probe — calls the CsWin32-generated binding for
    /// <c>SHQueryUserNotificationState</c> in <c>shell32.dll</c>. The binding
    /// is regenerated from <c>NativeMethods.txt</c>; the helper here is
    /// thin enough to inline if the indirection feels excessive.
    /// </summary>
    private static QUERY_USER_NOTIFICATION_STATE QueryUserNotificationState()
    {
        Windows.Win32.PInvoke.SHQueryUserNotificationState(out var state).ThrowOnFailure();
        return state;
    }
}
```

`QUERY_USER_NOTIFICATION_STATE` is the CsWin32-generated enum (not a hand-written wrapper); its members carry the `QUNS_` prefix to match the Win32 SDK. The `D3D11Interop` helpers in `Capture/` similarly call `Windows.Win32.PInvoke` for D3D11 / DXGI / monitor-enum APIs — there is no separate hand-rolled interop layer; all native calls funnel through CsWin32-generated bindings.

1 Hz polling on the UI thread's dispatcher. Microsecond-cheap; no measurable budget impact. The status row updates within ≤ 1 s of an alt-tab.

### 3.3 Composition-root wiring

[`App.xaml.cs:OnStartup`](../../../src/GamePartyHud/App.xaml.cs#L71) gains:

```csharp
_fullscreenDetector = new FullscreenDetector();
_fullscreenDetector.StateChanged += OnFullscreenChanged;
```

The `MainWindow.IController` surface adds:

```csharp
public interface IController
{
    // ...existing...
    bool IsGameFullscreen { get; }
    string? CaptureUnavailableReason { get; }   // null when capture is healthy
    event Action<bool>? FullscreenStateChanged;
}
```

`MainWindow` depends on the controller surface, not on `FullscreenDetector` directly — same indirection pattern as `PartyStateChanged`.

### 3.4 Status row in MainWindow

A new `InfoBar` inside `InPartySection`, below `MemberCountDisplay` ([`MainWindow.xaml:172`](../../../src/GamePartyHud/MainWindow.xaml#L172)):

```xml
<ui:InfoBar x:Name="FullscreenStatus"
            Margin="0,8,0,0"
            IsClosable="False"
            Severity="Informational"
            IsOpen="False"
            Title="Game in fullscreen mode"
            Message="Your party still sees your HP — but the HUD overlay is hidden by Windows on the same monitor. Switch the game to borderless windowed, or (if you have a second monitor) drag the HUD onto it."/>
```

`MainWindow.xaml.cs` flips `FullscreenStatus.IsOpen` in response to `IController.FullscreenStateChanged`. The visibility predicate is `IsInParty && IsFullscreen` — outside a party there's no overlay to hide, so the message would be confusing.

A second `InfoBar` near the top of the scroll region — `CaptureUnavailableBanner` — is shown when `IController.CaptureUnavailableReason` is non-null:

> **Screen capture unavailable.** Your GPU or display driver may not support the Windows screen-capture API. The HUD will still show your teammates, but your HP won't broadcast.
>
> *(`Severity="Warning"`, `IsClosable="True"`)*

### 3.5 One-time tray balloon

Per Q2: fire a tray balloon the **first time per session** that fullscreen is detected after the user joined a party. State is held by `App`:

```csharp
private bool _balloonShownThisSession;

private void OnFullscreenChanged(bool isFs)
{
    FullscreenStateChanged?.Invoke(isFs);

    if (isFs
        && _currentPartyId is not null
        && !_balloonShownThisSession)
    {
        _balloonShownThisSession = true;
        _tray?.ShowBalloon(
            title: "Game Party HUD",
            text:  "Your game is in fullscreen mode — the HUD is hidden, but your party still sees your HP. " +
                   "Switch to borderless windowed, or move the HUD to a second monitor.");
    }
}
```

`_balloonShownThisSession` is reset to `false` inside `LeavePartyAsync` so re-joining a party re-arms the one-shot — useful if the user closes and reopens their game between parties.

**Honest UX caveat (called out in §3.4 framing):** the balloon may not surface during exclusive fullscreen because Focus Assist's "Game" rule suppresses notifications. That's why the persistent in-app status row is the load-bearing surface — the balloon is a discoverability hint that fires on the *transition* into fullscreen (when Focus Assist hasn't fully kicked in on most systems) and reaches users who alt-tab back to the desktop.

### 3.6 `TrayIcon.ShowBalloon`

[`TrayIcon`](../../../src/GamePartyHud/Tray/TrayIcon.cs) wraps a `System.Windows.Forms.NotifyIcon`. New method:

```csharp
public void ShowBalloon(string title, string text)
{
    _notifyIcon.BalloonTipTitle = title;
    _notifyIcon.BalloonTipText = text;
    _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
    _notifyIcon.ShowBalloonTip(8000); // OS clamps; Win10+ uses ~5 s.
}
```

---

## 4. Testing strategy

Per [CLAUDE.md](../../../CLAUDE.md): pure logic gets unit tests (TDD), UI is manually verified, no flaky automation. The new code splits cleanly along that line.

### 4.1 Unit-tested (pure logic)

| Module | Coverage | Approach |
|---|---|---|
| `MonitorResolver` | Region on primary; region on secondary; region near monitor edges; region whose center is off all monitors (throws); region spanning two monitors (returns center-monitor + clipping caller). | Internal overload taking `IReadOnlyList<MonitorInfo>` is the test seam. Cover geometric math, not Win32 plumbing. |
| `BgraCropper` | Crop a known-pattern byte[] of monitor size to a sub-rect; verify exact bytes per pixel; verify alpha forced to 255; verify stride math when input row-pitch > `W*4` (synthetic stride); zero-size and 1-pixel edge cases. | Span overload takes raw inputs and returns raw outputs — no GPU. |
| `FullscreenDetector` | `IsFullscreen` matches the last probed state; `StateChanged` fires exactly on transitions, not on every poll; correct mapping of `QUNS_RUNNING_D3D_FULL_SCREEN` and `QUNS_PRESENTATION_MODE` to true, all others to false. | Test-only constructor with `Func<QUERY_USER_NOTIFICATION_STATE>` injection. |

The `BgraCropper` extraction (the only structural change) pulls stride / crop math out of the D3D11 function so it has a no-GPU testable surface. Worth the split because stride math has a long history of off-by-one bugs.

TDD order: write each test file first, watch it fail, then implement the unit it covers. The `MonitorResolver` tests are particularly valuable because the multi-monitor edge cases are easy to get wrong and impossible to manually exercise on a single-monitor dev machine.

### 4.2 Manually verified (smoke checklist)

These don't get automated tests — they need a real GPU, real monitors, a real game in a real fullscreen mode, or all of the above.

1. **Regression: borderless windowed capture.** With a known-calibrated HP bar, confirm post-change readings match pre-change for the same scene. (Side-by-side the two builds against a static screenshot displayed full-size in a fullscreen image viewer if MO2 access isn't available.)
2. **New behaviour: exclusive fullscreen capture.** Same HP bar, same calibration, game in true exclusive fullscreen — confirm HP reads correctly (today returns ~0 / black; after change should match windowed).
3. **Multi-monitor.** Calibrate HP on the secondary monitor while game is on primary; confirm capture works.
4. **Status row.** Join party → enter fullscreen game → InfoBar appears in MainWindow with the agreed wording. Alt-tab out → InfoBar disappears within ≤ 1 s. Leave party → InfoBar hidden regardless of game state.
5. **One-time balloon.** First fullscreen-after-join in a session → balloon fires. Toggle fullscreen off and on again same session → no second balloon. Leave party, rejoin → balloon re-arms (fires again on next fullscreen).
6. **Failure path: forced D3D11 init failure.** Hard to trigger naturally; a debug-only env-var gate (`#if DEBUG` reading `GPH_FORCE_CAPTURE_INIT_FAIL=1`) simulates it. Verify `CaptureUnavailableBanner` appears in MainWindow and the rest of the app keeps working.
7. **Performance soak.** [CLAUDE.md](../../../CLAUDE.md) mandates an 8-hour run before tagging a release; not a per-PR gate, but the merge note must call out "needs soak before next release tag."

### 4.3 Not tested

Explicit non-coverage to keep the surface honest:

- **Actual WGC frame retrieval** (`D3D11Interop.CaptureBgraAsync` minus the `BgraCropper` slice). Requires a real GPU; CI lacks one. Verified by smoke #1 and #2.
- **Tray balloon visibility under Focus Assist.** OS owns this; we surface the call and trust the OS. §3.5's caveat is the documentation.
- **`SHQueryUserNotificationState` correctness on every Windows build.** API stable since Vista; trusted. Manual smoke catches drift.

### 4.4 Existing tests must stay green

| File | Status |
|---|---|
| `tests/GamePartyHud.Tests/Capture/HpBarAnalyzerTests.cs` | Unchanged; analyzer still operates on a BGRA byte[] of `region.W * region.H * 4`. |
| `tests/GamePartyHud.Tests/Capture/HpBarDetectorTests.cs` | Unchanged. |
| `tests/GamePartyHud.Tests/Capture/HpSmootherTests.cs` | Unchanged. |
| `tests/GamePartyHud.Tests/Capture/SampleImageRegressionTests.cs` | Unchanged — runs against pre-captured PNGs, not the live capture path. |
| `tests/GamePartyHud.Tests/Capture/SyntheticBitmap.cs` | Unchanged; helper used by analyzer tests. |
| `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs` | Unchanged — `HpRegion` schema unchanged. |
| All `Party/`, `Network/`, `Hud/` tests | Unchanged. |

Current 96 tests go to ~111 (MonitorResolver + BgraCropper + FullscreenDetector). No existing test deleted or modified.

---

## 5. Honest impact assessment

This design **does not** make the HUD overlay visible above an exclusive-fullscreen DXGI game on the same monitor. That's blocked by [CLAUDE.md](../../../CLAUDE.md)'s anti-cheat-friendliness rule (the only known techniques are DXGI hooking / DLL injection). The design accepts this and improves a different set of scenarios:

| Scenario | Before | After |
|---|---|---|
| Single-monitor, exclusive fullscreen | HP capture returns black; teammates receive zero / stale HP. HUD invisible. | HP capture works → **teammates see correct HP**. HUD still invisible to the user themselves. |
| Multi-monitor, fullscreen on one of them | HP capture broken; HUD on monitor 2 is technically visible but the user's own card shows stale HP. | HP capture works; user drags HUD to monitor 2 → **fully working HUD experience including their own card**. |
| Frequent alt-tabber | HP broadcasts oscillate between correct and zero. | HP stays correct regardless of alt-tab state. |
| Anyone confused by missing HUD | Mysterious silence. | Status row + balloon explain why and what to do. |

The headline single-monitor-on-same-screen case is unchanged. The design is worth shipping if the affected players are predominantly multi-monitor users, alt-tabbers, or party-members-of-affected-players (who benefit from the broadcast-correctness improvement regardless of their own setup).

---

## 6. File summary

### New files

- `src/GamePartyHud/Capture/WgcScreenCapture.cs`
- `src/GamePartyHud/Capture/D3D11Interop.cs`
- `src/GamePartyHud/Capture/MonitorResolver.cs`
- `src/GamePartyHud/Capture/BgraCropper.cs`
- `src/GamePartyHud/Capture/NullScreenCapture.cs`
- `src/GamePartyHud/Diagnostics/FullscreenDetector.cs`
- `src/GamePartyHud/NativeMethods.txt`
- `tests/GamePartyHud.Tests/Capture/MonitorResolverTests.cs`
- `tests/GamePartyHud.Tests/Capture/BgraCropperTests.cs`
- `tests/GamePartyHud.Tests/Diagnostics/FullscreenDetectorTests.cs`

### Modified

- `src/GamePartyHud/GamePartyHud.csproj` — add `Microsoft.Windows.CsWin32` (build-time analyzer); drop `System.Drawing.Common` once `WindowsScreenCapture.cs` is removed.
- `src/GamePartyHud/App.xaml.cs` — try-catch around capture construction; create `FullscreenDetector`; expose `IsGameFullscreen`, `FullscreenStateChanged`, `CaptureUnavailableReason` on `IController`; manage `_balloonShownThisSession` and reset on `LeavePartyAsync`; call `_tray.ShowBalloon` on first fullscreen-after-join.
- `src/GamePartyHud/MainWindow.xaml` — add `FullscreenStatus` `InfoBar` inside `InPartySection`; add `CaptureUnavailableBanner` near top of scroll region.
- `src/GamePartyHud/MainWindow.xaml.cs` — subscribe to `FullscreenStateChanged`; flip `FullscreenStatus.IsOpen` based on `IsInParty && IsFullscreen`; read `CaptureUnavailableReason` to drive `CaptureUnavailableBanner`.
- `src/GamePartyHud/Tray/TrayIcon.cs` — add `ShowBalloon(string title, string text)`.

### Deleted

- `src/GamePartyHud/Capture/WindowsScreenCapture.cs` — replaced.

### Unchanged

- `src/GamePartyHud/Capture/IScreenCapture.cs`, `HpRegion.cs`, `HpCalibration.cs`, `Hsv.cs`, `HsvTolerance.cs`, `FillDirection.cs`, `HpBarAnalyzer.cs`, `HpBarDetector.cs`, `HpSmoother.cs`.
- `src/GamePartyHud/Party/`, `src/GamePartyHud/Network/`, `src/GamePartyHud/Hud/`, `src/GamePartyHud/Calibration/`, `src/GamePartyHud/Config/` — no changes.
- `src/GamePartyHud/app.manifest` — PerMonitorV2 already declared.

---

## 7. Decision log

For traceability — settled during brainstorming:

1. **Capture engine = Windows.Graphics.Capture, monitor-handle flavour.** Preserves `HpRegion`'s screen-space schema and the existing calibration UX. `HpRegion.Monitor` field stays at 0 in this PR; resolution-by-region-center handles multi-monitor without a schema bump. (Q1)
2. **Fullscreen UX = persistent in-app status row + one-time tray balloon per party-join.** The status row is the load-bearing surface; the balloon is a discoverability hint. Wording mentions the second-monitor workaround in both surfaces. (Q2 + clarification)
3. **Frame pool lifecycle = lazy session-per-capture, persistent D3D11 device.** Zero idle GPU cost; per-capture latency of ~50–250 ms is invisible at 2 s poll cadence. (Q3)
4. **No GDI fallback on WGC failure.** WGC-init failure swaps in `NullScreenCapture` plus a banner; silent degradation to GDI would mask the very symptom we're fixing. (Q4)
5. **Detection API = `SHQueryUserNotificationState`.** Exact signal the OS uses for Focus Assist's "Game" rule. Cheap, no DPI / monitor edge cases.
6. **Interop style = CsWin32 source generator.** Matches the project's hand-rolled-P/Invoke aesthetic but moves boilerplate to a build-time analyzer. No new runtime NuGet.
7. **First-frame timeout = 250 ms.** Generous on integrated GPUs, well under the 2 s poll. Single-tick failures non-fatal — log and move on.
8. **`BgraCropper` extracted from `D3D11Interop` for testability.** Stride math is bug-prone and easy to test in isolation against synthetic byte arrays.
9. **Detector poll rate = 1 Hz.** Microsecond-cheap API, sub-second responsiveness on alt-tab, no measurable budget impact.
10. **Honest scope.** The headline single-monitor-on-same-screen case is unchanged; documented in §5 so anyone reading the spec sees the limitation up front.

---

## 8. Implementation plan

A single implementation plan covers this spec end-to-end:

- `docs/superpowers/plans/2026-05-01-fullscreen-capture-plan.md`

The plan sequences the work as: (1) add CsWin32 + `NativeMethods.txt`; (2) `BgraCropper` + tests; (3) `MonitorResolver` + tests; (4) `D3D11Interop` + `WgcScreenCapture`; (5) `NullScreenCapture` + composition-root fallback; (6) `FullscreenDetector` + tests; (7) MainWindow status + capture-unavailable banner; (8) `TrayIcon.ShowBalloon` + balloon wiring; (9) delete `WindowsScreenCapture`; (10) manual smoke checklist.
