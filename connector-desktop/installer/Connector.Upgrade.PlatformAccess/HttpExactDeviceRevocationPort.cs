using Connector.Access.Client;

namespace Connector.Upgrade.PlatformAccess;

/// <summary>
/// Installer adapter over the enrollment client's pinned service identity,
/// DPAPI-backed receipt and issued mTLS credential.
/// </summary>
public sealed class HttpExactDeviceRevocationPort : IExactDeviceRevocationPort
{
    private const string AvailableEvidence = "device-self-revoke-v1:https-401-device_unauthorized";
    private const string UnavailableEvidence = "device-self-revoke-unavailable";
    private readonly IConnectorExactDeviceRevocationClient _client;

    public HttpExactDeviceRevocationPort(IConnectorExactDeviceRevocationClient client) =>
        _client = client ?? throw new ArgumentNullException(nameof(client));

    public async ValueTask<ExactDeviceRevocationReadiness> InspectAsync(
        CancellationToken cancellationToken)
    {
        var available = await _client
            .InspectExactDeviceRevocationAsync(cancellationToken)
            .ConfigureAwait(false);
        return new ExactDeviceRevocationReadiness(
            available,
            available ? AvailableEvidence : UnavailableEvidence);
    }

    public async ValueTask RevokeAndConfirmExactAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        CompensatablePlatformEnrollmentPort.ValidateDeviceId(deviceId);
        await _client
            .RevokeAndConfirmExactAsync(deviceId, cancellationToken)
            .ConfigureAwait(false);
    }
}
