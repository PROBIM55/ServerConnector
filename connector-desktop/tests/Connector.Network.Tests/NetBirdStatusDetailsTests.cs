using System.Net;
using System.Text.Json;

namespace Connector.Network.Tests;

public sealed class NetBirdStatusDetailsTests
{
    private const string Status = """
        {"daemonVersion":"0.79.0","cliVersion":"0.79.0","peers":{"total":2,"connected":2,"details":[
          {"fqdn":"gw.netbird.cloud","netbirdIp":"100.108.74.8","publicKey":"AQID","status":"Connected","connectionType":"P2P","networks":["10.20.0.0/16"]},
          {"fqdn":"relay.netbird.cloud","netbirdIp":"100.108.1.2/32","status":"Connected","connectionType":"Relayed"},
          {"fqdn":"broken","netbirdIp":"not-an-ip"}]}}
        """;

    [Fact]
    public void Peers_KeepOnlyAddressRoutesAndConnectionType()
    {
        using var document = JsonDocument.Parse(Status);
        var peers = NetBirdCliClient.ReadPeers(document.RootElement);

        Assert.Equal(2, peers.Count);
        Assert.False(peers[0].Relayed);
        Assert.True(peers[0].Connected);
        Assert.True(peers[1].Relayed);
        Assert.Equal(IPAddress.Parse("100.108.1.2"), peers[1].Address);
        Assert.Equal("0.79.0", NetBirdCliClient.ReadVersion(document.RootElement));
    }

    [Fact]
    public void Peer_ServesItsOwnAddressAndRoutedNetworksOnly()
    {
        var peer = new NetworkOverlayPeer(IPAddress.Parse("100.108.74.8"), true, false, ["10.20.0.0/16"]);

        Assert.True(peer.Serves(IPAddress.Parse("100.108.74.8")));
        Assert.True(peer.Serves(IPAddress.Parse("10.20.5.7")));
        Assert.False(peer.Serves(IPAddress.Parse("10.21.0.1")));
        Assert.False(peer.Serves(IPAddress.Parse("62.113.36.107")));
    }

    [Fact]
    public void Version_RejectsTextThatIsNotAVersionNumber()
    {
        using var document = JsonDocument.Parse("""{"daemonVersion":"0.79.0; rm -rf"}""");
        Assert.Null(NetBirdCliClient.ReadVersion(document.RootElement));
    }
}
