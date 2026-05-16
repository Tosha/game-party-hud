# Discord Party Notification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Post a single Discord webhook notification ("`{nickname}` created a party with id `{partyId}`") whenever a player clicks Create Party in the official release binary.

**Architecture:** Mirror the existing `RelayUrl` build-time-secret pattern: new `DiscordWebhookUrl` MSBuild property → `AssemblyMetadataAttribute` → `AppConfig.DefaultDiscordWebhookUrl` static. Add a small `DiscordNotifier` class (`src/GamePartyHud/Network/`) behind an `IDiscordNotifier` interface that POSTs a JSON content payload; empty URL = no-op so local dev / unconfigured forks send nothing. `App.JoinOrCreateAsync` gains a `wasCreated` flag and, only on `true`, fires the notifier in a fire-and-forget wrapper that catches everything so Discord can never gate party creation.

**Tech Stack:** C# 12, .NET 8, `HttpClient`, `System.Text.Json`, xUnit, MSBuild `AssemblyMetadataAttribute`, GitHub Actions secrets.

**Spec:** [docs/superpowers/specs/2026-05-16-discord-party-notification-design.md](../specs/2026-05-16-discord-party-notification-design.md)

---

## File Structure

**Create:**
- `src/GamePartyHud/Network/DiscordNotifier.cs` — `IDiscordNotifier` interface + `DiscordNotifier` sealed implementation.
- `tests/GamePartyHud.Tests/Network/DiscordNotifierTests.cs` — 4 unit tests using a fake `HttpMessageHandler`.

**Modify:**
- `src/GamePartyHud/GamePartyHud.csproj` — add `<DiscordWebhookUrl>` MSBuild property + new `<AssemblyAttribute>` entry.
- `src/GamePartyHud/Config/AppConfig.cs` — add `DefaultDiscordWebhookUrl` static accessor.
- `src/GamePartyHud/App.xaml.cs` — `_discord` field, startup init, `wasCreated` parameter, fire-and-forget wrapper, `OnExit` dispose.
- `.github/workflows/release.yml` — wire `GPH_DISCORD_WEBHOOK_URL` secret into the publish step.
- `docs/requirements.md` — short Telemetry disclosure under Non-functional requirements.

---

## Task 1: Add `DiscordWebhookUrl` build-time injection

**Files:**
- Modify: `src/GamePartyHud/GamePartyHud.csproj`

- [ ] **Step 1: Add the MSBuild property**

In `src/GamePartyHud/GamePartyHud.csproj`, find the `<PropertyGroup>` containing the `<RelayUrl>` property (around line 35). Append `<DiscordWebhookUrl>` right after `<RelayFallbackUrl>`:

```xml
<RelayUrl Condition="'$(RelayUrl)' == ''">wss://relay.example.invalid</RelayUrl>
<RelayFallbackUrl Condition="'$(RelayFallbackUrl)' == ''"></RelayFallbackUrl>
<!--
  Build-time Discord webhook injection. Default is empty: empty URL means
  the notifier is a no-op (see DiscordNotifier.cs). Local dev builds and
  forks that don't set GPH_DISCORD_WEBHOOK_URL in CI simply don't ping
  Discord; no failure mode to handle. Release builds inject the real URL
  via -p:DiscordWebhookUrl=... from the GPH_DISCORD_WEBHOOK_URL secret
  in release.yml.
-->
<DiscordWebhookUrl Condition="'$(DiscordWebhookUrl)' == ''"></DiscordWebhookUrl>
```

- [ ] **Step 2: Add the AssemblyMetadataAttribute entry**

In the same csproj, find the `<ItemGroup>` containing the two existing `<AssemblyAttribute>` entries for `RelayUrl` and `RelayFallbackUrl` (around line 39). Append a third entry:

```xml
<AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
    <_Parameter1>DiscordWebhookUrl</_Parameter1>
    <_Parameter2>$(DiscordWebhookUrl)</_Parameter2>
</AssemblyAttribute>
```

- [ ] **Step 3: Build to confirm**

