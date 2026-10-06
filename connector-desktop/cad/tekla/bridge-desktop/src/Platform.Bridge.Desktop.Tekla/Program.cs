// Bridge.Desktop.Tekla — long-running daemon на ПК пользователя.
// HTTP listener на 127.0.0.1:39421, единственный TeklaWorker thread,
// component adapter registry — точка входа для Connector → Tekla.
//
// CLI:
//   --port <n>           override 39421
//   --token <secret>     override token (default — read/create %LOCALAPPDATA%\Platform\Bridge\token.dat)
//   --tekla-version <s>  for /capabilities reporting (default "2025.0")
//   --tekla-session <s>  override old Open API remoting session (normally auto-detected)
//   --expose-model-path  включает modelPath в /capabilities (default off)
//   --log <path>         override log path
//
// Stop через Ctrl+C / SIGTERM / window close.

#nullable enable

using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Platform.Bridge.Desktop.Tekla.Auth;
using Platform.Bridge.Desktop.Tekla.Components.BridgeGirder;
using Platform.Bridge.Desktop.Tekla.Components.CrossMember;
using Platform.Bridge.Desktop.Tekla.Components.Pier;
using Platform.Bridge.Desktop.Tekla.ComponentRuntime;
using Platform.Bridge.Desktop.Tekla.ConstructiveRuntime;
using Platform.Bridge.Desktop.Tekla.Http;
using Platform.Bridge.Desktop.Tekla.Idempotency;
using Platform.Bridge.Desktop.Tekla.Logging;
using Platform.Bridge.Desktop.Tekla.Tekla;
using Platform.Contracts.TeklaPlan;

