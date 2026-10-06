using System.IO;
using System.Net.Http;
using System.Net;
using Connector.Access.Client;
using Connector.Access.Contracts;
using Connector.CommonAccess.Runtime;
using Connector.Network;
using Platform.Connector.Core;

namespace Connector.Desktop.Services;

// Deployment configuration belongs to the machine-protected admin bundle,
// never the per-user app or WebView. Enrollment credentials remain separate.
public sealed class CommonAccessRuntime : ICommonConnectorRequestTransport
{
    private readonly CommonConnectorConnectionCoordinator? _coordinator;
    private readonly IConnectorEnrollmentClient? _enrollmentClient;
    private readonly INetworkOverlayClient? _overlayClient;
    private readonly IProtectedServiceNetworkGate? _protectedNetworkGate;
    private readonly ICommonConnectorRequestTransport _requestTransport;
    private readonly string _statePath;
    private readonly Uri? _controlPlaneBaseUri;
    private readonly Uri? _netBirdManagementUri;
    private ManagedOverlayDestinationPolicy _smbDestinations = new([]);
    private int _selected;
    private int _readyObserved;
    private int _vpnSetupRequired;
    // The runtime assigns this only after it owns the connection lifecycle
    // mutex.  A request that was in flight for the preceding session must not
    // be able to invalidate the replacement session.
    private long _accessSessionGeneration;
    private long _readyObservedGeneration;

    private CommonAccessRuntime(string statePath, CommonConnectorConnectionCoordinator? coordinator,
        Uri? controlPlaneBaseUri, Uri? netBirdManagementUri,
        IConnectorEnrollmentClient? enrollmentClient = null,
        INetworkOverlayClient? overlayClient = null,
        IProtectedServiceNetworkGate? protectedNetworkGate = null)
    {
        _statePath = statePath;
        _coordinator = coordinator;
        _controlPlaneBaseUri = controlPlaneBaseUri;
        _netBirdManagementUri = netBirdManagementUri;
        _enrollmentClient = enrollmentClient;
        _overlayClient = overlayClient;
        _protectedNetworkGate = protectedNetworkGate;
        _requestTransport = new ProtectedControlPlaneRequestTransport(OpenProtectedServiceAsync);
        _selected = File.Exists(statePath) ? 1 : 0;
    }

    public bool IsSelected => Volatile.Read(ref _selected) != 0;
    public bool HasStoredCredential => File.Exists(_statePath);
    public bool IsConfigured => _coordinator is not null;
    public CommonConnectorConnectionSnapshot? Current => _coordinator?.Current;
    public bool IsReady => IsSelected && IsCurrentSnapshotReady(Current);
    public Uri ControlPlaneBaseUri => _controlPlaneBaseUri ??
        throw new InvalidOperationException("Сервис заданий не настроен администратором.");
    public bool IsSmbDestinationAllowed(IPAddress address) => IsReady && _smbDestinations.Contains(address);
    public string DisplayStatus => !IsConfigured ? "Подключение не настроено" :
        Volatile.Read(ref _vpnSetupRequired) != 0 ? "Требуется установка защищённого подключения" : Current?.Status switch
    {
        CommonConnectorConnectionStatus.Ready => "Подключено",
        CommonConnectorConnectionStatus.Enrolling or CommonConnectorConnectionStatus.ConnectingOverlay => "Подключение…",
        CommonConnectorConnectionStatus.AwaitingVpnAuthorization or CommonConnectorConnectionStatus.AccessPending => "Ожидает выдачи доступа",
        CommonConnectorConnectionStatus.Revoked => "Доступ отозван",
        CommonConnectorConnectionStatus.Denied => "Нет доступа",
        CommonConnectorConnectionStatus.Failed => "Не удалось подключиться",
        _ => "Не подключено"
    };

    /// <summary>
    /// Raised once when a previously observed Ready connection becomes invalid.
    /// Handlers must synchronously close admission; longer cleanup may continue asynchronously.
    /// </summary>
    public event Action<CommonAccessInvalidation>? ReadyInvalidated;

