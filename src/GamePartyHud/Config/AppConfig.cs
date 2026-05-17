using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using GamePartyHud.Diagnostics;
using GamePartyHud.Party;

namespace GamePartyHud.Config;

public sealed record AppConfig(
    IReadOnlyList<Preset> Presets,
    string ActivePresetId,
    HudPosition HudPosition,
    bool HudLocked,
    string? LastPartyId,
    int PollIntervalMs,
    string RelayUrl,
    string RelayFallbackUrl = "",
    bool FullscreenDisclaimerDismissed = false,
    double HudScale = 1.0)
{
    /// <summary>
    /// Default relay endpoint, injected at build time via the
    /// <c>RelayUrl</c> MSBuild property (see <c>GamePartyHud.csproj</c>). Local
    /// dev builds inherit the placeholder default; release builds in CI
    /// substitute the real URL from the <c>GPH_RELAY_URL</c> GitHub Actions
    /// secret. End users override per-machine via the <c>RelayUrl</c> field
    /// in <c>%AppData%\GamePartyHud\config.json</c>.
    /// </summary>
    public static string DefaultRelayUrl { get; } = ResolveAssemblyMetadata("RelayUrl", fallback: "wss://relay.example.invalid");

    /// <summary>
    /// Optional secondary relay endpoint, tried after <see cref="DefaultRelayUrl"/>
    /// fails to connect within the client's timeout. Used to route around ISPs
    /// that block the Cloudflare CIDR ranges where the primary Worker lives;
    /// the fallback runs on a non-Cloudflare host (e.g. an Oracle Always-Free
    /// VM) and proxies into the same Worker. Empty string means no fallback,
    /// preserving the single-URL path for forks that haven't set up a bridge.
    /// </summary>
    public static string DefaultRelayFallbackUrl { get; } = ResolveAssemblyMetadata("RelayFallbackUrl", fallback: "");

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

    private static string ResolveAssemblyMetadata(string key, string fallback)
    {
        var fromMetadata = typeof(AppConfig).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)
            ?.Value;
        if (!string.IsNullOrWhiteSpace(fromMetadata)) return fromMetadata;
        return fallback;
    }

    /// <summary>
    /// Sentinel id used by <see cref="Defaults"/> and the legacy-config migration
    /// in <c>ConfigStore.Load</c> for the auto-seeded preset, so successive runs
    /// reference a stable id rather than a fresh GUID each time.
    /// </summary>
    public const string DefaultPresetId = "default";

    public static AppConfig Defaults { get; } = new(
        Presets: new[]
        {
            new Preset(
                Id: DefaultPresetId,
                Name: "Default",
                Nickname: "Player",
                Role: Role.Utility,
                HpCalibration: null,
                StaminaCalibration: null,
                ManaCalibration: null),
        },
        ActivePresetId: DefaultPresetId,
        HudPosition: new HudPosition(100, 100),
        HudLocked: true,
        LastPartyId: null,
        PollIntervalMs: 700,
        RelayUrl: DefaultRelayUrl,
        RelayFallbackUrl: DefaultRelayFallbackUrl,
        FullscreenDisclaimerDismissed: false,
        HudScale: 1.0);

    /// <summary>
    /// The preset currently in use. Resolves <see cref="ActivePresetId"/> against
    /// <see cref="Presets"/>; falls back to the first preset (and logs a warn) if
    /// the id is stale — protects callers from an `IndexOutOfRangeException` if
    /// config.json was hand-edited or got out of sync mid-write. <c>ConfigStore.Load</c>
    /// also repairs this invariant; the fallback here is belt-and-suspenders.
    /// </summary>
    [JsonIgnore]
    public Preset ActivePreset
    {
        get
        {
            var match = Presets.FirstOrDefault(p => p.Id == ActivePresetId);
            if (match is not null) return match;
            Log.Warn($"AppConfig: ActivePresetId '{ActivePresetId}' did not match any preset; falling back to '{Presets[0].Id}'.");
            return Presets[0];
        }
    }
}

public sealed record HudPosition(double X, double Y);
