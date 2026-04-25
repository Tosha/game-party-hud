using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GamePartyHud.Network;

/// <summary>
/// Wire contract between the C# <see cref="RelayClient"/> and the TypeScript
/// Cloudflare Worker in the repo's <c>relay/</c> folder. Keep in lockstep with
/// <c>relay/src/protocol.ts</c>; the canonical JSON strings live in
/// <c>relay/test/fixtures.ts</c> and are mirrored verbatim in this project's
/// <c>RelayProtocolTests</c>.
/// </summary>
public static class RelayProtocol
{
    public abstract record ServerMessage;
    public sealed record Welcome(string PeerId, IReadOnlyList<string> Members) : ServerMessage;
    public sealed record PeerJoined(string PeerId)                             : ServerMessage;
    public sealed record PeerLeft(string PeerId)                               : ServerMessage;
    public sealed record Message(string FromPeerId, string Payload)            : ServerMessage;
    public sealed record ErrorMessage(string Reason)                           : ServerMessage;

    // Serializer options chosen so the output matches relay/test/fixtures.ts
    // byte-for-byte: camelCase (web default), no whitespace, and the relaxed
    // JS encoder so a quote inside the payload is escaped as \" — matching
    // JSON.stringify in the TS server. The default System.Text.Json encoder
    // would emit " instead, breaking byte-for-byte parity. We're sending
    // the result over a WebSocket text frame, not embedding it in HTML, so
    // the relaxed encoder's looser HTML-escape policy is safe here.
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string EncodeJoin(string peerId) =>
        JsonSerializer.Serialize(new { type = "join", peerId }, WireJson);

    public static string EncodeBroadcast(string payload) =>
        JsonSerializer.Serialize(new { type = "broadcast", payload }, WireJson);

    public static ServerMessage? DecodeServerMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return null;

            return typeEl.GetString() switch
            {
                "welcome" => new Welcome(
                    root.GetProperty("peerId").GetString() ?? "",
                    ReadStringArray(root.GetProperty("members"))),
                "peer-joined" => new PeerJoined(root.GetProperty("peerId").GetString() ?? ""),
                "peer-left"   => new PeerLeft(root.GetProperty("peerId").GetString() ?? ""),
                "message"     => new Message(
                    root.GetProperty("fromPeerId").GetString() ?? "",
                    root.GetProperty("payload").GetString() ?? ""),
                "error"       => new ErrorMessage(root.GetProperty("reason").GetString() ?? ""),
                _ => null
            };
        }
        catch (JsonException) { return null; }
        catch (KeyNotFoundException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement el)
    {
        var list = new List<string>(el.GetArrayLength());
        foreach (var item in el.EnumerateArray())
        {
            list.Add(item.GetString() ?? "");
        }
        return list;
    }
}
