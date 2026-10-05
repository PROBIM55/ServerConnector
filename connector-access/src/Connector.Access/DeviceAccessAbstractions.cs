using System.Data.Common;
using System.Security.Cryptography.X509Certificates;
using Connector.Access.Contracts;

namespace Connector.Access;

public enum PlatformAccessOperation
{
    IssueEnrollmentToken,
    EnrollDevice,
    AuthenticateDevice,
    ReadAccessProfile,
    RevokeDevice,
    ListDevices,
    ReadGrantCatalog,
    ReadUserGrants,
    UpdateUserGrants,
}

public sealed record PlatformAccessSnapshot(
    string UserId,
    string CompanyId,
    bool UserIsActive,
    bool MembershipIsActive,
    bool CompanyIsActive,
    bool IsAuthorized,
    long AccessRevision,
    IReadOnlyList<ModuleGrant> Modules,
    IReadOnlyList<ResourceGrant> Resources);

public interface IPlatformAccessDirectory
{
    ValueTask<PlatformAccessSnapshot?> AuthorizeAsync(
        string actorUserId,
        string subjectUserId,
        string companyId,
        PlatformAccessOperation operation,
        CancellationToken cancellationToken);
}

public interface IDeviceAccessDbConnectionFactory
{
    ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken);
}

public interface IX509DeviceCertificateIssuer
{
    X509Certificate2 IssuerCertificate { get; }

    ValueTask<X509Certificate2> IssueAsync(
        CertificateRequest certificateRequest,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        CancellationToken cancellationToken);
}

// Commands carry a stable id for provider idempotency. This library deliberately
// does not mint or return VPN/SMB credentials.
public interface IDeviceAccessGrantProvider
{
    string ProviderName { get; }

    ValueTask<DeviceAccessProviderReceipt> ApplyAsync(
        DeviceAccessProviderCommand command,
        CancellationToken cancellationToken);

    ValueTask<DeviceAccessProviderReceipt> RevokeAsync(
        DeviceAccessProviderCommand command,
        CancellationToken cancellationToken);
}

// The HTTP layer authenticates the exact issued device certificate before this
// reader is called. Implementations may return only a short-lived VPN bootstrap;
// API/SMB grants are never carried by this response.
public interface IConnectorVpnBootstrapReader
{
    ValueTask<DeviceAccessResult<ConnectorVpnBootstrap>> GetAsync(
        AuthenticatedDevice device,
        CancellationToken cancellationToken);

    ValueTask<DeviceAccessResult<ConnectorVpnTransportState>> GetStateAsync(
        AuthenticatedDevice device,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(DeviceAccessResult<ConnectorVpnTransportState>.Fail("vpn_state_unavailable", "VPN state is not available."));
}

public enum DeviceAccessProviderCommandKind { Apply, Revoke }

public sealed record DeviceAccessProviderCommand(
    string CommandId,
    string ProviderName,
    DeviceAccessProviderCommandKind Kind,
    string DeviceId,
    string UserId,
    string CompanyId,
    long DesiredRevision,
    IReadOnlyList<ModuleGrant> Modules,
    IReadOnlyList<ResourceGrant> Resources);

public sealed record ProviderDispatchResult(
    string CommandId,
    string ProviderName,
    bool Succeeded,
    string? Error);

// Providers must durably fence by (DeviceId, DesiredRevision, Kind): reject any
// lower revision and reject a different action at an already observed revision.
// The receipt is checked by the local outbox, but remote RPC and local DB commit
// are intentionally not represented as one atomic operation.
public sealed record DeviceAccessProviderReceipt(
    string CommandId,
    long AppliedRevision,
    DeviceAccessProviderCommandKind Kind);

public sealed class DeviceAccessOptions
{
    public TimeSpan EnrollmentTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan MaximumEnrollmentTokenLifetime { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan ClientCertificateLifetime { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan AccessProfileLifetime { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan EnrollmentIssuanceLease { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan EnrollmentProofMaximumLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan ProviderDispatchLease { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan ProviderRetryBackoff { get; set; } = TimeSpan.FromSeconds(30);
    public ISet<string> RequiredAccessProviders { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "api", "vpn", "smb" };
}

public sealed record IssueEnrollmentTokenCommand(
    string ActorUserId,
    string UserId,
    string CompanyId,
    TimeSpan? Lifetime = null);

public sealed record AuthenticatedDevice(
    string DeviceId,
    string UserId,
    string CompanyId,
    string CertificateSha256,
    long AccessRevision);

// This proof authorizes only enrollment response recovery/retry. It is never a
// device identity and cannot be used to read profiles or grants.
public sealed class EnrollmentKeyProof
{
    internal EnrollmentKeyProof(
        Guid enrollmentRequestId,
        string deviceId,
        string userId,
        string companyId,
        string publicKeySha256,
        DateTimeOffset validUntilUtc)
    {
        EnrollmentRequestId = enrollmentRequestId;
        DeviceId = deviceId;
        UserId = userId;
        CompanyId = companyId;
        PublicKeySha256 = publicKeySha256;
        ValidUntilUtc = validUntilUtc;
    }

    public Guid EnrollmentRequestId { get; }
    public string DeviceId { get; }
    public string UserId { get; }
    public string CompanyId { get; }
    public string PublicKeySha256 { get; }
    internal DateTimeOffset ValidUntilUtc { get; }
}

public sealed record RevokeDeviceCommand(
    string ActorUserId,
    string CompanyId,
    string DeviceId);

public sealed class DeviceAccessResult<T>
{
    private DeviceAccessResult(T? value, DeviceAccessFailure? failure)
    {
        Value = value;
        Failure = failure;
    }

    public bool IsSuccess => Failure is null;
    public T? Value { get; }
    public DeviceAccessFailure? Failure { get; }

    public static DeviceAccessResult<T> Success(T value) => new(value, null);

    public static DeviceAccessResult<T> Fail(string code, string message) =>
        new(default, new DeviceAccessFailure(code, message));
}
