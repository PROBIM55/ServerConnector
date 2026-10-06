using System.Net;

namespace Connector.Network;

/// <summary>Fixed administrative destination policy shared by non-HTTP adapters.</summary>
public sealed class ManagedOverlayDestinationPolicy
{
    private readonly OverlayAddressRange[] _ranges;

    public ManagedOverlayDestinationPolicy(IEnumerable<string> destinations)
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
    }

    public bool Contains(IPAddress address) => _ranges.Any(range => range.Contains(address));
}
