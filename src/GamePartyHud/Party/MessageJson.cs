using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GamePartyHud.Party;

/// <summary>
/// Wire-format encoder/decoder for <see cref="PartyMessage"/>s. Format is a small,
/// stable JSON shape so peers running different patch versions can still talk.
/// </summary>
public static class MessageJson
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Encode(PartyMessage msg) => msg switch
    {
        StateMessage s => JsonSerializer.Serialize(new
        {
            type = "state",
            peerId = s.PeerId,
            nick = s.Nick,
            role = s.Role,
            hp = s.Hp,
            t = s.T
        }, Opts),
        ByeMessage b => JsonSerializer.Serialize(new { type = "bye", peerId = b.PeerId }, Opts),
        KickMessage k => JsonSerializer.Serialize(new { type = "kick", target = k.Target }, Opts),
        _ => throw new ArgumentException("Unknown message type", nameof(msg))
    };

    public static PartyMessage? Decode(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) return null;
            var type = typeProp.GetString();

            return type switch
            {
                "state" => new StateMessage(
                    root.GetProperty("peerId").GetString() ?? "",
                    root.GetProperty("nick").GetString() ?? "",
                    ParseRole(root.GetProperty("role")),
                    ParseNullableFloat(root.GetProperty("hp")),
                    root.GetProperty("t").GetInt64()),
                "bye"  => new ByeMessage(root.GetProperty("peerId").GetString() ?? ""),
                "kick" => new KickMessage(root.GetProperty("target").GetString() ?? ""),
                _ => null
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Role ParseRole(JsonElement e) =>
        e.ValueKind == JsonValueKind.String
            ? Enum.Parse<Role>(e.GetString()!, ignoreCase: true)
            : (Role)e.GetInt32();

    private static float? ParseNullableFloat(JsonElement e) =>
        e.ValueKind == JsonValueKind.Null ? null : e.GetSingle();
}
