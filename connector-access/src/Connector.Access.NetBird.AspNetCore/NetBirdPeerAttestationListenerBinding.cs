using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;

namespace Connector.Access.NetBird.AspNetCore;

/// <summary>Validated binding details for the private NetBird peer-attestation listener.</summary>
public sealed class NetBirdPeerAttestationListenerBinding
{
    private readonly IPAddress _listenerAddress;

    private NetBirdPeerAttestationListenerBinding(IPAddress listenerAddress, int httpsPort, string daemonExecutablePath)
    {
        _listenerAddress = listenerAddress;
        HttpsPort = httpsPort;
        DaemonExecutablePath = daemonExecutablePath;
    }

    public IPAddress ListenerAddress => CopyAddress(_listenerAddress);
    public int HttpsPort { get; }
    public string DaemonExecutablePath { get; }

    /// <summary>Creates a binding after validating its literal address, safe overlay CIDR, port, and daemon path.</summary>
    public static NetBirdPeerAttestationListenerBinding CreateValidated(
        string? listenerIpAddress,
        string? allowedOverlayCidr,
        int httpsPort,
        string? daemonExecutablePath)
    {
        if (httpsPort is <= 0 or > 65535)
            throw new InvalidOperationException(
                "Connector Access NetBird peer attestation requires a valid HTTPS port.");

        if (!TryParseOverlayListenerAddress(listenerIpAddress, out var listenerAddress))
        {
            throw new InvalidOperationException(
                "Connector Access NetBird peer attestation listener must be a literal private overlay IP address.");
        }
        if (!TryParseSafeOverlayNetwork(allowedOverlayCidr, out var allowedOverlayNetwork))
        {
            throw new InvalidOperationException(
                "Connector Access NetBird peer attestation overlay CIDR must be a canonical CGNAT, RFC1918, or IPv6 ULA network.");
        }
        if (!Contains(allowedOverlayNetwork!, listenerAddress!))
        {
            throw new InvalidOperationException(
                "Connector Access NetBird peer attestation listener is outside the configured overlay CIDR.");
        }

        if (string.IsNullOrWhiteSpace(daemonExecutablePath) ||
            !Path.IsPathFullyQualified(daemonExecutablePath))
        {
            throw new InvalidOperationException(
                "Connector Access NetBird peer attestation daemon executable path must be absolute.");
        }

        string fullDaemonPath;
        try
        {
            fullDaemonPath = Path.GetFullPath(daemonExecutablePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException(
                "Connector Access NetBird peer attestation daemon executable path is invalid.",
                exception);
        }

        if (!File.Exists(fullDaemonPath))
            throw new InvalidOperationException(
                "Connector Access NetBird peer attestation daemon executable is missing.");

        return new NetBirdPeerAttestationListenerBinding(listenerAddress!, httpsPort, fullDaemonPath);
    }

    public bool MatchesConnection(IPAddress? localAddress, int localPort)
    {
        var normalizedLocalAddress = Normalize(localAddress);
        return normalizedLocalAddress is not null &&
               normalizedLocalAddress.Equals(Normalize(_listenerAddress)) &&
               localPort == HttpsPort;
    }

    internal bool Matches(HttpContext context) =>
        MatchesConnection(context.Connection.LocalIpAddress, context.Connection.LocalPort);

    private static IPAddress CopyAddress(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPAddress(address.GetAddressBytes(), address.ScopeId)
            : new IPAddress(address.GetAddressBytes());

    private static bool TryParseOverlayListenerAddress(string? value, out IPAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Contains('/') || value.Contains('%') ||
            !IPAddress.TryParse(value, out var parsed) ||
            parsed.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) ||
            IPAddress.IsLoopback(parsed) || parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.IPv6Any) ||
            IsMulticast(parsed))
        {
            return false;
        }

        address = parsed;
        return true;
    }

    private static bool TryParseSafeOverlayNetwork(string? value, out OverlayIpNetwork? network)
    {
        network = null;
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var separator = value.IndexOf('/');
        var prefixText = separator >= 0 && separator < value.Length - 1
            ? value[(separator + 1)..]
            : string.Empty;
        if (separator <= 0 || separator != value.LastIndexOf('/') ||
            separator == value.Length - 1 ||
            prefixText.Any(character => character is < '0' or > '9') ||
            !IPAddress.TryParse(value[..separator], out var address) ||
            address.IsIPv4MappedToIPv6 ||
            !int.TryParse(prefixText, out var prefixLength))
        {
            return false;
        }

        var addressBits = address.AddressFamily switch
        {
            AddressFamily.InterNetwork => 32,
            AddressFamily.InterNetworkV6 => 128,
            _ => 0,
        };
        if (addressBits == 0 || prefixLength < 0 || prefixLength > addressBits)
            return false;

        var canonicalAddress = Mask(address, prefixLength);
        if (!canonicalAddress.Equals(address)) return false;

        var candidate = new OverlayIpNetwork(canonicalAddress, prefixLength);
        var safe = address.AddressFamily == AddressFamily.InterNetwork
            ? IsSubnetOf(candidate, IPAddress.Parse("100.64.0.0"), 10) ||
              IsSubnetOf(candidate, IPAddress.Parse("10.0.0.0"), 8) ||
              IsSubnetOf(candidate, IPAddress.Parse("172.16.0.0"), 12) ||
              IsSubnetOf(candidate, IPAddress.Parse("192.168.0.0"), 16)
            : IsSubnetOf(candidate, IPAddress.Parse("fc00::"), 7);
        if (!safe) return false;

        network = candidate;
        return true;
    }

    private static bool IsSubnetOf(OverlayIpNetwork candidate, IPAddress parentAddress, int parentPrefixLength) =>
        candidate.NetworkAddress.AddressFamily == parentAddress.AddressFamily &&
        candidate.PrefixLength >= parentPrefixLength &&
        PrefixEquals(candidate.NetworkAddress, parentAddress, parentPrefixLength);

    private static bool Contains(OverlayIpNetwork network, IPAddress address) =>
        network.NetworkAddress.AddressFamily == address.AddressFamily &&
        PrefixEquals(network.NetworkAddress, address, network.PrefixLength);

    private static bool PrefixEquals(IPAddress left, IPAddress right, int prefixLength)
    {
        var leftBytes = left.GetAddressBytes();
        var rightBytes = right.GetAddressBytes();
        if (leftBytes.Length != rightBytes.Length) return false;

        var wholeBytes = prefixLength / 8;
        for (var index = 0; index < wholeBytes; index++)
        {
            if (leftBytes[index] != rightBytes[index]) return false;
        }

        var remainingBits = prefixLength % 8;
        if (remainingBits == 0) return true;
        var mask = (byte)(0xff << (8 - remainingBits));
        return (leftBytes[wholeBytes] & mask) == (rightBytes[wholeBytes] & mask);
    }

    private static IPAddress Mask(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        var wholeBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        if (remainingBits > 0)
        {
            bytes[wholeBytes] &= (byte)(0xff << (8 - remainingBits));
            wholeBytes++;
        }
        Array.Clear(bytes, wholeBytes, bytes.Length - wholeBytes);
        return new IPAddress(bytes);
    }

    private static IPAddress? Normalize(IPAddress? address) =>
        address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address;

    private static bool IsMulticast(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] is >= 224 and <= 239
            : bytes[0] == 0xff;
    }

    private sealed record OverlayIpNetwork(IPAddress NetworkAddress, int PrefixLength);
}
