using System.Net;
using System.Net.Sockets;
using Connector.Network;

namespace Connector.Desktop.Services;

/// <summary>
/// What the account card, the account popover, «Настройки» and the «Подключение» tile show about this computer.
/// Display facts only: the device name, its overlay address, the gateway address and whether the tunnel to it is
/// direct or relayed. Never a token, certificate, key or credential. The user's e-mail and the company name are
/// not known locally (the device access profile carries only identifiers), so they are not part of the facts.
/// </summary>
public sealed record ConnectorAccountFacts(
    string DeviceName,
    string? NetworkAddress,
    string? Gateway,
    string? Route,
    string? NetBirdVersion)
{
    public const string Direct = "direct";
    public const string Relay = "relay";

    public static ConnectorAccountFacts Build(string deviceName, NetworkOverlaySnapshot? overlay, Uri? gateway)
    {
        var address = overlay?.AssignedInternalAddresses
            .OrderBy(item => item.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .FirstOrDefault();
        var host = gateway?.IdnHost;
        string? route = null;
        if (overlay?.Peers is { Count: > 0 } peers && IPAddress.TryParse(host, out var destination))
        {
            var peer = peers.FirstOrDefault(item => item.Address.Equals(destination)) ??
                       peers.FirstOrDefault(item => item.Serves(destination));
            if (peer is { Connected: true }) route = peer.Relayed ? Relay : Direct;
        }
        return new ConnectorAccountFacts(
            string.IsNullOrWhiteSpace(deviceName) ? "—" : deviceName.Trim(),
            address?.ToString(),
            string.IsNullOrWhiteSpace(host) ? null : host,
            route,
            overlay?.DaemonVersion);
    }
}
