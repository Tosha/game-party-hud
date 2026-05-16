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
