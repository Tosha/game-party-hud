# Fullscreen capture & overlay-limitation UX — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the GDI BitBlt screen-capture backend with a Windows.Graphics.Capture (WGC) implementation so HP-bar reading works in exclusive-fullscreen DXGI games, and surface an honest in-app explanation of the residual overlay-visibility limitation (status row + one-time tray balloon).

**Architecture:** Two coordinated changes behind the unchanged `IScreenCapture` interface. The capture engine swap is the bigger piece (D3D11 + WGC + monitor-handle resolution + lazy-session-per-capture lifecycle). The fullscreen-state surface is a 1 Hz `SHQueryUserNotificationState` poller wired to a MainWindow `InfoBar` and a one-time tray balloon. See [`docs/superpowers/specs/2026-05-01-fullscreen-capture-design.md`](../specs/2026-05-01-fullscreen-capture-design.md) for the full design and rationale.

**Tech Stack:** C# 12 / .NET 8 / WPF, target `net8.0-windows10.0.19041.0`. WinRT projection (`Windows.Graphics.Capture`) for capture; `Microsoft.Windows.CsWin32` build-time analyzer for D3D11 / DXGI / shell32 P/Invoke; manual `[ComImport]` declarations for `IDirect3DDxgiInterfaceAccess` and `CreateDirect3D11DeviceFromDXGIDevice` (the WinRT/D3D11 bridging surface CsWin32 doesn't cover). xUnit + Moq for tests.

---

## Baseline check (before Task 1)

- [ ] **Step 0.1: Confirm existing tests pass on `main`**

```bash
dotnet test
```

Expected: green, ~96 tests pass. If anything is red on `main`, stop and fix that first — this plan assumes a clean baseline.

- [ ] **Step 0.2: Create a feature branch**

```bash
git checkout -b feat/fullscreen-capture
```

---

## Task 1: Add CsWin32 + NativeMethods.txt

**Files:**
- Modify: `src/GamePartyHud/GamePartyHud.csproj`
- Create: `src/GamePartyHud/NativeMethods.txt`

- [ ] **Step 1.1: Add the CsWin32 PackageReference**

Edit `src/GamePartyHud/GamePartyHud.csproj`. In the existing `<ItemGroup>` that lists `<PackageReference>`s (lines 38–41), add **before** the closing `</ItemGroup>`:

```xml
    <PackageReference Include="Microsoft.Windows.CsWin32" Version="0.3.106">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
```

`PrivateAssets=all` keeps the analyzer build-time-only; nothing about CsWin32 ends up in the published `.exe`.

- [ ] **Step 1.2: Create NativeMethods.txt with the API list**

Create `src/GamePartyHud/NativeMethods.txt` with exactly this content:

```
D3D11CreateDevice
D3D_DRIVER_TYPE
D3D11_CREATE_DEVICE_FLAG
D3D_FEATURE_LEVEL
ID3D11Device
ID3D11DeviceContext
ID3D11Texture2D
D3D11_TEXTURE2D_DESC
D3D11_BOX
D3D11_MAPPED_SUBRESOURCE
D3D11_USAGE
D3D11_CPU_ACCESS_FLAG
D3D11_BIND_FLAG
D3D11_MAP
DXGI_FORMAT
IDXGIDevice
SHQueryUserNotificationState
QUERY_USER_NOTIFICATION_STATE
MonitorFromPoint
GetMonitorInfoW
MONITORINFO
MONITOR_FROM_FLAGS
POINT
RECT
HMONITOR
```

- [ ] **Step 1.3: Verify the project still builds**

```bash
dotnet build src/GamePartyHud/GamePartyHud.csproj
```

Expected: succeeds. CsWin32 generates bindings under `Windows.Win32.PInvoke` and `Windows.Win32.Graphics.Direct3D11.*` namespaces. No code consumes them yet — Task 4+ will.

- [ ] **Step 1.4: Commit**

```bash
git add src/GamePartyHud/GamePartyHud.csproj src/GamePartyHud/NativeMethods.txt
git commit -m "chore(capture): add CsWin32 source generator + NativeMethods.txt for WGC interop"
```

---

## Task 2: BgraCropper (pure helper, TDD)

**Files:**
- Create: `src/GamePartyHud/Capture/BgraCropper.cs`
- Create: `tests/GamePartyHud.Tests/Capture/BgraCropperTests.cs`

- [ ] **Step 2.1: Write the failing tests**

Create `tests/GamePartyHud.Tests/Capture/BgraCropperTests.cs`:

```csharp
using System;
using GamePartyHud.Capture;
using Xunit;

namespace GamePartyHud.Tests.Capture;

public class BgraCropperTests
{
    [Fact]
    public void Marshal_TightStride_ReturnsExactBytes()
    {
        // 2x2 image, stride=8 (= width*4). Pixels: BGRA tuples.
        var src = new byte[]
        {
            10, 20, 30, 0xFF,   40, 50, 60, 0xFF,
            70, 80, 90, 0xFF,   100, 110, 120, 0xFF,
        };
        var result = BgraCropper.Marshal(src, srcRowPitch: 8, width: 2, height: 2);
        Assert.Equal(src, result);
    }

    [Fact]
    public void Marshal_PaddedStride_DropsPaddingBytes()
    {
        // 2x2 image with stride=12 (= width*4 + 4 bytes padding per row).
        var src = new byte[]
        {
            10, 20, 30, 0xFF,   40, 50, 60, 0xFF,    0, 0, 0, 0, // row 0 + padding
            70, 80, 90, 0xFF,   100, 110, 120, 0xFF, 0, 0, 0, 0, // row 1 + padding
        };
        var expected = new byte[]
        {
            10, 20, 30, 0xFF,   40, 50, 60, 0xFF,
            70, 80, 90, 0xFF,   100, 110, 120, 0xFF,
        };
        var result = BgraCropper.Marshal(src, srcRowPitch: 12, width: 2, height: 2);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Marshal_ForcesAlphaTo255()
    {
        // Source with alpha = 0 (which is what GDI+/WGC sometimes returns).
        var src = new byte[]
        {
            10, 20, 30, 0x00,   40, 50, 60, 0x80,
        };
        var result = BgraCropper.Marshal(src, srcRowPitch: 8, width: 2, height: 1);
        Assert.Equal(0xFF, result[3]);  // pixel 0 alpha
        Assert.Equal(0xFF, result[7]);  // pixel 1 alpha
        // BGR untouched
        Assert.Equal(10, result[0]); Assert.Equal(20, result[1]); Assert.Equal(30, result[2]);
        Assert.Equal(40, result[4]); Assert.Equal(50, result[5]); Assert.Equal(60, result[6]);
    }

    [Fact]
    public void Marshal_OnePixel_Works()
    {
        var src = new byte[] { 1, 2, 3, 0xFF };
        var result = BgraCropper.Marshal(src, srcRowPitch: 4, width: 1, height: 1);
        Assert.Equal(src, result);
    }

    [Fact]
    public void Marshal_LargeStride_OnlyCopiesRowBytes()
    {
        // Confirms we DON'T copy the full srcRowPitch * height — only width*4 per row.
        // A bug here would leak garbage from the padding bytes into the output.
        const int width = 3, height = 2, pitch = 32;  // wildly oversized stride
        var src = new byte[pitch * height];
        // Mark each "real" pixel byte; leave padding as 0xCC.
        for (int i = 0; i < src.Length; i++) src[i] = 0xCC;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width * 4; x++)
                src[y * pitch + x] = (byte)(y * 100 + x);

        var result = BgraCropper.Marshal(src, pitch, width, height);

        Assert.Equal(width * 4 * height, result.Length);
        // No 0xCC anywhere — padding must not leak.
        Assert.DoesNotContain((byte)0xCC, result);
        // Alpha bytes (every 4th) all 0xFF.
        for (int i = 3; i < result.Length; i += 4) Assert.Equal(0xFF, result[i]);
    }

    [Fact]
    public void Marshal_ZeroSize_ReturnsEmpty()
    {
        var result = BgraCropper.Marshal(ReadOnlySpan<byte>.Empty, srcRowPitch: 0, width: 0, height: 0);
        Assert.Empty(result);
    }
}
```

