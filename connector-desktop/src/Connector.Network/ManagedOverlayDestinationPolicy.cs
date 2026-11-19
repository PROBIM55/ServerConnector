using System.Net;

namespace Connector.Network;

/// <summary>Fixed administrative destination policy shared by non-HTTP adapters.</summary>
public sealed class ManagedOverlayDestinationPolicy
{
    private readonly OverlayAddressRange[] _ranges;
    private readonly HashSet<IPAddress> _routedAddresses;

    public ManagedOverlayDestinationPolicy(IEnumerable<string> destinations, IEnumerable<string>? routedDestinations = null)
    {
        ArgumentNullException.ThrowIfNull(destinations);
        var values = destinations.ToArray();
        if (values.Length > 128) throw new ArgumentException("Too many managed overlay destinations.");
        _ranges = values.Select(value =>
        {
            if (string.IsNullOrWhiteSpace(value) || value.Contains('/'))
                throw new ArgumentException("A managed destination must be one fixed private overlay address.");
            return OverlayAddressRange.Parse(value);
        }).ToArray();
        var routed = (routedDestinations ?? []).ToArray();
        if (routed.Length > 16) throw new ArgumentException("Too many fixed routed SMB destinations.");
        _routedAddresses = routed.Select(value =>
        {
            if (!IPAddress.TryParse(value, out var address) ||
                address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                address.ToString() != value || IPAddress.IsLoopback(address))
                throw new ArgumentException("A routed SMB destination must be one canonical IPv4 address.");
            var bytes = address.GetAddressBytes();
            if (bytes[0] is 0 or >= 224 || bytes[0] == 169 && bytes[1] == 254)
                throw new ArgumentException("A routed SMB destination must be unicast.");
            return address;
        }).ToHashSet();
    }

    public bool Contains(IPAddress address) => _ranges.Any(range => range.Contains(address)) || _routedAddresses.Contains(address);
}
