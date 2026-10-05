using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Connector.Access.NetBird;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNetBirdConnectorAccess(
        this IServiceCollection services,
        NetBirdOptions options,
        NetBirdStateProtectionOptions stateProtectionOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stateProtectionOptions);

        var validated = options.Validate();
        var validatedStateProtection = stateProtectionOptions.Validate();
        var stateProtection = NetBirdStateProtectionContext.CreatePersistent(validatedStateProtection);

        return AddNetBirdConnectorAccess(services, options, validated, stateProtection);
    }

    internal static IServiceCollection AddNetBirdConnectorAccess(
        this IServiceCollection services,
        NetBirdOptions options,
        IDataProtectionProvider stateProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(stateProtectionProvider);

        return AddNetBirdConnectorAccess(
            services,
            options,
            options.Validate(),
            NetBirdStateProtectionContext.Borrow(stateProtectionProvider));
    }

    private static IServiceCollection AddNetBirdConnectorAccess(
        IServiceCollection services,
        NetBirdOptions options,
        ValidatedNetBirdOptions validated,
        NetBirdStateProtectionContext stateProtection)
    {
        services.AddSingleton(_ => stateProtection);
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient("Connector.Access.NetBird", client =>
        {
            client.Timeout = validated.ManagementRequestTimeout;
        });
        services.AddSingleton(options);
        var providerRegistration = ServiceDescriptor.Singleton(provider => new NetBirdDeviceAccessGrantProvider(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("Connector.Access.NetBird"),
            provider.GetRequiredService<NetBirdOptions>(),
            provider.GetRequiredService<NetBirdStateProtectionContext>().Provider,
            provider.GetRequiredService<TimeProvider>()));
        services.Add(providerRegistration);
        services.AddSingleton<IDeviceAccessGrantProvider>(provider =>
            provider.GetRequiredService<NetBirdDeviceAccessGrantProvider>());
        var readerRegistration = ServiceDescriptor.Singleton<IConnectorVpnBootstrapReader>(provider =>
            provider.GetRequiredService<NetBirdDeviceAccessGrantProvider>());
        services.Add(readerRegistration);
        services.AddSingleton(new NetBirdConnectorAccessRegistration(
            providerRegistration,
            readerRegistration));
        return services;
    }

    /// <summary>
    /// Adds the private-overlay peer attestation dependencies after <see cref="AddNetBirdConnectorAccess"/>.
    /// This registration deliberately does not map an HTTP endpoint.
    /// </summary>
    public static IServiceCollection AddNetBirdPeerAttestation(
        this IServiceCollection services,
        NetBirdPeerAttestationOptions attestationOptions,
        NetBirdDaemonPeerSnapshotOptions daemonSnapshotOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(attestationOptions);
        ArgumentNullException.ThrowIfNull(daemonSnapshotOptions);

        if (!attestationOptions.TryValidate(out _) ||
            !daemonSnapshotOptions.TryValidate(out _))
        {
            throw new ArgumentException("NetBird peer attestation options are invalid.");
        }

        var registration = services.LastOrDefault(descriptor =>
            descriptor.ServiceType == typeof(NetBirdConnectorAccessRegistration))?.ImplementationInstance
            as NetBirdConnectorAccessRegistration;
        if (registration is null ||
            !ReferenceEquals(
                services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(NetBirdDeviceAccessGrantProvider)),
                registration.ProviderRegistration) ||
            !ReferenceEquals(
                services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IConnectorVpnBootstrapReader)),
                registration.ReaderRegistration))
        {
            throw new InvalidOperationException(
                "AddNetBirdConnectorAccess must be called before AddNetBirdPeerAttestation.");
        }

        services.AddSingleton(attestationOptions);
        services.AddSingleton(daemonSnapshotOptions);
        services.AddSingleton<NetBirdDaemonPeerSnapshotReader>();
        services.AddSingleton<INetBirdDaemonPeerSnapshotReader>(provider =>
            provider.GetRequiredService<NetBirdDaemonPeerSnapshotReader>());
        services.AddSingleton<NetBirdPeerAttestationService>();
        services.AddSingleton<INetBirdPeerAttestationService>(provider =>
            provider.GetRequiredService<NetBirdPeerAttestationService>());
        return services;
    }

    private sealed record NetBirdConnectorAccessRegistration(
        ServiceDescriptor ProviderRegistration,
        ServiceDescriptor ReaderRegistration);

    internal sealed class NetBirdStateProtectionContext : IDisposable
    {
        private const string ApplicationName = "Connector.Access.NetBird";
        private const long MaximumKeyFileLength = 1024 * 1024;
        private readonly IDisposable? _ownedProvider;
        private readonly X509Certificate2? _ownedCertificate;

        private NetBirdStateProtectionContext(
            IDataProtectionProvider provider,
            IDisposable? ownedProvider,
            X509Certificate2? ownedCertificate)
        {
            Provider = provider;
            _ownedProvider = ownedProvider;
            _ownedCertificate = ownedCertificate;
        }

        internal IDataProtectionProvider Provider { get; }

        internal static NetBirdStateProtectionContext Borrow(IDataProtectionProvider provider) =>
            new(provider, null, null);

        internal static NetBirdStateProtectionContext CreatePersistent(
            ValidatedNetBirdStateProtectionOptions options)
        {
            var password = Environment.GetEnvironmentVariable(
                options.DataProtectionCertificatePasswordEnvironmentVariable);
            if (string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException(
                    "NetBird Data Protection certificate password environment variable is unavailable.");
            }

            X509Certificate2? certificate = null;
            try
            {
                if (!File.Exists(options.DataProtectionCertificatePfxPath))
                {
                    throw new InvalidOperationException(
                        "NetBird Data Protection certificate PFX file is unavailable.");
                }

#pragma warning disable SYSLIB0057
                certificate = new X509Certificate2(
                    options.DataProtectionCertificatePfxPath,
                    password,
                    X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.EphemeralKeySet);
#pragma warning restore SYSLIB0057
                if (!certificate.HasPrivateKey)
                {
                    throw new InvalidOperationException(
                        "NetBird Data Protection certificate has no private key.");
                }

                using (var privateKey = certificate.GetRSAPrivateKey())
                {
                    if (privateKey is null)
                    {
                        throw new InvalidOperationException(
                            "NetBird Data Protection certificate must expose an RSA private key.");
                    }
                }

                Directory.CreateDirectory(options.DataProtectionKeyDirectory);
                var expectedKeyIds = ValidateEncryptedKeyFiles(options.DataProtectionKeyDirectory);
                var isolatedServices = new ServiceCollection();
                isolatedServices.AddDataProtection()
                    .PersistKeysToFileSystem(new DirectoryInfo(options.DataProtectionKeyDirectory))
                    .ProtectKeysWithCertificate(certificate)
                    .SetApplicationName(ApplicationName);
                var isolatedProvider = isolatedServices.BuildServiceProvider(new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true
                });
                try
                {
                    ValidateReadableKeys(
                        expectedKeyIds,
                        isolatedProvider.GetRequiredService<IKeyManager>());
                    return new NetBirdStateProtectionContext(
                        isolatedProvider.GetRequiredService<IDataProtectionProvider>(),
                        isolatedProvider,
                        certificate);
                }
                catch
                {
                    isolatedProvider.Dispose();
                    throw;
                }
            }
            catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
            {
                certificate?.Dispose();
                throw new InvalidOperationException(
                    "NetBird Data Protection certificate could not be loaded.",
                    exception);
            }
            catch
            {
                certificate?.Dispose();
                throw;
            }
        }

        private static HashSet<Guid> ValidateEncryptedKeyFiles(string keyDirectory)
        {
            var keyIds = new HashSet<Guid>();
            foreach (var path in Directory.EnumerateFiles(
                         keyDirectory,
                         "key-*.xml",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var file = new FileInfo(path);
                    if (file.Length is <= 0 or > MaximumKeyFileLength)
                    {
                        throw new InvalidDataException("The Data Protection key file size is invalid.");
                    }

                    using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        8192,
                        FileOptions.SequentialScan);
                    using var reader = XmlReader.Create(stream, new XmlReaderSettings
                    {
                        DtdProcessing = DtdProcessing.Prohibit,
                        XmlResolver = null,
                        MaxCharactersInDocument = MaximumKeyFileLength
                    });
                    var document = XDocument.Load(reader, LoadOptions.None);
                    var root = document.Root;
                    var idText = root?.Attribute("id")?.Value;
                    var fileIdText = Path.GetFileNameWithoutExtension(path)["key-".Length..];
                    if (root?.Name.LocalName != "key" ||
                        !Guid.TryParse(idText, out var keyId) ||
                        !Guid.TryParse(fileIdText, out var fileKeyId) ||
                        keyId != fileKeyId ||
                        !keyIds.Add(keyId))
                    {
                        throw new InvalidDataException("The Data Protection key identity is invalid.");
                    }

                    var encryptedSecrets = root
                        .Descendants()
                        .Where(element => element.Name.LocalName == "encryptedSecret")
                        .ToArray();
                    if (encryptedSecrets.Length != 1 ||
                        !encryptedSecrets[0].Descendants().Any(element =>
                            element.Name.LocalName == "EncryptedData") ||
                        root.Descendants().Any(element => element.Name.LocalName == "masterKey"))
                    {
                        throw new InvalidDataException(
                            "The Data Protection key is not protected by an encrypted envelope.");
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or XmlException or InvalidDataException)
                {
                    throw new InvalidOperationException(
                        $"NetBird Data Protection key ring contains an unreadable or unencrypted key file: {Path.GetFileName(path)}.",
                        exception);
                }
            }

            return keyIds;
        }

        private static void ValidateReadableKeys(
            HashSet<Guid> expectedKeyIds,
            IKeyManager keyManager)
        {
            try
            {
                var keys = keyManager.GetAllKeys().ToArray();
                var actualKeyIds = keys.Select(key => key.KeyId).ToHashSet();
                if (!actualKeyIds.SetEquals(expectedKeyIds))
                {
                    throw new InvalidDataException(
                        "The Data Protection key manager did not load every persisted key.");
                }

                foreach (var key in keys)
                {
                    _ = key.CreateEncryptor() ?? throw new CryptographicException(
                        $"Data Protection key {key.KeyId} did not create an encryptor.");
                }
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "NetBird Data Protection key ring cannot be decrypted with the configured certificate.",
                    exception);
            }
        }

        public void Dispose()
        {
            _ownedProvider?.Dispose();
            _ownedCertificate?.Dispose();
        }
    }
}