Run: `dotnet build`
Expected: Build succeeds with 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/GamePartyHud/GamePartyHud.csproj
git commit -m "chore(build): add DiscordWebhookUrl build-time secret injection"
```

---

## Task 2: Add `AppConfig.DefaultDiscordWebhookUrl`

**Files:**
- Modify: `src/GamePartyHud/Config/AppConfig.cs`

- [ ] **Step 1: Add the static accessor**

In `src/GamePartyHud/Config/AppConfig.cs`, find the existing `DefaultRelayFallbackUrl` static property (it uses the same `ResolveAssemblyMetadata` helper). Right after its `}` (before the private `ResolveAssemblyMetadata` method declaration), add:

```csharp
/// <summary>
/// Discord webhook endpoint for the party-creation notification. Injected
/// at build time via the <c>DiscordWebhookUrl</c> MSBuild property (see
/// <c>GamePartyHud.csproj</c>). Empty string by default; release builds in
/// CI substitute the real URL from the <c>GPH_DISCORD_WEBHOOK_URL</c>
/// GitHub Actions secret. Empty URL = notifier is a no-op (see
/// <c>DiscordNotifier</c>). Not a per-machine config field; never
/// persisted in <c>config.json</c>.
/// </summary>
public static string DefaultDiscordWebhookUrl { get; } =
    ResolveAssemblyMetadata("DiscordWebhookUrl", fallback: "");
```

Do **not** add a `HudScale`-style field to the `AppConfig` record itself; the webhook URL is strictly binary-owned.

- [ ] **Step 2: Build to confirm**

Run: `dotnet build`
Expected: Build succeeds with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/GamePartyHud/Config/AppConfig.cs
git commit -m "feat(config): expose DefaultDiscordWebhookUrl from build metadata"
```

---

## Task 3: Implement `DiscordNotifier` with unit tests (TDD)

**Files:**
- Create: `src/GamePartyHud/Network/DiscordNotifier.cs`
- Create: `tests/GamePartyHud.Tests/Network/DiscordNotifierTests.cs`

- [ ] **Step 1: Create the interface and class skeleton**

Create `src/GamePartyHud/Network/DiscordNotifier.cs` with:

```csharp
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GamePartyHud.Diagnostics;

namespace GamePartyHud.Network;

public interface IDiscordNotifier
{
    Task NotifyPartyCreatedAsync(string nickname, string partyId, CancellationToken ct = default);
}

public sealed class DiscordNotifier : IDiscordNotifier, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _webhookUrl;
    private readonly bool _ownsClient;

    public DiscordNotifier(string webhookUrl, HttpClient? httpClient = null)
    {
        _webhookUrl = webhookUrl ?? "";
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _ownsClient = httpClient is null;
    }

    public Task NotifyPartyCreatedAsync(string nickname, string partyId, CancellationToken ct = default)
    {
        // Filled in by Step 4 (TDD: tests first).
        throw new System.NotImplementedException();
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
```

- [ ] **Step 2: Create the failing test file**

Create `tests/GamePartyHud.Tests/Network/DiscordNotifierTests.cs` with:

```csharp
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GamePartyHud.Network;
using Xunit;

namespace GamePartyHud.Tests.Network;

public class DiscordNotifierTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();
        private readonly HttpStatusCode _status;
        public RecordingHandler(HttpStatusCode status = HttpStatusCode.NoContent) => _status = status;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            // Buffer the body now; HttpRequestMessage.Content is single-read
            // by default and the notifier disposes the request after SendAsync.
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(_status) { Content = new StringContent("") };
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("network is down");
    }

    [Fact]
    public async Task EmptyUrl_SendsNoRequest()
    {
        var handler = new RecordingHandler();
        using var notifier = new DiscordNotifier("", new HttpClient(handler));
        await notifier.NotifyPartyCreatedAsync("Anton", "ABCDEF");
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PostsExpectedJsonToWebhookUrl()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent);
        using var notifier = new DiscordNotifier(
            "https://discord.example/webhook", new HttpClient(handler));

        await notifier.NotifyPartyCreatedAsync("BananaBrain", "ABCDEF");

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://discord.example/webhook", req.RequestUri!.ToString());

        var body = handler.Bodies[0].Replace(" ", "");
        Assert.Contains("\"content\":\"BananaBrain created a party with id ABCDEF\"", body);
        Assert.Contains("\"parse\":[]", body);
    }

    [Fact]
    public async Task NonSuccessResponse_DoesNotThrow()
    {
        var handler = new RecordingHandler(HttpStatusCode.TooManyRequests);
        using var notifier = new DiscordNotifier(
            "https://discord.example/webhook", new HttpClient(handler));
        // Discord 429 / 5xx must be silent; caller is fire-and-forget.
        await notifier.NotifyPartyCreatedAsync("X", "ABCDEF");
    }

    [Fact]
    public async Task HandlerThrows_PropagatesSoCallerCanLog()
    {
        // The notifier itself doesn't swallow exceptions — that's App's job
        // in the fire-and-forget wrapper. The notifier stays honest; App's
        // wrapper is what actually keeps party creation safe.
        using var notifier = new DiscordNotifier(
            "https://discord.example/webhook", new HttpClient(new ThrowingHandler()));
        await Assert.ThrowsAsync<HttpRequestException>(
            () => notifier.NotifyPartyCreatedAsync("X", "ABCDEF"));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DiscordNotifierTests" --nologo`
