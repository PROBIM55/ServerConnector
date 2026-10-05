using Connector.Network;

namespace Connector.Upgrade.PlatformAccess;

/// <summary>
/// Narrow installer view over the existing common-connection coordinator. It keeps enrollment,
/// overlay identity and protected-service admission on the same credential/session owner.
/// </summary>
public interface IPlatformAccessSession
{
    CommonConnectorConnectionSnapshot Current { get; }

    ValueTask EnsurePreTokenReadinessAsync(
        Uri expectedManagementUri,
        CancellationToken cancellationToken);

    ValueTask<CommonConnectorConnectionSnapshot> EnrollAndConnectAsync(
        string oneTimeToken,
        string deviceDisplayName,
        CancellationToken cancellationToken);

    ValueTask<bool> VerifyProtectedServiceAsync(
        string serviceId,
        string relativeUri,
        CancellationToken cancellationToken);

    ValueTask DisconnectAsync(CancellationToken cancellationToken);
}

public sealed class CommonConnectorPlatformAccessSession : IPlatformAccessSession
{
    private readonly CommonConnectorConnectionCoordinator _coordinator;

    public CommonConnectorPlatformAccessSession(CommonConnectorConnectionCoordinator coordinator) =>
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public CommonConnectorConnectionSnapshot Current => _coordinator.Current;

    public ValueTask EnsurePreTokenReadinessAsync(
        Uri expectedManagementUri,
        CancellationToken cancellationToken) =>
        _coordinator.EnsureOverlayAvailableAsync(
            expectedManagementUri,
            allowUnpinnedRegistered: false,
            cancellationToken);

    public ValueTask<CommonConnectorConnectionSnapshot> EnrollAndConnectAsync(
        string oneTimeToken,
        string deviceDisplayName,
        CancellationToken cancellationToken) =>
        _coordinator.EnrollAndConnectAsync(oneTimeToken, deviceDisplayName, cancellationToken);

    public async ValueTask<bool> VerifyProtectedServiceAsync(
        string serviceId,
        string relativeUri,
        CancellationToken cancellationToken)
    {
        using var route = await _coordinator
            .OpenProtectedServiceAsync(serviceId, relativeUri, cancellationToken)
            .ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Head, route.Uri);
        using var response = await route.Client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        return response.StatusCode == System.Net.HttpStatusCode.NoContent;
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken) =>
        await _coordinator.DisconnectAsync(cancellationToken).ConfigureAwait(false);
}
