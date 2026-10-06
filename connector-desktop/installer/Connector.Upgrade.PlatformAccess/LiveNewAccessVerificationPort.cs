using System.Net;
using Connector.Access.Client;
using Connector.Access.Contracts;
using Connector.Network;
using Connector.Upgrade.Core;
using Connector.Upgrade.WindowsHost;

namespace Connector.Upgrade.PlatformAccess;

public sealed record ExactSmbAccessProbeRequest(
    string DeviceId,
    long Revision,
    DeviceAccessProfile Profile,
    NetworkOverlaySnapshot Overlay);

public sealed record ExactVpnPeerBindingProbeRequest(
    string DeviceId,
    long Revision,
    string PeerId,
    IReadOnlyList<IPAddress> ExpectedAssignedAddresses,
    NetworkOverlaySnapshot Overlay);

/// <summary>
/// Proves that the local NetBird public key belongs to the server-issued PeerId for this device.
/// NetBird PeerId is not derived from the key; management URI and assigned addresses alone
/// do not satisfy this contract.
/// </summary>
public interface IExactVpnPeerBindingProbe
{
    ValueTask<bool> VerifyAsync(
        ExactVpnPeerBindingProbeRequest request,
        CancellationToken cancellationToken);
}

public sealed class UnavailableExactVpnPeerBindingProbe : IExactVpnPeerBindingProbe
{
    public ValueTask<bool> VerifyAsync(
        ExactVpnPeerBindingProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }
}

/// <summary>
/// Must perform an authenticated native SMB read through the exact supplied overlay and clean up
/// only its own temporary mapping. An API receipt or open TCP/445 socket alone must return false.
/// </summary>
public interface IExactSmbAccessProbe
{
    ValueTask<bool> VerifyAuthenticatedReadAsync(
        ExactSmbAccessProbeRequest request,
        CancellationToken cancellationToken);
}

public sealed class UnavailableExactSmbAccessProbe : IExactSmbAccessProbe
{
    public ValueTask<bool> VerifyAuthenticatedReadAsync(
        ExactSmbAccessProbeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }
}

public sealed class CommonConnectorLiveAccessVerificationPort : ILiveNewAccessVerificationPort
{
    private const string ControlPlaneServiceId = "control-plane";
    private const string ControlPlaneHealthPath = "jobs/health";
    private readonly IConnectorEnrollmentClient _enrollment;
    private readonly IConnectorDeviceAccessClient _access;
    private readonly INetworkOverlayClient _overlay;
    private readonly IPlatformAccessSession _session;
    private readonly IExactVpnPeerBindingProbe _peerBinding;
    private readonly IExactSmbAccessProbe _smb;
    private readonly TimeProvider _time;
    private readonly TimeSpan _maximumObservationAge;
    private readonly TimeSpan _clockSkew;

    public CommonConnectorLiveAccessVerificationPort(
        IConnectorEnrollmentClient enrollment,
        INetworkOverlayClient overlay,
        IPlatformAccessSession session,
        IExactVpnPeerBindingProbe peerBinding,
        IExactSmbAccessProbe smb,
        TimeProvider? timeProvider = null,
        TimeSpan? maximumObservationAge = null,
        TimeSpan? clockSkew = null)
    {
        _enrollment = enrollment ?? throw new ArgumentNullException(nameof(enrollment));
        _access = enrollment as IConnectorDeviceAccessClient
            ?? throw new ArgumentException(
                "Enrollment and access verification must use the same issued credential store.",
                nameof(enrollment));
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _peerBinding = peerBinding ?? throw new ArgumentNullException(nameof(peerBinding));
        _smb = smb ?? throw new ArgumentNullException(nameof(smb));
        _time = timeProvider ?? TimeProvider.System;
        _maximumObservationAge = maximumObservationAge ?? TimeSpan.FromMinutes(2);
        _clockSkew = clockSkew ?? TimeSpan.FromMinutes(2);
        if (_maximumObservationAge <= TimeSpan.Zero || _maximumObservationAge > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(maximumObservationAge));
        if (_clockSkew < TimeSpan.Zero || _clockSkew > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(clockSkew));
    }

