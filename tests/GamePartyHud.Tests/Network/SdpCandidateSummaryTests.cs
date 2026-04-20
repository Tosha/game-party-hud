using GamePartyHud.Network;
using Xunit;

namespace GamePartyHud.Tests.Network;

/// <summary>
/// <see cref="PeerNetwork.SummarizeSdpCandidates"/> turns the SDP candidate lines
/// into a compact "host=X,srflx=Y,…" tag that goes in every "pre-generated offer
/// ready" log. The previous incident was diagnosed purely from an absent srflx
/// row in one peer's SDP — so this summary is load-bearing diagnostics.
/// </summary>
public class SdpCandidateSummaryTests
{
    private const string SdpHostOnly = """
v=0
o=- 0 0 IN IP4 0.0.0.0
s=-
t=0 0
m=application 9 UDP/DTLS/SCTP webrtc-datachannel
c=IN IP4 0.0.0.0
a=candidate:1 1 udp 2122260223 192.168.1.10 54321 typ host
a=sendrecv
""";

    private const string SdpHostAndSrflx = """
v=0
o=- 0 0 IN IP4 0.0.0.0
s=-
m=application 9 UDP/DTLS/SCTP webrtc-datachannel
a=candidate:1 1 udp 2122260223 192.168.1.10 54321 typ host
a=candidate:2 1 udp 1686052607 203.0.113.9 54321 typ srflx raddr 192.168.1.10 rport 54321
""";

    private const string SdpAllFourTypes = """
a=candidate:1 1 udp 2122260223 192.168.1.10 54321 typ host
a=candidate:2 1 udp 1686052607 203.0.113.9 54321 typ srflx
a=candidate:3 1 udp 1686052607 203.0.113.9 54322 typ prflx
a=candidate:4 1 udp 41885439  198.51.100.7 3478  typ relay
""";

    [Fact]
    public void HostOnly_IsReported()
    {
        Assert.Equal("host=1", PeerNetwork.SummarizeSdpCandidates(SdpHostOnly));
    }

    [Fact]
    public void HostPlusSrflx_IsReported()
    {
        Assert.Equal("host=1,srflx=1", PeerNetwork.SummarizeSdpCandidates(SdpHostAndSrflx));
    }

    [Fact]
    public void AllCandidateTypes_AreTallied()
    {
        Assert.Equal("host=1,srflx=1,prflx=1,relay=1", PeerNetwork.SummarizeSdpCandidates(SdpAllFourTypes));
    }

    [Fact]
    public void NoCandidateLines_ReportsNone()
    {
        Assert.Equal("<none>", PeerNetwork.SummarizeSdpCandidates("v=0\r\ns=-\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\n"));
    }

    [Fact]
    public void MultipleHostCandidates_AreCountedSeparately()
    {
        const string sdp = """
a=candidate:1 1 udp 2122260223 192.168.1.10 54321 typ host
a=candidate:2 1 udp 2122260223 10.0.0.5 54322 typ host
a=candidate:3 1 udp 2122260223 fe80::1 54323 typ host
""";
        Assert.Equal("host=3", PeerNetwork.SummarizeSdpCandidates(sdp));
    }
}
