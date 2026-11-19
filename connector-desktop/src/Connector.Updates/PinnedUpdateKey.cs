using System.Security.Cryptography;

namespace Connector.Updates;

/// <summary>
/// The one release-signing public key of the product (ECDSA P-256), compiled into this assembly from
/// <c>Connector.Desktop/Assets/update-public-key.pem</c>. There is no configuration, registry value or
/// file that can replace it; the private half exists only in the GitHub <c>release-signing</c> environment.
/// </summary>
public static class PinnedUpdateKey
{
    private const string ResourceName = "Connector.Updates.update-public-key.pem";

    public static ECDsa Load()
    {
        using var stream = typeof(PinnedUpdateKey).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException("The pinned update key is missing from the build.");
        using var reader = new StreamReader(stream);
        var pem = reader.ReadToEnd();
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
            RequireP256(key);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    internal static void RequireP256(ECDsa key)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        if (key.KeySize != 256 || parameters.Curve.Oid?.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new CryptographicException("The update key must be ECDSA P-256.");
    }
}
