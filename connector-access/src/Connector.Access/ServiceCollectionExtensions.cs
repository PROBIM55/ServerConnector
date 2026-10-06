using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Connector.Access;

public static class ServiceCollectionExtensions
{
    // Platform.Server supplies IPlatformAccessDirectory,
    // IDeviceAccessDbConnectionFactory and IX509DeviceCertificateIssuer.
    public static IServiceCollection AddConnectorAccessCore(
        this IServiceCollection services,
        Action<DeviceAccessOptions>? configure = null)
    {
        if (configure is null)
        {
            services.AddOptions<DeviceAccessOptions>();
        }
        else
        {
            services.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped(provider => new DbDeviceAccessRepository(
            provider.GetRequiredService<IDeviceAccessDbConnectionFactory>()));
        services.TryAddScoped(provider => new DbProviderOutboxRepository(
            provider.GetRequiredService<IDeviceAccessDbConnectionFactory>()));
        services.TryAddScoped(provider => new DeviceEnrollmentService(
            provider.GetRequiredService<DbDeviceAccessRepository>(),
            provider.GetRequiredService<IPlatformAccessDirectory>(),
            provider.GetRequiredService<IX509DeviceCertificateIssuer>(),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DeviceAccessOptions>>(),
            provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped(provider => new DeviceCertificateAuthenticator(
            provider.GetRequiredService<DbDeviceAccessRepository>(),
            provider.GetRequiredService<IPlatformAccessDirectory>(),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DeviceAccessOptions>>(),
            provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped(provider => new DeviceAccessProfileService(
            provider.GetRequiredService<DbDeviceAccessRepository>(),
            provider.GetRequiredService<DbProviderOutboxRepository>(),
            provider.GetRequiredService<IPlatformAccessDirectory>(),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DeviceAccessOptions>>(),
            provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped(provider => new DeviceAccessAdministrationService(
            provider.GetRequiredService<DbDeviceAccessRepository>(),
            provider.GetRequiredService<DbProviderOutboxRepository>(),
            provider.GetRequiredService<IPlatformAccessDirectory>(),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DeviceAccessOptions>>(),
            provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped(provider => new DeviceSelfRevocationService(
            provider.GetRequiredService<DbDeviceAccessRepository>(),
            provider.GetRequiredService<DbProviderOutboxRepository>(),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DeviceAccessOptions>>(),
            provider.GetRequiredService<TimeProvider>()));
        services.TryAddScoped(provider => new DeviceAccessOutboxProcessor(
            provider.GetRequiredService<DbProviderOutboxRepository>(),
            provider.GetServices<IDeviceAccessGrantProvider>(),
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<DeviceAccessOptions>>(),
            provider.GetRequiredService<TimeProvider>()));
        return services;
    }
}
