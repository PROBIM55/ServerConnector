using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Connector.Access.Smb.Helper;

namespace Connector.Access.Smb.Tests;

public sealed class SmbHelperServerCertificatePasswordTests
{
    private const string SecretName = "CONNECTOR_SMB_HELPER_SERVER_PFX_PASSWORD";
    private const string FixturePassword = "fixture-only-password-do-not-log";

    [Fact]
    public void ProtectedJson_ReadsOnlyRequestedSupportedPassword()
    {
        var options = Options(SmbHelperServerCertificatePassword.ProtectedFilePath);
        var calls = 0;
        var password = SmbHelperServerCertificatePassword.Load(
            options,
            _ => throw new InvalidOperationException("Environment fallback must not run."),
            path =>
            {
                calls++;
                Assert.Equal(SmbHelperServerCertificatePassword.ProtectedFilePath, path);
                return Encoding.UTF8.GetBytes("{\"" + SecretName + "\":\"" + FixturePassword + "\"}");
            });

        Assert.Equal(1, calls);
        Assert.Equal(FixturePassword, password);
    }

    [Fact]
    public void MissingProtectedFileFailsWithoutEnvironmentFallbackOrContentLeak()
    {
        var fallbackCalled = false;
        var exception = Assert.Throws<InvalidOperationException>(() => SmbHelperServerCertificatePassword.Load(
            Options(SmbHelperServerCertificatePassword.ProtectedFilePath),
            _ => { fallbackCalled = true; return FixturePassword; },
            _ => throw new IOException(FixturePassword)));

        Assert.False(fallbackCalled);
        Assert.Equal("SMB helper server certificate password is unavailable.", exception.Message);
        Assert.DoesNotContain(FixturePassword, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"CONNECTOR_SMB_HELPER_SERVER_PFX_PASSWORD\":\"fixture-only-password-do-not-log", "invalid JSON")]
    [InlineData("{}", "missing")]
    [InlineData("{\"NOT_SUPPORTED\":\"fixture-only-password-do-not-log\"}", "unsupported")]
    [InlineData("{\"PLATFORM_CONNECTOR_ACCESS_SERVER_CERT_PASSWORD\":\"fixture-only-password-do-not-log\"}", "platform key unsupported in dedicated helper file")]
    [InlineData("{\"CONNECTOR_SMB_HELPER_SERVER_PFX_PASSWORD\":42}", "non-string")]
    [InlineData("{\"CONNECTOR_SMB_HELPER_SERVER_PFX_PASSWORD\":\"a\",\"CONNECTOR_SMB_HELPER_SERVER_PFX_PASSWORD\":\"b\"}", "duplicate")]
    public void MalformedOrUnsupportedSecretJsonFailsGenerically(string json, string _)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => SmbHelperServerCertificatePassword.Load(
            Options(SmbHelperServerCertificatePassword.ProtectedFilePath),
            _ => throw new InvalidOperationException("No fallback."),
            _ => Encoding.UTF8.GetBytes(json)));

        Assert.Equal("SMB helper server certificate password is unavailable.", exception.Message);
        Assert.DoesNotContain(FixturePassword, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownConfiguredSecretNameAndNoncanonicalPathFailClosed()
    {
        var readCalled = false;
        var unknownName = new SmbHelperHostOptions
        {
            ServerCertificatePasswordFilePath = SmbHelperServerCertificatePassword.ProtectedFilePath,
            ServerCertificatePasswordSecretName = "UNSUPPORTED_SECRET",
            ServerCertificatePasswordEnvironmentVariable = "CONNECTOR_SMB_HELPER_SERVER_PFX_PASSWORD",
        };
        Assert.Throws<InvalidOperationException>(() => SmbHelperServerCertificatePassword.Load(
            unknownName, _ => FixturePassword, _ => { readCalled = true; return []; }));
        var wrongPath = Options(SmbHelperServerCertificatePassword.ProtectedFilePath + "..\\outside.json");
        Assert.Throws<InvalidOperationException>(() => SmbHelperServerCertificatePassword.Load(
            wrongPath, _ => FixturePassword, _ => { readCalled = true; return []; }));
        Assert.False(readCalled);
    }

    [Fact]
    public void EnvironmentFallbackIsUsedOnlyWhenSecretPathIsAbsent()
    {
        var options = Options("");
        var password = SmbHelperServerCertificatePassword.Load(
            new SmbHelperHostOptions { ServerCertificatePasswordEnvironmentVariable = "LEGACY_TEST_PASSWORD" },
            name => name == "LEGACY_TEST_PASSWORD" ? FixturePassword : null,
            _ => throw new InvalidOperationException("File must not be read."));
        Assert.Equal(FixturePassword, password);

        Assert.Throws<InvalidOperationException>(() => SmbHelperServerCertificatePassword.Load(
            Options(SmbHelperServerCertificatePassword.ProtectedFilePath),
            _ => FixturePassword,
            _ => throw new IOException("missing file")));
    }

    [Fact]
    public void PathMustBeExactCanonicalRuntimeLocation()
    {
        Assert.True(SmbHelperServerCertificatePassword.IsCanonicalPath(
            SmbHelperServerCertificatePassword.ProtectedFilePath,
            SmbHelperServerCertificatePassword.ProtectedFilePath));
        Assert.False(SmbHelperServerCertificatePassword.IsCanonicalPath(
            @"C:\Platform\runtime\connector-access\..\service-secrets.json",
            SmbHelperServerCertificatePassword.ProtectedFilePath));
        Assert.False(SmbHelperServerCertificatePassword.IsCanonicalPath(
            @"C:\Temp\service-secrets.json",
            SmbHelperServerCertificatePassword.ProtectedFilePath));
    }

    [Fact]
    public void ProtectedAclAcceptsTrustedOnlyAndRejectsUntrustedOwnerReadWriterAndNullDacl()
    {
        SmbHelperServerCertificatePassword.ValidateAcl(NewAcl(), isFile: true);
        SmbHelperServerCertificatePassword.ValidateAcl(NewAcl(extraSid: "S-1-5-21-1-2-3-1001", extraRights: FileSystemRights.Read), isFile: false);
        SmbHelperServerCertificatePassword.ValidateAcl(NewRootAnchorAcl(), isFile: false, allowTrustedInstallerOwner: true, allowUntrustedCreateDirectories: true);
        SmbHelperServerCertificatePassword.ValidateAcl(NewRuntimeAncestorAcl(), isFile: false, requireProtected: false);
        Assert.Throws<InvalidOperationException>(() => SmbHelperServerCertificatePassword.ValidateAcl(
            NewAcl(ownerSid: "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), isFile: false));
        Assert.Throws<InvalidOperationException>(() => SmbHelperServerCertificatePassword.ValidateAcl(
            NewRootAnchorAcl(FileSystemRights.DeleteSubdirectoriesAndFiles), isFile: false, allowTrustedInstallerOwner: true, allowUntrustedCreateDirectories: true));
        Assert.Throws<InvalidOperationException>(() => SmbHelperServerCertificatePassword.ValidateAcl(
            NewAcl(extraSid: "S-1-1-0", extraRights: FileSystemRights.Read), isFile: true));
        Assert.Throws<InvalidOperationException>(() => SmbHelperServerCertificatePassword.ValidateAcl(
            NewAcl(extraSid: "S-1-1-0", extraRights: FileSystemRights.WriteData), isFile: true));
        Assert.Throws<InvalidOperationException>(() => SmbHelperServerCertificatePassword.ValidateAcl(
            GenericWriteAcl(), isFile: true));
        Assert.Throws<InvalidOperationException>(() => SmbHelperServerCertificatePassword.ValidateAcl(
            NewAcl(extraSid: "S-1-1-0", extraRights: FileSystemRights.Modify), isFile: false));
        Assert.Throws<InvalidOperationException>(() => SmbHelperServerCertificatePassword.ValidateAcl(
            NewAcl(ownerSid: "S-1-1-0"), isFile: true));
        Assert.Throws<InvalidOperationException>(() => SmbHelperServerCertificatePassword.ValidateAcl(NullDacl(), isFile: true));
    }

    private static SmbHelperHostOptions Options(string secretPath) => new()
    {
        ServerCertificatePasswordFilePath = secretPath,
        ServerCertificatePasswordSecretName = SecretName,
        ServerCertificatePasswordEnvironmentVariable = "CONNECTOR_SMB_HELPER_SERVER_PFX_PASSWORD",
    };

    private static FileSecurity NewAcl(
        string ownerSid = "S-1-5-32-544",
        string? extraSid = null,
        FileSystemRights extraRights = FileSystemRights.Read)
    {
        var acl = new FileSecurity();
        acl.SetOwner(new SecurityIdentifier(ownerSid));
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-18"), FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-544"), FileSystemRights.FullControl, AccessControlType.Allow));
        if (extraSid is not null)
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(extraSid), extraRights, AccessControlType.Allow));
        return acl;
    }

    private static DirectorySecurity NewRootAnchorAcl(FileSystemRights? untrustedRights = null)
    {
        var acl = new DirectorySecurity();
        var extraAce = untrustedRights is null ? "" : $"(A;;0x{unchecked((int)untrustedRights.Value):X8};;;AU)";
        acl.SetSecurityDescriptorSddlForm(
            "O:S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464D:P" +
            "(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x00000004;;;AU)" +
            "(A;OICIIO;GA;;;CO)(A;OICIIO;0x00000002;;;AU)" + extraAce);
        return acl;
    }

    private static DirectorySecurity NewRuntimeAncestorAcl()
    {
        var acl = new DirectorySecurity();
        acl.SetSecurityDescriptorSddlForm("O:BAD:(A;ID;FA;;;SY)(A;ID;FA;;;BA)(A;ID;GR;;;AU)");
        return acl;
    }

    private static FileSecurity NullDacl()
    {
        var acl = new FileSecurity();
        acl.SetSecurityDescriptorSddlForm("O:BAG:BAD:NO_ACCESS_CONTROL");
        return acl;
    }

    private static FileSecurity GenericWriteAcl()
    {
        var owner = new SecurityIdentifier("S-1-5-32-544");
        var acl = new RawAcl(GenericAcl.AclRevision, 1);
        acl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, unchecked((int)0x40000000), new SecurityIdentifier("S-1-1-0"), false, null));
        var descriptor = new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected | ControlFlags.SelfRelative,
            owner, owner, null, acl);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        var file = new FileSecurity();
        file.SetSecurityDescriptorBinaryForm(bytes);
        return file;
    }
}
