using Xunit;
using System.Net;
using System.Text.Json;
using Connector.Desktop.Services;
using Connector.Network;

namespace Connector.Desktop.Tests;

public sealed class ConnectorAccountFactsTests
{
    private static NetworkOverlaySnapshot Overlay(params NetworkOverlayPeer[] peers) => new(
        NetworkServiceStatus.Ready, true, true, true, [IPAddress.Parse("fd00::5"), IPAddress.Parse("100.108.253.19")],
        DateTimeOffset.UtcNow, "ready", new Uri("https://vpn.example.test"), "0.79.0", peers);

    [Fact]
    public void Direct_route_address_gateway_and_version_come_from_the_overlay()
    {
        var facts = ConnectorAccountFacts.Build("LAGOM",
            Overlay(new NetworkOverlayPeer(IPAddress.Parse("100.108.74.8"), true, false, [])),
            new Uri("https://100.108.74.8:8443/api/platform/connector/access/v1/"));

        Assert.Equal(new ConnectorAccountFacts("LAGOM", "100.108.253.19", "100.108.74.8", ConnectorAccountFacts.Direct, "0.79.0"), facts);
    }

    [Fact]
    public void Relayed_route_through_a_routing_peer_is_reported_as_relay()
    {
        var facts = ConnectorAccountFacts.Build("LAGOM",
            Overlay(new NetworkOverlayPeer(IPAddress.Parse("100.108.1.1"), true, true, ["10.0.0.0/8"])),
            new Uri("https://10.1.2.3/api/"));

        Assert.Equal(ConnectorAccountFacts.Relay, facts.Route);
    }

    [Fact]
    public void Unknown_facts_stay_empty_and_nothing_secret_is_exposed()
    {
        var facts = ConnectorAccountFacts.Build(" ", null, null);

        Assert.Equal(new ConnectorAccountFacts("—", null, null, null, null), facts);
        var json = JsonSerializer.Serialize(ConnectorAccountFacts.Build("PC",
            Overlay(new NetworkOverlayPeer(IPAddress.Parse("100.108.74.8"), false, false, [])), new Uri("https://100.108.74.8/")));
        Assert.DoesNotContain("vpn.example.test", json, StringComparison.Ordinal);
        Assert.Null(JsonSerializer.Deserialize<ConnectorAccountFacts>(json)!.Route);
    }
}

public sealed class DesktopUpdateNotesTests
{
    [Fact]
    public void Notes_become_short_list_lines_without_bullets_or_links()
    {
        var lines = DesktopUpdateNotes.Lines("## 1.1.13\r\n- Работа рядом с прежним Structura Connector\n* Стандарт Tekla обновляется\n\nПодробнее: https://example.test\n• Защита заданий");

        Assert.Equal(["1.1.13", "Работа рядом с прежним Structura Connector", "Стандарт Tekla обновляется", "Защита заданий"], lines);
        Assert.Empty(DesktopUpdateNotes.Lines(null));
    }

    [Fact]
    public void Only_plain_version_numbers_are_shown()
    {
        Assert.Equal("1.1.13", DesktopUpdateNotes.SafeVersion(" 1.1.13 "));
        Assert.Null(DesktopUpdateNotes.SafeVersion("1.1.13<script>"));
    }
}
