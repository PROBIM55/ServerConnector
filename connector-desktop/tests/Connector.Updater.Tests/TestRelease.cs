using System.Security.Cryptography;
using System.Text;
using Connector.Updates;

namespace Connector.Updater.Tests;

/// <summary>A throw-away signing key and a builder of release descriptions in the exact catalog format.</summary>
internal sealed class TestRelease : IDisposable
{
    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public ReleaseDescriptionVerifier Verifier()
    {
        var publicKey = ECDsa.Create();
        publicKey.ImportSubjectPublicKeyInfo(Key.ExportSubjectPublicKeyInfo(), out _);
        return new ReleaseDescriptionVerifier(publicKey);
    }

    public static string Json(string version = "1.1.16", string channel = "stable", bool paused = false, string minimum = "1.1.15",
        string? fileName = null, long size = 1000, string sha256 = "", string extra = "", string schema = "2",
        string applicationId = "Structura.PlatformConnector", string notes = "[\"Быстрее подключение\"]")
    {
        if (sha256.Length == 0) sha256 = new string('a', 64);
        fileName ??= "PlatformConnector-" + version + ".exe";
        return "{\n" +
               $"  \"schemaVersion\": {schema},\n" +
               $"  \"applicationId\": \"{applicationId}\",\n" +
               $"  \"channel\": \"{channel}\",\n" +
               $"  \"version\": \"{version}\",\n" +
               "  \"publishedAtUtc\": \"2026-11-25T10:00:00Z\",\n" +
               $"  \"paused\": {(paused ? "true" : "false")},\n" +
               $"  \"minimumVersion\": \"{minimum}\",\n" +
               $"  \"installer\": {{ \"fileName\": \"{fileName}\", \"size\": {size}, \"sha256\": \"{sha256}\" }},\n" +
               $"  \"notes\": {notes}{extra}\n" +
               "}";
    }

    public byte[] Sign(byte[] bytes) =>
        Key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public (byte[] Description, byte[] Signature) Signed(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return (bytes, Sign(bytes));
    }

    public static ReleaseDescription Description(string version = "1.1.16", byte[]? payload = null, bool paused = false, string minimum = "1.1.15",
        string channel = "stable", string[]? notes = null)
    {
        var bytes = payload ?? Encoding.ASCII.GetBytes("installer " + version);
        var parsed = ReleaseVersion.Parse(version);
        return new ReleaseDescription(channel, parsed, DateTimeOffset.Parse("2026-11-25T10:00:00Z"), paused, ReleaseVersion.Parse(minimum),
            new ReleaseInstaller(ReleaseCatalogContract.InstallerFileName(parsed), bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()),
            notes ?? ["Что изменилось"]);
    }

    public void Dispose() => Key.Dispose();
}