    public bool ShouldUse(string? token = null) => IsSelected ||
        token?.StartsWith("cea1.", StringComparison.Ordinal) == true;

    public void Select() => Volatile.Write(ref _selected, 1);

    /// <summary>
    /// Binds subsequent Ready observations to a lifecycle session owned by the
    /// desktop composition.  This is deliberately separate from request
    /// cancellation: a queued, cancelled connect never changes the session.
    /// </summary>
    public void BeginSession(long generation)
    {
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
        Volatile.Write(ref _accessSessionGeneration, generation);
    }

    public bool HasPermission(string mode, string moduleId, ConnectorPermission permission)
    {
        var profile = Current?.AccessProfile;
        var product = mode == "platform" ? ConnectorProduct.Platform : ConnectorProduct.Structura;
        return IsReady && profile is not null && profile.ExpiresAtUtc > DateTimeOffset.UtcNow &&
            profile.Modules.Any(grant => grant.Product == product && grant.ModuleId == moduleId && grant.Permissions.Contains(permission));
    }

    public string[] AvailablePages(string mode) => new[] { "services", "tekla", "attributes", "folders", "converters", "agr", "bridge", "autocad" }
        .Where(page => HasPermission(mode, page == "bridge" ? "tekla" : page, ConnectorPermission.Read)).ToArray();

    public static CommonAccessRuntime Create(string runtimeRoot) =>
        CreateFromDeployment(CommonAccessDeploymentFactory.OpenProduction(runtimeRoot));

    internal static CommonAccessRuntime CreateForFixture(string runtimeRoot, string applicationDirectory) =>
        CreateFromDeployment(CommonAccessDeploymentFactory.OpenFixture(runtimeRoot, applicationDirectory));

    private static CommonAccessRuntime CreateFromDeployment(CommonAccessDeployment deployment)
    {
        return new CommonAccessRuntime(deployment.StateFilePath, deployment.Coordinator,
            deployment.ControlPlaneBaseUri, deployment.NetBirdManagementUri,
            deployment.EnrollmentClient, deployment.OverlayClient, deployment.ProtectedNetworkGate)
            { _smbDestinations = deployment.SmbDestinations };
    }

    /// <summary>
    /// Exposes configured, already-created runtime ports. Availability here does not assert
    /// live network readiness or upgrade safety; the composition performs those checks.
    /// This does not read a token or start an elevated helper.
    /// </summary>
    internal CommonAccessUpgradePortsResult TryGetUpgradeRuntimePorts()
    {
        if (_coordinator is null || _enrollmentClient is null || _overlayClient is null || _protectedNetworkGate is null)
            return CommonAccessUpgradePortsResult.Unavailable(CommonAccessUpgradePortsBlocker.DeploymentNotConfigured);

        return CommonAccessUpgradePortsResult.Available(new CommonAccessUpgradeRuntimePorts(
            _enrollmentClient, _overlayClient, _protectedNetworkGate, this, _smbDestinations));
    }

