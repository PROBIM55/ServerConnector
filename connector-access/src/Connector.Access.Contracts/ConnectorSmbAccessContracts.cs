namespace Connector.Access.Contracts;

public sealed record ConnectorSmbAccess(
    int SchemaVersion,
    string DeviceId,
    long Revision,
    DateTimeOffset ExpiresAtUtc,
    string? UserName,
    string? Password,
    IReadOnlyList<ConnectorSmbResourceAccess> Resources);

public sealed record ConnectorSmbResourceAccess(
    string ResourceId,
    string ResourceKind,
    string? ProjectId,
    IReadOnlyList<ConnectorPermission> Permissions,
    string ShareUnc);