    public async ValueTask<NewAccessVerification> VerifyAsync(
        PlatformEnrollmentReceipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        CompensatablePlatformEnrollmentPort.ValidateDeviceId(receipt.EnrollmentId);

        var issued = await _enrollment.GetReceiptAsync(cancellationToken).ConfigureAwait(false);
        if (issued is null || !string.Equals(issued.DeviceId, receipt.EnrollmentId, StringComparison.Ordinal))
            return new NewAccessVerification(false, false, false);

        var state = await _access.GetVpnStateAsync(cancellationToken).ConfigureAwait(false);
        var overlay = await _overlay.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!TryGetExactReadyOverlay(
                state,
                overlay,
                issued.DeviceId,
                issued.AccessRevision,
                out var expectedAddresses))
            return new NewAccessVerification(false, false, false);

        var vpn = await _peerBinding.VerifyAsync(
            new ExactVpnPeerBindingProbeRequest(
                state.DeviceId,
                state.Revision,
                state.PeerId!,
                expectedAddresses,
                overlay),
            cancellationToken).ConfigureAwait(false);
        if (!vpn)
            return new NewAccessVerification(false, false, false);

        var profile = await _access.GetAccessProfileAsync(cancellationToken).ConfigureAwait(false);
        if (!ExactAppliedProfile(profile, state, out var smbRequired))
            return new NewAccessVerification(false, true, false);

        if (!ExactReadySession(_session.Current, state, profile))
            return new NewAccessVerification(false, false, false);

        var platformApi = await VerifyProtectedControlPlaneAsync(cancellationToken).ConfigureAwait(false);
        if (!platformApi)
            return new NewAccessVerification(false, true, false);

        // The protected request is owned by the same coordinator session. Re-read after the
        // await so a concurrent reconnect cannot combine receipt/profile/SMB from device A with
        // an HTTP success established by device B.
        if (!ExactReadySession(_session.Current, state, profile))
            return new NewAccessVerification(false, false, false);

        // An applied profile with no resource Read grant requires no SMB probe. Never
        // manufacture SMB access: the verification records that SMB was not required.
        if (!smbRequired)
            return new NewAccessVerification(true, true, false, SmbRequired: false);

