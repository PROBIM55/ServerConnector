using System.Net;

namespace Connector.Network;

public enum NetBirdLocalIdentityStatus
{
    Unavailable,
    Available,
}

public sealed record NetBirdLocalIdentity(
    string WireGuardPublicKey,
    Uri ManagementUri,
    IReadOnlyList<IPAddress> AssignedInternalAddresses,
    DateTimeOffset ObservedAtUtc);

public sealed record NetBirdLocalIdentityReadResult(
    NetBirdLocalIdentityStatus Status,
    NetBirdLocalIdentity? Identity,
    string DiagnosticCode)
{
    public static NetBirdLocalIdentityReadResult Available(NetBirdLocalIdentity identity) =>
        new(NetBirdLocalIdentityStatus.Available, identity, "ready");

    public static NetBirdLocalIdentityReadResult Unavailable(string diagnosticCode) =>
        new(NetBirdLocalIdentityStatus.Unavailable, null, diagnosticCode);
}