Expected: All 4 tests fail with `NotImplementedException` (because the `NotifyPartyCreatedAsync` body still throws).

- [ ] **Step 4: Implement `NotifyPartyCreatedAsync`**

In `src/GamePartyHud/Network/DiscordNotifier.cs`, replace the body of `NotifyPartyCreatedAsync` with:

```csharp
public async Task NotifyPartyCreatedAsync(string nickname, string partyId, CancellationToken ct = default)
{
    if (string.IsNullOrWhiteSpace(_webhookUrl)) return;

    var payload = new
    {
        content = $"{nickname} created a party with id {partyId}",
        // Block @everyone / @here / role-mention abuse if a nickname ever
        // contains one of those tokens. Discord parses mentions by default.
        allowed_mentions = new { parse = Array.Empty<string>() },
    };
    var body = JsonSerializer.Serialize(payload);
    using var req = new HttpRequestMessage(HttpMethod.Post, _webhookUrl)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
    var resp = await _http.SendAsync(req, ct);
    if (!resp.IsSuccessStatusCode)
    {
        var detail = await resp.Content.ReadAsStringAsync(ct);
        Log.Warn($"Discord notify returned {(int)resp.StatusCode}: {detail}");
    }
}
```

Also change the method signature from `Task NotifyPartyCreatedAsync(...)` (synchronous return) to `async Task NotifyPartyCreatedAsync(...)` so the `await`s compile.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~DiscordNotifierTests" --nologo`
Expected: All 4 tests pass.

- [ ] **Step 6: Run the full test suite for regression check**

Run: `dotnet test --nologo`
Expected: All tests pass (existing suite plus the 4 new notifier tests).

- [ ] **Step 7: Commit**

```bash
git add src/GamePartyHud/Network/DiscordNotifier.cs tests/GamePartyHud.Tests/Network/DiscordNotifierTests.cs
git commit -m "feat(network): add DiscordNotifier with webhook content + mention guard"
```

---

## Task 4: Wire notifier into `App.JoinOrCreateAsync`

**Files:**
- Modify: `src/GamePartyHud/App.xaml.cs`

- [ ] **Step 1: Add the `_discord` field**

In `src/GamePartyHud/App.xaml.cs`, find the fields block at the top of the `App` class (around line 28-35, alongside `_tray`, `_store`, `_capture`, etc.). Add:

```csharp
private IDiscordNotifier? _discord;
```

You'll need to add `using GamePartyHud.Network;` at the top if it isn't already there (it should be — `RelayClient` is in that namespace and is already used).

- [ ] **Step 2: Initialize the notifier in `OnStartup`**

In `OnStartup`, find the line `_capture = new WindowsScreenCapture();` (around line 131-132). Insert two lines immediately after the existing `Log.Info("Screen capture: ...");` line:

```csharp
_capture = new WindowsScreenCapture();
Log.Info("Screen capture: WindowsScreenCapture (GDI BitBlt).");

_discord = new DiscordNotifier(AppConfig.DefaultDiscordWebhookUrl);
Log.Info(string.IsNullOrWhiteSpace(AppConfig.DefaultDiscordWebhookUrl)
    ? "Discord notifications: disabled (no webhook URL compiled in)."
    : "Discord notifications: enabled.");

_state = new PartyState();
```

- [ ] **Step 3: Add `wasCreated` parameter to `JoinOrCreateAsync` + update entrypoints**

Change the `JoinOrCreateAsync` method signature. Find the line:

```csharp
private async Task JoinOrCreateAsync(string partyId)
```

Change to:

```csharp
private async Task JoinOrCreateAsync(string partyId, bool wasCreated)
```

Find the two `IController` entrypoints (around line 73-77):

```csharp
Task MainWindow.IController.CreatePartyAsync() =>
    JoinOrCreateAsync(PartyIdGenerator.Generate());

Task MainWindow.IController.JoinPartyAsync(string partyId) =>
    JoinOrCreateAsync(partyId);
```

Change to:

```csharp
Task MainWindow.IController.CreatePartyAsync() =>
    JoinOrCreateAsync(PartyIdGenerator.Generate(), wasCreated: true);

Task MainWindow.IController.JoinPartyAsync(string partyId) =>
    JoinOrCreateAsync(partyId, wasCreated: false);
```

