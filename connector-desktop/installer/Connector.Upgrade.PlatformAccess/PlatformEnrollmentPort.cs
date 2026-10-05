using Connector.Access.Client;
using Connector.Network;
using Connector.Upgrade.Core;
using Connector.Upgrade.WindowsEnvironment;
using Connector.Upgrade.WindowsHost;

namespace Connector.Upgrade.PlatformAccess;

public sealed record ExactDeviceRevocationReadiness(bool Available, string EvidenceId);

/// <summary>
/// Must revoke exactly the supplied device and confirm the result idempotently after a crash.
/// A mere fire-and-forget revoke request does not satisfy this contract.
/// </summary>
public interface IExactDeviceRevocationPort
{
    ValueTask<ExactDeviceRevocationReadiness> InspectAsync(CancellationToken cancellationToken);

    ValueTask RevokeAndConfirmExactAsync(
        string deviceId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Current server protocol exposes administrator-cookie revocation only. This implementation keeps
/// the installer fail-closed until a device-bound, idempotently confirmable compensation route exists.
/// </summary>
public sealed class UnavailableExactDeviceRevocationPort : IExactDeviceRevocationPort
{
    public ValueTask<ExactDeviceRevocationReadiness> InspectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ExactDeviceRevocationReadiness(
            false,
            "device-self-revoke-not-deployed"));
    }

    public ValueTask RevokeAndConfirmExactAsync(string deviceId, CancellationToken cancellationToken) =>
        ValueTask.FromException(new WindowsHostManualRecoveryRequiredException(
            "Exact device self-revocation is not deployed; enrollment compensation cannot be proven."));
}

/// <summary>
/// Consumes a one-use token only after independent schema, local-overlay and exact-compensation
/// preflight. Any post-enrollment failure is compensated before it can be reported as failed.
/// </summary>
public sealed class CompensatablePlatformEnrollmentPort : IPlatformEnrollmentPort
{
    private readonly IPlatformAccessSchemaProbe _schema;
    private readonly IExactDeviceRevocationPort _revocation;
    private readonly IConnectorEnrollmentClient _enrollment;
    private readonly IPlatformAccessSession _session;
    private readonly Uri _expectedManagementUri;
    private readonly string _deviceDisplayName;
    private readonly SemaphoreSlim _operations = new(1, 1);

    public CompensatablePlatformEnrollmentPort(
        IPlatformAccessSchemaProbe schema,
        IExactDeviceRevocationPort revocation,
        IConnectorEnrollmentClient enrollment,
        IPlatformAccessSession session,
        Uri expectedManagementUri,
        string deviceDisplayName)
    {
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _revocation = revocation ?? throw new ArgumentNullException(nameof(revocation));
        _enrollment = enrollment ?? throw new ArgumentNullException(nameof(enrollment));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _expectedManagementUri = ValidateManagementUri(expectedManagementUri);
        _deviceDisplayName = string.IsNullOrWhiteSpace(deviceDisplayName) || deviceDisplayName.Length > 128
            ? throw new ArgumentException("A bounded device display name is required.", nameof(deviceDisplayName))
            : deviceDisplayName.Trim();
    }

