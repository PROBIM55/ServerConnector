using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace Connector.Access;

public sealed record DeviceSelfRevocationStatus(
    int SchemaVersion,
    string DeviceId,
    Guid EnrollmentRequestId,
    string State,
    long DesiredRevision,
    bool Completed);

public sealed class DeviceSelfRevocationService
{
    private readonly DbDeviceAccessRepository _devices;
    private readonly DbProviderOutboxRepository _outbox;
    private readonly DeviceAccessOptions _options;
    private readonly TimeProvider _timeProvider;

    internal DeviceSelfRevocationService(
        DbDeviceAccessRepository devices,
        DbProviderOutboxRepository outbox,
        IOptions<DeviceAccessOptions> options,
        TimeProvider timeProvider)
    {
        _devices = devices;
        _outbox = outbox;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public async ValueTask<DeviceAccessResult<DeviceSelfRevocationStatus>> RevokeAsync(
        X509Certificate2 presentedCertificate,
        string deviceId,
        Guid enrollmentRequestId,
        CancellationToken cancellationToken = default)
    {
        var authenticated = await AuthenticateTargetAsync(
            presentedCertificate, deviceId, enrollmentRequestId, cancellationToken);
        if (!authenticated.IsSuccess || authenticated.Value is null)
        {
            return DeviceAccessResult<DeviceSelfRevocationStatus>.Fail(
                authenticated.Failure?.Code ?? "self_revoke_unauthorized",
                authenticated.Failure?.Message ?? "The device cannot revoke this enrollment.");
        }

        var device = authenticated.Value;
        var requiredProviders = RequiredProviders();
        if (requiredProviders is null)
        {
            return ProviderConfigurationFailure();
        }

        if (device.RevokedAtUtc is null)
        {
            await _outbox.RevokeAndEnqueueAsync(
                device,
                requiredProviders,
                _timeProvider.GetUtcNow(),
                cancellationToken);

            // A concurrent idempotent request may have won the revoke fence.
            // Always derive the response from durable state after the attempt.
            device = await _devices.FindByDeviceIdAsync(device.DeviceId, cancellationToken);
            if (device is null || device.EnrollmentRequestId != enrollmentRequestId)
            {
                return Unauthorized();
            }
        }

        return await BuildStatusAsync(device, cancellationToken);
    }

    public async ValueTask<DeviceAccessResult<DeviceSelfRevocationStatus>> GetStatusAsync(
        X509Certificate2 presentedCertificate,
        string deviceId,
        Guid enrollmentRequestId,
        CancellationToken cancellationToken = default)
    {
        var authenticated = await AuthenticateTargetAsync(
            presentedCertificate, deviceId, enrollmentRequestId, cancellationToken);
        return !authenticated.IsSuccess || authenticated.Value is null
            ? Unauthorized()
            : await BuildStatusAsync(authenticated.Value, cancellationToken);
    }

    private async ValueTask<DeviceAccessResult<StoredDevice>> AuthenticateTargetAsync(
        X509Certificate2 certificate,
        string deviceId,
        Guid enrollmentRequestId,
        CancellationToken cancellationToken)
    {
        if (!IsCanonicalDeviceId(deviceId) || enrollmentRequestId == Guid.Empty)
        {
            return DeviceAccessResult<StoredDevice>.Fail(
                "self_revoke_unauthorized", "The device cannot revoke this enrollment.");
        }

        var now = _timeProvider.GetUtcNow();
        var notBefore = new DateTimeOffset(certificate.NotBefore.ToUniversalTime());
        var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
        if (now.Add(_options.ClockSkew) < notBefore || now.Subtract(_options.ClockSkew) >= notAfter ||
            !CertificatePolicy.IsClientAuthenticationCertificate(certificate))
        {
            return DeviceAccessResult<StoredDevice>.Fail(
                "self_revoke_unauthorized", "The device cannot revoke this enrollment.");
        }

        var certificateHash = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
        var stored = await _devices.FindByCertificateAsync(certificateHash, cancellationToken);
        if (stored is null || stored.CertificateSha256 is null || stored.CertificateExpiresAtUtc is null ||
            stored.CertificateExpiresAtUtc <= now || stored.DeviceId != deviceId ||
            stored.EnrollmentRequestId != enrollmentRequestId ||
            !((stored.EnrollmentStatus == "active" && stored.RevokedAtUtc is null) ||
              (stored.EnrollmentStatus == "revoked" && stored.RevokedAtUtc is not null)))
        {
            return DeviceAccessResult<StoredDevice>.Fail(
                "self_revoke_unauthorized", "The device cannot revoke this enrollment.");
        }

        byte[] storedPublicKeyHash;
        try
        {
            storedPublicKeyHash = Convert.FromHexString(stored.PublicKeySha256);
        }
        catch (FormatException)
        {
            return DeviceAccessResult<StoredDevice>.Fail(
                "self_revoke_unauthorized", "The device cannot revoke this enrollment.");
        }

        var presentedPublicKeyHash = SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo());
        return storedPublicKeyHash.Length == presentedPublicKeyHash.Length &&
               CryptographicOperations.FixedTimeEquals(storedPublicKeyHash, presentedPublicKeyHash)
            ? DeviceAccessResult<StoredDevice>.Success(stored)
            : DeviceAccessResult<StoredDevice>.Fail(
                "self_revoke_unauthorized", "The device cannot revoke this enrollment.");
    }

