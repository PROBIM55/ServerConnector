using System.Net;
using System.Security.Cryptography;
using System.Text;
using Connector.Access.Client;
using Connector.Access.Contracts;

namespace Connector.Network;

public enum CommonConnectorConnectionStatus
{
    Unknown,
    EnrollmentRequired,
    Enrolling,
    ConnectingOverlay,
    AwaitingVpnAuthorization,
    AccessPending,
    Ready,
    Revoked,
    Denied,
    Disconnected,
    Failed,
    Canceled,
}

public sealed record CommonConnectorConnectionSnapshot(
    CommonConnectorConnectionStatus Status,
    string? DeviceId,
    long? Revision,
    string DiagnosticCode,
    NetworkOverlaySnapshot? Overlay = null,
    DeviceAccessProfile? AccessProfile = null);

public sealed class CommonConnectorConnectionOptions
{
    public TimeSpan MaximumServerStateAge { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromMinutes(2);

    internal void Validate()
    {
        if (MaximumServerStateAge <= TimeSpan.Zero || MaximumServerStateAge > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(MaximumServerStateAge));
        if (ClockSkew < TimeSpan.Zero || ClockSkew > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(ClockSkew));
    }
}

public sealed class CommonConnectorConnectionCoordinator
{
    private readonly IConnectorEnrollmentClient _enrollment;
    private readonly IConnectorDeviceAccessClient _access;
    private readonly INetworkOverlayClient _overlay;
    private readonly IProtectedServiceNetworkGate _networkGate;
    private readonly CommonConnectorConnectionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private CommonConnectorConnectionSnapshot _current =
        new(CommonConnectorConnectionStatus.Unknown, null, null, "not_started");
    private Authorization? _authorization;
    // Process-local: the bootstrap this process already handed to 'netbird up'.
    private AppliedBootstrap? _appliedBootstrap;
    // Repeated 'up' failures with one issued setup key (hash only, never the key).
    private const int MaximumUpFailuresPerKey = 3;
    private string? _upFailureKey;
    private int _upFailures;

    public CommonConnectorConnectionCoordinator(
        IConnectorEnrollmentClient commonAccessClient,
        INetworkOverlayClient overlay,
        IProtectedServiceNetworkGate networkGate,
        CommonConnectorConnectionOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _enrollment = commonAccessClient ?? throw new ArgumentNullException(nameof(commonAccessClient));
        _access = commonAccessClient as IConnectorDeviceAccessClient
            ?? throw new ArgumentException(
                "The common access client must provide enrollment and issued device access through one credential state.",
                nameof(commonAccessClient));
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _networkGate = networkGate ?? throw new ArgumentNullException(nameof(networkGate));
        _options = options ?? new CommonConnectorConnectionOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public CommonConnectorConnectionSnapshot Current => Volatile.Read(ref _current);

    /// <summary>
    /// Read-only local readiness check before a one-time enrollment token is sent.
    /// The subsequent enrollment still performs its own fresh VPN and server checks.
    /// </summary>
    public async ValueTask EnsureOverlayAvailableAsync(Uri? expectedManagementUri,
        bool allowUnpinnedRegistered = false, CancellationToken cancellationToken = default)
    {
        if (expectedManagementUri is not null &&
            (!expectedManagementUri.IsAbsoluteUri || expectedManagementUri.Scheme != Uri.UriSchemeHttps ||
             !string.IsNullOrEmpty(expectedManagementUri.UserInfo)))
            throw new ArgumentException("Expected NetBird management URI must be HTTPS.", nameof(expectedManagementUri));
        var snapshot = await _overlay.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.Status is NetworkServiceStatus.NotInstalled or NetworkServiceStatus.Unknown or NetworkServiceStatus.Revoked)
            throw new NetworkGateException(snapshot.DiagnosticCode,
                "Требуется установка защищённого подключения администратором.");
        if (snapshot.ManagementUri is { } actualManagementUri)
        {
            if (expectedManagementUri is null && !allowUnpinnedRegistered ||
                expectedManagementUri is not null && !SameManagementUri(expectedManagementUri, actualManagementUri))
                throw new NetworkGateException("netbird_management_mismatch",
                    "NetBird is already configured for an unapproved management URL.");
        }
        else if (snapshot.Status != NetworkServiceStatus.Disconnected)
            throw new NetworkGateException("netbird_pre_enrollment_status_unsafe",
                "NetBird daemon is not a known inactive unregistered instance.");
    }