namespace Platform.Bridge.Desktop.Tekla
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var opts = ProgramOptions.Parse(args);
            using var log = new JsonLineLogger(opts.LogPath, alsoConsole: true);
            log.Info("bridge.desktop.starting", new { opts.Port, opts.LogPath, opts.TeklaVersion });

            var teklaSession = TeklaSessionBootstrap.ResolveAndApply(
                opts.TeklaVersion,
                opts.TeklaSessionName,
                log);

            var token = string.IsNullOrEmpty(opts.OverrideToken)
                ? BridgeTokenStore.LoadOrCreate(opts.TokenPath)
                : opts.OverrideToken!;
            log.Info("bridge.token.ready", new { tokenLen = token.Length, source = string.IsNullOrEmpty(opts.OverrideToken) ? "store" : "cli" });

            using var connection = new TeklaConnectionDisposable(new TeklaConnection(log));
            using var worker = new TeklaWorker(connection.Inner, log);

            // Phase 4 + Phase 10: registry с зарегистрированными адаптерами.
            // Phase 10 — добавление PierAdapter не потребовало правок в
            // ComponentRuntime/, Http/*, server'е, connector'е или web. Только
            // одна строка регистрации здесь — это и есть проверка plan §11
            // phase 10 ("<2 часа без рефакторинга").
            var registry = new ComponentAdapterRegistry();
            registry.Register(new BridgeGirderAdapter(opts.TeklaVersion));
            registry.Register(new CrossMemberAdapter(opts.TeklaVersion));
            registry.Register(new PierAdapter(opts.TeklaVersion));

            var objectMap = new ObjectMap(opts.ObjectMapPath);
            var idempotency = new IdempotencyStore(opts.IdempotencyLogPath);

            var router = new HttpRouter(log, expectedToken: token);
            var constructiveExecutors = new TeklaNativeCommandRegistry(
                new ITeklaNativeCommandExecutor[]
                {
                    new TeklaCreateBeamExecutor(),
                    new TeklaCreatePolyBeamExecutor(),
                    new TeklaCreateContourPlateExecutor(),
                    new TeklaApplyContourCornerExecutor(),
                    new TeklaApplyEdgeTreatmentExecutor(),
                    new TeklaApplyFittingExecutor(),
                    new TeklaApplyBooleanCutExecutor(),
                    new TeklaCreateHoleExecutor(),
                    new TeklaCreateBoltGroupExecutor(),
                    new TeklaCreateWeldExecutor(),
                    new TeklaCreateAssemblyExecutor(),
                });
            var constructiveCapabilities = new TeklaNativeCapabilityProvider(
                opts.TeklaVersion,
                constructiveExecutors);
            var health = new HealthHandler(worker, opts.TeklaVersion, teklaSession);
            var capabilities = new CapabilitiesHandler(
                worker,
                registry,
                opts.TeklaVersion,
                teklaSession,
                opts.ExposeModelPath,
                constructiveCapabilities);
            var teklaPlanPrepare = new TeklaPlanPrepareHandler(constructiveCapabilities);
            var teklaPlanApply = new TeklaPlanApplyHandler(
                worker,
                opts.TeklaVersion,
                constructiveCapabilities);
            var component = new ComponentHandler(registry, worker, objectMap, idempotency, log);
            var selection = new SelectionHandler(worker, registry);
            var modelSelection = new ModelSelectionHandler(worker);
            var modelSelect = new ModelSelectHandler(worker);
            var modelChangeSet = new ModelChangeSetHandler(worker, opts.TeklaVersion);
            var pluginReload = new PluginReloadHandler(worker);
            var fachwerkColumnRefresh = new FachwerkColumnRefreshHandler(worker);
            var pick = new PickHandler(worker);
            var udaFiles = new UdaFilesHandler(worker, log);
            var ifcAttribution = new IfcAttributionHandler(worker, log);

            router.Register("GET", "/health",       requiresAuth: false, (ctx, ct) => health.HandleAsync(ctx, ct));
            router.Register("GET", "/capabilities", requiresAuth: true,  (ctx, ct) => capabilities.HandleAsync(ctx, ct));
            router.Register("POST", "/constructive/tekla-plan/prepare", requiresAuth: true, (ctx, ct) => teklaPlanPrepare.HandleAsync(ctx, ct));
            router.Register("POST", "/constructive/tekla-plan/apply", requiresAuth: true, (ctx, ct) => teklaPlanApply.HandleAsync(ctx, ct));
            router.Register("POST", "/component/upsert", requiresAuth: true, (ctx, ct) => component.UpsertAsync(ctx, ct));
            router.Register("POST", "/component/modify", requiresAuth: true, (ctx, ct) => component.ModifyAsync(ctx, ct));
            router.Register("POST", "/component/delete", requiresAuth: true, (ctx, ct) => component.DeleteAsync(ctx, ct));
            router.Register("POST", "/component/read",   requiresAuth: true, (ctx, ct) => component.ReadAsync(ctx, ct));
            router.Register("POST", "/selection",        requiresAuth: true, (ctx, ct) => selection.HandleAsync(ctx, ct));
            router.Register("POST", "/model/selection",  requiresAuth: true, (ctx, ct) => modelSelection.HandleAsync(ctx, ct));
            router.Register("POST", "/model/select",     requiresAuth: true, (ctx, ct) => modelSelect.HandleAsync(ctx, ct));
            router.Register("POST", "/model/changes/apply", requiresAuth: true, (ctx, ct) => modelChangeSet.ApplyAsync(ctx, ct));
            router.Register("POST", "/plugins/fachwerk/reload", requiresAuth: true, (ctx, ct) => pluginReload.ReloadFachwerkAsync(ctx, ct));
            router.Register("POST", "/plugins/fachwerk/columns/refresh", requiresAuth: true, (ctx, ct) => fachwerkColumnRefresh.HandleAsync(ctx, ct));
            router.Register("POST", "/pick/axis",        requiresAuth: true, (ctx, ct) => pick.PickAxisAsync(ctx, ct));
            router.Register("POST", "/uda/files/apply",  requiresAuth: true, (ctx, ct) => udaFiles.ApplyAsync(ctx, ct));
            router.Register("POST", "/expert-attributes/ifc-attribution/scan",  requiresAuth: true, (ctx, ct) => ifcAttribution.ScanAsync(ctx, ct));
            router.Register("POST", "/expert-attributes/ifc-attribution/apply", requiresAuth: true, (ctx, ct) => ifcAttribution.ApplyAsync(ctx, ct));

            using var listener = new HttpListener();
            var prefix = $"http://127.0.0.1:{opts.Port}/";
            listener.Prefixes.Add(prefix);
            try { listener.Start(); }
            catch (HttpListenerException ex)
            {
                log.Error("http.listener.start_failed", new { err = ex.Message, code = ex.ErrorCode, prefix });
                return 11;
            }
            log.Info("http.listener.started", new { prefix });

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                log.Info("bridge.shutdown.signal", new { source = "ctrl_c" });
                cts.Cancel();
            };

            try
            {
                AcceptLoop(listener, router, log, cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { /* graceful */ }
            catch (Exception ex)
            {
                log.Error("http.accept_loop.fatal", new { err = ex.Message, type = ex.GetType().Name });
                return 12;
            }
            finally
            {
                try { listener.Stop(); } catch { /* ignore */ }
                log.Info("bridge.desktop.stopped");
            }
            return 0;
        }

        private static async Task AcceptLoop(HttpListener listener, HttpRouter router, JsonLineLogger log, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (ct.IsCancellationRequested) { break; }

                // Fire-and-forget per-request handling; HttpRouter does its own try/catch.
                _ = Task.Run(() => router.DispatchAsync(ctx, ct), ct);
            }
        }

        private sealed class ProgramOptions
        {
            public int Port { get; set; } = 39421;
            public string? OverrideToken { get; set; }
            public string TokenPath { get; set; } = BridgeTokenStore.DefaultPath;
            public string LogPath { get; set; } = Path.Combine(BridgeTokenStore.DefaultDir, "desktop.jsonl");
            public string ObjectMapPath { get; set; } = Path.Combine(BridgeTokenStore.DefaultDir, "object-map.json");
            public string IdempotencyLogPath { get; set; } = Path.Combine(BridgeTokenStore.DefaultDir, "operation-log.jsonl");
            public string TeklaVersion { get; set; } = "2025.0";
            public string? TeklaSessionName { get; set; }
            public bool ExposeModelPath { get; set; } = false;

            public static ProgramOptions Parse(string[] args)
            {
                var o = new ProgramOptions();
                for (int i = 0; i < args.Length; i++)
                {
                    var a = args[i];
                    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {a}");
                    switch (a)
                    {
                        case "--port": o.Port = int.Parse(Next()); break;
                        case "--token": o.OverrideToken = Next(); break;
                        case "--token-path": o.TokenPath = Next(); break;
                        case "--log": o.LogPath = Next(); break;
                        case "--object-map": o.ObjectMapPath = Next(); break;
                        case "--idempotency-log": o.IdempotencyLogPath = Next(); break;
                        case "--tekla-version": o.TeklaVersion = Next(); break;
                        case "--tekla-session": o.TeklaSessionName = Next(); break;
                        case "--expose-model-path": o.ExposeModelPath = true; break;
                        case "-h": case "--help":
                            PrintHelp();
                            Environment.Exit(0);
                            break;
                        default:
                            throw new ArgumentException($"Unknown arg: {a}");
                    }
                }
                return o;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Bridge.Desktop.Tekla — long-running Tekla integration daemon");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --port <n>             listener port (default 39421)");
            Console.WriteLine("  --token <secret>       override token (default: load/create %LOCALAPPDATA%\\Platform\\Bridge\\token.dat)");
            Console.WriteLine("  --token-path <path>    override token file location");
            Console.WriteLine("  --log <path>           override log file (default: %LOCALAPPDATA%\\Platform\\Bridge\\desktop.jsonl)");
            Console.WriteLine("  --object-map <path>    persistent externalObjectId→guid map (default: %LOCALAPPDATA%\\Platform\\Bridge\\object-map.json)");
            Console.WriteLine("  --idempotency-log <p>  operation-log.jsonl path (default: %LOCALAPPDATA%\\Platform\\Bridge\\operation-log.jsonl)");
            Console.WriteLine("  --tekla-version <s>    reported in /capabilities (default 2025.0)");
            Console.WriteLine("  --tekla-session <s>    old Open API remoting session (auto-detected by default)");
            Console.WriteLine("  --expose-model-path    include modelPath in /capabilities (default off)");
        }

        private sealed class TeklaConnectionDisposable : IDisposable
        {
            public TeklaConnection Inner { get; }
            public TeklaConnectionDisposable(TeklaConnection inner) { Inner = inner; }
            public void Dispose() { /* no resources to release; placeholder for future */ }
        }
    }
}
