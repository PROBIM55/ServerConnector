using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Connector.Access;

// This permits a TLS handshake, not application access. Bootstrap certificates
// may reach only enrollment recovery: the endpoint must still bind their key to
// a pending registry entry. Registered identities are checked on every request.
public sealed class ConnectorAccessTlsCertificatePolicy(
    X509Certificate2 configuredIssuer,
    TimeProvider clock,
    DeviceAccessOptions options)
{
    public bool Validate(X509Certificate2 certificate)
    {
        try
        {
            var now = clock.GetUtcNow();
            var before = new DateTimeOffset(certificate.NotBefore.ToUniversalTime());
            var after = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
            if (certificate.RawData.Length > 16384 || !CertificatePolicy.IsClientAuthenticationCertificate(certificate) ||
                before > now.Add(options.ClockSkew) || after <= now.Subtract(options.ClockSkew)) return false;
            var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
            if (usage is null || usage.KeyUsages != X509KeyUsageFlags.DigitalSignature) return false;
            using var rsa = certificate.GetRSAPublicKey();
            using var ec = certificate.GetECDsaPublicKey();
            if (rsa is not null ? rsa.KeySize < 2048 : ec?.ExportParameters(false).Curve.Oid.Value is not
                ("1.2.840.10045.3.1.7" or "1.3.132.0.34" or "1.3.132.0.35")) return false;

            var selfIssued = certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);
            if (selfIssued && (after <= before || after - before > options.EnrollmentProofMaximumLifetime)) return false;
            if (!selfIssued && !certificate.IssuerName.RawData.AsSpan().SequenceEqual(configuredIssuer.SubjectName.RawData)) return false;

            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(selfIssued ? certificate : configuredIssuer);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.VerificationTime = now.UtcDateTime;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(Contracts.DeviceAccessProtocol.ClientAuthenticationOid));
            return chain.Build(certificate);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
