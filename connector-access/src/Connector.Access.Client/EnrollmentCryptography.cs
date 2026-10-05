using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Connector.Access.Contracts;

namespace Connector.Access.Client;

internal static class EnrollmentCryptography
{
    private const string P256Oid = "1.2.840.10045.3.1.7";

    public static StoredEnrollmentState CreatePending(
        ValidatedClientOptions options,
        string deviceDisplayName)
    {
        if (string.IsNullOrWhiteSpace(deviceDisplayName) || deviceDisplayName.Length > 128)
        {
            throw new ArgumentException("A device display name between 1 and 128 characters is required.", nameof(deviceDisplayName));
        }

        var requestId = Guid.NewGuid();
        using var key = CreateKey(options.KeyAlgorithm);
        var request = CreateCertificateRequest(key, $"CN=connector-enrollment-{requestId:N}");
        var privateKey = ExportPrivateKey(key);
        var spki = ExportSubjectPublicKeyInfo(key);
        return new StoredEnrollmentState
        {
            RequestId = requestId,
            ServiceBaseUri = options.ServiceBaseUri.AbsoluteUri,
            IssuerPinSha256 = options.IssuerPinHex,
            KeyAlgorithm = options.KeyAlgorithm,
            PrivateKeyPkcs8 = privateKey,
            CertificateSigningRequestPem = request.CreateSigningRequestPem(),
            PublicKeySha256 = Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant(),
            DeviceDisplayName = deviceDisplayName,
            Status = StoredEnrollmentStatus.Pending,
        };
    }

    public static KeyMaterial LoadAndValidateKey(StoredEnrollmentState state)
    {
        AsymmetricAlgorithm key;
        try
        {
            key = state.KeyAlgorithm switch
            {
                ConnectorEnrollmentKeyAlgorithm.EcdsaP256 => ImportEcdsa(state.PrivateKeyPkcs8),
                ConnectorEnrollmentKeyAlgorithm.Rsa2048 => ImportRsa(state.PrivateKeyPkcs8),
                _ => throw new CryptographicException("Unsupported connector key algorithm."),
            };
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            throw new ConnectorEnrollmentStateException("The stored connector private key is invalid.", exception);
        }

        try
        {
            var expectedSpki = ExportSubjectPublicKeyInfo(key);
            var expectedHash = SHA256.HashData(expectedSpki);
            byte[] storedHash;
            try
            {
                storedHash = Convert.FromHexString(state.PublicKeySha256);
            }
            catch (FormatException exception)
            {
                throw new ConnectorEnrollmentStateException("The stored public-key fingerprint is invalid.", exception);
            }

            if (!CryptographicOperations.FixedTimeEquals(expectedHash, storedHash))
            {
                throw new ConnectorEnrollmentStateException("The stored connector key does not match its fingerprint.");
            }

            var signingRequest = CertificateRequest.LoadSigningRequestPem(
                state.CertificateSigningRequestPem,
                HashAlgorithmName.SHA256,
                CertificateRequestLoadOptions.Default,
                RSASignaturePadding.Pkcs1);
            var csrSpki = signingRequest.PublicKey.ExportSubjectPublicKeyInfo();
            if (!CryptographicOperations.FixedTimeEquals(expectedSpki, csrSpki))
            {
                throw new ConnectorEnrollmentStateException("The stored CSR and private key do not match.");
            }

            return new KeyMaterial(key, expectedSpki);
        }
        catch (ConnectorEnrollmentStateException)
        {
            key.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or FormatException)
        {
            key.Dispose();
            throw new ConnectorEnrollmentStateException("The stored CSR is invalid or does not match its private key.", exception);
        }
    }

    public static X509Certificate2 CreateBootstrapCertificate(
        StoredEnrollmentState state,
        KeyMaterial key,
        ValidatedClientOptions options)
    {
        var now = options.TimeProvider.GetUtcNow();
        var request = CreateCertificateRequest(key.Key, $"CN=connector-enrollment-{state.RequestId:N}");
        using var generated = request.CreateSelfSigned(now.AddMinutes(-1), now.Add(options.BootstrapCertificateLifetime));
        return ReimportForWindowsTls(generated);
    }

