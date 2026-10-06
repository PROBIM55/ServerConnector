// POST /plugins/fachwerk/reload
// Runs the Tekla-side development macro on the worker thread so plugin/dialog
// refresh can be driven without taking control of the Tekla UI.

#nullable enable

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Platform.Bridge.Desktop.Tekla.Tekla;
using Tekla.Structures.Model.Operations;

namespace Platform.Bridge.Desktop.Tekla.Http
{
    public sealed class PluginReloadHandler
    {
        private const string FachwerkReloadMacro = @"..\modeling\ReloadFachwerkPlugin.cs";
        private readonly TeklaWorker _worker;

        public PluginReloadHandler(TeklaWorker worker)
        {
            _worker = worker;
        }

        public async Task<HttpResult> ReloadFachwerkAsync(RequestContext request, CancellationToken ct)
        {
            try
            {
                var result = await _worker.RunAsync(_ => RunAndWait(ct), ct);
                return result.Started
                    ? HttpResult.Ok(new
                    {
                        ok = true,
                        macro = FachwerkReloadMacro,
                        completed = result.Completed,
                        elapsedMilliseconds = result.ElapsedMilliseconds
                    })
                    : HttpResult.ServerError(
                        "TEKLA_MACRO_NOT_STARTED",
                        $"Tekla did not start macro '{FachwerkReloadMacro}'.");
            }
            catch (TeklaDisconnectedException ex)
            {
                return HttpResult.ServiceUnavailable("TEKLA_DISCONNECTED", ex.Message);
            }
            catch (TeklaStaleHandleException ex)
            {
                return HttpResult.ServiceUnavailable("BRIDGE_STALE_TEKLA", ex.Message);
            }
            catch (Exception ex)
            {
                return HttpResult.ServerError("TEKLA_PLUGIN_RELOAD_FAILED", ex.Message);
            }
        }

        private static ReloadResult RunAndWait(CancellationToken ct)
        {
            var stopwatch = Stopwatch.StartNew();
            var started = Operation.RunMacro(FachwerkReloadMacro);
            if (!started)
            {
                return new ReloadResult(false, false, stopwatch.ElapsedMilliseconds);
            }

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (Operation.IsMacroRunning() && DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(100);
            }

            return new ReloadResult(
                true,
                !Operation.IsMacroRunning(),
                stopwatch.ElapsedMilliseconds);
        }

        private sealed record ReloadResult(bool Started, bool Completed, long ElapsedMilliseconds);
    }
}
