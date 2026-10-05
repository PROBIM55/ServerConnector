namespace Connector.Access.Contracts;

// This contract shares Platform user/company identifiers. It does not own
// accounts, passwords, companies or a second user-authentication database.
public static class DeviceAccessProtocol
{
    public const int Version = 1;
    public const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";
}

public sealed record DeviceEnrollmentRequest(
    int SchemaVersion, Guid RequestId, string EnrollmentToken,
    string CertificateSigningRequestPem, string DeviceDisplayName);

public sealed record DeviceEnrollmentResponse(
    int SchemaVersion, Guid RequestId, string DeviceId,
    string ClientCertificatePem, string IssuerCertificatePem,
    DateTimeOffset CertificateExpiresAtUtc, long AccessRevision);

public sealed record DeviceIdentity(
    string DeviceId, string UserId, string CompanyId,
    string PublicKeySha256, string CertificateSha256,
    DateTimeOffset CertificateExpiresAtUtc, DateTimeOffset? RevokedAtUtc);

public enum ConnectorProduct { Structura, Platform }
public enum ConnectorPermission { Read, Execute, Publish, Manage }

public sealed record ModuleGrant(
    ConnectorProduct Product, string ModuleId, IReadOnlyList<ConnectorPermission> Permissions);

public sealed record ResourceGrant(
    string ResourceId, string ResourceKind, string? ProjectId,
    IReadOnlyList<ConnectorPermission> Permissions);

// A profile grants permissions only after its server-side owner has confirmed
// both identity and transport. DesiredRevision alone is never applied access.
public sealed record DeviceAccessProfile(
    int SchemaVersion, string DeviceId, string UserId, string CompanyId,
    long DesiredRevision, long AppliedRevision, DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<ModuleGrant> Modules, IReadOnlyList<ResourceGrant> Resources);

public sealed record DeviceAccessProviderStatus(
    string ProviderName, long DesiredRevision, long AppliedRevision,
    string DesiredAction, string Status);

public sealed record DeviceAccessAdminDevice(
    string DeviceId, string UserId, string CompanyId, string DisplayName,
    string Status, long DesiredRevision, long AppliedRevision,
    IReadOnlyList<DeviceAccessProviderStatus> Providers,
    DateTimeOffset? CertificateNotBeforeUtc, DateTimeOffset? CertificateNotAfterUtc,
    DateTimeOffset? LastSeenAtUtc, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record ConnectorAccessAdminProfile(
    string UserId, string CompanyId, long Revision,
    IReadOnlyList<ModuleGrant> Modules, IReadOnlyList<ResourceGrant> Resources);

public sealed record ConnectorAccessCatalogModule(
    ConnectorProduct Product, string ModuleId, string Label,
    IReadOnlyList<ConnectorPermission> Permissions);

public sealed record ConnectorAccessCatalogResource(
    string ResourceId, string ResourceKind, string? ProjectId, string Label,
    IReadOnlyList<ConnectorPermission> Permissions);

public sealed record ConnectorAccessAdminCatalog(
    IReadOnlyList<ConnectorAccessCatalogModule> Modules,
    IReadOnlyList<ConnectorAccessCatalogResource> Resources);

public sealed record ConnectorVpnBootstrap(
    string SetupKey, string ManagementUri, long Revision, DateTimeOffset ExpiresAtUtc);

public sealed record ConnectorVpnTransportState(
    string DeviceId, long Revision, string Status, string? PeerId,
    string ManagementUri, IReadOnlyList<string> ExpectedAssignedAddresses,
    DateTimeOffset ObservedAtUtc);

public sealed record EnrollmentTokenIssue(
    Guid TokenId, string Token, DateTimeOffset ExpiresAtUtc);

public sealed record DeviceAccessFailure(string Code, string Message);
