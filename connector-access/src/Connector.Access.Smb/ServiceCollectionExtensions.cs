using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using Connector.Access;
using System.Security.Cryptography.X509Certificates;

namespace Connector.Access.Smb;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSmbConnectorAccess(this IServiceCollection services, SmbProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var validated = options.Validate();
        var keyPassword = Environment.GetEnvironmentVariable(validated.DataProtectionCertificatePasswordEnvironmentVariable);
        if (string.IsNullOrEmpty(keyPassword))
            throw new InvalidOperationException("SMB Data Protection certificate password environment variable is unavailable.");
#pragma warning disable SYSLIB0057
        var keyCertificate = new X509Certificate2(validated.DataProtectionCertificatePfxPath, keyPassword,
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.EphemeralKeySet);
#pragma warning restore SYSLIB0057
        if (!keyCertificate.HasPrivateKey)
            throw new InvalidOperationException("SMB Data Protection certificate has no private key.");
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(options.DataProtectionKeyDirectory))
            .ProtectKeysWithCertificate(keyCertificate)
            .SetApplicationName("Connector.Access.Smb");
        services.AddSingleton(options);
        services.AddSingleton(_ => SmbHelperClient.CreateMutualTlsClient(options));
        services.AddSingleton<SmbDeviceAccessGrantProvider>();
        services.AddSingleton<IDeviceAccessGrantProvider>(provider => provider.GetRequiredService<SmbDeviceAccessGrantProvider>());
        services.AddSingleton<IConnectorSmbAccessReader>(provider => provider.GetRequiredService<SmbDeviceAccessGrantProvider>());
        return services;
    }
}