        var smb = await _smb.VerifyAuthenticatedReadAsync(
            new ExactSmbAccessProbeRequest(state.DeviceId, state.Revision, profile, overlay),
            cancellationToken).ConfigureAwait(false);
        if (!ExactReadySession(_session.Current, state, profile))
            return new NewAccessVerification(false, false, false);
        return new NewAccessVerification(true, true, smb);
    }

    private async ValueTask<bool> VerifyProtectedControlPlaneAsync(CancellationToken cancellationToken)
    {
        return await _session.VerifyProtectedServiceAsync(
            ControlPlaneServiceId,
            ControlPlaneHealthPath,
            cancellationToken).ConfigureAwait(false);
    }

    private bool TryGetExactReadyOverlay(
        ConnectorVpnTransportState state,
        NetworkOverlaySnapshot overlay,
        string deviceId,
        long minimumRevision,
        out IReadOnlyList<IPAddress> expectedAddresses)
    {
        expectedAddresses = Array.Empty<IPAddress>();
        var now = _time.GetUtcNow();
        if (!string.Equals(state.DeviceId, deviceId, StringComparison.Ordinal) ||
            state.Revision < minimumRevision || state.Revision < 1 ||
            !string.Equals(state.Status, "ready", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(state.PeerId) ||
            state.ObservedAtUtc < now - _maximumObservationAge || state.ObservedAtUtc > now + _clockSkew ||
            overlay.ObservedAtUtc < now - _maximumObservationAge || overlay.ObservedAtUtc > now + _clockSkew ||
            overlay.Status != NetworkServiceStatus.Ready || !overlay.StartupCheckPassed ||
            !overlay.ManagementConnected || !overlay.SignalConnected || overlay.ManagementUri is null ||
            !TryManagementUri(state.ManagementUri, out var expectedManagement) ||
            !SameManagementUri(expectedManagement, overlay.ManagementUri))
            return false;

        var expected = state.ExpectedAssignedAddresses
            .Select(ParseAddress)
            .Where(value => value is not null)
            .Cast<IPAddress>()
            .ToHashSet();
        var actual = overlay.AssignedInternalAddresses
            .Where(IsUsableAddress)
            .ToHashSet();
        if (expected.Count == 0 || !expected.SetEquals(actual))
            return false;
        expectedAddresses = expected.OrderBy(value => value.ToString(), StringComparer.Ordinal).ToArray();
        return true;
    }

    private bool ExactAppliedProfile(
        DeviceAccessProfile? profile,
        ConnectorVpnTransportState state,
        out bool smbRequired)
    {
        smbRequired = false;
        if (profile is null ||
            profile.SchemaVersion != DeviceAccessProtocol.Version ||
            !string.Equals(profile.DeviceId, state.DeviceId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(profile.UserId) || string.IsNullOrWhiteSpace(profile.CompanyId) ||
            state.Revision < 1 ||
            profile.DesiredRevision != state.Revision ||
            profile.AppliedRevision != state.Revision ||
            profile.ExpiresAtUtc <= _time.GetUtcNow() ||
            profile.Modules is null || profile.Resources is null)
            return false;

        if (profile.Modules.Any(module => module is null ||
                !Enum.IsDefined(module.Product) || string.IsNullOrWhiteSpace(module.ModuleId) ||
                module.Permissions is null || module.Permissions.Any(permission => !Enum.IsDefined(permission))) ||
            profile.Resources.Any(resource => resource is null ||
                string.IsNullOrWhiteSpace(resource.ResourceId) || string.IsNullOrWhiteSpace(resource.ResourceKind) ||
                resource.Permissions is null || resource.Permissions.Any(permission => !Enum.IsDefined(permission))))
            return false;

        smbRequired = profile.Resources.Any(resource => resource.Permissions.Contains(ConnectorPermission.Read));
        return true;
    }

    private bool ExactReadySession(
        CommonConnectorConnectionSnapshot snapshot,
        ConnectorVpnTransportState state,
        DeviceAccessProfile profile) =>
        snapshot.Status == CommonConnectorConnectionStatus.Ready &&
        string.Equals(snapshot.DeviceId, state.DeviceId, StringComparison.Ordinal) &&
        snapshot.Revision == state.Revision &&
        snapshot.AccessProfile is { } currentProfile &&
        string.Equals(currentProfile.DeviceId, profile.DeviceId, StringComparison.Ordinal) &&
        currentProfile.DesiredRevision == profile.DesiredRevision &&
        currentProfile.AppliedRevision == profile.AppliedRevision &&
        currentProfile.ExpiresAtUtc > _time.GetUtcNow();

    private static IPAddress? ParseAddress(string value)
    {
        var text = value.Split('/', 2)[0];
        return IPAddress.TryParse(text, out var address) && IsUsableAddress(address) ? address : null;
    }

    private static bool IsUsableAddress(IPAddress address) =>
        !IPAddress.IsLoopback(address) &&
        !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any);

    private static bool TryManagementUri(string value, out Uri uri)
    {
        var valid = Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
            parsed.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(parsed.UserInfo) &&
            string.IsNullOrEmpty(parsed.Query) && string.IsNullOrEmpty(parsed.Fragment);
        uri = parsed!;
        return valid;
    }

    private static bool SameManagementUri(Uri expected, Uri actual) =>
        expected.Scheme == actual.Scheme &&
        string.Equals(expected.IdnHost, actual.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        expected.Port == actual.Port &&
        string.Equals(expected.AbsolutePath.TrimEnd('/'), actual.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
}
