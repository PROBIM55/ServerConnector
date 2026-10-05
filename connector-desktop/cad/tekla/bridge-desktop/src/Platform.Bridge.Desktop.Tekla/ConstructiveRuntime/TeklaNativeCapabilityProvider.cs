#nullable enable

using System.Reflection;
using Platform.Contracts.TeklaPlan;

namespace Platform.Bridge.Desktop.Tekla.ConstructiveRuntime
{
    public sealed class TeklaNativeCapabilityProvider
    {
        private readonly TeklaRuntimeCapabilities _runtime;
        private readonly TeklaNativeCommandRegistry _nativeExecutors;

        public TeklaNativeCapabilityProvider(
            string teklaVersion,
            TeklaNativeCommandRegistry nativeExecutors)
        {
            _nativeExecutors = nativeExecutors;
            var adapterVersion = typeof(TeklaNativeCapabilityProvider).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(TeklaNativeCapabilityProvider).Assembly.GetName().Version?.ToString()
                ?? "0.0.0";
            _runtime = nativeExecutors.SharedRegistry.CreateRuntimeCapabilities(
                adapterId: "tekla-native",
                adapterVersion,
                teklaVersion);
        }

        public TeklaRuntimeCapabilities Runtime => _runtime;
        public TeklaPlanExecutorRegistry Executors => _nativeExecutors.SharedRegistry;
        public TeklaNativeCommandRegistry NativeExecutors => _nativeExecutors;

        public object Describe() => new
        {
            schemaVersions = new[] { TeklaPlanContract.SchemaVersion },
            prepare = true,
            apply = _nativeExecutors.HasApplyReadyExecutors,
            registeredExecutors = _nativeExecutors.SharedRegistry.All.Count,
            adapterId = _runtime.AdapterId,
            adapterVersion = _runtime.AdapterVersion,
            teklaVersion = _runtime.TeklaVersion,
            capabilities = _runtime.Flags,
        };
    }
}
