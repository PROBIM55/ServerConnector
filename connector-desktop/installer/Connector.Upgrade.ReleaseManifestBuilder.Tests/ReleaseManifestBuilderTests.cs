using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Connector.Upgrade.ReleaseManifestBuilder;
using Xunit;

namespace Connector.Upgrade.ReleaseManifestBuilder.Tests;

public sealed class ReleaseManifestBuilderTests
{
    [Fact]
    public void Writes_exact_schema_v2_and_p1363_signature_without_private_key()
    {
        using var fixture = new Fixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(fixture.KeyPath, key.ExportPkcs8PrivateKeyPem());
        var builder = fixture.Builder();
        builder.Build(fixture.Request());

        var manifest = File.ReadAllBytes(Path.Combine(fixture.Stage, "helper-release.json"));
        var signature = File.ReadAllBytes(Path.Combine(fixture.Stage, "helper-release.sig"));
        Assert.Equal(64, signature.Length);
        Assert.True(key.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        using var json = JsonDocument.Parse(manifest);
        Assert.Equal(new[] { "schemaVersion", "version", "helperRelativePath", "sha256", "authenticodeSignerThumbprint", "velopackSetup" },
            json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Connector.Upgrade.MachineHelper.exe", json.RootElement.GetProperty("helperRelativePath").GetString());
        Assert.Equal("Setup.exe", json.RootElement.GetProperty("velopackSetup").GetProperty("relativePath").GetString());
        Assert.False(Encoding.UTF8.GetString(manifest).Contains("PRIVATE KEY", StringComparison.Ordinal));
    }

    [Fact]
    public void Writes_schema_v3_with_only_signed_hashes_and_sizes_without_authenticode_inspection()
    {
        using var fixture = new Fixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(fixture.KeyPath, key.ExportPkcs8PrivateKeyPem());
        var inspector = new FakeInspector("publisher");
        fixture.Builder(inspector).Build(fixture.Request() with { PublisherName = null, SchemaVersion = 3 });

        var manifest = File.ReadAllBytes(Path.Combine(fixture.Stage, "helper-release.json"));
        var signature = File.ReadAllBytes(Path.Combine(fixture.Stage, "helper-release.sig"));
        Assert.True(key.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.Equal(0, inspector.InspectionCalls);
        using var json = JsonDocument.Parse(manifest);
        Assert.Equal(new[] { "schemaVersion", "version", "helperRelativePath", "size", "sha256", "velopackSetup" },
            json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(3, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(3, json.RootElement.GetProperty("size").GetInt64());
        Assert.Equal(new[] { "relativePath", "size", "sha256", "packageId", "version" },
            json.RootElement.GetProperty("velopackSetup").EnumerateObject().Select(p => p.Name));
        Assert.False(Encoding.UTF8.GetString(manifest).Contains("authenticode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Writes_schema_v4_with_exact_caller_helper_and_setup_pins_without_authenticode()
    {
        using var fixture = new Fixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(fixture.KeyPath, key.ExportPkcs8PrivateKeyPem());
        var inspector = new FakeInspector("publisher");
        fixture.Builder(inspector).Build(fixture.Request() with { PublisherName = null, SchemaVersion = 4, CallerPath = fixture.CallerPath });

        var manifest = File.ReadAllBytes(Path.Combine(fixture.Stage, "helper-release.json"));
        var signature = File.ReadAllBytes(Path.Combine(fixture.Stage, "helper-release.sig"));
        Assert.True(key.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.Equal(0, inspector.InspectionCalls);
        using var json = JsonDocument.Parse(manifest);
        Assert.Equal(4, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(new[] { "schemaVersion", "version", "helperRelativePath", "size", "sha256", "velopackSetup", "callerImage" },
            json.RootElement.EnumerateObject().Select(p => p.Name));
        var caller = json.RootElement.GetProperty("callerImage");
        Assert.Equal(new[] { "relativePath", "size", "sha256" }, caller.EnumerateObject().Select(p => p.Name));
        Assert.Equal("StructuraConnectorInstaller\\Bootstrapper\\Connector.Upgrade.Bootstrapper.exe", caller.GetProperty("relativePath").GetString());
        Assert.Equal(3, caller.GetProperty("size").GetInt64());
        Assert.Equal(Convert.ToHexString(SHA256.HashData([7, 8, 9])), caller.GetProperty("sha256").GetString());
        Assert.False(Encoding.UTF8.GetString(manifest).Contains("authenticode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Schema_v4_requires_the_fixed_bootstrapper_caller_file()
    {
        using var fixture = new Fixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(fixture.KeyPath, key.ExportPkcs8PrivateKeyPem());
        Assert.Throws<InvalidDataException>(() => fixture.Builder().Build(fixture.Request() with { PublisherName = null, SchemaVersion = 4 }));
        Assert.Throws<InvalidDataException>(() => fixture.Builder().Build(fixture.Request() with { PublisherName = null, SchemaVersion = 4, CallerPath = fixture.SetupPath }));
        Assert.False(File.Exists(Path.Combine(fixture.Stage, "helper-release.json")));
    }

    [Theory]
    [InlineData("publisher")]
    [InlineData("timestamp")]
    [InlineData("thumbprint")]
    public void Rejects_untrusted_authenticode_evidence(string failure)
    {
        using var fixture = new Fixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(fixture.KeyPath, key.ExportPkcs8PrivateKeyPem());
        var inspector = new FakeInspector(failure);
        Assert.Throws<InvalidDataException>(() => fixture.Builder(inspector).Build(fixture.Request()));
        Assert.False(File.Exists(Path.Combine(fixture.Stage, "helper-release.json")));
    }

    [Fact]
    public void Rejects_wrong_names_key_in_repo_and_output_overwrite()
    {
        using var fixture = new Fixture();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(fixture.KeyPath, key.ExportPkcs8PrivateKeyPem());
        var request = fixture.Request();
        Assert.Throws<InvalidDataException>(() => fixture.Builder().Build(request with { HelperPath = fixture.SetupPath }));
        Assert.Throws<InvalidDataException>(() => fixture.Builder().Build(request with { PrivateKeyPemPath = Path.Combine(fixture.Repo, "key.pem") }));
        fixture.Builder().Build(request);
        Assert.Throws<IOException>(() => fixture.Builder().Build(request));
    }

    [Fact]
    public void Rejects_multiple_or_wrong_curve_private_pem()
    {
        using var fixture = new Fixture();
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var builder = fixture.Builder();
        File.WriteAllText(fixture.KeyPath, p256.ExportPkcs8PrivateKeyPem() + Environment.NewLine + p256.ExportPkcs8PrivateKeyPem());
        Assert.Throws<InvalidDataException>(() => builder.Build(fixture.Request()));
        File.WriteAllText(fixture.KeyPath, p384.ExportPkcs8PrivateKeyPem());
        Assert.Throws<InvalidDataException>(() => builder.Build(fixture.Request()));
        Assert.False(File.Exists(Path.Combine(fixture.Stage, "helper-release.json")));
    }

    [Theory]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    public void Protected_staging_rejects_untrusted_acl_control_rights(FileSystemRights right)
    {
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var ace = new FileSystemAccessRule(users, right, AccessControlType.Allow);
        var trusted = new HashSet<string>(StringComparer.Ordinal) { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value };

        Assert.True(ReleaseManifestBuilder.GrantsMutationToUntrustedSid(ace, trusted));
    }

    [Theory]
    [InlineData("GA")]
    [InlineData("GW")]
    public void Protected_staging_rejects_generic_acl_rights(string sddlRight)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm($"O:BAG:BAD:(A;;{sddlRight};;;BU)");
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var ace = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Single(rule => rule.IdentityReference == users);
        var trusted = new HashSet<string>(StringComparer.Ordinal)
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value
        };

        Assert.True(ReleaseManifestBuilder.GrantsMutationToUntrustedSid(ace, trusted));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "release-manifest-builder-" + Guid.NewGuid().ToString("N"));
        public string Stage => Path.Combine(Root, "stage");
        public string Repo => Path.Combine(Root, "repo");
        public string HelperPath => Path.Combine(Stage, "Connector.Upgrade.MachineHelper.exe");
        public string SetupPath => Path.Combine(Stage, "Setup.exe");
        public string CallerPath => Path.Combine(Stage, "Connector.Upgrade.Bootstrapper.exe");
        public string KeyPath => Path.Combine(Root, "private.pem");
        public Fixture() { Directory.CreateDirectory(Stage); Directory.CreateDirectory(Repo); File.WriteAllBytes(HelperPath, [1, 2, 3]); File.WriteAllBytes(SetupPath, [4, 5, 6]); File.WriteAllBytes(CallerPath, [7, 8, 9]); }
        public ReleaseBuildRequest Request() => new(HelperPath, SetupPath, Stage, KeyPath, "1.2.3", "StructuraConnector", "4.5.6", "Structura Test Publisher", Repo);
        public ReleaseManifestBuilder Builder(FakeInspector? inspector = null) => new(inspector ?? new FakeInspector(null), _ => { });
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class FakeInspector(string? failure) : IAuthenticodeInspector
    {
        public int InspectionCalls { get; private set; }
        public AuthenticodeInfo Inspect(string path) => new(
            InspectPublisher(),
            failure == "thumbprint" ? "bad" : "0123456789ABCDEF0123456789ABCDEF01234567",
            failure != "timestamp");
        private string InspectPublisher() { InspectionCalls++; return failure == "publisher" ? "Wrong Publisher" : "Structura Test Publisher"; }
    }
}
