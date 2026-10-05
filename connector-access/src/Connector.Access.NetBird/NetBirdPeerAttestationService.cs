using System.Net;
using Connector.Access.Contracts;

namespace Connector.Access.NetBird;

public interface INetBirdPeerAttestationService
{
    ValueTask<NetBirdPeerAttestationResult> AttestAsync(
        NetBirdPeerAttestationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class NetBirdPeerAttestationService : INetBirdPeerAttestationService
{
    private const int MaximumIdentityLength = 256;

    private readonly NetBirdPeerAttestationOptions _options;
    private readonly IConnectorVpnBootstrapReader _vpnStateReader;
    private readonly INetBirdDaemonPeerSnapshotReader _peerSnapshotReader;
    private readonly TimeProvider _timeProvider;

    public NetBirdPeerAttestationService(
        NetBirdPeerAttestationOptions options,
        IConnectorVpnBootstrapReader vpnStateReader,
        INetBirdDaemonPeerSnapshotReader peerSnapshotReader,
        TimeProvider timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _vpnStateReader = vpnStateReader ?? throw new ArgumentNullException(nameof(vpnStateReader));
        _peerSnapshotReader = peerSnapshotReader ?? throw new ArgumentNullException(nameof(peerSnapshotReader));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async ValueTask<NetBirdPeerAttestationResult> AttestAsync(
        NetBirdPeerAttestationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_options.TryValidate(out var options) || options is null)
            return Reject(NetBirdPeerAttestationFailureReason.InvalidConfiguration);
        if (!TryValidateRequest(request, out var localIp, out var remoteIp))
            return Reject(NetBirdPeerAttestationFailureReason.InvalidRequest);
        if (!localIp!.Equals(options.ExpectedServerOverlayListenerIp))
            return Reject(NetBirdPeerAttestationFailureReason.ListenerIpMismatch);

        DeviceAccessResult<ConnectorVpnTransportState> stateResult;
        try
        {
            stateResult = await _vpnStateReader
                .GetStateAsync(request.Device, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Reject(NetBirdPeerAttestationFailureReason.DependencyUnavailable);
        }

        if (!stateResult.IsSuccess || stateResult.Value is null)
            return Reject(NetBirdPeerAttestationFailureReason.TransportStateUnavailable);

        var state = stateResult.Value;
        if (!MatchesAuthenticatedDevice(state, request.Device))
            return Reject(NetBirdPeerAttestationFailureReason.TransportStateMismatch);

        var now = _timeProvider.GetUtcNow();
        if (!IsFresh(state.ObservedAtUtc, now, options))
            return Reject(NetBirdPeerAttestationFailureReason.TransportStateStale);
        if (!TryMatchAssignedSource(state.ExpectedAssignedAddresses, remoteIp!, out var canonicalRemoteIp))
            return Reject(NetBirdPeerAttestationFailureReason.SourceIpMismatch);

        NetBirdDaemonPeerSnapshotResult peerSnapshot;
        try
        {
            peerSnapshot = await _peerSnapshotReader
                .ReadAsync(canonicalRemoteIp!, request.ClaimedWireGuardPublicKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Reject(NetBirdPeerAttestationFailureReason.DependencyUnavailable);
        }

        if (peerSnapshot is not NetBirdDaemonPeerExact exactPeer)
            return Reject(NetBirdPeerAttestationFailureReason.PeerSnapshotNotExact);

        DeviceAccessResult<ConnectorVpnTransportState> recheckedStateResult;
        try
        {
            recheckedStateResult = await _vpnStateReader
                .GetStateAsync(request.Device, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Reject(NetBirdPeerAttestationFailureReason.DependencyUnavailable);
        }

        if (!recheckedStateResult.IsSuccess || recheckedStateResult.Value is null)
            return Reject(NetBirdPeerAttestationFailureReason.TransportStateUnavailable);

        var recheckedState = recheckedStateResult.Value;
        now = _timeProvider.GetUtcNow();
        if (!MatchesAuthenticatedDevice(recheckedState, request.Device) ||
            !SamePeerBinding(state, recheckedState))
        {
            return Reject(NetBirdPeerAttestationFailureReason.TransportStateMismatch);
        }
        if (!IsFresh(recheckedState.ObservedAtUtc, now, options))
            return Reject(NetBirdPeerAttestationFailureReason.TransportStateStale);
        if (!TryMatchAssignedSource(recheckedState.ExpectedAssignedAddresses, remoteIp!, out var recheckedRemoteIp) ||
            !string.Equals(recheckedRemoteIp, canonicalRemoteIp, StringComparison.Ordinal))
        {
            return Reject(NetBirdPeerAttestationFailureReason.SourceIpMismatch);
        }
        if (!IsExactPeerEvidence(exactPeer.Evidence, remoteIp!, request.ClaimedWireGuardPublicKey, now, options))
            return Reject(NetBirdPeerAttestationFailureReason.PeerSnapshotNotExact);

        var stateFreshUntil = recheckedState.ObservedAtUtc.Add(options.MaximumTransportStateAge);
        var peerFreshUntil = exactPeer.Evidence.LastWireguardHandshake.Add(options.MaximumPeerHandshakeAge);
        var expiresAt = now.Add(options.AttestationLifetime);
        if (expiresAt > stateFreshUntil)
            expiresAt = stateFreshUntil;
        if (expiresAt > peerFreshUntil)
            expiresAt = peerFreshUntil;
        if (expiresAt <= now)
            return Reject(NetBirdPeerAttestationFailureReason.TransportStateStale);

        return new NetBirdPeerAttestationAccepted(new NetBirdPeerAttestation(
            request.Nonce,
            request.Device.DeviceId,
            request.Device.AccessRevision,
            request.Device.CertificateSha256,
            recheckedState.PeerId!,
            recheckedRemoteIp!,
            options.ExpectedServerOverlayListenerIp.ToString(),
            request.ClaimedWireGuardPublicKey,
            recheckedState.ObservedAtUtc,
            now,
            expiresAt));
    }

    private static bool TryValidateRequest(
        NetBirdPeerAttestationRequest? request,
        out IPAddress? localIp,
        out IPAddress? remoteIp)
    {
        localIp = null;
        remoteIp = null;
        return request?.Device is not null &&
               IsBoundedIdentity(request.Device.DeviceId) &&
               request.Device.AccessRevision > 0 &&
               NetBirdPeerAttestationValidation.IsCanonicalCertificateSha256(request.Device.CertificateSha256) &&
               NetBirdPeerAttestationValidation.TryParseSocketIp(request.ObservedLocalIp, out localIp) &&
               NetBirdPeerAttestationValidation.TryParseSocketIp(request.ObservedRemoteIp, out remoteIp) &&
               NetBirdWireGuardKey.TryValidate(request.ClaimedWireGuardPublicKey) &&
               NetBirdPeerAttestationValidation.IsCanonicalNonce(request.Nonce);
    }

    private static bool MatchesAuthenticatedDevice(
        ConnectorVpnTransportState state,
        AuthenticatedDevice device) =>
        string.Equals(state.DeviceId, device.DeviceId, StringComparison.Ordinal) &&
        state.Revision == device.AccessRevision &&
        string.Equals(state.Status, "ready", StringComparison.Ordinal) &&
        IsBoundedIdentity(state.PeerId);

    private static bool IsFresh(
        DateTimeOffset observedAtUtc,
        DateTimeOffset now,
        ValidatedNetBirdPeerAttestationOptions options) =>
        observedAtUtc > now.Subtract(options.MaximumTransportStateAge) &&
        observedAtUtc <= now.Add(options.MaximumFutureClockSkew);

    private static bool IsExactPeerEvidence(
        NetBirdDaemonPeerEvidence? evidence,
        IPAddress remoteIp,
        string claimedPublicKey,
        DateTimeOffset now,
        ValidatedNetBirdPeerAttestationOptions options)
    {
        if (evidence is null ||
            !string.Equals(evidence.PublicKey, claimedPublicKey, StringComparison.Ordinal) ||
            !string.Equals(evidence.Status, "Connected", StringComparison.Ordinal) ||
            evidence.LastWireguardHandshake > now ||
            evidence.LastWireguardHandshake <= now.Subtract(options.MaximumPeerHandshakeAge) ||
            !NetBirdPeerAttestationValidation.TryParseSocketIp(evidence.NetBirdIp, out var ipv4))
        {
            return false;
        }

        IPAddress? ipv6 = null;
        if (evidence.NetBirdIpv6 is not null &&
            !NetBirdPeerAttestationValidation.TryParseSocketIp(evidence.NetBirdIpv6, out ipv6))
        {
            return false;
        }

        return ipv4!.Equals(remoteIp) || ipv6?.Equals(remoteIp) == true;
    }

    private static bool SamePeerBinding(
        ConnectorVpnTransportState before,
        ConnectorVpnTransportState after) =>
        string.Equals(before.DeviceId, after.DeviceId, StringComparison.Ordinal) &&
        before.Revision == after.Revision &&
        string.Equals(before.Status, after.Status, StringComparison.Ordinal) &&
        string.Equals(before.PeerId, after.PeerId, StringComparison.Ordinal) &&
        TryGetAssignedAddresses(before.ExpectedAssignedAddresses, out var beforeAddresses) &&
        TryGetAssignedAddresses(after.ExpectedAssignedAddresses, out var afterAddresses) &&
        beforeAddresses.SetEquals(afterAddresses);

    private static bool TryMatchAssignedSource(
        IReadOnlyList<string>? assignedAddresses,
        IPAddress remoteIp,
        out string? canonicalRemoteIp)
    {
        canonicalRemoteIp = null;
        if (!TryGetAssignedAddresses(assignedAddresses, out var addresses) || !addresses.Contains(remoteIp))
            return false;

        canonicalRemoteIp = remoteIp.ToString();
        return true;
    }

    private static bool TryGetAssignedAddresses(
        IReadOnlyList<string>? assignedAddresses,
        out HashSet<IPAddress> addresses)
    {
        addresses = [];
        if (assignedAddresses is null || assignedAddresses.Count is < 1 or > 2)
            return false;

        foreach (var value in assignedAddresses)
        {
            if (!NetBirdPeerAttestationValidation.TryParseSocketIp(value, out var assigned) ||
                !addresses.Add(assigned!))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsBoundedIdentity(string? value) =>
        value is not null && value.Length is >= 1 and <= MaximumIdentityLength &&
        !string.IsNullOrWhiteSpace(value) && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static NetBirdPeerAttestationRejected Reject(NetBirdPeerAttestationFailureReason reason) => new(reason);
}
