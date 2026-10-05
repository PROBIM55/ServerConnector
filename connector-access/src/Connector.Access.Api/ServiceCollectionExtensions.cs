using Microsoft.Extensions.DependencyInjection;

namespace Connector.Access.Api;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApiConnectorAccess(this IServiceCollection services, ApiAccessOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        var provider = new ApiDeviceAccessGrantProvider(options);
        services.AddSingleton(provider);
        services.AddSingleton<IDeviceAccessGrantProvider>(provider);
        services.AddSingleton<IConnectorApiAccessPolicyReader>(provider);
        return services;
    }
}
