using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace Connector.Access;

public sealed class DeviceCertificateAuthenticator
{
    private readonly DbDeviceAccessRepository _repository;
    private readonly IPlatformAccessDirectory _directory;
    private readonly DeviceAccessOptions _options;
    private readonly TimeProvider _timeProvider;

    internal DeviceCertificateAuthenticator(
        DbDeviceAccessRepository repository,
        IPlatformAccessDirectory directory,
        IOptions<DeviceAccessOptions> options,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _directory = directory;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public async ValueTask<DeviceAccessResult<AuthenticatedDevice>> AuthenticateAsync(
        X509Certificate2 presentedCertificate,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var notBefore = new DateTimeOffset(presentedCertificate.NotBefore.ToUniversalTime());
        var notAfter = new DateTimeOffset(presentedCertificate.NotAfter.ToUniversalTime());
        if (now.Add(_options.ClockSkew) < notBefore || now.Subtract(_options.ClockSkew) >= notAfter ||
            !CertificatePolicy.IsClientAuthenticationCertificate(presentedCertificate))
        {
            return Failure("invalid_certificate", "The client certificate is not currently valid for device authentication.");
        }

        var certificateHash = Convert.ToHexString(SHA256.HashData(presentedCertificate.RawData)).ToLowerInvariant();
        var stored = await _repository.FindByCertificateAsync(certificateHash, cancellationToken);
        if (stored is null || stored.CertificateSha256 is null || stored.CertificateExpiresAtUtc is null ||
            stored.RevokedAtUtc is not null || stored.EnrollmentStatus != "active" || stored.CertificateExpiresAtUtc <= now)
        {
            return Failure("certificate_not_registered", "The client certificate is not active in the device registry.");
        }

        var publicKeyHash = Convert.ToHexString(
            SHA256.HashData(presentedCertificate.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(publicKeyHash),
                Convert.FromHexString(stored.PublicKeySha256)))
        {
            return Failure("certificate_mismatch", "The registered device key does not match the certificate.");
        }

        var access = await _directory.AuthorizeAsync(
            stored.UserId,
            stored.UserId,
            stored.CompanyId,
            PlatformAccessOperation.AuthenticateDevice,
            cancellationToken);
        if (access is null || access.UserId != stored.UserId || access.CompanyId != stored.CompanyId ||
            !access.UserIsActive || !access.MembershipIsActive || !access.CompanyIsActive || !access.IsAuthorized)
        {
            return Failure("access_denied", "The current platform identity is not allowed to authenticate this device.");
        }

        return DeviceAccessResult<AuthenticatedDevice>.Success(
            new AuthenticatedDevice(
                stored.DeviceId,
                stored.UserId,
                stored.CompanyId,
                certificateHash,
                access.AccessRevision));
    }

    private static DeviceAccessResult<AuthenticatedDevice> Failure(string code, string message) =>
        DeviceAccessResult<AuthenticatedDevice>.Fail(code, message);
}
