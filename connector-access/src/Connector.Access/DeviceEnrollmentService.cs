using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Connector.Access.Contracts;
using Microsoft.Extensions.Options;

namespace Connector.Access;

public sealed class DeviceEnrollmentService
{
    private readonly DbDeviceAccessRepository _repository;
    private readonly IPlatformAccessDirectory _directory;
    private readonly IX509DeviceCertificateIssuer _issuer;
    private readonly DeviceAccessOptions _options;
    private readonly TimeProvider _timeProvider;

    internal DeviceEnrollmentService(
        DbDeviceAccessRepository repository,
        IPlatformAccessDirectory directory,
        IX509DeviceCertificateIssuer issuer,
        IOptions<DeviceAccessOptions> options,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _directory = directory;
        _issuer = issuer;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public async ValueTask<DeviceAccessResult<EnrollmentTokenIssue>> IssueTokenAsync(
        IssueEnrollmentTokenCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!ValidIdentifier(command.ActorUserId) || !ValidIdentifier(command.UserId) ||
            !ValidIdentifier(command.CompanyId))
        {
            return DeviceAccessResult<EnrollmentTokenIssue>.Fail("invalid_request", "Invalid platform identity.");
        }

        var access = await _directory.AuthorizeAsync(
            command.ActorUserId,
            command.UserId,
            command.CompanyId,
            PlatformAccessOperation.IssueEnrollmentToken,
            cancellationToken);
        if (!DirectoryAllows(access, command.UserId, command.CompanyId))
        {
            return DeviceAccessResult<EnrollmentTokenIssue>.Fail("access_denied", "Enrollment token issuance is not allowed.");
        }

        var lifetime = command.Lifetime ?? _options.EnrollmentTokenLifetime;
        if (lifetime <= TimeSpan.Zero || lifetime > _options.MaximumEnrollmentTokenLifetime)
        {
            return DeviceAccessResult<EnrollmentTokenIssue>.Fail("invalid_lifetime", "Enrollment token lifetime is outside policy.");
        }

        var now = _timeProvider.GetUtcNow();
        var generated = EnrollmentTokenCodec.Create();
        await _repository.InsertTokenAsync(
            generated.TokenId,
            generated.Hash,
            command.UserId,
            command.CompanyId,
            command.ActorUserId,
            now,
            now.Add(lifetime),
            cancellationToken);

        return DeviceAccessResult<EnrollmentTokenIssue>.Success(
            new EnrollmentTokenIssue(generated.TokenId, generated.Token, now.Add(lifetime)));
    }

