# Discord notification on party creation

**Date:** 2026-05-16
**Scope:** `src/GamePartyHud/Network/DiscordNotifier.cs` (new), `src/GamePartyHud/App.xaml.cs`, `src/GamePartyHud/Config/AppConfig.cs`, `src/GamePartyHud/GamePartyHud.csproj`, `.github/workflows/release.yml`, `docs/requirements.md`.

## Goals

When a player creates a party (clicks "Create new party" in MainWindow), the app posts a single Discord webhook message to the project's channel:

> `{nickname}` created a party with id `{partyId}`

Joining an existing party does not notify. The webhook URL is binary-owned (build-time secret), not user-configurable.

## Non-goals

- No notification on party join, leave, kick, member-count change, or any other party-lifecycle event.
- No per-machine override of the webhook URL — no `config.json` field, no UI surface.
- No retry on failure. Fire-and-forget; if Discord eats the message, we don't double-post on the next click.
- No rate-limit handling beyond logging the 429 response. Party creation is rare enough that we won't hit the 30 req/min webhook ceiling in practice.
- No rich embeds. Plain text content only, per the requested message format.
- No opt-out toggle for end users (decision: telemetry-style, disclosed in `requirements.md`).

## Design

### 1. Build-time secret wiring

Mirrors the existing `RelayUrl` pattern.

**`GamePartyHud.csproj`** — add an MSBuild property defaulting to empty, plus an `AssemblyMetadataAttribute`:

```xml
<DiscordWebhookUrl Condition="'$(DiscordWebhookUrl)' == ''"></DiscordWebhookUrl>
...
<AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute">
    <_Parameter1>DiscordWebhookUrl</_Parameter1>
    <_Parameter2>$(DiscordWebhookUrl)</_Parameter2>
</AssemblyAttribute>
```

Unlike `RelayUrl`'s `wss://relay.example.invalid` placeholder, the empty default is meaningful: empty URL = no-op. Local dev builds and forks that don't set the secret simply don't ping anything; no failure mode.

**`AppConfig.cs`** — add a static accessor using the existing `ResolveAssemblyMetadata` helper:

```csharp
public static string DefaultDiscordWebhookUrl { get; } =
    ResolveAssemblyMetadata("DiscordWebhookUrl", fallback: "");
```

Not added as an `AppConfig` record field — Discord URL is strictly binary-owned, never persisted in `config.json`, never read from per-machine state.

**`release.yml`** — add an env var alongside the existing relay secrets:

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

The release workflow does **not** fail if `GPH_DISCORD_WEBHOOK_URL` is unset (unlike `GPH_RELAY_URL`, which is load-bearing). Discord notifications are optional infrastructure.

### 2. `DiscordNotifier` class

Lives at `src/GamePartyHud/Network/DiscordNotifier.cs`. Interface + sealed implementation. One job.

```csharp
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

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
```

Key choices:

- **Empty URL → no-op.** Clean local-dev path with one branch and zero side effects.
- **Wire format.** Discord webhooks accept `POST application/json` with `{"content": "..."}`. Success = `204 No Content`. Non-2xx logged but not thrown — callers stay simple.
- **`allowed_mentions = { parse: [] }`** blocks any `@here` / `@everyone` / role mentions in case a nickname contains one. Defensive but cheap.
- **Optional injected `HttpClient`** for tests with a fake `HttpMessageHandler`; production constructs its own with a 10 s timeout (Discord can be slow).
- **`Log.Warn` on HTTP failure** — never bubbles up to the caller.
- **Notifier itself does NOT swallow `HttpRequestException` / network errors.** That's the caller's job (see §3). Keeps the notifier honest and the wrapper boundary clear.

### 3. Wiring into `App.JoinOrCreateAsync`

**New field** on `App`:

```csharp
private IDiscordNotifier? _discord;
```

**In `OnStartup`**, after the existing `_capture = new WindowsScreenCapture();` line:

```csharp
_discord = new DiscordNotifier(AppConfig.DefaultDiscordWebhookUrl);
Log.Info(string.IsNullOrWhiteSpace(AppConfig.DefaultDiscordWebhookUrl)
    ? "Discord notifications: disabled (no webhook URL compiled in)."
    : "Discord notifications: enabled.");
```