    public async ValueTask<PlatformEnrollmentReceipt> EnrollAsync(
        OneTimePlatformToken token,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Value.Length > 4096)
            throw new ArgumentException("The one-time Platform token exceeds the protocol limit.", nameof(token));

        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Every operation before this point is read-only. The token is not disclosed unless the
            // exact server schema, safe local daemon state and rollback route are independently ready.
            if (!await _schema.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
                throw new UpgradeInvariantException("The exact Platform access schema is unavailable.");

            var revocation = await _revocation.InspectAsync(cancellationToken).ConfigureAwait(false);
            if (!revocation.Available || string.IsNullOrWhiteSpace(revocation.EvidenceId))
                throw new UpgradeInvariantException(
                    "Enrollment is blocked because exact device compensation is unavailable.");

            await _session.EnsurePreTokenReadinessAsync(
                _expectedManagementUri,
                cancellationToken).ConfigureAwait(false);

            var existing = await _enrollment.GetReceiptAsync(cancellationToken).ConfigureAwait(false);
            if (existing is not null)
                throw new UpgradeInvariantException(
                    "A Platform device enrollment already exists; the one-use token was not consumed.");

            string? enrolledDeviceId = null;
            try
            {
                var connected = await _session.EnrollAndConnectAsync(
                    token.Value,
                    _deviceDisplayName,
                    cancellationToken).ConfigureAwait(false);
                var issued = await _enrollment.GetReceiptAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new UpgradeInvariantException("Enrollment produced no durable issued credential.");
                enrolledDeviceId = issued.DeviceId;
                RequireExactConnectedIdentity(connected, issued.DeviceId, issued.AccessRevision);
                return new PlatformEnrollmentReceipt(issued.DeviceId);
            }
            catch (Exception enrollmentFailure)
            {
                enrolledDeviceId ??= await TryReadEnrolledDeviceIdAsync().ConfigureAwait(false);
                if (enrolledDeviceId is null)
                    throw new WindowsHostManualRecoveryRequiredException(
                        "Enrollment was attempted, but its exact server identity could not be recovered for compensation.",
                        enrollmentFailure);

                try
                {
                    await _revocation.RevokeAndConfirmExactAsync(
                        enrolledDeviceId,
                        CancellationToken.None).ConfigureAwait(false);
                    await _session.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception compensationFailure)
                {
                    throw new WindowsHostManualRecoveryRequiredException(
                        $"Enrollment for device '{enrolledDeviceId}' failed and exact compensation could not be confirmed.",
                        new AggregateException(enrollmentFailure, compensationFailure));
                }

                throw;
            }
        }
        finally
        {
            _operations.Release();
        }
    }

    public async ValueTask RemoveAsync(
        PlatformEnrollmentReceipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateDeviceId(receipt.EnrollmentId);
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var readiness = await _revocation.InspectAsync(cancellationToken).ConfigureAwait(false);
            if (!readiness.Available || string.IsNullOrWhiteSpace(readiness.EvidenceId))
                throw new WindowsHostManualRecoveryRequiredException(
                    "Exact device compensation became unavailable before rollback.");
            await _revocation.RevokeAndConfirmExactAsync(
                receipt.EnrollmentId,
                cancellationToken).ConfigureAwait(false);
            await _session.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
        }
    }

    private async ValueTask<string?> TryReadEnrolledDeviceIdAsync()
    {
        try
        {
            var receipt = await _enrollment.GetReceiptAsync(CancellationToken.None).ConfigureAwait(false);
            receipt ??= await _enrollment.ResumeAsync(CancellationToken.None).ConfigureAwait(false);
            ValidateDeviceId(receipt.DeviceId);
            return receipt.DeviceId;
        }
        catch
        {
            return null;
        }
    }

    private static void RequireExactConnectedIdentity(
        CommonConnectorConnectionSnapshot connected,
        string deviceId,
        long enrollmentRevision)
    {
        ValidateDeviceId(deviceId);
        if (connected.Status != CommonConnectorConnectionStatus.Ready ||
            !string.Equals(connected.DeviceId, deviceId, StringComparison.Ordinal) ||
            connected.Revision is null || connected.Revision < enrollmentRevision ||
            connected.AccessProfile is null ||
            !string.Equals(connected.AccessProfile.DeviceId, deviceId, StringComparison.Ordinal) ||
            connected.AccessProfile.AppliedRevision != connected.Revision ||
            connected.AccessProfile.DesiredRevision != connected.Revision)
            throw new UpgradeInvariantException("Enrollment did not establish the exact issued device identity.");
    }

    internal static void ValidateDeviceId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length != 36 ||
            !deviceId.StartsWith("dev_", StringComparison.Ordinal) ||
            deviceId.AsSpan(4).IndexOfAnyExcept("0123456789abcdefABCDEF") >= 0)
            throw new UpgradeInvariantException("The Platform enrollment identity is invalid.");
    }

    private static Uri ValidateManagementUri(Uri value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.IsAbsoluteUri || value.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(value.UserInfo) || !string.IsNullOrEmpty(value.Query) ||
            !string.IsNullOrEmpty(value.Fragment))
            throw new ArgumentException("An absolute credential-free HTTPS NetBird management URI is required.", nameof(value));
        return value;
    }
}
