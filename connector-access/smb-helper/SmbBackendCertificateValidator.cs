using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Connector.Access.Smb.Helper;

internal static class SmbBackendCertificateValidator
{
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

    internal static bool IsAllowed(
        X509Certificate2 certificate,
        IReadOnlyList<byte[]> allowedSha256,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(allowedSha256);
        var instant = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
        if (certificate.NotBefore.ToUniversalTime() > instant || certificate.NotAfter.ToUniversalTime() <= instant)
            return false;

        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
        if (eku is null || !eku.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == ClientAuthenticationOid))
            return false;

        if (allowedSha256.Count == 0 || allowedSha256.Any(pin => pin is null || pin.Length != 32))
            return false;
        var observed = SHA256.HashData(certificate.RawData);
        return allowedSha256.Any(expected => CryptographicOperations.FixedTimeEquals(expected, observed));
    }
}