- [ ] **Step 4: Fire the notification after a successful join**

In `JoinOrCreateAsync`, find the success block near the end of the method:

```csharp
_config = _config with { LastPartyId = partyId };
_store!.Save(_config);
Log.Info($"Party '{partyId}' joined. Self peer id={selfPeer}. Capture+broadcast loop started.");

PartyStateChanged?.Invoke();
```

Insert the fire-and-forget call between `Log.Info(...)` and `PartyStateChanged?.Invoke()`:

```csharp
_config = _config with { LastPartyId = partyId };
_store!.Save(_config);
Log.Info($"Party '{partyId}' joined. Self peer id={selfPeer}. Capture+broadcast loop started.");

if (wasCreated)
{
    // Fire-and-forget — Discord must never gate party creation. The
    // wrapper catches everything so a 4xx / network blip / DNS hiccup
    // can't surface as an unobserved task exception in the global
    // handler.
    _ = NotifyDiscordPartyCreatedAsync(_config.Nickname, partyId);
}

PartyStateChanged?.Invoke();
```

- [ ] **Step 5: Add the fire-and-forget wrapper method**

In `App.xaml.cs`, add this method right after `JoinOrCreateAsync` (before `LeavePartyAsync`):

```csharp
private async Task NotifyDiscordPartyCreatedAsync(string nickname, string partyId)
{
    if (_discord is null) return;
    try { await _discord.NotifyPartyCreatedAsync(nickname, partyId); }
    catch (Exception ex) { Log.Warn("Discord notification failed: " + ex.Message); }
}
```

- [ ] **Step 6: Dispose the notifier in `OnExit`**

In `OnExit`, find the existing teardown block at the end (around line 372-373):

```csharp
_tray?.Dispose();
_capture?.Dispose();
base.OnExit(e);
```

Insert one line before `_tray?.Dispose();`:

```csharp
(_discord as IDisposable)?.Dispose();
_tray?.Dispose();
_capture?.Dispose();
base.OnExit(e);
```

- [ ] **Step 7: Build to confirm everything compiles**

Run: `dotnet build`
Expected: Build succeeds with 0 errors.

- [ ] **Step 8: Run the full test suite for regression check**

Run: `dotnet test --nologo`
Expected: All tests pass. (No new tests in this task — App composition is exercised via manual smoke in Task 7.)

- [ ] **Step 9: Commit**

```bash
git add src/GamePartyHud/App.xaml.cs
git commit -m "feat(app): notify Discord webhook on Create Party (fire-and-forget)"
```

---

## Task 5: Wire `GPH_DISCORD_WEBHOOK_URL` into the release workflow

**Files:**
- Modify: `.github/workflows/release.yml`

- [ ] **Step 1: Add the env var + publish-time MSBuild property pass-through**

In `.github/workflows/release.yml`, find the `Publish` step (around line 56-68). Add `GPH_DISCORD_WEBHOOK_URL` to the `env:` block and a `-p:DiscordWebhookUrl=...` argument to the publish command. The new step block should look like:

```yaml
- name: Publish
  env:
    GPH_RELAY_URL: ${{ secrets.GPH_RELAY_URL }}
    GPH_RELAY_FALLBACK_URL: ${{ secrets.GPH_RELAY_FALLBACK_URL }}
    GPH_DISCORD_WEBHOOK_URL: ${{ secrets.GPH_DISCORD_WEBHOOK_URL }}
  run: >
    dotnet publish src/GamePartyHud/GamePartyHud.csproj
    -c Release
    -r win-x64
    --self-contained true
    -p:PublishSingleFile=true
    -p:IncludeNativeLibrariesForSelfExtract=true
    "-p:RelayUrl=${env:GPH_RELAY_URL}"
    "-p:RelayFallbackUrl=${env:GPH_RELAY_FALLBACK_URL}"
    "-p:DiscordWebhookUrl=${env:GPH_DISCORD_WEBHOOK_URL}"
```

Do **not** extend the "Verify relay URL secret is configured" step. Unlike `GPH_RELAY_URL` (load-bearing), `GPH_DISCORD_WEBHOOK_URL` is optional — when unset the binary just doesn't ping anything, which is the correct behaviour for a fork.

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/release.yml
git commit -m "chore(ci): inject GPH_DISCORD_WEBHOOK_URL at publish time"
```

---

## Task 6: Add Telemetry disclosure to `requirements.md`

**Files:**
- Modify: `docs/requirements.md`

- [ ] **Step 1: Insert the Telemetry subsection**

In `docs/requirements.md`, find the `### Cost` section under `## Non-functional requirements` (around line 81-84). Immediately after the `### Cost` block (after its last bullet "Free to use. No accounts, no subscriptions."), insert:

```markdown
### Telemetry

- The official release binary sends a single notification (`{nickname} created a party with id {partyId}`) to the project's Discord whenever you **create** a party. Nothing is sent when you join an existing party, and nothing else is sent at any other time. The webhook URL is compiled into the official build only; forks and source builds without the `GPH_DISCORD_WEBHOOK_URL` secret send nothing.
```

The result should read:

```markdown
### Cost

- **Zero hosting cost.** Players' apps connect directly to each other using free public infrastructure. No paid servers.
- **Free to use.** No accounts, no subscriptions.

### Telemetry

- The official release binary sends a single notification (`{nickname} created a party with id {partyId}`) to the project's Discord whenever you **create** a party. Nothing is sent when you join an existing party, and nothing else is sent at any other time. The webhook URL is compiled into the official build only; forks and source builds without the `GPH_DISCORD_WEBHOOK_URL` secret send nothing.

### Safety & compatibility
```

- [ ] **Step 2: Commit**

```bash
git add docs/requirements.md
git commit -m "docs: disclose Discord party-create telemetry in requirements"
```

---

## Task 7: Manual smoke test

**Files:** None (manual verification per `CLAUDE.md` UI-testing policy).

The change is invisible in the UI; verification is "does the Discord ping fire on Create and not on Join?". You'll need a test Discord webhook to point at — either re-use the project's, or create a throwaway in a private channel for the test.

- [ ] **Step 1: Build with no webhook URL (default path)**

Run: `dotnet build`
Run: `dotnet run --project src/GamePartyHud`

Expected:
- App starts.
- `%AppData%\GamePartyHud\app.log` contains the line `Discord notifications: disabled (no webhook URL compiled in).`

- [ ] **Step 2: Create a party with no webhook configured**

In the running app, click "Create new party". A 6-char party ID appears.

Expected:
- No Discord notification (no webhook URL).
- `app.log` does NOT contain any `Discord notify returned ...` or `Discord notification failed: ...` line.

Close the app.

- [ ] **Step 3: Build with a test webhook URL**

```bash
dotnet build src/GamePartyHud/GamePartyHud.csproj "-p:DiscordWebhookUrl=https://discord.com/api/webhooks/PASTE_TEST_WEBHOOK_HERE"
```

Then `dotnet run --project src/GamePartyHud --no-build`.

Expected:
- App starts.
- `app.log` contains `Discord notifications: enabled.`

- [ ] **Step 4: Create a party, confirm the ping fires**

Click "Create new party". A 6-char party ID appears (e.g. `BANAN1`).

Expected:
- Within ~1–2 seconds, the test Discord channel shows: `<your nickname> created a party with id BANAN1`.
- `app.log` does NOT contain any warning about the Discord call.
- Party creation in the UI is unaffected — the InfoBar shows "Party created. Share the ID above with your teammates." as usual.

- [ ] **Step 5: Leave the party, then join it back, confirm NO ping**

Click "Leave party". Re-open and paste the same 6-char ID into the "Join" field, click "Join".

Expected:
- No new Discord notification fires (joining is not a notification trigger).

- [ ] **Step 6: Test failure resilience (intentionally bad URL)**

Close the app. Rebuild with a deliberately broken URL:

```bash
dotnet build src/GamePartyHud/GamePartyHud.csproj "-p:DiscordWebhookUrl=https://discord.com/api/webhooks/0/invalid-token-that-will-404"
```

Run the app, click "Create new party".

Expected:
- Party is still created (the InfoBar shows success).
- `app.log` contains a `Discord notify returned 401:` or `Discord notify returned 404:` warning line (the exact code depends on what Discord returns for an invalid token).
- No app crash, no error dialog, no unobserved task exception line in the log.

- [ ] **Step 7: No commit**

Verification-only task. If any of the above failed, file a follow-up commit per the failure; otherwise the implementation is complete.

---

## Verification Summary

After all tasks complete:

- `dotnet build` succeeds with 0 warnings, 0 errors (`CLAUDE.md`: warnings-as-errors enabled).
- `dotnet test --nologo` reports all green (existing tests + 4 new notifier tests).
- Manual Task 7 checklist passes end-to-end (with and without webhook configured, success and 4xx paths).

No new package references. No new config fields. No capture, party-state, networking-protocol, or relay changes.