**`JoinOrCreateAsync` signature** gains a `wasCreated` parameter. The two `IController` entrypoints diverge by intent:

```csharp
Task MainWindow.IController.CreatePartyAsync() =>
    JoinOrCreateAsync(PartyIdGenerator.Generate(), wasCreated: true);

Task MainWindow.IController.JoinPartyAsync(string partyId) =>
    JoinOrCreateAsync(partyId, wasCreated: false);

private async Task JoinOrCreateAsync(string partyId, bool wasCreated)
{
    // ... existing body unchanged ...
    Log.Info($"Party '{partyId}' joined. ...");

    if (wasCreated)
    {
        // Fire-and-forget — Discord must never gate party creation. The
        // wrapper catches everything so a 4xx / network blip / DNS hiccup
        // can't surface as an unobserved task exception in the global
        // handler.
        _ = NotifyDiscordPartyCreatedAsync(_config.Nickname, partyId);
    }

    PartyStateChanged?.Invoke();
}

private async Task NotifyDiscordPartyCreatedAsync(string nickname, string partyId)
{
    if (_discord is null) return;
    try { await _discord.NotifyPartyCreatedAsync(nickname, partyId); }
    catch (Exception ex) { Log.Warn("Discord notification failed: " + ex.Message); }
}
```

**Why fire from `JoinOrCreateAsync` rather than the entrypoint:** by waiting until *after* `_orch.StartLoops()` we know the party is genuinely live (relay accepted us, orchestrator running). Firing earlier risks sending "X created party ABCDEF" when the relay was actually unreachable and the user got a "couldn't connect" dialog instead.

**`OnExit`** disposes the notifier alongside the existing teardown:

```csharp
(_discord as IDisposable)?.Dispose();
```

### 4. Testing

Per `CLAUDE.md`: pure logic gets unit tests, UI is manually verified. The notifier is pure logic (HTTP + JSON serialisation), so it's testable end-to-end with a fake message handler.

**New file:** `tests/GamePartyHud.Tests/Network/DiscordNotifierTests.cs`.

```csharp
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

**No UI test additions.** The change is invisible in the UI. Manual verification = build with `GPH_DISCORD_WEBHOOK_URL` set locally, run, click Create Party, watch the Discord channel for the message.

### 5. User-facing disclosure

Add a short paragraph to `docs/requirements.md` under "Non-functional requirements":

> **Telemetry.** The official release binary sends a single notification (`{nickname}` created a party with id `{partyId}`) to the project's Discord whenever you create a party. No other data is sent and nothing is sent when you join an existing party. The webhook URL is compiled into the official build only; forks and source builds without the `GPH_DISCORD_WEBHOOK_URL` secret send nothing.

Keeps the project's "free, no-tracking" reputation intact by being upfront about the one thing that does go out over the wire.

## Risks & mitigations

- **Discord rate limit (30 req/min per webhook).** Party creation is rare — handful per hour at peak. If we ever hit it, 429 is logged and the next call works fine. No retry, no queue.
- **Replay on reconnect.** A party that loses the relay connection and reconnects must NOT re-notify. Already handled: `wasCreated == true` only on the user's explicit "Create" click — reconnect paths go through different code that never sets the flag.
- **Notifier-induced startup failure.** Constructor never throws (just stores the URL); HttpClient creation can OOM in extreme cases but that would crash the app regardless.
- **Webhook URL leak via decompilation.** Anyone can `ildasm` the .exe and read `AssemblyMetadataAttribute` values. Treat the webhook URL as semi-public — anyone determined enough can spam our channel. Discord lets us rotate the URL if abuse becomes a problem; nothing else relies on it.
- **Nickname injection.** Nicknames are 32-char max (enforced by MainWindow's `MaxLength`). `allowed_mentions = { parse: [] }` blocks `@everyone` / `@here` / role pings. Markdown formatting characters in nicknames would render as Discord markdown — acceptable / amusing edge case, not a security issue.

## Out of scope / future ideas

- Notify on party-disband, member-join, member-leave, kick — none requested.
- Configurable webhook URL per install — explicitly rejected (telemetry, not user-controlled).
- Switch to richer Discord embeds (timestamp, app version footer, etc.) — possible later if useful.
- Tracking application version / OS in the message — adds privacy footprint, not requested.