    public static X509Certificate2 CreateIssuedTlsCertificate(
        StoredEnrollmentState state,
        KeyMaterial key,
        ValidatedClientOptions options)
    {
        if (state.Response is null)
        {
            throw new ConnectorEnrollmentStateException("A pending enrollment has no issued device certificate.");
        }

        return ValidateResponseAndAttachKey(state.Response, state, key, options);
    }

    public static X509Certificate2 ValidateResponseAndAttachKey(
        DeviceEnrollmentResponse response,
        StoredEnrollmentState state,
        KeyMaterial key,
        ValidatedClientOptions options)
    {
        if (response.SchemaVersion != DeviceAccessProtocol.Version ||
            response.RequestId != state.RequestId ||
            string.IsNullOrWhiteSpace(response.DeviceId) ||
            response.DeviceId.Length > 256 ||
            response.AccessRevision < 0)
        {
            throw new ConnectorEnrollmentProtocolException("The enrollment response does not match the pending request.");
        }

        using var issuer = LoadCertificate(response.IssuerCertificatePem, "issuer");
        var issuerHash = SHA256.HashData(issuer.RawData);
        if (!CryptographicOperations.FixedTimeEquals(issuerHash, options.IssuerPin))
        {
            throw new ConnectorEnrollmentProtocolException("The enrollment response issuer does not match the configured CA pin.");
        }

        var issuerConstraints = issuer.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
        var issuerUsage = issuer.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
        if (issuerConstraints is not { CertificateAuthority: true } ||
            issuerUsage is null ||
            !issuerUsage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign))
        {
            throw new ConnectorEnrollmentProtocolException("The configured enrollment issuer is not a certificate authority.");
        }

        using var leaf = LoadCertificate(response.ClientCertificatePem, "device");
        if (leaf.HasPrivateKey ||
            !string.Equals(leaf.SubjectName.Name, $"CN=connector-device-{response.DeviceId}", StringComparison.Ordinal) ||
            !leaf.IssuerName.RawData.AsSpan().SequenceEqual(issuer.SubjectName.RawData))
        {
            throw new ConnectorEnrollmentProtocolException("The issued device certificate has an invalid identity or issuer.");
        }

        var actualSpki = leaf.PublicKey.ExportSubjectPublicKeyInfo();
        if (!CryptographicOperations.FixedTimeEquals(actualSpki, key.SubjectPublicKeyInfo))
        {
            throw new ConnectorEnrollmentProtocolException("The issued device certificate does not contain the CSR public key.");
        }

        var constraints = leaf.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
        var usage = leaf.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
        var eku = leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
        if (constraints is null || constraints.CertificateAuthority ||
            usage is null || usage.KeyUsages != X509KeyUsageFlags.DigitalSignature ||
            eku is null || eku.EnhancedKeyUsages.Count != 1 ||
            eku.EnhancedKeyUsages[0].Value != DeviceAccessProtocol.ClientAuthenticationOid)
        {
            throw new ConnectorEnrollmentProtocolException("The issued device certificate violates the client-authentication policy.");
        }

        var now = options.TimeProvider.GetUtcNow();
        var notBefore = new DateTimeOffset(leaf.NotBefore.ToUniversalTime());
        var notAfter = new DateTimeOffset(leaf.NotAfter.ToUniversalTime());
        var issuerNotBefore = new DateTimeOffset(issuer.NotBefore.ToUniversalTime());
        var issuerNotAfter = new DateTimeOffset(issuer.NotAfter.ToUniversalTime());
        if (notBefore > now.Add(options.ClockSkew) ||
            notAfter <= now.Subtract(options.ClockSkew) ||
            issuerNotBefore > now.Add(options.ClockSkew) ||
            issuerNotAfter < notAfter ||
            Math.Abs((response.CertificateExpiresAtUtc - notAfter).TotalSeconds) > 1)
        {
            throw new ConnectorEnrollmentProtocolException("The issued certificate lifetime is invalid.");
        }

