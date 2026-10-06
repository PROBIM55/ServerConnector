using Connector.Access.Contracts;

namespace Connector.Access.Client;

public enum ConnectorEnrollmentKeyAlgorithm
{
    EcdsaP256,
    Rsa2048,
}

public sealed class ConnectorAccessClientOptions
{
    public required Uri ServiceBaseUri { get; init; }
    public required string TrustedIssuerCertificateSha256 { get; init; }
    public ConnectorEnrollmentKeyAlgorithm KeyAlgorithm { get; init; } = ConnectorEnrollmentKeyAlgorithm.EcdsaP256;
    public TimeSpan BootstrapCertificateLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromMinutes(2);
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal ValidatedClientOptions Validate()
    {
        ArgumentNullException.ThrowIfNull(ServiceBaseUri);
        ArgumentNullException.ThrowIfNull(TimeProvider);
        if (!ServiceBaseUri.IsAbsoluteUri || ServiceBaseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Connector enrollment requires an absolute HTTPS service URI.", nameof(ServiceBaseUri));
        }

        if (!string.IsNullOrEmpty(ServiceBaseUri.UserInfo))
        {
            throw new ArgumentException("The service URI must not contain credentials.", nameof(ServiceBaseUri));
        }

        if (BootstrapCertificateLifetime <= TimeSpan.Zero || BootstrapCertificateLifetime > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(BootstrapCertificateLifetime));
        }

        if (ClockSkew < TimeSpan.Zero || ClockSkew > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(ClockSkew));
        }

        byte[] issuerPin;
        try
        {
            issuerPin = Convert.FromHexString(TrustedIssuerCertificateSha256);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The trusted issuer pin must be a SHA-256 hexadecimal value.", nameof(TrustedIssuerCertificateSha256), exception);
        }

        if (issuerPin.Length != 32)
        {
            throw new ArgumentException("The trusted issuer pin must contain exactly 32 bytes.", nameof(TrustedIssuerCertificateSha256));
        }

        var normalizedBaseUri = new Uri(ServiceBaseUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/", UriKind.Absolute);
        return new ValidatedClientOptions(
            normalizedBaseUri,
            Convert.ToHexString(issuerPin).ToLowerInvariant(),
            issuerPin,
            KeyAlgorithm,
            BootstrapCertificateLifetime,
            ClockSkew,
            TimeProvider);
    }
}

public interface IConnectorEnrollmentClient
{
    ValueTask<DeviceEnrollmentResponse> EnrollAsync(
        string enrollmentToken,
        string deviceDisplayName,
        CancellationToken cancellationToken = default);

    ValueTask<DeviceEnrollmentResponse> ResumeAsync(CancellationToken cancellationToken = default);

    ValueTask<DeviceEnrollmentResponse?> GetReceiptAsync(CancellationToken cancellationToken = default);
}

public interface IConnectorDeviceAccessClient
{
    ValueTask<DeviceAccessProfile> GetAccessProfileAsync(CancellationToken cancellationToken = default);
    ValueTask<ConnectorVpnBootstrap> GetVpnBootstrapAsync(CancellationToken cancellationToken = default);
    ValueTask<ConnectorVpnTransportState> GetVpnStateAsync(CancellationToken cancellationToken = default);
}

public interface IConnectorIssuedCredentialSource
{
    ValueTask<ConnectorIssuedCertificateLease> AcquireIssuedCertificateAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Probes and executes the device-bound compensation route using the durable
/// enrollment receipt and its issued certificate. Implementations must never
/// substitute an administrator credential or a different device identity.
/// </summary>
public interface IConnectorExactDeviceRevocationClient
{
    ValueTask<bool> InspectExactDeviceRevocationAsync(CancellationToken cancellationToken = default);

    ValueTask RevokeAndConfirmExactAsync(
        string deviceId,
        CancellationToken cancellationToken = default);
}

public sealed class ConnectorIssuedCertificateLease : IDisposable
{
    internal ConnectorIssuedCertificateLease(string deviceId, long enrollmentRevision, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate)
    {
        DeviceId = deviceId;
        EnrollmentRevision = enrollmentRevision;
        Certificate = certificate;
    }

    public string DeviceId { get; }
    public long EnrollmentRevision { get; }
    public System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate { get; }
    public void Dispose() => Certificate.Dispose();
}

public class ConnectorEnrollmentException : Exception
{
    public ConnectorEnrollmentException(string message) : base(message) { }
    public ConnectorEnrollmentException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class ConnectorEnrollmentStateException : ConnectorEnrollmentException
{
    public ConnectorEnrollmentStateException(string message) : base(message) { }
    public ConnectorEnrollmentStateException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class ConnectorEnrollmentProtocolException : ConnectorEnrollmentException
{
    public ConnectorEnrollmentProtocolException(string message, int? statusCode = null, string? errorCode = null)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public int? StatusCode { get; }
    public string? ErrorCode { get; }
}

internal sealed record ValidatedClientOptions(
    Uri ServiceBaseUri,
    string IssuerPinHex,
    byte[] IssuerPin,
    ConnectorEnrollmentKeyAlgorithm KeyAlgorithm,
    TimeSpan BootstrapCertificateLifetime,
    TimeSpan ClockSkew,
    TimeProvider TimeProvider);

internal enum StoredEnrollmentStatus
{
    Pending,
    Completed,
}

internal sealed class StoredEnrollmentState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required Guid RequestId { get; init; }
    public required string ServiceBaseUri { get; init; }
    public required string IssuerPinSha256 { get; init; }
    public required ConnectorEnrollmentKeyAlgorithm KeyAlgorithm { get; init; }
    public required byte[] PrivateKeyPkcs8 { get; init; }
    public required string CertificateSigningRequestPem { get; init; }
    public required string PublicKeySha256 { get; init; }
    public required string DeviceDisplayName { get; init; }
    public required StoredEnrollmentStatus Status { get; init; }
    public DeviceEnrollmentResponse? Response { get; init; }
}