    public async Task ConnectAsync(string? token, bool reconnect, CancellationToken cancellationToken)
    {
        Select();
        var coordinator = RequireCoordinator();
        try
        {
            try
            {
                // Older machine deployments have no URI pin. They can resume the
                // server-bound device identity, but a new one-time token is accepted
                // only while the local daemon is known to be unregistered.
                await coordinator.EnsureOverlayAvailableAsync(_netBirdManagementUri,
                    allowUnpinnedRegistered: reconnect, cancellationToken: cancellationToken);
                Volatile.Write(ref _vpnSetupRequired, 0);
            }
            catch (NetworkGateException)
            {
                Volatile.Write(ref _vpnSetupRequired, 1);
                throw;
            }
            if (reconnect)
                await coordinator.ResumeAndConnectAsync(cancellationToken);
            else
            {
                if (string.IsNullOrWhiteSpace(token) || token.Length > 4096)
                    throw new InvalidOperationException("Нужен токен подключения.");
                await coordinator.EnrollAndConnectAsync(token, Environment.MachineName, cancellationToken);
            }
        }
        finally { ObserveConnectionState(Volatile.Read(ref _accessSessionGeneration)); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_coordinator is not null) await _coordinator.DisconnectAsync(cancellationToken);
        }
        finally { ObserveConnectionState(Volatile.Read(ref _accessSessionGeneration)); }
    }

    public async ValueTask<ProtectedServiceRoute> OpenProtectedServiceAsync(
        string serviceId,
        string relativeUri,
        CancellationToken cancellationToken)
    {
        var requestGeneration = Volatile.Read(ref _accessSessionGeneration);
        ObserveConnectionState(requestGeneration);
        try
        {
            return await RequireCoordinator().OpenProtectedServiceAsync(serviceId, relativeUri, cancellationToken)
                .ConfigureAwait(false);
        }
        finally { ObserveConnectionState(requestGeneration); }
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativeUri, object? body,
        IReadOnlyDictionary<string, string> managedHeaders, CancellationToken cancellationToken)
        => _requestTransport.SendAsync(method, relativeUri, body, managedHeaders, cancellationToken);

    private CommonConnectorConnectionCoordinator RequireCoordinator() => _coordinator ??
        throw new InvalidOperationException("Подключение нового типа ещё не настроено администратором.");

    private void ObserveConnectionState(long requestGeneration)
    {
        var snapshot = Current;
        if (IsSelected && IsCurrentSnapshotReady(snapshot))
        {
            // A completion from a previous connection cannot claim Ready for
            // the replacement session that started while it was in flight.
            if (requestGeneration == Volatile.Read(ref _accessSessionGeneration))
            {
                Volatile.Write(ref _readyObservedGeneration, requestGeneration);
                Volatile.Write(ref _readyObserved, 1);
            }
            return;
        }
        if (snapshot is null || Interlocked.Exchange(ref _readyObserved, 0) == 0) return;

        var invalidation = new CommonAccessInvalidation(
            Volatile.Read(ref _readyObservedGeneration), snapshot);
        var handlers = ReadyInvalidated;
        if (handlers is null) return;
        foreach (Action<CommonAccessInvalidation> handler in handlers.GetInvocationList())
        {
            try { handler(invalidation); }
            catch { }
        }
    }

    private static bool IsCurrentSnapshotReady(CommonConnectorConnectionSnapshot? snapshot) =>
        snapshot is { Status: CommonConnectorConnectionStatus.Ready, AccessProfile: { } profile } &&
        profile.ExpiresAtUtc > DateTimeOffset.UtcNow;

}

internal enum CommonAccessUpgradePortsBlocker
{
    DeploymentNotConfigured = 1,
}

internal sealed record CommonAccessUpgradeRuntimePorts(
    IConnectorEnrollmentClient EnrollmentClient,
    INetworkOverlayClient OverlayClient,
    IProtectedServiceNetworkGate ProtectedNetworkGate,
    ICommonConnectorRequestTransport Transport,
    ManagedOverlayDestinationPolicy SmbDestinations);

internal sealed record CommonAccessUpgradePortsResult(
    CommonAccessUpgradeRuntimePorts? Ports, CommonAccessUpgradePortsBlocker? Blocker)
{
    internal bool IsAvailable => Ports is not null && Blocker is null;
    internal static CommonAccessUpgradePortsResult Available(CommonAccessUpgradeRuntimePorts ports) => new(ports, null);
    internal static CommonAccessUpgradePortsResult Unavailable(CommonAccessUpgradePortsBlocker blocker) => new(null, blocker);
}

/// <summary>
/// A Ready-to-invalid transition is permanently associated with the session
/// that originally observed Ready, not with whichever connect happens to be
/// current when a delayed protected request completes.
/// </summary>
public sealed record CommonAccessInvalidation(
    long SessionGeneration,
    CommonConnectorConnectionSnapshot Snapshot);
