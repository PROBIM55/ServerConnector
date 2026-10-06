// Registry адаптеров по паре (componentType, schemaVersion).
// Резолвит запросы на правильный адаптер; capabilities-сводка для /capabilities.

#nullable enable

using System;
using System.Collections.Generic;

namespace Platform.Bridge.Desktop.Tekla.ComponentRuntime
{
    public sealed class ComponentAdapterRegistry
    {
        private readonly Dictionary<(string Type, int Version), ITeklaComponentAdapter> _adapters
            = new();

        public void Register(ITeklaComponentAdapter adapter)
        {
            if (adapter is null) throw new ArgumentNullException(nameof(adapter));
            var key = (adapter.ComponentType, adapter.SchemaVersion);
            if (_adapters.ContainsKey(key))
                throw new InvalidOperationException(
                    $"Adapter for ({adapter.ComponentType} v{adapter.SchemaVersion}) is already registered.");
            _adapters[key] = adapter;
        }

        public bool TryResolve(string componentType, int schemaVersion, out ITeklaComponentAdapter adapter)
        {
            return _adapters.TryGetValue((componentType, schemaVersion), out adapter!);
        }

        /// <summary>Все зарегистрированные адаптеры — для /capabilities снапшота.</summary>
        public IEnumerable<ITeklaComponentAdapter> All => _adapters.Values;
    }
}