- [ ] **Step 2.2: Run tests to confirm they fail**

```bash
dotnet test --filter "FullyQualifiedName~BgraCropperTests"
```

Expected: compilation error (`BgraCropper` doesn't exist yet).

- [ ] **Step 2.3: Implement `BgraCropper`**

Create `src/GamePartyHud/Capture/BgraCropper.cs`:

```csharp
using System;
using System.Runtime.InteropServices;

namespace GamePartyHud.Capture;

/// <summary>
/// Pure helper for marshalling a strided BGRA buffer into a tight, allocation-shaped
/// <c>byte[]</c> sized exactly <c>width*height*4</c>. Forces every pixel's alpha to 255,
/// because GDI+/WGC sometimes returns 0 in the alpha channel and downstream code
/// (HpBarAnalyzer) treats 0-alpha as transparent.
///
/// Extracted from the WGC capture path so the stride math has a no-GPU testable
/// surface — D3D11's <c>RowPitch</c> may exceed <c>width*4</c>, which makes
/// row-by-row copying mandatory.
/// </summary>
public static class BgraCropper
{
    /// <summary>Span overload used by tests (and any in-process caller).</summary>
    public static byte[] Marshal(ReadOnlySpan<byte> src, int srcRowPitch, int width, int height)
    {
        if (width == 0 || height == 0) return Array.Empty<byte>();

        int rowBytes = width * 4;
        var dst = new byte[rowBytes * height];

        for (int y = 0; y < height; y++)
        {
            var srcRow = src.Slice(y * srcRowPitch, rowBytes);
            srcRow.CopyTo(dst.AsSpan(y * rowBytes, rowBytes));
        }
        for (int i = 3; i < dst.Length; i += 4) dst[i] = 0xFF;
        return dst;
    }

    /// <summary>Pointer overload used by D3D11Interop after <c>Map</c>.</summary>
    public static unsafe byte[] Marshal(IntPtr src, int srcRowPitch, int width, int height)
    {
        if (width == 0 || height == 0) return Array.Empty<byte>();
        int totalBytes = checked(srcRowPitch * height);
        var span = new ReadOnlySpan<byte>((void*)src, totalBytes);
        return Marshal(span, srcRowPitch, width, height);
    }
}
```

The pointer overload requires `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` in the csproj. Add it now to avoid a build break in step 2.4:

Edit `src/GamePartyHud/GamePartyHud.csproj`. In the first `<PropertyGroup>` (around line 14, after `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`), add:

```xml
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
```

- [ ] **Step 2.4: Run tests to confirm they pass**

```bash
dotnet test --filter "FullyQualifiedName~BgraCropperTests"
```

Expected: 6 tests pass.

- [ ] **Step 2.5: Commit**

```bash
git add src/GamePartyHud/Capture/BgraCropper.cs src/GamePartyHud/GamePartyHud.csproj tests/GamePartyHud.Tests/Capture/BgraCropperTests.cs
git commit -m "feat(capture): BgraCropper — pure stride-aware sub-rect marshalling helper"
```

---

## Task 3: MonitorResolver (pure logic, TDD)

**Files:**
- Create: `src/GamePartyHud/Capture/MonitorResolver.cs`
- Create: `tests/GamePartyHud.Tests/Capture/MonitorResolverTests.cs`

- [ ] **Step 3.1: Write the failing tests**

Create `tests/GamePartyHud.Tests/Capture/MonitorResolverTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using GamePartyHud.Capture;
using Xunit;

namespace GamePartyHud.Tests.Capture;

public class MonitorResolverTests
{
    private static MonitorInfo M(long handle, int x, int y, int w, int h)
        => new MonitorInfo(new IntPtr(handle), new Rectangle(x, y, w, h));

    private static readonly IReadOnlyList<MonitorInfo> TwoMonitors =
        new[]
        {
            M(1, 0,    0, 2560, 1440),  // primary
            M(2, 2560, 0, 1920, 1080),  // secondary, right of primary
        };

    [Fact]
    public void RegionOnPrimary_ReturnsPrimaryMonitor()
    {
        var region = new HpRegion(0, X: 100, Y: 200, W: 200, H: 20);
        var (h, origin) = MonitorResolver.ResolveFor(region, TwoMonitors);
        Assert.Equal(new IntPtr(1), h);
        Assert.Equal(new Point(0, 0), origin);
    }

    [Fact]
    public void RegionOnSecondary_ReturnsSecondaryMonitor()
    {
        // Center is at (3000, 210), which is on monitor 2 (starts at x=2560).
        var region = new HpRegion(0, X: 2900, Y: 200, W: 200, H: 20);
        var (h, origin) = MonitorResolver.ResolveFor(region, TwoMonitors);
        Assert.Equal(new IntPtr(2), h);
        Assert.Equal(new Point(2560, 0), origin);
    }

    [Fact]
    public void RegionCenterOffAllMonitors_Throws()
    {
        // Center at (10000, 10000) — outside both monitors.
        var region = new HpRegion(0, X: 9900, Y: 9900, W: 200, H: 200);
        Assert.Throws<InvalidOperationException>(
            () => MonitorResolver.ResolveFor(region, TwoMonitors));
    }

    [Fact]
    public void RegionStraddlingMonitorBoundary_ResolvesByCenter()
    {
        // Center at (2600, 100). Monitor 2 starts at x=2560, so center is on monitor 2.
        var region = new HpRegion(0, X: 2500, Y: 50, W: 200, H: 100);
        var (h, _) = MonitorResolver.ResolveFor(region, TwoMonitors);
        Assert.Equal(new IntPtr(2), h);
    }

    [Fact]
    public void RegionExactlyAtPrimaryEdge_ResolvesByCenter()
    {
        // Center at (2459, 100). Just barely on monitor 1.
        var region = new HpRegion(0, X: 2400, Y: 50, W: 118, H: 100);
        var (h, _) = MonitorResolver.ResolveFor(region, TwoMonitors);
        Assert.Equal(new IntPtr(1), h);
    }

    [Fact]
    public void SingleMonitorLayout_AlwaysReturnsThatMonitor()
    {
        var single = new[] { M(42, 0, 0, 1920, 1080) };
        var region = new HpRegion(0, X: 800, Y: 400, W: 100, H: 20);
        var (h, origin) = MonitorResolver.ResolveFor(region, single);
        Assert.Equal(new IntPtr(42), h);
        Assert.Equal(new Point(0, 0), origin);
    }
}
```

- [ ] **Step 3.2: Run tests to confirm they fail**

```bash
dotnet test --filter "FullyQualifiedName~MonitorResolverTests"
```

Expected: compilation errors (`MonitorResolver`, `MonitorInfo` don't exist).

- [ ] **Step 3.3: Implement `MonitorResolver`**

Create `src/GamePartyHud/Capture/MonitorResolver.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace GamePartyHud.Capture;

/// <summary>
/// Holds an enumerated monitor's HMONITOR plus its top-left in virtual-desktop
/// coordinates. Internal because callers outside <see cref="MonitorResolver"/>
/// should not be modeling monitors.
/// </summary>
public readonly record struct MonitorInfo(IntPtr Hmonitor, Rectangle VirtualBounds);

/// <summary>
/// Resolves a screen-space <see cref="HpRegion"/> to the monitor it belongs on,
/// returning the HMONITOR plus the origin of that monitor in virtual-desktop
/// coordinates. The origin is what callers use to rebase region.X / region.Y
/// into monitor-local coordinates before cropping a captured frame.
///
/// "Belongs on" = the monitor whose virtual-bounds contains the region's
/// center point. Center-point (rather than top-left) avoids an off-by-one
/// surprise if the user calibrated right at a monitor edge.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MonitorResolver
{
    public static (IntPtr Hmonitor, Point Origin) ResolveFor(HpRegion region)
        => ResolveFor(region, EnumerateMonitors());

    internal static (IntPtr Hmonitor, Point Origin) ResolveFor(
        HpRegion region,
        IReadOnlyList<MonitorInfo> monitors)
    {
        var center = new Point(region.X + region.W / 2, region.Y + region.H / 2);
        foreach (var m in monitors)
        {
            if (m.VirtualBounds.Contains(center))
                return (m.Hmonitor, m.VirtualBounds.Location);
        }
        throw new InvalidOperationException(
            $"HpRegion center ({center.X}, {center.Y}) is not on any connected monitor.");
    }

    private static IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        var list = new List<MonitorInfo>();
        unsafe
        {
            PInvoke.EnumDisplayMonitors(
                hdc: default,
                lprcClip: null,
                lpfnEnum: (HMONITOR hMonitor, HDC _, RECT* _, LPARAM _) =>
                {
                    var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
                    if (PInvoke.GetMonitorInfo(hMonitor, ref info))
                    {
                        var bounds = new Rectangle(
                            info.rcMonitor.left,
                            info.rcMonitor.top,
                            info.rcMonitor.right - info.rcMonitor.left,
                            info.rcMonitor.bottom - info.rcMonitor.top);
                        list.Add(new MonitorInfo(new IntPtr(hMonitor.Value), bounds));
                    }
                    return true;
                },
                dwData: default);
        }
        return list;
    }
}
```

The `EnumDisplayMonitors` API isn't currently in `NativeMethods.txt`. Add it now:

Edit `src/GamePartyHud/NativeMethods.txt` and append two lines:

```
EnumDisplayMonitors
HDC
```

- [ ] **Step 3.4: Run tests to confirm they pass**

```bash
dotnet test --filter "FullyQualifiedName~MonitorResolverTests"
```

Expected: 6 tests pass. (Production overload `ResolveFor(HpRegion)` isn't tested here — it would require real monitors. The internal overload covers the geometric logic.)

- [ ] **Step 3.5: Commit**

```bash
git add src/GamePartyHud/Capture/MonitorResolver.cs src/GamePartyHud/NativeMethods.txt tests/GamePartyHud.Tests/Capture/MonitorResolverTests.cs
git commit -m "feat(capture): MonitorResolver — pure HpRegion → HMONITOR + origin"
```

---

## Task 4: NullScreenCapture

**Files:**
- Create: `src/GamePartyHud/Capture/NullScreenCapture.cs`

- [ ] **Step 4.1: Implement**

Create `src/GamePartyHud/Capture/NullScreenCapture.cs`:

```csharp
using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace GamePartyHud.Capture;

/// <summary>
/// No-op <see cref="IScreenCapture"/> used when <c>WgcScreenCapture</c> fails to
/// initialise (rare — typically a software-only GPU or a broken display driver).
/// Returns <see cref="Array.Empty{T}"/>, which <see cref="HpBarAnalyzer"/>
/// treats the same as "no calibration": no HP, no broadcast, no UI flash. The
/// app keeps running so the user can still see teammates.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NullScreenCapture : IScreenCapture
{
    public ValueTask<byte[]> CaptureBgraAsync(HpRegion region, CancellationToken ct = default)
        => ValueTask.FromResult(Array.Empty<byte>());
}
```

No test — the body is one line and adding a test would be tautology.

- [ ] **Step 4.2: Verify the project builds**

```bash
dotnet build src/GamePartyHud/GamePartyHud.csproj
```

Expected: succeeds.

- [ ] **Step 4.3: Commit**

```bash
git add src/GamePartyHud/Capture/NullScreenCapture.cs
git commit -m "feat(capture): NullScreenCapture — no-op fallback for WGC-init failure"
```

---

## Task 5: Direct3D11Helper (manual COM interop bridge)

**Files:**
- Create: `src/GamePartyHud/Capture/Direct3D11Helper.cs`

This is the WinRT/D3D11 bridging surface that CsWin32 doesn't generate (it covers Win32 + COM-via-headers but not the WinRT projection's interop functions). Two hand-written declarations: the `IDirect3DDxgiInterfaceAccess` COM interface and the `CreateDirect3D11DeviceFromDXGIDevice` flat function.

- [ ] **Step 5.1: Implement**

Create `src/GamePartyHud/Capture/Direct3D11Helper.cs`:

```csharp
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32.Graphics.Direct3D11;
using WinRT;

namespace GamePartyHud.Capture;

/// <summary>
/// Bridges the WinRT <c>Windows.Graphics.DirectX.Direct3D11</c> projection and
/// the underlying D3D11 COM objects. Two crossings are needed:
///
///   1. <c>ID3D11Device</c> → <see cref="IDirect3DDevice"/>, so the device we
///      created via <c>D3D11CreateDevice</c> can be handed to a
///      <c>Direct3D11CaptureFramePool</c>.
///   2. <see cref="IDirect3DSurface"/> → <c>ID3D11Texture2D</c>, so the captured
///      WGC frame surface can be sampled with <c>CopySubresourceRegion</c>.
///
/// The interface guid and entry point are both publicly documented Microsoft
/// interop surfaces (see <c>windows.graphics.directx.direct3d11.interop.h</c>
/// in the Windows SDK).
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class Direct3D11Helper
{
    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    /// <summary>
    /// Wraps an <c>ID3D11Device</c> as a WinRT <see cref="IDirect3DDevice"/> so
    /// it can be passed to <c>Direct3D11CaptureFramePool.Create</c>.
    /// </summary>
    public static IDirect3DDevice CreateDirect3DDevice(ID3D11Device d3dDevice)
    {
        // The ID3D11Device is also an IDXGIDevice (same COM object, different facets).
        var dxgiDevicePtr = Marshal.GetIUnknownForObject(d3dDevice);
        try
        {
            int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevicePtr, out var graphicsDevicePtr);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                return MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevicePtr);
            }
            finally
            {
                Marshal.Release(graphicsDevicePtr);
            }
        }
        finally
        {
            Marshal.Release(dxgiDevicePtr);
        }
    }

    /// <summary>
    /// Extracts the underlying <c>ID3D11Texture2D</c> from a captured WGC
    /// surface so it can be sampled / copied via the D3D11 device context.
    /// </summary>
    public static ID3D11Texture2D GetD3D11Texture2D(Windows.Graphics.DirectX.Direct3D11.IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = typeof(ID3D11Texture2D).GUID;
        var ptr = access.GetInterface(ref iid);
        try
        {
            return (ID3D11Texture2D)Marshal.GetObjectForIUnknown(ptr);
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }
}
```

- [ ] **Step 5.2: Verify the project builds**

```bash
dotnet build src/GamePartyHud/GamePartyHud.csproj
```

Expected: succeeds. If the build fails complaining about `WinRT.MarshalInterface` not found, add `<CsWinRTUseWindowsUIXamlProjections>true</CsWinRTUseWindowsUIXamlProjections>` is **not** what we want — instead, the issue is that `WinRT.Runtime` is implicit in `net8.0-windows10.0.19041.0`. If still missing, add `<PackageReference Include="Microsoft.Windows.SDK.NET.Ref" Version="10.0.19041.55" />` — but try the build first; the standard SDK projection should cover it.

- [ ] **Step 5.3: Commit**

```bash
git add src/GamePartyHud/Capture/Direct3D11Helper.cs
git commit -m "feat(capture): Direct3D11Helper — WinRT/D3D11 bridging interop"
```

---

## Task 6: WgcScreenCapture

**Files:**
- Create: `src/GamePartyHud/Capture/WgcScreenCapture.cs`

This is the biggest single-file task. Manual smoke tests live in Task 14 — there's no automated test for this module since it requires a real GPU.

- [ ] **Step 6.1: Implement**

Create `src/GamePartyHud/Capture/WgcScreenCapture.cs`:

```csharp
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using GamePartyHud.Diagnostics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi.Common;

namespace GamePartyHud.Capture;

/// <summary>
/// Screen capture backed by Windows.Graphics.Capture. Works on
/// exclusive-fullscreen DXGI games (the GDI BitBlt backend it replaces does not).
/// Per-call lifecycle: a fresh <see cref="GraphicsCaptureSession"/> + frame pool
/// is created, fired, awaited for one frame (with a 250 ms timeout), then
/// disposed. The D3D11 device is constructed once and reused for the lifetime
/// of the instance.
///
/// Not anti-cheat-relevant: WGC uses the same OS path as Game Bar, OBS, and
/// Discord overlay; no DXGI hooking, no DLL injection.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WgcScreenCapture : IScreenCapture, IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private bool _disposed;

    public WgcScreenCapture()
    {
        unsafe
        {
            ID3D11Device? device = null;
            ID3D11DeviceContext? context = null;
            D3D_FEATURE_LEVEL achieved;

            int hr = PInvoke.D3D11CreateDevice(
                pAdapter: null,
                DriverType: D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                Software: default,
                Flags: D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                pFeatureLevels: null,
                FeatureLevels: 0,
                SDKVersion: 7,  // D3D11_SDK_VERSION
                ppDevice: out device,
                pFeatureLevel: &achieved,
                ppImmediateContext: out context);

            Marshal.ThrowExceptionForHR(hr);
            _device = device ?? throw new InvalidOperationException("D3D11CreateDevice returned null device.");
            _context = context ?? throw new InvalidOperationException("D3D11CreateDevice returned null context.");
        }

        _winrtDevice = Direct3D11Helper.CreateDirect3DDevice(_device);
    }

    public async ValueTask<byte[]> CaptureBgraAsync(HpRegion region, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (region.W <= 0 || region.H <= 0) return Array.Empty<byte>();
        ct.ThrowIfCancellationRequested();

        try
        {
            var (hmonitor, monitorOrigin) = MonitorResolver.ResolveFor(region);

            var item = CaptureItemForMonitor(hmonitor);

            using var pool = Direct3D11CaptureFramePool.Create(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                numberOfBuffers: 2,
                size: item.Size);
            using var session = pool.CreateCaptureSession(item);

            // Disable the yellow capture border on Windows 11 22H2+; silent no-op elsewhere.
            try { session.IsBorderRequired = false; } catch { /* property not present on older Win11 */ }

            var tcs = new TaskCompletionSource<Direct3D11CaptureFrame>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pool.FrameArrived += (s, _) =>
            {
                var f = s.TryGetNextFrame();
                if (f is not null) tcs.TrySetResult(f);
            };
            session.StartCapture();

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var reg = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token));

            using var frame = await tcs.Task.ConfigureAwait(false);

            return CropToBgra(frame, region, monitorOrigin);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"WgcScreenCapture: single-tick capture failure ({ex.GetType().Name}: {ex.Message}); returning empty.");
            return Array.Empty<byte>();
        }
    }

    private static GraphicsCaptureItem CaptureItemForMonitor(IntPtr hmonitor)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = typeof(GraphicsCaptureItem).GUID;
        var raw = interop.CreateForMonitor(hmonitor, iid);
        return MarshalInterface<GraphicsCaptureItem>.FromAbi(raw);
    }

    private byte[] CropToBgra(Direct3D11CaptureFrame frame, HpRegion region, Point monitorOrigin)
    {
        // 1. Get the underlying ID3D11Texture2D from the captured WGC surface.
        using var srcTexture = Direct3D11Helper.GetD3D11Texture2D(frame.Surface);

        // 2. Compute the monitor-local crop box, clipped to the captured surface size.
        var srcDesc = new D3D11_TEXTURE2D_DESC();
        srcTexture.GetDesc(out srcDesc);

        int localX = Math.Max(0, region.X - monitorOrigin.X);
        int localY = Math.Max(0, region.Y - monitorOrigin.Y);
        int width  = Math.Min(region.W, (int)srcDesc.Width  - localX);
        int height = Math.Min(region.H, (int)srcDesc.Height - localY);
        if (width <= 0 || height <= 0)
        {
            Log.Warn($"WgcScreenCapture: region clipped to zero on monitor {monitorOrigin} (region={region}); returning empty.");
            return Array.Empty<byte>();
        }

        // 3. Create a CPU-readable staging texture sized exactly to the crop.
        var stagingDesc = new D3D11_TEXTURE2D_DESC
        {
            Width  = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new Windows.Win32.Graphics.Dxgi.Common.DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = (uint)D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            MiscFlags = 0,
        };

        ID3D11Texture2D? staging = null;
        unsafe { _device.CreateTexture2D(&stagingDesc, null, out staging); }
        if (staging is null) throw new InvalidOperationException("CreateTexture2D returned null.");

        try
        {
            // 4. Copy the crop region from src into the staging texture.
            var box = new D3D11_BOX
            {
                left = (uint)localX,
                top = (uint)localY,
                front = 0,
                right = (uint)(localX + width),
                bottom = (uint)(localY + height),
                back = 1,
            };
            unsafe
            {
                _context.CopySubresourceRegion(
                    staging, 0,
                    DstX: 0, DstY: 0, DstZ: 0,
                    srcTexture, 0,
                    &box);
            }

            // 5. Map the staging texture and marshal into a tight byte[].
            D3D11_MAPPED_SUBRESOURCE mapped;
            unsafe
            {
                _context.Map(staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
            }
            try
            {
                return BgraCropper.Marshal(
                    src: mapped.pData,
                    srcRowPitch: (int)mapped.RowPitch,
                    width: width,
                    height: height);
            }
            finally
            {
                _context.Unmap(staging, 0);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(staging);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // ID3D11Device and context are RCWs; release them via Marshal so finalisers don't race.
        try { Marshal.ReleaseComObject(_context); } catch { }
        try { Marshal.ReleaseComObject(_device);  } catch { }
        // _winrtDevice is a WinRT projection; its underlying lifetime is shared with _device.
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr hwnd, [In] ref Guid iid);
        IntPtr CreateForMonitor(IntPtr hmonitor, [In] ref Guid iid);
    }
}
```

A few things to be aware of when this builds:

- `WinRT.MarshalInterface` and `.As<T>()` come from the implicit `WinRT.Runtime` reference brought in by the `net8.0-windows10.0.19041.0` TFM. No new package needed.
- `D3D11_SDK_VERSION` is the constant `7`; CsWin32 may or may not generate it. If it does, prefer the constant.
- The `IGraphicsCaptureItemInterop` interface GUID `{3628E81B-3CAC-4C60-B7F4-23CE0E0C3356}` is the standard Windows-documented value.

- [ ] **Step 6.2: Verify the project builds**

```bash
dotnet build src/GamePartyHud/GamePartyHud.csproj
```

Expected: succeeds. If the build complains about ambiguous `D3D11_SAMPLE_DESC` between two namespaces, fully qualify with `Windows.Win32.Graphics.Dxgi.Common.DXGI_SAMPLE_DESC` (already done above).

- [ ] **Step 6.3: Commit**

```bash
git add src/GamePartyHud/Capture/WgcScreenCapture.cs
git commit -m "feat(capture): WgcScreenCapture — Windows.Graphics.Capture backend"
```

---

## Task 7: Wire WgcScreenCapture into the composition root

**Files:**
- Modify: `src/GamePartyHud/App.xaml.cs`
- Modify: `src/GamePartyHud/MainWindow.xaml.cs` (add `CaptureUnavailableReason` to the `IController` interface)

- [ ] **Step 7.1: Extend `IController`**

Edit `src/GamePartyHud/MainWindow.xaml.cs`. Find the `IController` interface (around line 31) and add a new property:

```csharp
        /// <summary>
        /// Non-null when the screen-capture backend failed to initialise
        /// (rare — typically a software-only GPU or a broken display driver).
        /// MainWindow surfaces this as a banner.
        /// </summary>
        string? CaptureUnavailableReason { get; }
```

Insert it after the existing `MemberCount` property and before `event Action? PartyStateChanged;`.

- [ ] **Step 7.2: Implement the property in `App`**

Edit `src/GamePartyHud/App.xaml.cs`. Find the `MainWindow.IController surface` region (around line 37) and:

1. Add a backing field next to `_capture`:

```csharp
    private string? _captureUnavailableReason;
```

2. Add the explicit interface implementation next to the other `MainWindow.IController.*` members:

```csharp
    string? MainWindow.IController.CaptureUnavailableReason => _captureUnavailableReason;
```

- [ ] **Step 7.3: Replace the capture construction with try/catch**

Still in `src/GamePartyHud/App.xaml.cs`. Find lines 117–118:

```csharp
        _capture = new WindowsScreenCapture();
        Log.Info("Screen capture: WindowsScreenCapture (GDI BitBlt).");
```

Replace with:

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
            _captureUnavailableReason = $"{ex.GetType().Name}: {ex.Message}";
        }
```

Also change the field declaration on line 29 from:

```csharp
    private WindowsScreenCapture? _capture;
```

to:

```csharp
    private IScreenCapture? _capture;
```

This widens the field type so it can hold either `WgcScreenCapture` or `NullScreenCapture`.

- [ ] **Step 7.4: Update `OnExit` for IDisposable on the wider type**

Still in `src/GamePartyHud/App.xaml.cs`. Find around line 341:

```csharp
        _capture?.Dispose();
```

Replace with:

```csharp
        if (_capture is IDisposable capDisposable) capDisposable.Dispose();
```

(`IScreenCapture` itself isn't `IDisposable`; `WgcScreenCapture` is, `NullScreenCapture` isn't.)

- [ ] **Step 7.5: Build and run the app**

```bash
dotnet build src/GamePartyHud/GamePartyHud.csproj
dotnet run --project src/GamePartyHud/GamePartyHud.csproj
```

Expected: app starts, `app.log` includes `Screen capture: WgcScreenCapture (Windows.Graphics.Capture).`. Quit the app from the tray.

- [ ] **Step 7.6: Commit**

```bash
git add src/GamePartyHud/App.xaml.cs src/GamePartyHud/MainWindow.xaml.cs
git commit -m "feat(capture): wire WgcScreenCapture into composition root with NullScreenCapture fallback"
```

---

## Task 8: CaptureUnavailableBanner in MainWindow

**Files:**
- Modify: `src/GamePartyHud/MainWindow.xaml`
- Modify: `src/GamePartyHud/MainWindow.xaml.cs`

- [ ] **Step 8.1: Add the InfoBar to MainWindow.xaml**

Edit `src/GamePartyHud/MainWindow.xaml`. Find the start of the inner `<StackPanel>` inside the `ScrollViewer` (line 23, just after `<StackPanel>`). Insert this **as the first child** of that `StackPanel`, before the *Your settings* heading:

```xml
                <ui:InfoBar x:Name="CaptureUnavailableBanner"
                            Margin="0,0,0,12"
                            IsClosable="True"
                            Severity="Warning"
                            IsOpen="False"
                            Title="Screen capture unavailable"
                            Message="Your GPU or display driver may not support the Windows screen-capture API. The HUD will still show your teammates, but your HP won't broadcast."/>
```

- [ ] **Step 8.2: Wire the visibility in code-behind**

Edit `src/GamePartyHud/MainWindow.xaml.cs`. In the constructor (around line 67) add a call to a new method `RefreshCaptureBanner` after `RefreshPartyState();`:

```csharp
        RefreshCaptureBanner();
```

Then add the method itself anywhere in the class (e.g. just below `RefreshPartyState`):

```csharp
    private void RefreshCaptureBanner()
    {
        var reason = _ctl.CaptureUnavailableReason;
        if (!string.IsNullOrEmpty(reason))
        {
            CaptureUnavailableBanner.IsOpen = true;
            Log.Info($"MainWindow: capture-unavailable banner shown ({reason}).");
        }
        else
        {
            CaptureUnavailableBanner.IsOpen = false;
        }
    }
```

This only needs to run once at startup — the WGC-init outcome doesn't change at runtime.

- [ ] **Step 8.3: Smoke (optional, debug-only)**

If you want to verify the banner before continuing, temporarily edit `App.xaml.cs` step 7.3's try block to throw immediately:

```csharp
        try
        {
            throw new InvalidOperationException("forced for smoke");
            _capture = new WgcScreenCapture();
            ...
```

Run the app. Banner should appear in MainWindow with the agreed wording. Revert the temp throw before committing.

- [ ] **Step 8.4: Commit**

```bash
git add src/GamePartyHud/MainWindow.xaml src/GamePartyHud/MainWindow.xaml.cs
git commit -m "feat(ui): CaptureUnavailableBanner in MainWindow for WGC-init failures"
```

---

## Task 9: Delete WindowsScreenCapture and System.Drawing.Common

**Files:**
- Delete: `src/GamePartyHud/Capture/WindowsScreenCapture.cs`
- Modify: `src/GamePartyHud/GamePartyHud.csproj`

- [ ] **Step 9.1: Delete the file**

```bash
git rm src/GamePartyHud/Capture/WindowsScreenCapture.cs
```

- [ ] **Step 9.2: Remove the System.Drawing.Common PackageReference**

Edit `src/GamePartyHud/GamePartyHud.csproj`. Remove this line:

```xml
    <PackageReference Include="System.Drawing.Common" Version="8.0.10" />
```

- [ ] **Step 9.3: Build and run all tests**

```bash
dotnet build
dotnet test
```

Expected: build succeeds, all tests (existing 96 + new 12) pass.

If the build fails complaining about missing `System.Drawing.*` types in `TrayIcon.cs`, that's because some `System.Drawing.Color` / `Font` usages there relied on `System.Drawing.Common`. The fix in that case: re-add the PackageReference. WinForms+WPF on Windows usually pull these from the framework reference assemblies, but if they don't here, that's fine — the goal of dropping the dep was housekeeping, not survival-critical.

- [ ] **Step 9.4: Commit**

```bash
git add -u
git commit -m "chore(capture): remove obsolete GDI BitBlt backend (replaced by WgcScreenCapture)"
```

---

## Task 10: FullscreenDetector (TDD)

**Files:**
- Create: `src/GamePartyHud/Diagnostics/FullscreenDetector.cs`
- Create: `tests/GamePartyHud.Tests/Diagnostics/FullscreenDetectorTests.cs`

- [ ] **Step 10.1: Write the failing tests**

Create `tests/GamePartyHud.Tests/Diagnostics/FullscreenDetectorTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using GamePartyHud.Diagnostics;
using Windows.Win32.UI.Shell;
using Xunit;

namespace GamePartyHud.Tests.Diagnostics;

public class FullscreenDetectorTests
{
    [Fact]
    public void RunningD3DFullScreen_IsTreatedAsFullscreen()
    {
        var probe = StaticProbe(QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN);
        using var d = new FullscreenDetector(probe);
        Assert.True(d.IsFullscreen);
    }

    [Fact]
    public void PresentationMode_IsTreatedAsFullscreen()
    {
        var probe = StaticProbe(QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE);
        using var d = new FullscreenDetector(probe);
        Assert.True(d.IsFullscreen);
    }

    [Theory]
    [InlineData(QUERY_USER_NOTIFICATION_STATE.QUNS_ACCEPTS_NOTIFICATIONS)]
    [InlineData(QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY)]
    [InlineData(QUERY_USER_NOTIFICATION_STATE.QUNS_QUIET_TIME)]
    [InlineData(QUERY_USER_NOTIFICATION_STATE.QUNS_NOT_PRESENT)]
    public void OtherStates_AreNotFullscreen(QUERY_USER_NOTIFICATION_STATE s)
    {
        using var d = new FullscreenDetector(StaticProbe(s));
        Assert.False(d.IsFullscreen);
    }

    [Fact]
    public void StateChanged_FiresOnTransition()
    {
        var states = new Queue<QUERY_USER_NOTIFICATION_STATE>(new[]
        {
            QUERY_USER_NOTIFICATION_STATE.QUNS_ACCEPTS_NOTIFICATIONS,
            QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN,
        });
        Func<QUERY_USER_NOTIFICATION_STATE> probe = () => states.Dequeue();

        var transitions = new List<bool>();
        using var d = new FullscreenDetector(probe);
        d.StateChanged += b => transitions.Add(b);

        // Force a second poll synchronously; the public surface uses a 1 Hz timer
        // which is too coarse for tests.
        d.PollForTesting();

        Assert.Single(transitions);
        Assert.True(transitions[0]);
    }

    [Fact]
    public void StateChanged_DoesNotFireOnRepeatSameState()
    {
        var probe = StaticProbe(QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN);
        var transitions = new List<bool>();
        using var d = new FullscreenDetector(probe);
        d.StateChanged += b => transitions.Add(b);

        d.PollForTesting();
        d.PollForTesting();
        d.PollForTesting();

        Assert.Empty(transitions); // initial state was already fullscreen — no transitions since
    }

    private static Func<QUERY_USER_NOTIFICATION_STATE> StaticProbe(QUERY_USER_NOTIFICATION_STATE s)
        => () => s;
}
```

Note the `using Windows.Win32.UI.Shell;` — that's where CsWin32 emits the `QUERY_USER_NOTIFICATION_STATE` enum.

- [ ] **Step 10.2: Run tests to confirm they fail**

```bash
dotnet test --filter "FullyQualifiedName~FullscreenDetectorTests"
```

Expected: compilation errors (`FullscreenDetector` doesn't exist).

- [ ] **Step 10.3: Implement `FullscreenDetector`**

Create `src/GamePartyHud/Diagnostics/FullscreenDetector.cs`:

```csharp
using System;
using System.Runtime.Versioning;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace GamePartyHud.Diagnostics;

/// <summary>
/// Polls <c>SHQueryUserNotificationState</c> at 1 Hz and exposes a boolean
/// "is the user in an exclusive-fullscreen DXGI app or a fullscreen
/// presentation" signal. This is the same signal Windows itself uses to
/// decide whether Focus Assist's "Game" rule should suppress notifications,
/// and exactly the condition that defeats the HUD overlay's
/// <c>Topmost=True</c>.
/// </summary>
[SupportedOSPlatform("windows")]
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

    public FullscreenDetector() : this(QueryFromShell32) { }

    /// <summary>Test-only constructor with an injectable state probe.</summary>
    internal FullscreenDetector(Func<QUERY_USER_NOTIFICATION_STATE> stateProbe)
    {
        _stateProbe = stateProbe;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += (_, _) => Poll();
        Poll();          // populate initial state synchronously
        _timer.Start();
    }

    /// <summary>Public for tests; calls the same poll the timer drives.</summary>
    internal void PollForTesting() => Poll();

    private void Poll()
    {
        var s = _stateProbe();
        IsFullscreen = s == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN
                    || s == QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE;
    }

    public void Dispose() => _timer.Stop();

    private static QUERY_USER_NOTIFICATION_STATE QueryFromShell32()
    {
        PInvoke.SHQueryUserNotificationState(out var state).ThrowOnFailure();
        return state;
    }
}
```

The `internal` test seam (the second constructor and `PollForTesting`) is exposed to the test project via the existing `<InternalsVisibleTo Include="GamePartyHud.Tests" />` in the csproj.

- [ ] **Step 10.4: Run tests to confirm they pass**

```bash
dotnet test --filter "FullyQualifiedName~FullscreenDetectorTests"
```

Expected: 8 tests pass (2 + 4 from `[InlineData]` + 2).

- [ ] **Step 10.5: Commit**

```bash
git add src/GamePartyHud/Diagnostics/FullscreenDetector.cs tests/GamePartyHud.Tests/Diagnostics/FullscreenDetectorTests.cs
git commit -m "feat(diagnostics): FullscreenDetector — 1 Hz SHQueryUserNotificationState poller"
```

---

## Task 11: Wire FullscreenDetector into composition root

**Files:**
- Modify: `src/GamePartyHud/MainWindow.xaml.cs` (extend `IController`)
- Modify: `src/GamePartyHud/App.xaml.cs`

- [ ] **Step 11.1: Extend `IController`**

Edit `src/GamePartyHud/MainWindow.xaml.cs`. In the `IController` interface (just under `CaptureUnavailableReason` from Task 7.1), add:

```csharp
        /// <summary>True when the foreground app is in exclusive-fullscreen DXGI mode.</summary>
        bool IsGameFullscreen { get; }

        /// <summary>Fires whenever IsGameFullscreen changes.</summary>
        event Action<bool>? FullscreenStateChanged;
```

- [ ] **Step 11.2: Add the detector field and wire it up**

Edit `src/GamePartyHud/App.xaml.cs`.

Add field next to `_capture`:

```csharp
    private FullscreenDetector? _fullscreenDetector;
```

In the `using` statements at the top of the file, add:

```csharp
using GamePartyHud.Diagnostics;
```

(`Diagnostics.Log` is already imported; this adds the namespace for `FullscreenDetector`.)

Add the explicit interface implementations in the `MainWindow.IController surface` region:

```csharp
    bool MainWindow.IController.IsGameFullscreen => _fullscreenDetector?.IsFullscreen ?? false;

    public event Action<bool>? FullscreenStateChanged;
```

In `OnStartup`, just after `_capture` is assigned (Task 7.3), add:

```csharp
        _fullscreenDetector = new FullscreenDetector();
        _fullscreenDetector.StateChanged += OnFullscreenChanged;
        Log.Info($"FullscreenDetector started; initial state: {_fullscreenDetector.IsFullscreen}.");
```

Add the handler method anywhere in the class (e.g. next to `OnKickRequested`):

```csharp
    private void OnFullscreenChanged(bool isFullscreen)
    {
        Log.Info($"FullscreenDetector: state changed → {(isFullscreen ? "fullscreen" : "windowed")}.");
        FullscreenStateChanged?.Invoke(isFullscreen);
        // Balloon wiring is added in Task 13.
    }
```

In `OnExit` (around line 327), add disposal before `_tray?.Dispose();`:

```csharp
        _fullscreenDetector?.Dispose();
```

- [ ] **Step 11.3: Build**

```bash
dotnet build
```

Expected: succeeds.

- [ ] **Step 11.4: Commit**

```bash
git add src/GamePartyHud/App.xaml.cs src/GamePartyHud/MainWindow.xaml.cs
git commit -m "feat(diagnostics): wire FullscreenDetector into composition root"
```

---

## Task 12: FullscreenStatus InfoBar in MainWindow

**Files:**
- Modify: `src/GamePartyHud/MainWindow.xaml`
- Modify: `src/GamePartyHud/MainWindow.xaml.cs`

- [ ] **Step 12.1: Add the InfoBar inside InPartySection**

Edit `src/GamePartyHud/MainWindow.xaml`. Find `InPartySection` (around line 135). Inside it, **after** the `MemberCountDisplay` `TextBlock` (around line 174) and **before** the `<StackPanel Orientation="Horizontal">` that holds the Leave button (around line 176), insert:

```xml
                    <ui:InfoBar x:Name="FullscreenStatus"
                                Margin="0,0,0,10"
                                IsClosable="False"
                                Severity="Informational"
                                IsOpen="False"
                                Title="Game in fullscreen mode"
                                Message="Your party still sees your HP — but the HUD overlay is hidden by Windows on the same monitor. Switch the game to borderless windowed, or (if you have a second monitor) drag the HUD onto it."/>
```

- [ ] **Step 12.2: Subscribe to fullscreen events in code-behind**

Edit `src/GamePartyHud/MainWindow.xaml.cs`. In the constructor, just after `_ctl.PartyStateChanged += OnCtlPartyStateChanged;`, add:

```csharp
        _ctl.FullscreenStateChanged += OnFullscreenStateChanged;
        RefreshFullscreenStatus();
```

Add the handler:

```csharp
    private void OnFullscreenStateChanged(bool _)
    {
        Dispatcher.Invoke(RefreshFullscreenStatus);
    }

    private void RefreshFullscreenStatus()
    {
        bool inParty = _ctl.CurrentPartyId is { Length: > 0 };
        FullscreenStatus.IsOpen = inParty && _ctl.IsGameFullscreen;
    }
```

Also extend `RefreshPartyState` to call `RefreshFullscreenStatus()` so the status row updates when the user joins or leaves a party. Find `RefreshPartyState` (around line 111) and add at the end of the method body:

```csharp
        RefreshFullscreenStatus();
```

- [ ] **Step 12.3: Manual smoke**

```bash
dotnet run --project src/GamePartyHud/GamePartyHud.csproj
```

In the running app, calibrate an HP region (use any solid red rectangle on screen) → join a party → launch any exclusive-fullscreen game (or any DirectX fullscreen demo). Check:

- The `FullscreenStatus` InfoBar appears in MainWindow within ≤ 1 s of entering fullscreen.
- The InfoBar disappears within ≤ 1 s of alt-tabbing out.
- Leaving the party hides the InfoBar regardless of game state.
- Outside a party, the InfoBar never appears.

If you don't have a fullscreen game handy, test by running PowerPoint and starting a slide show (which sets `QUNS_PRESENTATION_MODE`).

- [ ] **Step 12.4: Commit**

```bash
git add src/GamePartyHud/MainWindow.xaml src/GamePartyHud/MainWindow.xaml.cs
git commit -m "feat(ui): FullscreenStatus InfoBar — explains hidden HUD when game is fullscreen"
```

---

## Task 13: Tray balloon

**Files:**
- Modify: `src/GamePartyHud/Tray/TrayIcon.cs`
- Modify: `src/GamePartyHud/App.xaml.cs`

- [ ] **Step 13.1: Add `ShowBalloon` to TrayIcon**

Edit `src/GamePartyHud/Tray/TrayIcon.cs`. Inside the class (e.g. just below `SetPartyId`, around line 63), add:

```csharp
    /// <summary>
    /// Shows a tray balloon. May be eaten by Focus Assist's "Game" automatic
    /// rule during exclusive fullscreen — that's the OS's call, not ours. The
    /// in-app status row in MainWindow is the load-bearing surface for the
    /// fullscreen explanation; this is a discoverability hint that fires on
    /// the *transition* into fullscreen.
    /// </summary>
    public void ShowBalloon(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = ToolTipIcon.Info;
        _icon.ShowBalloonTip(8000);  // OS clamps to ~5s on Win10+
    }
```

- [ ] **Step 13.2: Wire the one-shot in App**

Edit `src/GamePartyHud/App.xaml.cs`.

Add a field next to `_currentPartyId`:

```csharp
    private bool _balloonShownThisSession;
```

Update `OnFullscreenChanged` (added in Task 11.2) to fire the balloon:

```csharp
    private void OnFullscreenChanged(bool isFullscreen)
    {
        Log.Info($"FullscreenDetector: state changed → {(isFullscreen ? "fullscreen" : "windowed")}.");
        FullscreenStateChanged?.Invoke(isFullscreen);

        if (isFullscreen
            && _currentPartyId is not null
            && !_balloonShownThisSession)
        {
            _balloonShownThisSession = true;
            _tray?.ShowBalloon(
                title: "Game Party HUD",
                text:  "Your game is in fullscreen mode — the HUD is hidden, but your party still sees your HP. " +
                       "Switch to borderless windowed, or move the HUD to a second monitor.");
            Log.Info("Tray balloon shown: fullscreen explanation (one-shot per party).");
        }
    }
```

Reset the one-shot on leave so re-joining re-arms it. In `LeavePartyAsync` (around line 272), find:

```csharp
        Log.Info($"Leaving party '{_currentPartyId}'.");
        _orch = null;
        _currentPartyId = null;
        _tray?.SetPartyId(null);
```

Add at the end of that block:

```csharp
        _balloonShownThisSession = false;
```

- [ ] **Step 13.3: Manual smoke**

```bash
dotnet run --project src/GamePartyHud/GamePartyHud.csproj
```

- Join a party. Enter fullscreen. Balloon appears (or is suppressed by Focus Assist — `app.log` should still record `Tray balloon shown:`).
- Alt-tab out, alt-tab back into fullscreen. **No second balloon** in the same session.
- Leave the party, rejoin, enter fullscreen again. Balloon re-appears.

- [ ] **Step 13.4: Commit**

```bash
git add src/GamePartyHud/Tray/TrayIcon.cs src/GamePartyHud/App.xaml.cs
git commit -m "feat(tray): one-time balloon on first fullscreen-after-party-join"
```

---

## Task 14: Manual smoke checklist

This task produces no commits — it's the gate before opening a PR. Run through every item, jot results into the eventual PR description.

- [ ] **Step 14.1: Regression — borderless windowed capture**

Calibrate an HP region against a known visual (e.g., a solid red rectangle in a 1920×1080 borderless windowed image viewer). Run the app → join a party → confirm HP reads accurately. Compare against an `app.log` from before this branch (or a commit just before Task 6). Readings should match within ±1 % at full HP.

- [ ] **Step 14.2: New behaviour — exclusive fullscreen capture**

Same calibration, same red rectangle, but show it via an exclusive-fullscreen DirectX viewer (or your actual game in true exclusive fullscreen — verify with `nvidia-smi` or Special K's HUD if you have it). On `main` today this returns black; on this branch HP should match the windowed reading.

- [ ] **Step 14.3: Multi-monitor**

If you have a second monitor: calibrate the HP region on the secondary monitor while a windowed game is on primary. Confirm HP reads correctly. Then swap (calibrate on primary, game on secondary). Both should work.

- [ ] **Step 14.4: Status row**

Join party → enter fullscreen → `FullscreenStatus` InfoBar appears in MainWindow with the agreed wording within 1 s.
Alt-tab out → InfoBar disappears within 1 s.
Leave party → InfoBar hidden regardless of game state.

- [ ] **Step 14.5: One-time balloon**

First fullscreen-after-join: balloon fires (visible if Focus Assist permits; logged in `app.log` either way).
Toggle fullscreen off and on again: **no** second balloon.
Leave party, rejoin, enter fullscreen: balloon fires again.

- [ ] **Step 14.6: Failure path — forced D3D11 init failure**

Temporarily edit Task 7.3's try block to throw `new InvalidOperationException("forced for smoke")` immediately. Run the app → verify `CaptureUnavailableBanner` appears in MainWindow with the warning copy → `app.log` shows the error. Revert the temp throw before continuing.

- [ ] **Step 14.7: Performance sanity**

Open Task Manager → Details → find `GamePartyHud.exe`. With the app running and a party joined and a 2 s poll interval (default), CPU should sit at 0–1 %, memory should sit < 100 MB, GPU usage should sit at < 1 % when sampled over 60 seconds. If any of these blow the budget per [`CLAUDE.md`](../../../CLAUDE.md) section "Performance budget", do not merge.

The full 8-hour soak required by [`CLAUDE.md`](../../../CLAUDE.md) before tagging a release is **not** a per-PR gate — flag it in the merge note as a release blocker.

- [ ] **Step 14.8: Open the PR**

```bash
git push -u origin feat/fullscreen-capture
gh pr create --title "feat(capture): WGC backend + fullscreen-mode UX" --body "$(cat <<'EOF'
## Summary
- Replace GDI BitBlt screen capture with Windows.Graphics.Capture so HP-bar reading works in exclusive-fullscreen DXGI games (the GDI backend returned black there).
- Add a persistent in-app status row + one-time tray balloon that explains the residual overlay-visibility limitation, with the second-monitor workaround called out.
- No attempt to draw the HUD over an exclusive-fullscreen swap chain (banned by anti-cheat-friendliness rule). See [`docs/superpowers/specs/2026-05-01-fullscreen-capture-design.md`](docs/superpowers/specs/2026-05-01-fullscreen-capture-design.md) §5 for the honest scope assessment.

## Test plan
- [x] All 14 tasks of the implementation plan ([`docs/superpowers/plans/2026-05-01-fullscreen-capture-plan.md`](docs/superpowers/plans/2026-05-01-fullscreen-capture-plan.md)) completed.
- [x] All ~108 unit tests pass (`dotnet test`).
- [x] Manual smoke checklist (Task 14) passed.
- [ ] **Release blocker:** 8-hour soak required by CLAUDE.md before tagging next release.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

---

## Self-review check

- **Spec coverage:**
  - §1 (Architecture overview) → composition-root changes in Task 7, threading via Tasks 6+11.
  - §2.1 (CsWin32) → Task 1.
  - §2.2–2.3 (WgcScreenCapture lifecycle + capture flow) → Task 6.
  - §2.4 (lazy session rationale) → Task 6 implementation comments.
  - §2.5 (MonitorResolver) → Task 3.
  - §2.6 (BgraCropper) → Task 2.
  - §2.7 (DPI invariant) → preserved by Task 6's coordinate translation; no separate task needed.
  - §2.8 (error handling) → Task 6's try/catch; Tasks 7+8 cover the init-failure surface.
  - §2.9 (NullScreenCapture) → Task 4.
  - §3.1–3.2 (FullscreenDetector + SHQueryUserNotificationState) → Task 10.
  - §3.3 (composition-root wiring) → Task 11.
  - §3.4 (status row + capture-unavailable banner) → Tasks 8 + 12.
  - §3.5 (one-time balloon) → Task 13.
  - §3.6 (TrayIcon.ShowBalloon) → Task 13.1.
  - §4 (testing strategy) → unit tests in Tasks 2/3/10; manual smoke in Task 14.
  - §6 (file summary) → all listed files appear in tasks.
  - §7 (decision log) → reflected in implementation choices throughout.

- **No placeholders:** All steps contain concrete code or commands. No `TODO` / `TBD` / "implement appropriate error handling" anywhere.

- **Type consistency:**
  - `MonitorInfo` declared in Task 3 with `(IntPtr Hmonitor, Rectangle VirtualBounds)`; consumed in Task 3 only.
  - `BgraCropper.Marshal` has two overloads (`ReadOnlySpan<byte>` and `IntPtr`); both consumed in Task 6's `WgcScreenCapture.CropToBgra`.
  - `IController.CaptureUnavailableReason` introduced in Task 7.1; consumed in Task 8.2.
  - `IController.IsGameFullscreen` and `FullscreenStateChanged` introduced in Task 11.1; consumed in Task 12.2.
  - `FullscreenDetector.PollForTesting` added as `internal` in Task 10.3; used by Task 10.1 tests via `<InternalsVisibleTo>`.
  - `_balloonShownThisSession` declared in Task 13.2; reset in same task's `LeavePartyAsync` edit.
  - `TrayIcon._icon` (the actual field name) used in Task 13.1, not the spec's incorrect `_notifyIcon`.
