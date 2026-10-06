using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Connector.Access;

internal sealed class ValidatedCertificateRequest : IDisposable
{
    public required CertificateRequest Request { get; init; }
    public required AsymmetricAlgorithm PublicKey { get; init; }
    public required string PublicKeySha256 { get; init; }
    public required string CsrSha256 { get; init; }
    public required byte[] CsrDer { get; init; }

    public void Dispose()
    {
        PublicKey.Dispose();
    }
}

internal static class CertificatePolicy
{
    private const string RsaOid = "1.2.840.113549.1.1.1";
    private const string EcPublicKeyOid = "1.2.840.10045.2.1";

    public static ValidatedCertificateRequest LoadAndRebuild(string pem, string deviceId)
    {
        // Default validates the PKCS#10 signature. In particular, do not use
        // SkipSignatureValidation or UnsafeLoadCertificateExtensions here.
        var pemFields = PemEncoding.Find(pem);
        var label = pem[pemFields.Label].ToString();
        if (label is not ("CERTIFICATE REQUEST" or "NEW CERTIFICATE REQUEST"))
        {
            throw new CryptographicException("Expected a PKCS#10 certificate request.");
        }

        var csrDer = Convert.FromBase64String(pem[pemFields.Base64Data].ToString());
        var signingRequest = CertificateRequest.LoadSigningRequest(
            csrDer,
            HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.Default,
            RSASignaturePadding.Pkcs1);

        var spki = signingRequest.PublicKey.ExportSubjectPublicKeyInfo();
        var publicKeyHash = Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant();
        var subject = new X500DistinguishedName($"CN=connector-device-{deviceId}");

        AsymmetricAlgorithm key;
        CertificateRequest rebuilt;
        switch (signingRequest.PublicKey.Oid.Value)
        {
            case RsaOid:
                var rsa = RSA.Create();
                rsa.ImportSubjectPublicKeyInfo(spki, out var rsaRead);
                if (rsaRead != spki.Length || rsa.KeySize < 2048)
                {
                    rsa.Dispose();
                    throw new CryptographicException("RSA key must be at least 2048 bits.");
                }

                key = rsa;
                rebuilt = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                break;

            case EcPublicKeyOid:
                var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(spki, out var ecRead);
                var curveOid = ecdsa.ExportParameters(false).Curve.Oid.Value;
                if (ecRead != spki.Length || curveOid is not (
                        "1.2.840.10045.3.1.7" or "1.3.132.0.34" or "1.3.132.0.35"))
                {
                    ecdsa.Dispose();
                    throw new CryptographicException("Only NIST P-256, P-384 and P-521 keys are approved.");
                }

                key = ecdsa;
                rebuilt = new CertificateRequest(subject, ecdsa, HashAlgorithmName.SHA256);
                break;

            default:
                throw new CryptographicException("Unsupported public key algorithm.");
        }

        rebuilt.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        rebuilt.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        rebuilt.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(Connector.Access.Contracts.DeviceAccessProtocol.ClientAuthenticationOid) },
            true));
        rebuilt.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rebuilt.PublicKey, false));

        return new ValidatedCertificateRequest
        {
            Request = rebuilt,
            PublicKey = key,
            PublicKeySha256 = publicKeyHash,
            CsrSha256 = Convert.ToHexString(SHA256.HashData(csrDer)).ToLowerInvariant(),
            CsrDer = csrDer,
        };
    }

    public static ValidatedCertificateRequest LoadAndRebuild(ReadOnlySpan<byte> csrDer, string deviceId) =>
        LoadAndRebuild(PemEncoding.WriteString("CERTIFICATE REQUEST", csrDer), deviceId);

    public static string ValidateIssuedCertificate(
        X509Certificate2 certificate,
        string deviceId,
        string expectedPublicKeySha256,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter)
    {
        if (certificate.HasPrivateKey)
        {
            throw new CryptographicException("The issuer returned private key material.");
        }

        if (!string.Equals(
                certificate.SubjectName.Name,
                $"CN=connector-device-{deviceId}",
                StringComparison.Ordinal))
        {
            throw new CryptographicException("The issuer changed the controlled certificate subject.");
        }

        var actualPublicKeyHash = Convert.ToHexString(
            SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualPublicKeyHash),
                Convert.FromHexString(expectedPublicKeySha256)))
        {
            throw new CryptographicException("The issuer changed the CSR public key.");
        }

        var basicConstraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
        if (basicConstraints is null || basicConstraints.CertificateAuthority)
        {
            throw new CryptographicException("A device certificate must be an explicit non-CA certificate.");
        }

        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
        if (eku is null || eku.EnhancedKeyUsages.Count != 1 ||
            eku.EnhancedKeyUsages[0].Value != Connector.Access.Contracts.DeviceAccessProtocol.ClientAuthenticationOid)
        {
            throw new CryptographicException("A device certificate must be client-auth-only.");
        }

        var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
        if (usage is null || usage.KeyUsages != X509KeyUsageFlags.DigitalSignature)
        {
            throw new CryptographicException("A device certificate must only permit digital signatures.");
        }

        var certNotBefore = new DateTimeOffset(certificate.NotBefore.ToUniversalTime());
        var certNotAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
        if (certNotBefore < notBefore.AddMinutes(-1) || certNotAfter > notAfter.AddMinutes(1) ||
            certNotAfter <= certNotBefore)
        {
            throw new CryptographicException("The issuer returned an invalid certificate lifetime.");
        }

        return Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
    }

    public static bool IsClientAuthenticationCertificate(X509Certificate2 certificate)
    {
        var constraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
        return constraints is { CertificateAuthority: false } &&
               eku is not null &&
               eku.EnhancedKeyUsages.Cast<Oid>().Any(
                   oid => oid.Value == Connector.Access.Contracts.DeviceAccessProtocol.ClientAuthenticationOid);
    }
}
