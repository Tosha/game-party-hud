using System.Linq;
using System.Reflection;
using GamePartyHud.Capture;
using GamePartyHud.Party;

namespace GamePartyHud.Config;

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
    bool FullscreenDisclaimerDismissed = false)
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

    private static string ResolveAssemblyMetadata(string key, string fallback)
    {
        var fromMetadata = typeof(AppConfig).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)
            ?.Value;
        if (!string.IsNullOrWhiteSpace(fromMetadata)) return fromMetadata;
        return fallback;
    }

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
        PollIntervalMs: 700,
        RelayUrl: DefaultRelayUrl,
        RelayFallbackUrl: DefaultRelayFallbackUrl,
        FullscreenDisclaimerDismissed: false);
}

public sealed record HudPosition(double X, double Y, int Monitor);
