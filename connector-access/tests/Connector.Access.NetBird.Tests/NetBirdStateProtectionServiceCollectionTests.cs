using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Connector.Access.NetBird.Tests;

public sealed class NetBirdStateProtectionServiceCollectionTests
{
    [Fact]
    public void RegistrationRejectsMissingPasswordBeforeAddingServices()
    {
        var root = CreateFixtureRoot();
        var passwordEnvironmentVariable = UniqueEnvironmentVariable();
        var certificatePath = CreateCertificate(root, "test-password");
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddNetBirdConnectorAccess(
                ProviderOptions(root),
                ProtectionOptions(root, certificatePath, passwordEnvironmentVariable)));

        Assert.Contains("password environment variable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(services);
    }

    [Fact]
    public void RegistrationRejectsMissingAndCorruptCertificatesBeforeAddingServices()
    {
        var root = CreateFixtureRoot();
        var passwordEnvironmentVariable = UniqueEnvironmentVariable();
        Environment.SetEnvironmentVariable(passwordEnvironmentVariable, "test-password");
        try
        {
            var missingServices = new ServiceCollection();
            Assert.Throws<InvalidOperationException>(() =>
                missingServices.AddNetBirdConnectorAccess(
                    ProviderOptions(root),
                    ProtectionOptions(root, Path.Combine(root, "missing.pfx"), passwordEnvironmentVariable)));
            Assert.Empty(missingServices);

            var corruptCertificatePath = Path.Combine(root, "corrupt.pfx");
            File.WriteAllText(corruptCertificatePath, "not a certificate");
            var corruptServices = new ServiceCollection();
            Assert.Throws<InvalidOperationException>(() =>
                corruptServices.AddNetBirdConnectorAccess(
                    ProviderOptions(root),
                    ProtectionOptions(root, corruptCertificatePath, passwordEnvironmentVariable)));
            Assert.Empty(corruptServices);
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordEnvironmentVariable, null);
        }
    }

    [Fact]
    public void RecreatedProviderUnprotectsPayloadFromPersistentKeyRing()
    {
        var root = CreateFixtureRoot();
        var passwordEnvironmentVariable = UniqueEnvironmentVariable();
        const string password = "test-password";
        var certificatePath = CreateCertificate(root, password);
        Environment.SetEnvironmentVariable(passwordEnvironmentVariable, password);
        try
        {
            var protectionOptions = ProtectionOptions(root, certificatePath, passwordEnvironmentVariable);
            string payload;
            using (var first = BuildProvider(root, protectionOptions))
            {
                payload = NetBirdProtector(first).Protect("setup-key-secret");
            }

            using var second = BuildProvider(root, protectionOptions);
            Assert.Equal("setup-key-secret", NetBirdProtector(second).Unprotect(payload));
            Assert.NotEmpty(Directory.EnumerateFiles(
                protectionOptions.DataProtectionKeyDirectory,
                "*.xml",
                SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordEnvironmentVariable, null);
        }
    }

    [Fact]
    public void RegistrationRejectsPreexistingPlaintextKeyBeforeAddingServices()
    {
        var root = CreateFixtureRoot();
        var keyDirectory = Path.Combine(root, "netbird-keys");
        var plaintextProvider = DataProtectionProvider.Create(
            new DirectoryInfo(keyDirectory),
            builder => builder.SetApplicationName("Connector.Access.NetBird"));
        _ = plaintextProvider.CreateProtector("fixture").Protect("fixture-secret");

        var passwordEnvironmentVariable = UniqueEnvironmentVariable();
        const string password = "test-password";
        var certificatePath = CreateCertificate(root, password);
        Environment.SetEnvironmentVariable(passwordEnvironmentVariable, password);
        try
        {
            var services = new ServiceCollection();
            var exception = Assert.Throws<InvalidOperationException>(() =>
                services.AddNetBirdConnectorAccess(
                    ProviderOptions(root),
                    ProtectionOptions(root, certificatePath, passwordEnvironmentVariable)));

            Assert.Contains("unencrypted key file", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(services);
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordEnvironmentVariable, null);
        }
    }

    [Fact]
    public void RegistrationRejectsKeyRingEncryptedByDifferentCertificateBeforeAddingServices()
    {
        var root = CreateFixtureRoot();
        var passwordEnvironmentVariable = UniqueEnvironmentVariable();
        const string password = "test-password";
        var certificatePath = CreateCertificate(root, password);
        Environment.SetEnvironmentVariable(passwordEnvironmentVariable, password);
        try
        {
            var protectionOptions = ProtectionOptions(root, certificatePath, passwordEnvironmentVariable);
            using (var first = BuildProvider(root, protectionOptions))
            {
                _ = NetBirdProtector(first).Protect("fixture-secret");
            }

            CreateCertificate(root, password);
            var services = new ServiceCollection();
            var exception = Assert.Throws<InvalidOperationException>(() =>
                services.AddNetBirdConnectorAccess(ProviderOptions(root), protectionOptions));

            Assert.Contains("cannot be decrypted", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(services);
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordEnvironmentVariable, null);
        }
    }

    [Fact]
    public void SmbStyleGlobalProviderDoesNotReplaceOrBackNetBirdProvider()
    {
        var root = CreateFixtureRoot();
        var passwordEnvironmentVariable = UniqueEnvironmentVariable();
        const string password = "test-password";
        var certificatePath = CreateCertificate(root, password);
        Environment.SetEnvironmentVariable(passwordEnvironmentVariable, password);
        try
        {
            var services = new ServiceCollection();
            services.AddDataProtection()
                .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "smb-keys")))
                .SetApplicationName("Connector.Access.Smb");
            var globalProviderRegistrations = services.Count(descriptor =>
                descriptor.ServiceType == typeof(IDataProtectionProvider));

            services.AddNetBirdConnectorAccess(
                ProviderOptions(root),
                ProtectionOptions(root, certificatePath, passwordEnvironmentVariable));

            Assert.Equal(globalProviderRegistrations, services.Count(descriptor =>
                descriptor.ServiceType == typeof(IDataProtectionProvider)));
            using var provider = services.BuildServiceProvider();
            var globalProvider = provider.GetRequiredService<IDataProtectionProvider>();
            var netBirdProvider = provider
                .GetRequiredService<ServiceCollectionExtensions.NetBirdStateProtectionContext>()
                .Provider;
            Assert.NotSame(globalProvider, netBirdProvider);

            var protectedSetupKey = netBirdProvider
                .CreateProtector("Connector.Access.NetBird.SetupKey.v1")
                .Protect("setup-key-secret");
            Assert.Throws<CryptographicException>(() => globalProvider
                .CreateProtector("Connector.Access.NetBird.SetupKey.v1")
                .Unprotect(protectedSetupKey));
        }
        finally
        {
            Environment.SetEnvironmentVariable(passwordEnvironmentVariable, null);
        }
    }

    private static ServiceProvider BuildProvider(
        string root,
        NetBirdStateProtectionOptions protectionOptions)
    {
        var services = new ServiceCollection();
        services.AddNetBirdConnectorAccess(ProviderOptions(root), protectionOptions);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    private static IDataProtector NetBirdProtector(ServiceProvider provider) => provider
        .GetRequiredService<ServiceCollectionExtensions.NetBirdStateProtectionContext>()
        .Provider
        .CreateProtector("Connector.Access.NetBird.SetupKey.v1");

    private static NetBirdOptions ProviderOptions(string root) => new()
    {
        ManagementUri = new Uri("https://netbird.example.test/"),
        AccessToken = "test-access-token",
        StateDirectory = Path.Combine(root, "state")
    };

    private static NetBirdStateProtectionOptions ProtectionOptions(
        string root,
        string certificatePath,
        string passwordEnvironmentVariable) => new()
    {
        DataProtectionKeyDirectory = Path.Combine(root, "netbird-keys"),
        DataProtectionCertificatePfxPath = certificatePath,
        DataProtectionCertificatePasswordEnvironmentVariable = passwordEnvironmentVariable
    };

    private static string CreateFixtureRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "connector-access-netbird-state-protection-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateCertificate(string root, string password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Connector.Access.NetBird.Tests",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        var path = Path.Combine(root, "state-protection.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        return path;
    }

    private static string UniqueEnvironmentVariable() =>
        "CONNECTOR_NETBIRD_DP_TEST_" + Guid.NewGuid().ToString("N");
}
