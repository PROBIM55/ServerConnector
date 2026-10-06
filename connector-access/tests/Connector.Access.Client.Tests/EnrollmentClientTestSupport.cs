using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Connector.Access.Contracts;

namespace Connector.Access.Client.Tests;

internal sealed class DelegateHttpInvokerFactory : IEnrollmentHttpInvokerFactory
{
    private readonly Func<X509Certificate2?, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

    public DelegateHttpInvokerFactory(
        Func<X509Certificate2?, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        _send = send;
    }

    public int CreateCount { get; private set; }

    public HttpMessageInvoker Create(X509Certificate2? clientCertificate)
    {
        CreateCount++;
        return new HttpMessageInvoker(new DelegateHandler(
            (request, cancellationToken) => _send(clientCertificate, request, cancellationToken)));
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => _send(request, cancellationToken);
    }
}

internal sealed class TestCertificateAuthority : IDisposable
{
    private readonly RSA _key;
    private readonly X509Certificate2 _certificate;

    public TestCertificateAuthority()
    {
        _key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Connector Access Test CA",
            _key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        _certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
    }

    public string Pin => Convert.ToHexString(SHA256.HashData(_certificate.RawData)).ToLowerInvariant();

    public DeviceEnrollmentResponse Issue(DeviceEnrollmentRequest enrollment)
    {
        var signingRequest = CertificateRequest.LoadSigningRequestPem(
            enrollment.CertificateSigningRequestPem,
            HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.Default,
            RSASignaturePadding.Pkcs1);
        var spki = signingRequest.PublicKey.ExportSubjectPublicKeyInfo();
        using var publicKey = ImportPublicKey(signingRequest.PublicKey.Oid.Value, spki);
        var deviceId = $"device-{enrollment.RequestId:N}";
        var leafRequest = publicKey switch
        {
            RSA rsa => new CertificateRequest(
                $"CN=connector-device-{deviceId}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            ECDsa ecdsa => new CertificateRequest(
                $"CN=connector-device-{deviceId}", ecdsa, HashAlgorithmName.SHA256),
            _ => throw new InvalidOperationException(),
        };
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(DeviceAccessProtocol.ClientAuthenticationOid) },
            true));
        leafRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(leafRequest.PublicKey, false));
        var notAfter = DateTimeOffset.UtcNow.AddDays(30);
        var serial = RandomNumberGenerator.GetBytes(16);
        var signatureGenerator = X509SignatureGenerator.CreateForRSA(_key, RSASignaturePadding.Pkcs1);
        using var leaf = leafRequest.Create(
            _certificate.SubjectName,
            signatureGenerator,
            DateTimeOffset.UtcNow.AddMinutes(-2),
            notAfter,
            serial);
        return new DeviceEnrollmentResponse(
            DeviceAccessProtocol.Version,
            enrollment.RequestId,
            deviceId,
            leaf.ExportCertificatePem(),
            _certificate.ExportCertificatePem(),
            new DateTimeOffset(leaf.NotAfter.ToUniversalTime()),
            17);
    }

    public static byte[] ReadCsrSpki(string csrPem)
    {
        var request = CertificateRequest.LoadSigningRequestPem(
            csrPem,
            HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.Default,
            RSASignaturePadding.Pkcs1);
        return request.PublicKey.ExportSubjectPublicKeyInfo();
    }

    public static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(value),
    };

    public static HttpResponseMessage Failure(HttpStatusCode status, string code) => new(status)
    {
        Content = JsonContent.Create(new DeviceAccessFailure(code, "rejected")),
    };

    public void Dispose()
    {
        _certificate.Dispose();
        _key.Dispose();
    }

    private static AsymmetricAlgorithm ImportPublicKey(string? oid, byte[] spki)
    {
        if (oid == "1.2.840.113549.1.1.1")
        {
            var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(spki, out _);
            return rsa;
        }

        if (oid == "1.2.840.10045.2.1")
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
            return ecdsa;
        }

        throw new CryptographicException("Unsupported test CSR public key.");
    }
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "connector-access-client-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
