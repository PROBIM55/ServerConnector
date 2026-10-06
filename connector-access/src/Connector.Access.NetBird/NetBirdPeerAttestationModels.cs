using System.Net;
using System.Net.Sockets;

namespace Connector.Access.NetBird;

public sealed class NetBirdPeerAttestationOptions
{
    public required string ExpectedServerOverlayListenerIp { get; init; }
    public TimeSpan MaximumTransportStateAge { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan MaximumPeerHandshakeAge { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan MaximumFutureClockSkew { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan AttestationLifetime { get; init; } = TimeSpan.FromSeconds(30);

    internal bool TryValidate(out ValidatedNetBirdPeerAttestationOptions? validated)
    {
        validated = null;
        if (!NetBirdPeerAttestationValidation.TryParseSocketIp(
                ExpectedServerOverlayListenerIp,
                out var listenerIp) ||
            MaximumTransportStateAge <= TimeSpan.Zero ||
            MaximumTransportStateAge > TimeSpan.FromMinutes(5) ||
            MaximumPeerHandshakeAge <= TimeSpan.Zero ||
            MaximumPeerHandshakeAge > TimeSpan.FromMinutes(5) ||
            MaximumFutureClockSkew < TimeSpan.Zero ||
            MaximumFutureClockSkew > TimeSpan.FromMinutes(1) ||
            AttestationLifetime <= TimeSpan.Zero ||
            AttestationLifetime > TimeSpan.FromMinutes(1))
        {
            return false;
        }

        validated = new ValidatedNetBirdPeerAttestationOptions(
            listenerIp!,
            MaximumTransportStateAge,
            MaximumPeerHandshakeAge,
            MaximumFutureClockSkew,
            AttestationLifetime);
        return true;
    }
}

public sealed record NetBirdPeerAttestationRequest(
    AuthenticatedDevice Device,
    string ObservedLocalIp,
    string ObservedRemoteIp,
    string ClaimedWireGuardPublicKey,
    string Nonce);

public sealed record NetBirdPeerAttestation(
    string Nonce,
    string DeviceId,
    long AccessRevision,
    string CertificateSha256,
    string PeerId,
    string SourceIp,
    string ListenerIp,
    string WireGuardPublicKey,
    DateTimeOffset TransportStateObservedAtUtc,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public enum NetBirdPeerAttestationFailureReason
{
    InvalidConfiguration,
    InvalidRequest,
    ListenerIpMismatch,
    TransportStateUnavailable,
    TransportStateMismatch,
    TransportStateStale,
    SourceIpMismatch,
    PeerSnapshotNotExact,
    DependencyUnavailable
}

public abstract record NetBirdPeerAttestationResult
{
    private protected NetBirdPeerAttestationResult()
    {
    }
}

public sealed record NetBirdPeerAttestationAccepted(NetBirdPeerAttestation Attestation)
    : NetBirdPeerAttestationResult;

public sealed record NetBirdPeerAttestationRejected(NetBirdPeerAttestationFailureReason Reason)
    : NetBirdPeerAttestationResult;

internal sealed record ValidatedNetBirdPeerAttestationOptions(
    IPAddress ExpectedServerOverlayListenerIp,
    TimeSpan MaximumTransportStateAge,
    TimeSpan MaximumPeerHandshakeAge,
    TimeSpan MaximumFutureClockSkew,
    TimeSpan AttestationLifetime);

internal static class NetBirdPeerAttestationValidation
{
    public static bool TryParseSocketIp(string? value, out IPAddress? address)
    {
        address = null;
        if (value is null || value.Length is < 2 or > 45 ||
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

    public static bool IsCanonicalNonce(string? value)
    {
        if (value is null || value.Length is < 22 or > 86 ||
            value.Any(character =>
                !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
        {
            return false;
        }

        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 += new string('=', (4 - base64.Length % 4) % 4);
            var bytes = Convert.FromBase64String(base64);
            return bytes.Length is >= 16 and <= 64 &&
                   string.Equals(ToBase64Url(bytes), value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool IsCanonicalCertificateSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsMulticast(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] is >= 224 and <= 239
            : bytes[0] == 0xff;
    }
}