    public ValueTask<CommonConnectorConnectionSnapshot> EnrollAndConnectAsync(
        string oneTimeToken,
        string deviceDisplayName,
        CancellationToken cancellationToken = default) =>
        RunSerializedAsync(async ct =>
        {
            Set(CommonConnectorConnectionStatus.Enrolling, null, null, "enrolling");
            var receipt = await _enrollment.EnrollAsync(oneTimeToken, deviceDisplayName, ct).ConfigureAwait(false);
            return await EstablishAsync(receipt, ct).ConfigureAwait(false);
        }, cancellationToken);

    public ValueTask<CommonConnectorConnectionSnapshot> ResumeAndConnectAsync(
        CancellationToken cancellationToken = default) =>
        RunSerializedAsync(async ct =>
        {
            Set(CommonConnectorConnectionStatus.Enrolling, null, null, "resuming_enrollment");
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var receipt = await _enrollment.ResumeAsync(ct).ConfigureAwait(false);
                    return await EstablishAsync(receipt, ct).ConfigureAwait(false);
                }
                catch (ConnectorEnrollmentProtocolException exception) when (
                    attempt == 1 && IsIssuedCredentialRejected(exception) &&
                    _enrollment is IConnectorAccountSessionClient accounts)
                {
                    // A device revoked on the server (signed out elsewhere, by the
                    // administrator, or a sign-out whose confirmation was lost) can
                    // never be restored. It is set aside only after the server rejects
                    // its key-bound proof twice with agreeing clocks. A clock mismatch
                    // is reported as such; a transient failure of the re-proof (server busy,
                    // network) is reported as itself so the wait retries; any other failure
                    // keeps the credential and reports the original rejection.
                    CloseAuthorizationForNewAdmissions();
                    bool? forgotten;
                    try { forgotten = await accounts.ForgetRejectedEnrollmentAsync(ct).ConfigureAwait(false); }
                    catch (Exception other) when (other is not ConnectorClockSkewException && !ct.IsCancellationRequested)
                    {
                        if (CommonConnectorProvisioningWaiter.IsTransient(other, ct)) throw;
                        forgotten = null;
                    }
                    if (forgotten is null) throw;
                    if (forgotten.Value)
                        return Set(CommonConnectorConnectionStatus.EnrollmentRequired, null, null, "device_revoked");
                    // The server accepted the key and refreshed the receipt: retry once.
                }
            }
        }, cancellationToken);

    public ValueTask<CommonConnectorConnectionSnapshot> ReconnectAsync(
        CancellationToken cancellationToken = default) =>
        RunSerializedAsync(async ct =>
        {
            var receipt = await _enrollment.GetReceiptAsync(ct).ConfigureAwait(false);
            if (receipt is null)
                return Set(CommonConnectorConnectionStatus.EnrollmentRequired, null, null, "enrollment_required");
            return await EstablishAsync(receipt, ct).ConfigureAwait(false);
        }, cancellationToken);

    public ValueTask<CommonConnectorConnectionSnapshot> DisconnectAsync(
        CancellationToken cancellationToken = default) =>
        RunSerializedAsync(ct =>
        {
            ct.ThrowIfCancellationRequested();
            var previous = Current;
            RevokeAuthorization();
            return ValueTask.FromResult(Set(
                CommonConnectorConnectionStatus.Disconnected,
                previous.DeviceId,
                previous.Revision,
                "disconnected_by_user"));
        }, cancellationToken);

    public ValueTask<CommonConnectorConnectionSnapshot> SignOutAsync(CancellationToken cancellationToken = default) =>
        RunSerializedAsync(async ct =>
        {
            RevokeAuthorization();
            _appliedBootstrap = null;
            if (_enrollment is not IConnectorAccountSessionClient accounts ||
                _enrollment is not IConnectorExactDeviceRevocationClient revocation)
                throw new InvalidOperationException("Выход из учётной записи недоступен в этой версии.");
            var identity = await accounts.GetSignOutIdentityAsync(ct).ConfigureAwait(false);
            if (identity is not null)
            {
                if (identity.DeviceId is not null)
                {
                    await revocation.RevokeAndConfirmExactAsync(identity.DeviceId, ct).ConfigureAwait(false);
                }
                await accounts.ClearRevokedSessionAsync(identity, ct).ConfigureAwait(false);
            }
            return Set(CommonConnectorConnectionStatus.EnrollmentRequired, null, null, "signed_out");
        }, cancellationToken);

    public ValueTask<ProtectedServiceRoute> OpenProtectedServiceAsync(
        string serviceId,
        string relativeUri,
        CancellationToken cancellationToken = default) =>
        RunSerializedAsync(async ct =>
        {
            var authorization = _authorization;
            if (authorization is null)
                throw new NetworkGateException("common_connection_not_ready", "The common connector connection is not authorized.");

            var state = await _access.GetVpnStateAsync(ct).ConfigureAwait(false);
            var overlay = await _overlay.GetStatusAsync(ct).ConfigureAwait(false);
            if (!TryValidateReadyState(state, authorization.DeviceId, authorization.Revision, overlay, out var stateCode))
            {
                var status = StatusFor(state.Status);
                if (status is CommonConnectorConnectionStatus.Revoked or CommonConnectorConnectionStatus.Denied)
                    CloseAuthorizationForNewAdmissions();
                else
                    RevokeAuthorization();
                var diagnosticCode = status switch
                {
                    CommonConnectorConnectionStatus.Revoked => "vpn_revoked",
                    CommonConnectorConnectionStatus.Denied => "vpn_denied",
                    _ => stateCode,
                };
                Set(status, state.DeviceId, state.Revision, diagnosticCode, overlay);
                throw new NetworkGateException("common_connection_not_ready", "The server-issued VPN identity is no longer current.");
            }

            var profile = await _access.GetAccessProfileAsync(ct).ConfigureAwait(false);
            ValidateProfile(profile, state);
            var identity = authorization.CreateTransportIdentity(state);
            var route = _networkGate is ProtectedServiceNetworkGate nativeGate
                ? await nativeGate.ResolveAndProbeFromSnapshotAsync(identity, serviceId, relativeUri, overlay, ct).ConfigureAwait(false)
                : await _networkGate.ResolveAndProbeAsync(identity, serviceId, relativeUri, ct).ConfigureAwait(false);
            Set(CommonConnectorConnectionStatus.Ready, state.DeviceId, state.Revision, "ready", route.Overlay, profile);
            return route;
        }, cancellationToken);

    private async ValueTask<CommonConnectorConnectionSnapshot> EstablishAsync(
        DeviceEnrollmentResponse receipt,
        CancellationToken cancellationToken)
    {
        RevokeAuthorization();
        ConnectorVpnTransportState state;
        try
        {
            state = await _access.GetVpnStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ConnectorEnrollmentProtocolException exception) when (IsProvisioningFailure(exception, "vpn_state_unavailable"))
        {
            // A fresh device has no provider state until its authenticated profile
            // request durably schedules reconciliation. This request never admits
            // work: Ready still requires the exact VPN peer and applied profile.
            var ambiguous = false;
            try { await _access.GetAccessProfileAsync(cancellationToken).ConfigureAwait(false); }
            catch (ConnectorEnrollmentProtocolException pending) when (IsAccessPending(pending, out ambiguous)) { }
            return Set(CommonConnectorConnectionStatus.AccessPending, receipt.DeviceId,
                receipt.AccessRevision, ambiguous ? "access_providers_pending_or_denied" : "access_providers_initializing");
        }
        ValidateStateEnvelope(state, receipt);

        if (IsStatus(state, "revoked"))
            return Set(CommonConnectorConnectionStatus.Revoked, state.DeviceId, state.Revision, "vpn_revoked");
        if (IsStatus(state, "denied"))
            return Set(CommonConnectorConnectionStatus.Denied, state.DeviceId, state.Revision, "vpn_denied");
        // The provider reports «unknown»/«degraded» while it reconciles the peer. Nothing is
        // admitted; the same device is polled again.
        if (IsStatus(state, "unknown") || IsStatus(state, "error") || IsStatus(state, "degraded"))
            return Set(CommonConnectorConnectionStatus.AwaitingVpnAuthorization, state.DeviceId, state.Revision,
                IsStatus(state, "degraded") ? "vpn_state_degraded" : "vpn_state_unknown");

        DeviceAccessProfile? pendingProfile = null;
        if (IsStatus(state, "ready"))
        {
            // Polling an unapplied profile does not require launching the CLI again.
            // No work is admitted until the subsequent fresh overlay proof succeeds.
            try { pendingProfile = await _access.GetAccessProfileAsync(cancellationToken).ConfigureAwait(false); }
            catch (ConnectorEnrollmentProtocolException exception) when (IsAccessPending(exception, out var ambiguous))
            {
                return Set(CommonConnectorConnectionStatus.AccessPending, state.DeviceId, state.Revision,
                    ambiguous ? "access_profile_pending_or_denied" : "access_profile_pending");
            }
            if (!IsProfileForRevision(pendingProfile, state))
                return Set(CommonConnectorConnectionStatus.AccessPending, state.DeviceId, state.Revision,
                    "access_profile_revision_pending");
        }

        NetworkOverlaySnapshot overlay;
        if (IsStatus(state, "ready"))
        {
            overlay = await _overlay.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!TryValidateReadyState(state, receipt.DeviceId, state.Revision, overlay, out var mismatch))
                return Set(StatusForMismatch(mismatch), state.DeviceId, state.Revision, mismatch, overlay);
        }
        else if (IsStatus(state, "connecting") || IsStatus(state, "pending"))
        {
            Set(CommonConnectorConnectionStatus.ConnectingOverlay, state.DeviceId, state.Revision, "vpn_bootstrap_required");
            if (_appliedBootstrap is { } applied && applied.Matches(state))
            {
                // This process already ran 'up' with the setup key issued for this exact
                // device revision. While the daemon stays registered with that management
                // plane, the server only has to see the peer: never re-run the CLI per poll.
                overlay = await _overlay.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                if (overlay.Status is NetworkServiceStatus.Ready or NetworkServiceStatus.Connecting or NetworkServiceStatus.Degraded &&
                    overlay.ManagementUri is { } registered && SameManagementUri(applied.ManagementUri, registered))
                    return Set(CommonConnectorConnectionStatus.AwaitingVpnAuthorization, state.DeviceId, state.Revision,
                        "vpn_authorization_pending", overlay);
                _appliedBootstrap = null; // The daemon lost the registration: bootstrap again.
            }
            ConnectorVpnBootstrap bootstrap;
            try { bootstrap = await _access.GetVpnBootstrapAsync(cancellationToken).ConfigureAwait(false); }
            catch (ConnectorEnrollmentProtocolException exception) when (IsProvisioningFailure(exception, "vpn_bootstrap_unavailable"))
            {
                return Set(CommonConnectorConnectionStatus.AwaitingVpnAuthorization, state.DeviceId,
                    state.Revision, "vpn_bootstrap_pending");
            }
            // The server advanced the revision between the two reads: re-read both next poll.
            if (bootstrap.Revision != state.Revision)
                return Set(CommonConnectorConnectionStatus.AwaitingVpnAuthorization, state.DeviceId, state.Revision,
                    bootstrap.Revision > state.Revision ? "vpn_revision_advanced" : "vpn_revision_regressed");
            var managementUri = new Uri(bootstrap.ManagementUri, UriKind.Absolute);
            var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bootstrap.SetupKey)));
            try
            {
                overlay = await _overlay.ConnectAsync(
                    new NetworkOverlayBootstrap(managementUri, bootstrap.SetupKey),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (NetworkGateException exception) when (exception.Code == "netbird_not_ready")
            {
                // An accepted 'up' can finish before the OS/daemon reports Ready.
                // Re-read and classify; do not consume another enrollment token.
                overlay = await _overlay.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is NetworkGateException { Code: "netbird_up_failed" } or TimeoutException)
            {
                // A failed or hung 'up' is retried with backoff, but not forever with one key.
                var failures = _upFailureKey == keyHash ? _upFailures + 1 : 1;
                (_upFailureKey, _upFailures) = failures >= MaximumUpFailuresPerKey ? (null, 0) : (keyHash, failures);
                if (failures >= MaximumUpFailuresPerKey)
                    throw new NetworkGateException("netbird_up_failed_repeatedly",
                        "NetBird did not start with the issued setup key after repeated attempts.", exception);
                throw;
            }
            (_upFailureKey, _upFailures) = (null, 0);
            _appliedBootstrap = new AppliedBootstrap(state.DeviceId, state.Revision, managementUri);

            state = await _access.GetVpnStateAsync(cancellationToken).ConfigureAwait(false);
            ValidateStateEnvelope(state, receipt);
            if (IsStatus(state, "revoked"))
                return Set(CommonConnectorConnectionStatus.Revoked, state.DeviceId, state.Revision, "vpn_revoked", overlay);
            if (IsStatus(state, "denied"))
                return Set(CommonConnectorConnectionStatus.Denied, state.DeviceId, state.Revision, "vpn_denied", overlay);
            if (!IsStatus(state, "ready"))
                return Set(CommonConnectorConnectionStatus.AwaitingVpnAuthorization, state.DeviceId, state.Revision,
                    "vpn_authorization_pending", overlay);
            if (!TryValidateReadyState(state, receipt.DeviceId, state.Revision, overlay, out var mismatch))
                return Set(StatusForMismatch(mismatch), state.DeviceId, state.Revision, mismatch, overlay);
        }
        else
        {
            return Set(CommonConnectorConnectionStatus.Unknown, state.DeviceId, state.Revision, "vpn_state_status_unknown");
        }

        Set(CommonConnectorConnectionStatus.AccessPending, state.DeviceId, state.Revision, "access_profile_pending", overlay);
        DeviceAccessProfile profile;
        try { profile = pendingProfile ?? await _access.GetAccessProfileAsync(cancellationToken).ConfigureAwait(false); }
        catch (ConnectorEnrollmentProtocolException exception) when (IsAccessPending(exception, out var ambiguous))
        {
            return Set(CommonConnectorConnectionStatus.AccessPending, state.DeviceId, state.Revision,
                ambiguous ? "access_profile_pending_or_denied" : "access_profile_pending", overlay);
        }
        if (!IsProfileForRevision(profile, state))
            return Set(CommonConnectorConnectionStatus.AccessPending, state.DeviceId, state.Revision,
                "access_profile_revision_pending", overlay);
        _authorization = new Authorization(state.DeviceId, state.Revision);
        return Set(CommonConnectorConnectionStatus.Ready, state.DeviceId, state.Revision, "ready", overlay, profile);
    }

    /// <summary>
    /// Mismatches that settle while the server and the local daemon converge keep the wait
    /// pending; a foreign management plane, another device or an untrusted daemon do not.
    /// </summary>
    private static CommonConnectorConnectionStatus StatusForMismatch(string mismatch) =>
        mismatch is "overlay_not_ready" or "overlay_status_unavailable" or "overlay_address_mismatch"
            ? CommonConnectorConnectionStatus.AwaitingVpnAuthorization
            : CommonConnectorConnectionStatus.Unknown;

    /// <summary>
    /// The profile must belong to this device; a revision that differs from the VPN state is
    /// the server applying a newer grant between two reads, so the caller polls again.
    /// </summary>
    private bool IsProfileForRevision(DeviceAccessProfile profile, ConnectorVpnTransportState state)
    {
        if (profile.SchemaVersion != DeviceAccessProtocol.Version ||
            !string.Equals(profile.DeviceId, state.DeviceId, StringComparison.Ordinal) ||
            profile.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            throw new ConnectorEnrollmentProtocolException("The access profile does not match the authorized VPN transport revision.");
        return profile.DesiredRevision == state.Revision && profile.AppliedRevision == state.Revision;
    }

    /// <summary>
    /// «access_pending» is the server still applying grants. The older «access_pending_or_denied»
    /// cannot tell pending from denied, so the wait caps it. «access_denied», «device_not_active» and
    /// «provider_configuration» are definitive and propagate.
    /// </summary>
    private static bool IsAccessPending(ConnectorEnrollmentProtocolException exception, out bool ambiguous)
    {
        ambiguous = IsProvisioningFailure(exception, "access_pending_or_denied");
        return ambiguous || IsProvisioningFailure(exception, "access_pending");
    }

    private static bool IsProvisioningFailure(ConnectorEnrollmentProtocolException exception, string code) =>
        exception.StatusCode == (int)HttpStatusCode.Forbidden &&
        string.Equals(exception.ErrorCode, code, StringComparison.Ordinal);

    private void ValidateStateEnvelope(ConnectorVpnTransportState state, DeviceEnrollmentResponse receipt)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!string.Equals(state.DeviceId, receipt.DeviceId, StringComparison.Ordinal) ||
            state.Revision < receipt.AccessRevision || state.Revision < 1)
            throw new ConnectorEnrollmentProtocolException("The VPN transport state does not belong to the enrolled device revision.");
        var now = _timeProvider.GetUtcNow();
        if (state.ObservedAtUtc < now.Subtract(_options.MaximumServerStateAge) ||
            state.ObservedAtUtc > now.Add(_options.ClockSkew))
            // The server's observation is outside the accepted window of the local clock.
            throw new ConnectorClockSkewException(now - state.ObservedAtUtc);
        if (!TryParseManagementUri(state.ManagementUri, out _))
            throw new ConnectorEnrollmentProtocolException("The VPN transport state management URI is invalid.");
    }

    private bool TryValidateReadyState(
        ConnectorVpnTransportState state,
        string deviceId,
        long revision,
        NetworkOverlaySnapshot overlay,
        out string diagnosticCode)
    {
        diagnosticCode = "vpn_identity_changed";
        if (!string.Equals(state.DeviceId, deviceId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(state.PeerId) && IsStatus(state, "ready")) return false;
        diagnosticCode = state.Revision > revision ? "vpn_revision_advanced" : "vpn_revision_regressed";
        if (state.Revision != revision) return false;
        diagnosticCode = "vpn_state_not_ready";
        if (!IsStatus(state, "ready")) return false;
        diagnosticCode = "vpn_observation_invalid";
        if (
            state.ObservedAtUtc < _timeProvider.GetUtcNow().Subtract(_options.MaximumServerStateAge) ||
            state.ObservedAtUtc > _timeProvider.GetUtcNow().Add(_options.ClockSkew)) return false;
        diagnosticCode = "overlay_identity_mismatch";
        if (!TryParseManagementUri(state.ManagementUri, out var expectedManagement) ||
            overlay.ManagementUri is not null && !SameManagementUri(expectedManagement, overlay.ManagementUri)) return false;
        diagnosticCode = "overlay_not_ready";
        if (overlay.Status is NetworkServiceStatus.NotInstalled or NetworkServiceStatus.Unknown or NetworkServiceStatus.Revoked)
        {
            // A trusted daemon whose status command failed (restarting after 'up') settles;
            // a missing or untrusted installation does not.
            diagnosticCode = overlay.Status == NetworkServiceStatus.Unknown &&
                             overlay.DiagnosticCode is "netbird_status_unknown" or "netbird_status_invalid"
                ? "overlay_status_unavailable"
                : "overlay_trust_unavailable";
            return false;
        }
        if (overlay.Status != NetworkServiceStatus.Ready || !overlay.StartupCheckPassed ||
            !overlay.ManagementConnected || !overlay.SignalConnected || overlay.ManagementUri is null) return false;
        // The server's expected addresses follow the peer it last observed.
        diagnosticCode = "overlay_address_mismatch";

        var expectedAddresses = state.ExpectedAssignedAddresses
            .Select(ParseAddress)
            .Where(address => address is not null)
            .Cast<IPAddress>()
            .ToHashSet();
        var observedAddresses = overlay.AssignedInternalAddresses
            .Where(address => !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any))
            .ToHashSet();
        if (expectedAddresses.Count == 0 || !expectedAddresses.SetEquals(observedAddresses))
            return false;

        diagnosticCode = "ready";
        return true;
    }

    private void ValidateProfile(DeviceAccessProfile profile, ConnectorVpnTransportState state)
    {
        if (profile.SchemaVersion != DeviceAccessProtocol.Version ||
            !string.Equals(profile.DeviceId, state.DeviceId, StringComparison.Ordinal) ||
            profile.DesiredRevision != state.Revision || profile.AppliedRevision != state.Revision ||
            profile.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            throw new ConnectorEnrollmentProtocolException("The access profile does not match the authorized VPN transport revision.");
    }

    private ValueTask<T> RunSerializedAsync<T>(Func<CancellationToken, ValueTask<T>> operation, CancellationToken cancellationToken) =>
        RunSerializedCoreAsync(operation, cancellationToken);

    private async ValueTask<T> RunSerializedCoreAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RevokeAuthorization();
            Set(CommonConnectorConnectionStatus.Canceled, Current.DeviceId, Current.Revision, "canceled");
            throw;
        }
        catch (ConnectorEnrollmentProtocolException exception) when (RequiresBrowserSignIn(exception))
        {
            CloseAuthorizationForNewAdmissions();
            Set(
                CommonConnectorConnectionStatus.EnrollmentRequired,
                Current.DeviceId,
                Current.Revision,
                exception.ErrorCode == "pending_enrollment_absent"
                    ? "pending_enrollment_absent"
                    : "issued_credential_rejected");
            throw;
        }
        catch (NetworkGateException exception) when (
            Current.Status != CommonConnectorConnectionStatus.Ready &&
            string.Equals(exception.Code, "common_connection_not_ready", StringComparison.Ordinal))
        {
            // Admission is already closed by an actionable state. Repeated callers
            // must not erase it with a generic failure.
            throw;
        }
        catch (Exception exception)
        {
            RevokeAuthorization();
            Set(CommonConnectorConnectionStatus.Failed, Current.DeviceId, Current.Revision, DiagnosticFor(exception));
            throw;
        }
        finally
        {
            _operations.Release();
        }
    }

    private static bool RequiresBrowserSignIn(ConnectorEnrollmentProtocolException exception) =>
        IsIssuedCredentialRejected(exception) ||
        (exception.StatusCode == (int)HttpStatusCode.NotFound &&
         string.Equals(exception.ErrorCode, "pending_enrollment_absent", StringComparison.Ordinal));

    private static bool IsIssuedCredentialRejected(ConnectorEnrollmentProtocolException exception) =>
        exception.StatusCode == (int)HttpStatusCode.Unauthorized &&
        string.Equals(exception.ErrorCode, "device_unauthorized", StringComparison.Ordinal);

    private CommonConnectorConnectionSnapshot Set(
        CommonConnectorConnectionStatus status,
        string? deviceId,
        long? revision,
        string diagnosticCode,
        NetworkOverlaySnapshot? overlay = null,
        DeviceAccessProfile? profile = null)
    {
        var snapshot = new CommonConnectorConnectionSnapshot(status, deviceId, revision, diagnosticCode, overlay, profile);
        Volatile.Write(ref _current, snapshot);
        return snapshot;
    }

    private static CommonConnectorConnectionStatus StatusFor(string status) =>
        status.ToLowerInvariant() switch
        {
            "revoked" => CommonConnectorConnectionStatus.Revoked,
            "denied" => CommonConnectorConnectionStatus.Denied,
            "connecting" or "pending" => CommonConnectorConnectionStatus.AwaitingVpnAuthorization,
            _ => CommonConnectorConnectionStatus.Unknown,
        };

    private static bool IsStatus(ConnectorVpnTransportState state, string value) =>
        string.Equals(state.Status, value, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseManagementUri(string value, out Uri uri)
    {
        var valid = Uri.TryCreate(value, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps &&
                    string.IsNullOrEmpty(parsed.UserInfo) && string.IsNullOrEmpty(parsed.Query) && string.IsNullOrEmpty(parsed.Fragment);
        uri = parsed!;
        return valid;
    }

    private static bool SameManagementUri(Uri expected, Uri actual) =>
        expected.Scheme == actual.Scheme &&
        string.Equals(expected.IdnHost, actual.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        expected.Port == actual.Port &&
        string.Equals(expected.AbsolutePath.TrimEnd('/'), actual.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);

    private static IPAddress? ParseAddress(string value)
    {
        var addressText = value.Split('/', 2)[0];
        return IPAddress.TryParse(addressText, out var address) && !IPAddress.IsLoopback(address) &&
               !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any)
            ? address
            : null;
    }

    private static string DiagnosticFor(Exception exception) => exception switch
    {
        ConnectorClockSkewException => "clock_skew",
        ConnectorEnrollmentProtocolException { ErrorCode: { } code } when CommonConnectorProvisioningWaiter.DefinitiveServerCodes.Contains(code) => code,
        ConnectorEnrollmentProtocolException { StatusCode: 502 or 503 or 504 } => "service_temporarily_unavailable",
        HttpRequestException { StatusCode: HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout } => "service_temporarily_unavailable",
        ConnectorEnrollmentProtocolException protocol when !string.IsNullOrWhiteSpace(protocol.ErrorCode) => protocol.ErrorCode,
        ConnectorEnrollmentProtocolException => "access_protocol_error",
        ConnectorEnrollmentStateException => "enrollment_state_error",
        NetworkGateException network => network.Code,
        TimeoutException => "timeout",
        _ => "connection_failed",
    };

    private void RevokeAuthorization()
    {
        var authorization = _authorization;
        _authorization = null;
        authorization?.Revoke();
    }

    private void CloseAuthorizationForNewAdmissions() => _authorization = null;

    private sealed record AppliedBootstrap(string DeviceId, long Revision, Uri ManagementUri)
    {
        public bool Matches(ConnectorVpnTransportState state) =>
            string.Equals(state.DeviceId, DeviceId, StringComparison.Ordinal) && state.Revision == Revision &&
            TryParseManagementUri(state.ManagementUri, out var management) && SameManagementUri(ManagementUri, management);
    }

    private sealed class Authorization
    {
        private readonly CancellationTokenSource _lifetime = new();

        public Authorization(string deviceId, long revision)
        {
            DeviceId = deviceId;
            Revision = revision;
        }

        public string DeviceId { get; }
        public long Revision { get; }

        public ProtectedOverlayTransportIdentity CreateTransportIdentity(ConnectorVpnTransportState state)
        {
            if (!string.Equals(state.DeviceId, DeviceId, StringComparison.Ordinal) || state.Revision != Revision ||
                string.IsNullOrWhiteSpace(state.PeerId) || !TryParseManagementUri(state.ManagementUri, out var managementUri))
                throw new NetworkGateException("overlay_identity_mismatch", "The server-issued transport identity changed.");
            var addresses = state.ExpectedAssignedAddresses.Select(ParseAddress).Where(value => value is not null).Cast<IPAddress>().ToArray();
            return new ProtectedOverlayTransportIdentity(DeviceId, Revision, state.PeerId, managementUri, addresses, _lifetime.Token);
        }

        public void Revoke()
        {
            if (!_lifetime.IsCancellationRequested) _lifetime.Cancel();
        }
    }
}
