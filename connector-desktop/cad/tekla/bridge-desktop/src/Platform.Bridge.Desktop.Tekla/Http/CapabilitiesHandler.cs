// GET /capabilities — authenticated, отдаёт полную картину (см. plan §7.3).
// Список зарегистрированных адаптеров + текущее состояние Tekla модели.

#nullable enable

using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Platform.Bridge.Desktop.Tekla.ConstructiveRuntime;
using Platform.Bridge.Desktop.Tekla.ComponentRuntime;
using Platform.Bridge.Desktop.Tekla.Tekla;

namespace Platform.Bridge.Desktop.Tekla.Http
{
    public sealed class CapabilitiesHandler
    {
        private readonly TeklaWorker _worker;
        private readonly ComponentAdapterRegistry _registry;
        private readonly string _bridgeVersion;
        private readonly string _teklaVersion;
        private readonly string? _teklaSession;
        private readonly bool _exposeModelPath;
        private readonly TeklaNativeCapabilityProvider _constructiveCapabilities;

        public CapabilitiesHandler(TeklaWorker worker, ComponentAdapterRegistry registry,
                                   string teklaVersion, string? teklaSession, bool exposeModelPath,
                                   TeklaNativeCapabilityProvider constructiveCapabilities)
        {
            _worker = worker;
            _registry = registry;
            _teklaVersion = teklaVersion;
            _teklaSession = teklaSession;
            _exposeModelPath = exposeModelPath;
            _constructiveCapabilities = constructiveCapabilities;
            _bridgeVersion = typeof(CapabilitiesHandler).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(CapabilitiesHandler).Assembly.GetName().Version?.ToString()
                ?? "0.0.0";
        }

        public async Task<HttpResult> HandleAsync(RequestContext request, CancellationToken ct)
        {
            bool connected;
            string? modelName = null;
            string? modelPath = null;
            string? modelFingerprint = null;

            try
            {
                (connected, modelName, modelPath, modelFingerprint) = await _worker.RunAsync(model =>
                {
                    var c = model is not null;
                    var mn = c ? SafeName(model!) : null;
                    var actualPath = c ? SafePath(model!) : null;
                    var mp = c && _exposeModelPath ? actualPath : null;
                    var fingerprint = c
                        ? TeklaModelIdentity.ComputeFingerprint(_teklaVersion, mn ?? string.Empty, actualPath ?? string.Empty)
                        : null;
                    return (c, mn, mp, fingerprint);
                }, ct);
            }
            catch (TeklaDisconnectedException)
            {
                connected = false;
            }
            catch
            {
                connected = false;
            }

            // Adapter capabilities — schema/operations/plugin-DLL existence — НЕ
            // зависят от Tekla running state. BridgeGirder и Pier адаптеры
            // проверяют наличие плагина по file system, а не через Open API.
            // Передаём null чтобы избежать второго worker round-trip; адаптер
            // обязан корректно обрабатывать null model (interface signature
            // Model? model — это явный контракт).
            var capabilities = _registry.All
                .Select(a => a.GetCapabilities(model: null))
                .ToList();

            return HttpResult.Ok(new
            {
                ok = true,
                bridgeVersion = _bridgeVersion,
                provider = "tekla",
                tekla = new
                {
                    connected,
                    version = _teklaVersion,
                    apiVersion = typeof(global::Tekla.Structures.Model.Model).Assembly.GetName().Version?.ToString(),
                    session = _teklaSession,
                    modelName,
                    modelPath, // null если не connected или exposeModelPath=false
                    modelFingerprint,
                },
                supportedComponents = capabilities,
                constructivePlans = _constructiveCapabilities.Describe(),
            });
        }

        private static string? SafeName(global::Tekla.Structures.Model.Model m)
        {
            try { return m.GetInfo()?.ModelName; } catch { return null; }
        }
        private static string? SafePath(global::Tekla.Structures.Model.Model m)
        {
            try { return m.GetInfo()?.ModelPath; } catch { return null; }
        }
    }
}
