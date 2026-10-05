using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Connector.Access;

/// <summary>
/// Issues device certificates from a configured CA certificate loaded from a PKCS#12 file.
/// The issuer certificate is product-neutral; hosts remain responsible for their TLS listeners.
/// </summary>
public sealed class X509DeviceCertificateIssuer : IX509DeviceCertificateIssuer, IDisposable
{
    private readonly X509Certificate2 _issuerWithPrivateKey;

    private X509DeviceCertificateIssuer(X509Certificate2 issuerWithPrivateKey)
    {
        _issuerWithPrivateKey = issuerWithPrivateKey;
        IssuerCertificate = new X509Certificate2(issuerWithPrivateKey.Export(X509ContentType.Cert));
    }

    public X509Certificate2 IssuerCertificate { get; }

    /// <summary>
    /// Loads a PKCS#12 issuer certificate. If <paramref name="passwordEnvironmentVariable"/>
    /// is supplied, its value is read at load time and is never retained by this object.
    /// </summary>
    public static X509DeviceCertificateIssuer Load(
        string certificatePath,
        string? passwordEnvironmentVariable = null)
    {
        if (string.IsNullOrWhiteSpace(certificatePath))
            throw new InvalidOperationException("Device issuer certificate path is required.");

        var fullPath = Path.GetFullPath(certificatePath);
        if (!File.Exists(fullPath))
            throw new InvalidOperationException("Device issuer certificate file is missing.");

        var passwordVariable = passwordEnvironmentVariable?.Trim();
        var password = string.IsNullOrEmpty(passwordVariable)
            ? null
            : Environment.GetEnvironmentVariable(passwordVariable)
              ?? throw new InvalidOperationException("Device issuer certificate password environment variable is missing.");
        var issuer = new X509Certificate2(
            fullPath,
            password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);

        try
        {
            if (!issuer.HasPrivateKey)
                throw new InvalidOperationException("Device issuer certificate has no private key.");

            var constraints = issuer.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
            if (constraints is null || !constraints.CertificateAuthority)
                throw new InvalidOperationException("Device issuer certificate is not a CA.");

            var now = DateTime.UtcNow;
            if (now < issuer.NotBefore.ToUniversalTime() || now > issuer.NotAfter.ToUniversalTime())
                throw new InvalidOperationException("Device issuer certificate is outside its validity period.");

            var keyUsage = issuer.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
            if (keyUsage is null || (keyUsage.KeyUsages & X509KeyUsageFlags.KeyCertSign) == 0)
                throw new InvalidOperationException("Device issuer certificate does not permit certificate signing.");

            return new X509DeviceCertificateIssuer(issuer);
        }
        catch
        {
            issuer.Dispose();
            throw;
        }
    }

    public ValueTask<X509Certificate2> IssueAsync(
        CertificateRequest certificateRequest,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(certificateRequest);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(certificateRequest.Create(
            _issuerWithPrivateKey,
            notBefore,
            notAfter,
            RandomNumberGenerator.GetBytes(16)));
    }

    public void Dispose()
    {
        IssuerCertificate.Dispose();
        _issuerWithPrivateKey.Dispose();
    }
}