    private async ValueTask<DeviceAccessResult<DeviceSelfRevocationStatus>> BuildStatusAsync(
        StoredDevice device,
        CancellationToken cancellationToken)
    {
        var requiredProviders = RequiredProviders();
        if (requiredProviders is null)
        {
            return ProviderConfigurationFailure();
        }

        if (device.RevokedAtUtc is null)
        {
            return DeviceAccessResult<DeviceSelfRevocationStatus>.Success(new(
                1, device.DeviceId, device.EnrollmentRequestId, "active", device.DesiredRevision, false));
        }

        var states = await _outbox.ReadStatesAsync(device.DeviceId, cancellationToken);
        var completed = requiredProviders.All(provider =>
        {
            var matches = states.Where(state =>
                    string.Equals(state.ProviderName, provider, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            return matches.Length == 1 &&
                   matches[0].DesiredAction == "revoke" &&
                   matches[0].Status == "revoked" &&
                   matches[0].DesiredRevision == device.DesiredRevision &&
                   matches[0].AppliedRevision >= device.DesiredRevision;
        });

        return DeviceAccessResult<DeviceSelfRevocationStatus>.Success(new(
            1,
            device.DeviceId,
            device.EnrollmentRequestId,
            completed ? "completed" : "pending",
            device.DesiredRevision,
            completed));
    }

    private IReadOnlyCollection<string>? RequiredProviders()
    {
        if (_options.RequiredAccessProviders.Count == 0 ||
            _options.RequiredAccessProviders.Any(string.IsNullOrWhiteSpace))
        {
            return null;
        }

        return _options.RequiredAccessProviders.ToArray();
    }

    private static bool IsCanonicalDeviceId(string deviceId) =>
        deviceId.Length == 36 && deviceId.StartsWith("dev_", StringComparison.Ordinal) &&
        Guid.TryParseExact(deviceId.AsSpan(4), "N", out _);

    private static DeviceAccessResult<DeviceSelfRevocationStatus> Unauthorized() =>
        DeviceAccessResult<DeviceSelfRevocationStatus>.Fail(
            "self_revoke_unauthorized", "The device cannot revoke this enrollment.");

    private static DeviceAccessResult<DeviceSelfRevocationStatus> ProviderConfigurationFailure() =>
        DeviceAccessResult<DeviceSelfRevocationStatus>.Fail(
            "provider_configuration", "Required access providers are not configured.");
}
