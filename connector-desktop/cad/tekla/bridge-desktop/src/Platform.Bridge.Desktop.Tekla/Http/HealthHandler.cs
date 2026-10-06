// GET /health — public, без auth, без чувствительных данных. См. plan §7.2.
// Минимум: ok + bridgeVersion + teklaConnected (boolean snapshot).

#nullable enable

using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Platform.Bridge.Desktop.Tekla.Tekla;

namespace Platform.Bridge.Desktop.Tekla.Http
{
    public sealed class HealthHandler
    {
        private readonly TeklaWorker _worker;
        private readonly string _bridgeVersion;
        private readonly string _teklaVersion;
        private readonly string? _teklaSession;

        public HealthHandler(TeklaWorker worker, string teklaVersion, string? teklaSession)
        {
            _worker = worker;
            _teklaVersion = teklaVersion;
            _teklaSession = teklaSession;
            _bridgeVersion = typeof(HealthHandler).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(HealthHandler).Assembly.GetName().Version?.ToString()
                ?? "0.0.0";
        }

        public Task<HttpResult> HandleAsync(RequestContext request, CancellationToken ct)
        {
            // Keep health strictly process-level. A Tekla API probe here can
            // block the worker queue and make every connector operation look
            // dead while Tekla is busy or slow to reconnect.
            return Task.FromResult(HttpResult.Ok(new
            {
                ok = true,
                bridgeVersion = _bridgeVersion,
                teklaConnected = _worker.LastKnownConnected,
                teklaProbeSkipped = true,
                teklaVersion = _teklaVersion,
                teklaApiVersion = typeof(global::Tekla.Structures.Model.Model).Assembly.GetName().Version?.ToString(),
                teklaSession = _teklaSession,
            }));
        }
    }
}
