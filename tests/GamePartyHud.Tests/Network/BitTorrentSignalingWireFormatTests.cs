using System;
using System.Linq;
using System.Text.Json;
using GamePartyHud.Network;
using Xunit;

namespace GamePartyHud.Tests.Network;

/// <summary>
/// Wire-format regression. The earlier implementation sent <c>info_hash</c> as a
/// 40-char hex string and <c>peer_id</c> as 32-char hex; the Node-based
/// <c>bittorrent-tracker</c> enforces <c>length === 20</c> on both, so those
/// announces were silently dropped and peers never discovered each other.
/// These tests pin the fix: each wire identifier must be exactly 20 code points
/// and round-trip through hex losslessly.
/// </summary>
public class BitTorrentSignalingWireFormatTests
{
    [Fact]
    public void HexToBinary_ProducesOneCharPerHexPair()
    {
        const string hex = "b769f18e0fabc123456789abcdef0123456789ab"; // 40 chars → 20 bytes
        var bin = BitTorrentSignaling.HexToBinary(hex);
        Assert.Equal(20, bin.Length);
        // First byte = 0xb7 = 183
        Assert.Equal(0xb7, bin[0]);
        Assert.Equal(0x69, bin[1]);
        Assert.Equal(0xab, bin[19]);
    }

    [Fact]
    public void HexToBinary_AllowsFullByteRange_0x00_to_0xFF()
    {
        // 20 bytes spanning 0x00..0x13 to include NUL and control chars.
        var hex = string.Concat(Enumerable.Range(0, 20).Select(i => i.ToString("x2")));
        var bin = BitTorrentSignaling.HexToBinary(hex);
        Assert.Equal(20, bin.Length);
        for (int i = 0; i < 20; i++) Assert.Equal(i, bin[i]);
    }

    [Fact]
    public void HexBinaryRoundTrip_IsLossless()
    {
        var rng = new Random(12345);
        var bytes = new byte[20];
        rng.NextBytes(bytes);
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        var bin = BitTorrentSignaling.HexToBinary(hex);
        Assert.Equal(hex, BitTorrentSignaling.BinaryToHex(bin));
    }

    [Fact]
    public void HexToBinary_RejectsOddLength()
    {
        Assert.Throws<ArgumentException>(() => BitTorrentSignaling.HexToBinary("abc"));
    }

    [Fact]
    public void HexToBinary_RejectsNonHexChars()
    {
        Assert.Throws<ArgumentException>(() => BitTorrentSignaling.HexToBinary("zz00"));
    }

    /// <summary>
    /// End-to-end JSON shape check. We can't stand up a real public tracker in
    /// unit tests, but we can verify that an announce serialized through the
    /// signaling layer's wire encoding parses back to <c>info_hash.length === 20</c>
    /// and <c>peer_id.length === 20</c> — exactly what Node-based trackers
    /// validate against.
    /// </summary>
    [Fact]
    public void SerializedAnnounceFields_DecodeToTwentyCodePoints()
    {
        var infoHashHex = "b769f18e0fabc123456789abcdef0123456789ab";
        var peerIdHex   = "a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe";

        var payload = new
        {
            action    = "announce",
            info_hash = BitTorrentSignaling.HexToBinary(infoHashHex),
            peer_id   = BitTorrentSignaling.HexToBinary(peerIdHex),
            numwant   = 20,
            left      = 0
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default
        });

        using var doc = JsonDocument.Parse(json);
        var ih = doc.RootElement.GetProperty("info_hash").GetString()!;
        var pid = doc.RootElement.GetProperty("peer_id").GetString()!;
        Assert.Equal(20, ih.Length);
        Assert.Equal(20, pid.Length);
        Assert.Equal(infoHashHex, BitTorrentSignaling.BinaryToHex(ih));
        Assert.Equal(peerIdHex,   BitTorrentSignaling.BinaryToHex(pid));
    }
}