        using (var chain = new X509Chain())
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(issuer);
            chain.ChainPolicy.ExtraStore.Add(issuer);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.VerificationTime = now.UtcDateTime;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(DeviceAccessProtocol.ClientAuthenticationOid));
            if (!chain.Build(leaf) || chain.ChainElements.Count < 2 ||
                !chain.ChainElements[chain.ChainElements.Count - 1].Certificate.RawData.AsSpan().SequenceEqual(issuer.RawData))
            {
                throw new ConnectorEnrollmentProtocolException("The device certificate signature is not rooted in the configured issuer.");
            }
        }

        using var withPrivateKey = key.Key switch
        {
            RSA rsa => leaf.CopyWithPrivateKey(rsa),
            ECDsa ecdsa => leaf.CopyWithPrivateKey(ecdsa),
            _ => throw new ConnectorEnrollmentStateException("Unsupported connector private key type."),
        };
        return ReimportForWindowsTls(withPrivateKey);
    }

    private static CertificateRequest CreateCertificateRequest(AsymmetricAlgorithm key, string subject)
    {
        var request = key switch
        {
            RSA rsa => new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            ECDsa ecdsa => new CertificateRequest(subject, ecdsa, HashAlgorithmName.SHA256),
            _ => throw new CryptographicException("Unsupported connector key algorithm."),
        };
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(DeviceAccessProtocol.ClientAuthenticationOid) },
            true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request;
    }

    private static AsymmetricAlgorithm CreateKey(ConnectorEnrollmentKeyAlgorithm algorithm) => algorithm switch
    {
        ConnectorEnrollmentKeyAlgorithm.EcdsaP256 => ECDsa.Create(ECCurve.NamedCurves.nistP256),
        ConnectorEnrollmentKeyAlgorithm.Rsa2048 => RSA.Create(2048),
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    private static RSA ImportRsa(byte[] pkcs8)
    {
        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(pkcs8, out var read);
        if (read != pkcs8.Length || rsa.KeySize < 2048)
        {
            rsa.Dispose();
            throw new CryptographicException("RSA enrollment keys must be at least 2048 bits.");
        }

        return rsa;
    }

    private static ECDsa ImportEcdsa(byte[] pkcs8)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(pkcs8, out var read);
        if (read != pkcs8.Length || ecdsa.ExportParameters(false).Curve.Oid.Value != P256Oid)
        {
            ecdsa.Dispose();
            throw new CryptographicException("ECDSA enrollment keys must use NIST P-256.");
        }

        return ecdsa;
    }

    private static byte[] ExportPrivateKey(AsymmetricAlgorithm key) => key switch
    {
        RSA rsa => rsa.ExportPkcs8PrivateKey(),
        ECDsa ecdsa => ecdsa.ExportPkcs8PrivateKey(),
        _ => throw new CryptographicException("Unsupported connector key algorithm."),
    };

    private static byte[] ExportSubjectPublicKeyInfo(AsymmetricAlgorithm key) => key switch
    {
        RSA rsa => rsa.ExportSubjectPublicKeyInfo(),
        ECDsa ecdsa => ecdsa.ExportSubjectPublicKeyInfo(),
        _ => throw new CryptographicException("Unsupported connector key algorithm."),
    };

    private static X509Certificate2 LoadCertificate(string pem, string role)
    {
        try
        {
            return X509Certificate2.CreateFromPem(pem);
        }
        catch (CryptographicException exception)
        {
            throw new ConnectorEnrollmentProtocolException($"The enrollment {role} certificate is invalid: {exception.Message}");
        }
    }

    // Windows SChannel cannot reliably use the ephemeral key handles produced by
    // CreateSelfSigned/CopyWithPrivateKey. A normal PFX reimport creates a temporary
    // Windows key container which is removed when the certificate is disposed.
    private static X509Certificate2 ReimportForWindowsTls(X509Certificate2 certificate)
    {
        var pfx = certificate.Export(X509ContentType.Pfx);
        try
        {
            return new X509Certificate2(pfx);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    internal sealed class KeyMaterial : IDisposable
    {
        public KeyMaterial(AsymmetricAlgorithm key, byte[] subjectPublicKeyInfo)
        {
            Key = key;
            SubjectPublicKeyInfo = subjectPublicKeyInfo;
        }

        public AsymmetricAlgorithm Key { get; }
        public byte[] SubjectPublicKeyInfo { get; }

        public void Dispose()
        {
            Key.Dispose();
            CryptographicOperations.ZeroMemory(SubjectPublicKeyInfo);
        }
    }
}
