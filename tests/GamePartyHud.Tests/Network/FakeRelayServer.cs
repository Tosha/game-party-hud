using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GamePartyHud.Tests.Network;

/// <summary>
/// Minimal in-process WebSocket server for testing <c>RelayClient</c>. Binds to
/// a loopback port, accepts exactly one connection at a time, and exposes the
/// active <see cref="WebSocket"/> so tests can drive it: send server frames,
/// receive client frames, close from the server side.
///
/// Not a full relay implementation — tests that care about routing logic drive
/// that behaviour via <see cref="SendFromServerAsync"/>.
/// </summary>
public sealed class FakeRelayServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private WebSocket? _active;
    private readonly ConcurrentQueue<string> _received = new();
    private readonly SemaphoreSlim _receivedSignal = new(0);

    public string WsUrl { get; }

    public FakeRelayServer()
    {
        var port = FindFreePort();
        var prefix = $"http://localhost:{port}/";
        _listener.Prefixes.Add(prefix);
        _listener.Start();
        WsUrl = $"ws://localhost:{port}/party/TEST";
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { return; }

            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 426;
                ctx.Response.Close();
                continue;
            }

            var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
            _active = wsCtx.WebSocket;
            _ = Task.Run(() => ReadLoopAsync(wsCtx.WebSocket));
        }
    }

    private async Task ReadLoopAsync(WebSocket ws)
    {
        var buf = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
        {
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(buf, _cts.Token).ConfigureAwait(false); }
            catch { return; }

            if (r.MessageType == WebSocketMessageType.Close) return;
            var text = Encoding.UTF8.GetString(buf, 0, r.Count);
            _received.Enqueue(text);
            _receivedSignal.Release();
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for the next frame the client sent us.</summary>
    public async Task<string> NextReceivedAsync(TimeSpan timeout)
    {
        if (!await _receivedSignal.WaitAsync(timeout).ConfigureAwait(false))
            throw new TimeoutException("FakeRelayServer: no client frame arrived in time.");
        _received.TryDequeue(out var msg);
        return msg!;
    }

    /// <summary>Sends a text frame from the server to the currently-connected client.</summary>
    public async Task SendFromServerAsync(string text)
    {
        var ws = _active ?? throw new InvalidOperationException("No active WebSocket connection.");
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, _cts.Token).ConfigureAwait(false);
    }

    /// <summary>Closes the active connection from the server side.</summary>
    public async Task CloseFromServerAsync(WebSocketCloseStatus code = WebSocketCloseStatus.NormalClosure, string reason = "bye")
    {
        var ws = _active;
        if (ws is null) return;
        try { await ws.CloseAsync(code, reason, CancellationToken.None).ConfigureAwait(false); } catch { }
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        return ValueTask.CompletedTask;
    }

    private static int FindFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
