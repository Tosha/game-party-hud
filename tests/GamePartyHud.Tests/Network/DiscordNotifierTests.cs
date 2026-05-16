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

        // JsonSerializer.Serialize emits no whitespace between tokens by default,
        // so substring matches work directly against the raw body.
        var body = handler.Bodies[0];
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