    public async ValueTask<DeviceAccessResult<DeviceEnrollmentResponse>> EnrollAsync(
        DeviceEnrollmentRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.SchemaVersion != DeviceAccessProtocol.Version || request.RequestId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.DeviceDisplayName) || request.DeviceDisplayName.Length > 128 ||
            !EnrollmentTokenCodec.TryParse(request.EnrollmentToken, out var parsedToken))
        {
            return EnrollmentFailure("invalid_request", "The enrollment request is invalid.");
        }

        var now = _timeProvider.GetUtcNow();
        var binding = await _repository.ResolveUsableTokenAsync(parsedToken, now, cancellationToken);
        if (binding is null)
        {
            return EnrollmentFailure("invalid_token", "The enrollment token is invalid, expired or already used.");
        }

        var access = await _directory.AuthorizeAsync(
            binding.UserId,
            binding.UserId,
            binding.CompanyId,
            PlatformAccessOperation.EnrollDevice,
            cancellationToken);
        if (!DirectoryAllows(access, binding.UserId, binding.CompanyId))
        {
            return EnrollmentFailure("access_denied", "The current platform identity is not eligible for enrollment.");
        }

        var deviceId = $"dev_{Guid.NewGuid():N}";
        ValidatedCertificateRequest certificateRequest;
        try
        {
            certificateRequest = CertificatePolicy.LoadAndRebuild(request.CertificateSigningRequestPem, deviceId);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or FormatException)
        {
            return EnrollmentFailure("invalid_csr", "The certificate request is invalid or violates key policy.");
        }

        using (certificateRequest)
        {
            EnrollmentClaim claim;
            try
            {
                claim = await _repository.ClaimEnrollmentAsync(
                    parsedToken,
                    binding,
                    request.RequestId,
                    deviceId,
                    request.DeviceDisplayName.Trim(),
                    certificateRequest.CsrSha256,
                    certificateRequest.CsrDer,
                    certificateRequest.PublicKeySha256,
                    now,
                    cancellationToken);
            }
            catch (System.Data.Common.DbException)
            {
                return EnrollmentFailure("enrollment_conflict", "Enrollment lost an atomic race; request a new token if needed.");
            }

            if (claim.Status != EnrollmentClaimStatus.Claimed)
            {
                var code = claim.Status switch
                {
                    EnrollmentClaimStatus.TokenAlreadyUsed => "token_used",
                    EnrollmentClaimStatus.DeviceAlreadyExists => "device_exists",
                    _ => "invalid_token",
                };
                return EnrollmentFailure(code, "Enrollment cannot be completed with this token and key.");
            }

            return await IssuePendingCertificateAsync(
                deviceId,
                request.RequestId,
                access!.AccessRevision,
                now,
                cancellationToken);
        }
    }

    public async ValueTask<DeviceAccessResult<EnrollmentKeyProof>> AuthenticateEnrollmentKeyAsync(
        X509Certificate2 tlsProofCertificate,
        Guid enrollmentRequestId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var notBefore = new DateTimeOffset(tlsProofCertificate.NotBefore.ToUniversalTime());
        var notAfter = new DateTimeOffset(tlsProofCertificate.NotAfter.ToUniversalTime());
        if (!tlsProofCertificate.SubjectName.RawData.AsSpan().SequenceEqual(tlsProofCertificate.IssuerName.RawData) ||
            now.Add(_options.ClockSkew) < notBefore || now.Subtract(_options.ClockSkew) >= notAfter ||
            notAfter - notBefore > _options.EnrollmentProofMaximumLifetime)
        {
            return DeviceAccessResult<EnrollmentKeyProof>.Fail(
                "invalid_enrollment_proof",
                "Enrollment recovery requires a current short-lived self-issued TLS certificate.");
        }

        var publicKeyHash = Convert.ToHexString(
            SHA256.HashData(tlsProofCertificate.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        var stored = await _repository.FindByEnrollmentRequestAsync(enrollmentRequestId, cancellationToken);
        if (stored is null || stored.RevokedAtUtc is not null ||
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(publicKeyHash),
                Convert.FromHexString(stored.PublicKeySha256)))
        {
            return DeviceAccessResult<EnrollmentKeyProof>.Fail(
                "recovery_denied",
                "The TLS proof key does not match the claimed enrollment.");
        }

        var access = await _directory.AuthorizeAsync(
            stored.UserId,
            stored.UserId,
            stored.CompanyId,
            PlatformAccessOperation.EnrollDevice,
            cancellationToken);
        if (!DirectoryAllows(access, stored.UserId, stored.CompanyId))
        {
            return DeviceAccessResult<EnrollmentKeyProof>.Fail(
                "access_denied",
                "The current platform identity is not eligible for enrollment recovery.");
        }

        return DeviceAccessResult<EnrollmentKeyProof>.Success(new EnrollmentKeyProof(
            enrollmentRequestId,
            stored.DeviceId,
            stored.UserId,
            stored.CompanyId,
            stored.PublicKeySha256,
            notAfter));
    }

    public async ValueTask<DeviceAccessResult<DeviceEnrollmentResponse>> RecoverOrResumeAsync(
        EnrollmentKeyProof proof,
        CancellationToken cancellationToken = default)
    {
        var stored = await _repository.FindByEnrollmentRequestAsync(proof.EnrollmentRequestId, cancellationToken);
        if (stored is null || stored.DeviceId != proof.DeviceId || stored.UserId != proof.UserId ||
            stored.CompanyId != proof.CompanyId || stored.PublicKeySha256 != proof.PublicKeySha256 ||
            stored.RevokedAtUtc is not null || proof.ValidUntilUtc <= _timeProvider.GetUtcNow())
        {
            return EnrollmentFailure("recovery_denied", "Enrollment proof no longer matches the claimed enrollment.");
        }

        var access = await _directory.AuthorizeAsync(
            stored.UserId,
            stored.UserId,
            stored.CompanyId,
            PlatformAccessOperation.EnrollDevice,
            cancellationToken);
        if (!DirectoryAllows(access, stored.UserId, stored.CompanyId))
        {
            return EnrollmentFailure("access_denied", "Current platform access denies enrollment recovery.");
        }

        if (stored.EnrollmentStatus == "active" && stored.CertificatePem is not null &&
            stored.IssuerCertificatePem is not null && stored.CertificateExpiresAtUtc is not null &&
            stored.CertificateExpiresAtUtc > _timeProvider.GetUtcNow())
        {
            return EnrollmentResponse(stored, access!.AccessRevision);
        }

        if (stored.EnrollmentStatus != "pending_certificate" || stored.OriginalCsrDer is null)
        {
            return EnrollmentFailure("recovery_denied", "Enrollment is not recoverable in its current state.");
        }

        return await IssuePendingCertificateAsync(
            stored.DeviceId,
            stored.EnrollmentRequestId,
            access!.AccessRevision,
            _timeProvider.GetUtcNow(),
            cancellationToken);
    }

    public async ValueTask<DeviceAccessResult<DeviceEnrollmentResponse>> RecoverResponseAsync(
        Guid enrollmentRequestId,
        AuthenticatedDevice authenticatedDevice,
        CancellationToken cancellationToken = default)
    {
        var stored = await _repository.FindByDeviceIdAsync(authenticatedDevice.DeviceId, cancellationToken);
        if (stored is null || stored.EnrollmentRequestId != enrollmentRequestId ||
            stored.UserId != authenticatedDevice.UserId || stored.CompanyId != authenticatedDevice.CompanyId ||
            stored.CertificateSha256 != authenticatedDevice.CertificateSha256 ||
            stored.RevokedAtUtc is not null || stored.EnrollmentStatus != "active" ||
            stored.CertificatePem is null || stored.IssuerCertificatePem is null ||
            stored.CertificateExpiresAtUtc is null || stored.CertificateExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            return EnrollmentFailure("recovery_denied", "Registered device identity does not match this enrollment.");
        }

        var access = await _directory.AuthorizeAsync(
            stored.UserId,
            stored.UserId,
            stored.CompanyId,
            PlatformAccessOperation.EnrollDevice,
            cancellationToken);
        if (!DirectoryAllows(access, stored.UserId, stored.CompanyId))
        {
            return EnrollmentFailure("access_denied", "Current platform access denies enrollment response recovery.");
        }

        return EnrollmentResponse(stored, access!.AccessRevision);
    }

    private async ValueTask<DeviceAccessResult<DeviceEnrollmentResponse>> IssuePendingCertificateAsync(
        string deviceId,
        Guid requestId,
        long accessRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var attemptId = Guid.NewGuid().ToString("N");
        var stored = await _repository.TryAcquireIssuanceLeaseAsync(
            deviceId,
            requestId,
            attemptId,
            now,
            now.Add(_options.EnrollmentIssuanceLease),
            cancellationToken);
        if (stored?.OriginalCsrDer is null)
        {
            return EnrollmentFailure("issuance_in_progress", "Certificate issuance is already in progress.");
        }

        try
        {
            using var certificateRequest = CertificatePolicy.LoadAndRebuild(stored.OriginalCsrDer, deviceId);
            var notBefore = now.Subtract(_options.ClockSkew);
            var notAfter = now.Add(_options.ClientCertificateLifetime);
            using var issued = await _issuer.IssueAsync(
                certificateRequest.Request,
                notBefore,
                notAfter,
                cancellationToken);
            var certificateHash = CertificatePolicy.ValidateIssuedCertificate(
                issued,
                deviceId,
                certificateRequest.PublicKeySha256,
                notBefore,
                notAfter);
            EnsureIssuedByConfiguredIssuer(issued, _issuer.IssuerCertificate, now);

            var certificatePem = issued.ExportCertificatePem();
            var issuerPem = _issuer.IssuerCertificate.ExportCertificatePem();
            var expiresAt = new DateTimeOffset(issued.NotAfter.ToUniversalTime());
            if (!await _repository.CompleteEnrollmentAsync(
                    deviceId,
                    attemptId,
                    certificateHash,
                    certificatePem,
                    issuerPem,
                    expiresAt,
                    accessRevision,
                    now,
                    cancellationToken))
            {
                return EnrollmentFailure("enrollment_conflict", "Enrollment completion was rejected.");
            }

            return DeviceAccessResult<DeviceEnrollmentResponse>.Success(new DeviceEnrollmentResponse(
                DeviceAccessProtocol.Version,
                requestId,
                deviceId,
                certificatePem,
                issuerPem,
                expiresAt,
                accessRevision));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _repository.RecordIssuanceFailureAsync(
                deviceId,
                attemptId,
                exception.GetType().Name,
                now,
                cancellationToken);
            return EnrollmentFailure("certificate_issue_failed", "Certificate issuance failed and may be resumed with enrollment key proof.");
        }
    }

    private static DeviceAccessResult<DeviceEnrollmentResponse> EnrollmentResponse(
        StoredDevice stored,
        long accessRevision) =>
        DeviceAccessResult<DeviceEnrollmentResponse>.Success(new DeviceEnrollmentResponse(
            DeviceAccessProtocol.Version,
            stored.EnrollmentRequestId,
            stored.DeviceId,
            stored.CertificatePem!,
            stored.IssuerCertificatePem!,
            stored.CertificateExpiresAtUtc!.Value,
            accessRevision));

    private static bool DirectoryAllows(PlatformAccessSnapshot? access, string userId, string companyId) =>
        access is not null && access.UserId == userId && access.CompanyId == companyId &&
        access.UserIsActive && access.MembershipIsActive && access.CompanyIsActive && access.IsAuthorized;

    private static bool ValidIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256;

    private static DeviceAccessResult<DeviceEnrollmentResponse> EnrollmentFailure(string code, string message) =>
        DeviceAccessResult<DeviceEnrollmentResponse>.Fail(code, message);

    private static void EnsureIssuedByConfiguredIssuer(
        X509Certificate2 certificate,
        X509Certificate2 issuerCertificate,
        DateTimeOffset verificationTime)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(issuerCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = verificationTime.UtcDateTime;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(DeviceAccessProtocol.ClientAuthenticationOid));
        if (!chain.Build(certificate))
        {
            throw new CryptographicException("The certificate was not signed by the configured issuer.");
        }
    }
}
