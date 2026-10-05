using System.Net;

namespace Connector.Access.NetBird;

public sealed class NetBirdDaemonPeerSnapshotOptions
{
    public required string ExecutablePath { get; init; }
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaximumOutputBytes { get; init; } = 1024 * 1024;
    public TimeSpan MaximumHandshakeAge { get; init; } = TimeSpan.FromMinutes(2);

    internal bool TryValidate(out ValidatedNetBirdDaemonPeerSnapshotOptions? validated)
    {
        validated = null;
        if (string.IsNullOrWhiteSpace(ExecutablePath) ||
            !Path.IsPathFullyQualified(ExecutablePath) ||
            CommandTimeout <= TimeSpan.Zero ||
            CommandTimeout > TimeSpan.FromMinutes(2) ||
            MaximumOutputBytes <= 0 ||
            MaximumOutputBytes > 8 * 1024 * 1024 ||
            MaximumHandshakeAge <= TimeSpan.Zero ||
            MaximumHandshakeAge > TimeSpan.FromDays(1))
        {
            return false;
        }

        try
        {
            validated = new ValidatedNetBirdDaemonPeerSnapshotOptions(
                Path.GetFullPath(ExecutablePath),
                CommandTimeout,
                MaximumOutputBytes,
                MaximumHandshakeAge);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}

public enum NetBirdDaemonPeerUnavailableReason
{
    InvalidConfiguration,
    InvalidExpectedPeer,
    ExecutableUnavailable,
    CommandFailed,
    CommandTimedOut,
    OutputTooLarge,
    MalformedOutput,
    ExactPeerNotFound
}

public enum NetBirdDaemonPeerStaleReason
{
    NotConnected,
    MissingHandshake,
    ExpiredHandshake,
    FutureHandshake
}

public abstract record NetBirdDaemonPeerSnapshotResult
{
    private protected NetBirdDaemonPeerSnapshotResult()
    {
    }
}

public sealed record NetBirdDaemonPeerUnavailable(NetBirdDaemonPeerUnavailableReason Reason)
    : NetBirdDaemonPeerSnapshotResult;

public sealed record NetBirdDaemonPeerAmbiguous(int ConflictingPeerCount)
    : NetBirdDaemonPeerSnapshotResult;

public sealed record NetBirdDaemonPeerStale(NetBirdDaemonPeerStaleReason Reason)
    : NetBirdDaemonPeerSnapshotResult;

public sealed record NetBirdDaemonPeerEvidence(
    string NetBirdIp,
    string? NetBirdIpv6,
    string PublicKey,
    string Status,
    DateTimeOffset LastWireguardHandshake);

public sealed record NetBirdDaemonPeerExact(NetBirdDaemonPeerEvidence Evidence)
    : NetBirdDaemonPeerSnapshotResult;

internal sealed record ValidatedNetBirdDaemonPeerSnapshotOptions(
    string ExecutablePath,
    TimeSpan CommandTimeout,
    int MaximumOutputBytes,
    TimeSpan MaximumHandshakeAge);

internal sealed record ParsedNetBirdDaemonPeer(
    string NetBirdIp,
    IPAddress ParsedNetBirdIp,
    string? NetBirdIpv6,
    IPAddress? ParsedNetBirdIpv6,
    string PublicKey,
    string Status,
    DateTimeOffset LastWireguardHandshake);
