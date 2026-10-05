namespace Platform.Connector.Core;

/// <summary>Minimal immutable registration contract shared by executable modules.</summary>
public sealed record SharedModuleDescriptor
{
    public SharedModuleDescriptor(
        string moduleId,
        string displayName,
        IEnumerable<ConnectorProductId> productIds,
        IEnumerable<string> executorIds)
    {
        ModuleId = NormalizeRequired(moduleId, nameof(moduleId));
        DisplayName = NormalizeRequired(displayName, nameof(displayName));

        var products = productIds?.Distinct().ToArray()
            ?? throw new ArgumentNullException(nameof(productIds));
        if (products.Length == 0 || products.Any(product => !Enum.IsDefined(product)))
        {
            throw new ArgumentException("At least one valid product id is required.", nameof(productIds));
        }

        var executors = executorIds?
            .Select(executorId => NormalizeRequired(executorId, nameof(executorIds)).ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? throw new ArgumentNullException(nameof(executorIds));
        if (executors.Length == 0)
        {
            throw new ArgumentException("At least one executor id is required.", nameof(executorIds));
        }

        ProductIds = Array.AsReadOnly(products);
        ExecutorIds = Array.AsReadOnly(executors);
    }

    public string ModuleId { get; }
    public string DisplayName { get; }
    public IReadOnlyList<ConnectorProductId> ProductIds { get; }
    public IReadOnlyList<string> ExecutorIds { get; }

    private static string NormalizeRequired(string? value, string parameterName)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? throw new ArgumentException("Value is required.", parameterName)
            : normalized;
    }
}

public sealed class SharedModuleCatalog
{
    private readonly IReadOnlyDictionary<string, SharedModuleDescriptor> _byModuleId;
    private readonly IReadOnlyDictionary<string, SharedModuleDescriptor> _byExecutorId;

    public SharedModuleCatalog(IEnumerable<SharedModuleDescriptor>? descriptors = null)
    {
        var modules = new Dictionary<string, SharedModuleDescriptor>(StringComparer.OrdinalIgnoreCase);
        var executors = new Dictionary<string, SharedModuleDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach (var descriptor in descriptors ?? [])
        {
            if (!modules.TryAdd(descriptor.ModuleId, descriptor))
            {
                throw new InvalidOperationException($"Module '{descriptor.ModuleId}' is registered more than once.");
            }
            foreach (var executorId in descriptor.ExecutorIds)
            {
                if (!executors.TryAdd(executorId, descriptor))
                {
                    throw new InvalidOperationException($"Executor '{executorId}' is registered by more than one module.");
                }
            }
        }

        _byModuleId = modules;
        _byExecutorId = executors;
        Descriptors = Array.AsReadOnly(modules.Values.ToArray());
    }

    public IReadOnlyList<SharedModuleDescriptor> Descriptors { get; }

    public bool TryGetModule(string moduleId, out SharedModuleDescriptor descriptor)
        => _byModuleId.TryGetValue(moduleId, out descriptor!);

    public bool TryGetByExecutor(string executorId, out SharedModuleDescriptor descriptor)
        => _byExecutorId.TryGetValue(executorId, out descriptor!);
}
