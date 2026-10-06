using Connector.Access.Contracts;

namespace Connector.Access.Api;

public sealed record ConnectorApiAccessPolicy(
    string DeviceId,
    string UserId,
    string CompanyId,
    long Revision,
    IReadOnlyList<ModuleGrant> Modules,
    IReadOnlyList<ResourceGrant> Resources);

public interface IConnectorApiAccessPolicyReader
{
    ValueTask<DeviceAccessResult<ConnectorApiAccessPolicy>> GetCurrentAsync(
        AuthenticatedDevice device,
        CancellationToken cancellationToken);
}
