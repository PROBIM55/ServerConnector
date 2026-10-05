using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Connector.Desktop.Features.GraphiteWeb;
using Connector.Desktop;
using Connector.Desktop.Features.Connector;
using Connector.Desktop.Features.Tekla.Standard;
using Connector.Desktop.Features.Tekla.Patching;
using Connector.Desktop.Mvvm;
using Connector.Desktop.Features.ConverterWorkspace;
using Connector.Desktop.Features.PlatformTools;
using Connector.Desktop.Models;
using Connector.Desktop.Services;
using Connector.Desktop.Shell;
using Connector.Platform;
using Platform.Connector.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace Connector.UiSmoke;

internal static class GraphiteWebSmoke
{
    internal static int Run(string outputDirectory, string? fbxPart = null, string? gltfpack = null, string? ifcWorker = null)
    {
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var resource in new[] { "Theme", "Graphite" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"/Connector.Desktop;component/Resources/{resource}.xaml", UriKind.Relative) });
        int exitCode = 1;
        application.Dispatcher.BeginInvoke(async () =>
        {
            try { await RunAsync(output, fbxPart, gltfpack, ifcWorker); exitCode = 0; }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { application.Dispatcher.InvokeShutdown(); }
        });
        Dispatcher.Run();
        return exitCode;
    }

    private static async Task RunAsync(string output, string? fbxPart, string? gltfpack, string? ifcWorker)
    {
        UserStateFingerprint userStateBefore = CaptureUserStateFingerprint();
        // The renderer contract first runs in its own invisible HWND. The real
        // MainWindow Show/Loaded lifecycle is accepted separately below.
        using var source = new HwndSource(new HwndSourceParameters("Connector isolated UI smoke")
        {
            Width = 1180, Height = 822, WindowStyle = unchecked((int)0x80000000),
            PositionX = -32000, PositionY = -32000
        });
        var view = new GraphiteWebView { Width = 1180, Height = 822 };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        view.Loaded += (_, _) => loaded.TrySetResult();
        int commands = 0;
        view.Configure((request, _) =>
        {
            if (request.Command != "ifc-detect") throw new InvalidOperationException("Unexpected smoke command.");
            commands++;
            return Task.FromResult<object?>(new { snapshot = new { status = "Smoke: native RPC received" } });
        }, Console.WriteLine);
        source.RootVisual = view;
        view.Measure(new Size(1180, 822));
        view.Arrange(new Rect(0, 0, 1180, 822));
        view.UpdateLayout();
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await view.InitializeAsync(Path.Combine(output, "isolated-webview-profile"));
        var browser = (WebView2)view.FindName("Browser");
        var core = browser.CoreWebView2 ?? throw new InvalidOperationException("Native WebView2 failed to initialize.");
        try
        {
            await WaitForDocumentAsync(core);
            var snapshots = new List<object>();
            foreach (var (name, query) in new[]
            {
                ("tekla-ifc", "?page=tekla&sub=ifc"),
                ("tekla-exports", "?page=tekla&sub=exports"),
                ("converters", "?page=converters")
            })
            {
                var navigated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                EventHandler<CoreWebView2NavigationCompletedEventArgs> done = (_, e) =>
                {
                    if (e.IsSuccess) navigated.TrySetResult();
                    else navigated.TrySetException(new InvalidOperationException(e.WebErrorStatus.ToString()));
                };
                core.NavigationCompleted += done;
                try
                {
                    core.Navigate("https://connector.local/desktop.html" + query);
                    await navigated.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    await WaitForDocumentAsync(core);
                    var json = await core.ExecuteScriptAsync("JSON.stringify({url:location.href,title:document.querySelector('#page-title').textContent,width:innerWidth,height:innerHeight,sidebar:document.querySelector('.side-nav').getBoundingClientRect().width,desktop:window.CONNECTOR_DESKTOP})");
                    snapshots.Add(JsonSerializer.Deserialize<string>(json)!);
                    using var image = File.Create(Path.Combine(output, name + ".png"));
                    await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
                }
                finally { core.NavigationCompleted -= done; }
            }
            if (commands != 0) throw new InvalidOperationException("Rendering executed a business command.");
            await core.ExecuteScriptAsync("window.__smokeResponse=null; window.CONNECTOR_DESKTOP_BRIDGE.invoke('ifc-detect',{mode:'structura',fields:{}}).then(value=>window.__smokeResponse=value)");
            var result = "null";
            using (var responseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                while (result == "null")
                {
                    result = await core.ExecuteScriptAsync("window.__smokeResponse ? JSON.stringify(window.__smokeResponse) : null");
                    if (result == "null") await Task.Delay(50, responseTimeout.Token);
                }
            }
            using var response = JsonDocument.Parse(JsonSerializer.Deserialize<string>(result)!);
            if (!response.RootElement.GetProperty("ok").GetBoolean() || commands != 1)
                throw new InvalidOperationException("Native RPC was not round-tripped.");
            await view.PublishSnapshotAsync(new { reset = true, status = "Smoke: snapshot delivered", files = Array.Empty<object>(), ifcFiles = Array.Empty<object>(), history = Array.Empty<object>() });
            await WaitForTextAsync(core, "Smoke: snapshot delivered");
            core.Navigate("https://example.invalid/");
            await core.ExecuteScriptAsync("Promise.resolve()");
            if (!core.Source.StartsWith("https://connector.local/desktop.html", StringComparison.Ordinal))
                throw new InvalidOperationException("Untrusted navigation was allowed.");
            await File.WriteAllTextAsync(Path.Combine(output, "native-webview-smoke.json"), JsonSerializer.Serialize(new { snapshots, commands, noForeground = true, blockedExternalNavigation = true }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("PASS: native WebView2 initialized inside invisible owned HWND; 3 approved pages, real JSON RPC and pushed snapshot, external navigation blocked; no user profile/settings/foreground.");
        }
        finally { await view.DisposeAsync(); await view.DisposeAsync(); }

        await RunSettingsHydrationAsync(source, output);
        await RunNativeBoundaryContractsAsync(source, output);
        await RunMainWindowAcceptanceAsync(output, userStateBefore);

        if (fbxPart is not null && gltfpack is not null && ifcWorker is not null)
            await RunControllerEnginesAsync(source, output, fbxPart, gltfpack, ifcWorker);

        UserStateFingerprint userStateAfter = CaptureUserStateFingerprint();
        bool userStateUnchanged = userStateBefore == userStateAfter;
        await File.WriteAllTextAsync(Path.Combine(output, "native-user-state-isolation.json"), JsonSerializer.Serialize(new
        {
            userStateUnchanged,
            before = userStateBefore,
            after = userStateAfter
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (!userStateUnchanged)
            throw new InvalidOperationException("Native smoke changed the real ConnectorAgentDesktop user-state tree.");
    }

    private static async Task RunControllerEnginesAsync(HwndSource source, string output, string fbxPart, string gltfpack, string ifcWorker)
    {
        var input = Path.Combine(output, "native-source.ifc");
        var secondInput = Path.Combine(output, "native-source-second.ifc");
        await File.WriteAllTextAsync(input, EngineSmoke.IfcFixture);
        await File.WriteAllTextAsync(secondInput, EngineSmoke.IfcFixture.Replace("Smoke proxy", "Second smoke proxy", StringComparison.Ordinal));
        var results = Path.Combine(output, "native-results"); Directory.CreateDirectory(results);
        await using var runtime = new ConnectorRuntimeServices(Path.Combine(output, "isolated-agent"), ifcWorker, gltfpack);
        var host = new IsolatedHost();
        var running = new ConcurrentQueue<string>();
        var executionScopes = new ConcurrentDictionary<string, ConnectorProductId>();
        var activeJobs = new HashSet<string>();
        var maxActive = 0;
        runtime.Host.JobStatusChanged += status =>
        {
            if (status.Scope is { } scope) executionScopes.TryAdd(status.RequestId, scope.ProductId);
            lock (activeJobs)
            {
                if (status.Status == JobStatus.Running && activeJobs.Add(status.RequestId)) running.Enqueue(status.RequestId);
                else if (status.Status is JobStatus.Success or JobStatus.Error or JobStatus.Cancelled or JobStatus.Timeout)
                    activeJobs.Remove(status.RequestId);
                maxActive = Math.Max(maxActive, activeJobs.Count);
            }
        };
        var shell = CreateIsolatedShell(host, runtime, output, "engines");
        using var workspace = new ConverterWorkspaceModule(runtime.ConverterClient, action => source.Dispatcher.BeginInvoke(action));
        using var tools = new PlatformToolsModule();
        await using var controller = new GraphiteDesktopController(host, runtime, shell, tools.ViewModel, workspace.ViewModel,
            target => { Console.WriteLine("Native picker target: " + target); return target switch { "folder-input" => Path.GetFullPath(fbxPart), "ifc-output" or "output-root" => results, _ => null }; },
            target => target == "ifc-input" ? [input, secondInput] : null);
        await using var view = new GraphiteWebView { Width = 1180, Height = 822 };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        view.Loaded += (_, _) => loaded.TrySetResult();
        view.Configure(controller.HandleAsync, Console.WriteLine, controller.CreateDocumentSnapshot);
        controller.SnapshotChanged += snapshot => _ = view.PublishSnapshotAsync(snapshot);
        source.RootVisual = view;
        view.Measure(new Size(1180, 822)); view.Arrange(new Rect(0, 0, 1180, 822)); view.UpdateLayout();
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await view.PublishSnapshotAsync(controller.CreateSnapshot());
        await view.InitializeAsync(Path.Combine(output, "isolated-engine-webview-profile"));
        var core = ((WebView2)view.FindName("Browser")).CoreWebView2;
        await WaitForDocumentAsync(core);
        // Click the real approved controls; only the OS picker port is supplied
        // by this smoke, so no mouse, dialog or network interaction is required.
        await core.ExecuteScriptAsync("document.querySelector('[data-page=converters]').click();document.querySelector('[data-conv=ifc]').click();document.querySelector('[data-act=pick-ifc]').click()");
        await WaitForConditionAsync(core, "document.querySelector('.table tbody').innerText.includes('native-source.ifc')");
        await WaitForConditionAsync(core, "document.querySelector('.table tbody').innerText.includes('native-source-second.ifc')");
        await core.ExecuteScriptAsync("document.querySelector('[data-act=choose-folder][data-target=ifc-output]').click()");
        await WaitForConditionAsync(core, "document.querySelector('#ifc-output').value.includes('native-results')");
        var outputPreferences = await host.PreferencesSaved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (outputPreferences.ConverterOutputDirectory != results)
            throw new InvalidOperationException("Native IFC output picker did not persist its isolated preferences.");
        core.Reload();
        await WaitForDocumentAsync(core);
        await WaitForConditionAsync(core, "document.querySelector('#ifc-output')?.value==='native-results'");
        // The page mode can change while the Agent queue is working. The typed
        // request must keep the product that was active at admission.
        await core.ExecuteScriptAsync("document.querySelector('[data-page=converters]').click();document.querySelector('[data-conv=ifc]').click();document.querySelector('[data-mode=platform]').click()");
        await WaitForConditionAsync(core, "document.querySelector('[data-mode=platform]').getAttribute('aria-pressed')==='true'");
        await InvokeBridgeAsync(core, "ifc-analyze", "{mode:'platform',page:'converters',converter:'ifc',fields:{}}");
        await core.ExecuteScriptAsync("document.querySelector('[data-mode=structura]').click()");
        await WaitForConditionAsync(core, "document.querySelector('[data-mode=structura]').getAttribute('aria-pressed')==='true'");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        while (workspace.ViewModel.Jobs.Count < 2 || workspace.ViewModel.IsSubmitting)
            await Task.Delay(50, timeout.Token);
        var analysis = workspace.ViewModel.Jobs.Where(job => job.Operation == ConverterJobOperation.Analyze).ToArray();
        if (analysis.Length != 2 || analysis.Any(job => job.State != "Готово" || !string.IsNullOrWhiteSpace(job.OutputPath) || !File.Exists(job.ReportPath)))
            throw new InvalidOperationException("Approved IFC analysis controls did not produce report-only results for both inputs.");
        if (analysis.Any(job => job.Product != ConnectorProductId.Platform) ||
            analysis.Any(job => !executionScopes.TryGetValue(job.RequestId, out var product) || product != ConnectorProductId.Platform))
            throw new InvalidOperationException("Queued Platform analysis lost its typed execution scope after the mode changed.");
        var analysisSnapshot = JsonSerializer.Serialize(controller.CreateSnapshot());
        if (!analysisSnapshot.Contains("\"operation\":\"analyze\"", StringComparison.Ordinal) ||
            !analysisSnapshot.Contains("\"hasValidation\":false", StringComparison.Ordinal))
            throw new InvalidOperationException("IFC analysis snapshot exposed an optimization validation/result contract.");
        await core.ExecuteScriptAsync("document.querySelector('[data-act=ifc-optimize]').click()");
        // Navigation must remain in the user's selected mode while native job
        // progress continues. A stale background snapshot must not switch it.
        await core.ExecuteScriptAsync("document.querySelector('[data-mode=platform]').click()");
        await WaitForConditionAsync(core, "document.querySelector('[data-mode=platform]').getAttribute('aria-pressed')==='true'");
        while (workspace.ViewModel.Jobs.Count < 4 || workspace.ViewModel.IsSubmitting)
            await Task.Delay(50, timeout.Token);
        var ifc = workspace.ViewModel.Jobs.Where(job => job.Operation == ConverterJobOperation.Optimize).ToArray();
        if (ifc.Length != 2 || ifc.Any(job => job.State != "Готово" || !File.Exists(job.OutputPath) || !File.Exists(job.ReportPath)))
            throw new InvalidOperationException("Approved IFC optimization controls did not produce real results and reports for both inputs.");
        if (ifc.Any(job => job.Product != ConnectorProductId.Structura) ||
            ifc.Any(job => !executionScopes.TryGetValue(job.RequestId, out var product) || product != ConnectorProductId.Structura))
            throw new InvalidOperationException("Queued Structura optimization lost its typed execution scope after the mode changed.");
        var ifcRunning = running.Where(id => ifc.Any(job => job.RequestId == id)).ToArray();
        var expectedFifo = new[]
        {
            ifc.Single(job => string.Equals(job.InputPath, input, StringComparison.OrdinalIgnoreCase)).RequestId,
            ifc.Single(job => string.Equals(job.InputPath, secondInput, StringComparison.OrdinalIgnoreCase)).RequestId
        };
        if (maxActive != 1 || !ifcRunning.SequenceEqual(expectedFifo))
            throw new InvalidOperationException("IFC batches did not retain one-Agent FIFO execution.");
        await WaitForConditionAsync(core, "document.querySelector('[data-mode=platform]').getAttribute('aria-pressed')==='true'");
        await core.ExecuteScriptAsync("document.querySelector('[data-mode=structura]').click();document.querySelector('[data-conv=fbx]').click();document.querySelector('[data-act=pick-folder]').click()");
        await WaitForConditionAsync(core, "document.querySelector('#page-content').innerText.includes('SM_TestPart_001')");
        await core.ExecuteScriptAsync("document.querySelector('[data-act=choose-folder][data-target=output-root]').click()");
        await WaitForConditionAsync(core, "document.querySelector('#output-root').value.includes('native-results')");
        await core.ExecuteScriptAsync("document.querySelector('[data-act=start]').click()");
        while (workspace.ViewModel.Jobs.Count < 5 || workspace.ViewModel.IsSubmitting)
            await Task.Delay(50, timeout.Token);
        var fbx = workspace.ViewModel.Jobs.First();
        if (fbx.State != "Готово" || !File.Exists(fbx.OutputPath) || !File.Exists(fbx.ReportPath))
            throw new InvalidOperationException("Approved FBX controls did not produce a real result.");
        var savedHistory = await runtime.Host.PrepareLocalAsync(timeout.Token);
        int engineStartsBeforeRestore = running.Count;
        using var restoredWorkspace = new ConverterWorkspaceViewModel(runtime.ConverterClient, action => source.Dispatcher.BeginInvoke(action));
        restoredWorkspace.RestoreSavedJobs(savedHistory.Jobs);
        var completed = analysis.Concat(ifc).Append(fbx).ToArray();
        if (restoredWorkspace.Jobs.Count != completed.Length ||
            completed.Any(job => restoredWorkspace.Jobs.SingleOrDefault(saved => saved.RequestId == job.RequestId) is not
                { State: "Готово" } restored || restored.Product != job.Product ||
                restored.OutputPath != job.OutputPath || restored.ReportPath != job.ReportPath) ||
            running.Count != engineStartsBeforeRestore)
            throw new InvalidOperationException("Shared Agent history did not restore completed outputs/reports and product scope without re-running an engine.");
        var recoveryPayload = JsonSerializer.SerializeToElement(new { inputPath = input, outputDirectory = results, profile = "exact" });
        var recoveryCreated = DateTime.UtcNow;
        var queuedRecovery = new LocalJobSnapshot(
            new ConnectorJobEnvelope(1, "smoke-recovery-queued", "converters", CadProvider.Tekla, JobOperation.Export,
                recoveryCreated, recoveryPayload, ExecutorId: ConverterWorkspaceViewModel.IfcOptimizeExecutor,
                Scope: ExecutionScope.DeviceLocal(ConnectorProductId.Platform)),
            new ConnectorJobStatusEnvelope(1, "smoke-recovery-queued", savedHistory.DeviceId, "converters", CadProvider.Tekla,
                JobStatus.Queued, recoveryCreated, "Job is queued for recovery.", 0,
                ExecutorId: ConverterWorkspaceViewModel.IfcOptimizeExecutor, Scope: ExecutionScope.DeviceLocal(ConnectorProductId.Platform)),
            StoredLocalJobState.Queued, recoveryCreated);
        var interruptedRecovery = new LocalJobSnapshot(
            new ConnectorJobEnvelope(1, "smoke-recovery-interrupted", "converters", CadProvider.Tekla, JobOperation.Export,
                recoveryCreated.AddTicks(1), recoveryPayload, ExecutorId: ConverterWorkspaceViewModel.IfcOptimizeExecutor,
                Scope: ExecutionScope.DeviceLocal(ConnectorProductId.Structura)),
            new ConnectorJobStatusEnvelope(1, "smoke-recovery-interrupted", savedHistory.DeviceId, "converters", CadProvider.Tekla,
                JobStatus.Interrupted, recoveryCreated.AddTicks(1), "Connector restarted while this operation was Running.",
                ErrorCode: "LOCAL_RECOVERY_INTERRUPTED", ExecutorId: ConverterWorkspaceViewModel.IfcOptimizeExecutor,
                Scope: ExecutionScope.DeviceLocal(ConnectorProductId.Structura)),
            StoredLocalJobState.Running, recoveryCreated.AddTicks(1));
        restoredWorkspace.RestoreSavedJobs([queuedRecovery, interruptedRecovery]);
        if (restoredWorkspace.Jobs.Single(job => job.RequestId == queuedRecovery.Job.RequestId) is not { State: "В очереди", Product: ConnectorProductId.Platform } ||
            restoredWorkspace.Jobs.Single(job => job.RequestId == interruptedRecovery.Job.RequestId) is not { State: "Прервано", Product: ConnectorProductId.Structura } ||
            running.Count != engineStartsBeforeRestore)
            throw new InvalidOperationException("Queued or interrupted local recovery history was not rendered truthfully without an engine re-run.");
        var safeSnapshot = JsonSerializer.Serialize(controller.CreateSnapshot(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (safeSnapshot.Contains(JsonSerializer.Serialize(output).Trim('"'), StringComparison.OrdinalIgnoreCase) ||
            safeSnapshot.Contains(JsonSerializer.Serialize(input).Trim('"'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Private input/output paths escaped the native picker boundary.");
        await core.ExecuteScriptAsync("document.querySelector('[data-page=converters]').click();document.querySelector('[data-conv=fbx]').click()");
        using (var image = File.Create(Path.Combine(output, "converters-real-result.png")))
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
        await File.WriteAllTextAsync(Path.Combine(output, "native-controller-engines.json"), JsonSerializer.Serialize(new { analysis = analysis.Select(job => new { job.State, job.OutputPath, job.ReportPath, product = job.Product }), ifc = ifc.Select(job => new { job.State, job.OutputPath, job.ReportPath, product = job.Product }), fbx = new { fbx.State, fbx.OutputPath, fbx.ReportPath, product = fbx.Product }, scopeByRequest = executionScopes, sameAgentFifo = true, restoredSavedHistory = true, queuedAndInterruptedHistoryTruthful = true, engineStartsBeforeRestore, backgroundPreservedPlatform = true, offline = !runtime.Host.IsRunning }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: approved WebView IFC analysis and two-file optimization plus FBX use one actual Agent/installed engines; typed product scope, saved-history outputs/reports, queued/interrupted recovery status and FIFO survive without an engine re-run, and private paths remain native.");
    }

    private static async Task WaitForConditionAsync(CoreWebView2 core, string condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (await core.ExecuteScriptAsync("Boolean(" + condition + ")") != "true")
                await Task.Delay(50, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Failed condition: " + condition);
            Console.WriteLine(await core.ExecuteScriptAsync("JSON.stringify({url:location.href,text:document.querySelector('#page-content').innerText})"));
            throw;
        }
    }

    private static async Task RunSettingsHydrationAsync(HwndSource source, string output)
    {
        // Only an in-memory legacy settings fixture. Never load/save the user's file.
        const string secret = "isolated-settings-credential";
        var host = new IsolatedHost { AllowCredentialProbe = true };
        host.Settings.TokenCipherBase64 = SettingsService.EncryptToken(secret);
        host.Settings.TeklaStandardLocalPath = @"C:\HydrationFixture\CompanyFirm";
        host.Settings.TeklaExtensionsLocalPath = @"C:\HydrationFixture\CompanyExtensions";
        host.Settings.TeklaLibrariesLocalPath = @"C:\HydrationFixture\CompanyLibraries";
        host.Settings.AutoStart = false;
        host.Settings.HeartbeatSeconds = 73;
        host.Settings.VpnEnabled = true;
        host.Settings.VpnConfigCipherBase64 = "isolated-vpn-config";
        var platform = new FakePlatformConnection(host.CallOrder);
        await using var runtime = new ConnectorRuntimeServices(Path.Combine(output, "settings-agent"));
        var shell = CreateIsolatedShell(host, runtime, output, "settings");
        var patch = (PatchingViewModel)((FrameworkElement)shell.Tekla.Module("Патчинг")!.View).DataContext;
        patch.TeklaBin = @"C:\HydrationFixture\SelectedTekla";
        patch.StagingDir = @"C:\HydrationFixture\SelectedPatch";
        var sharing = (Connector.Desktop.Features.Tekla.ModelSharing.ModelSharingViewModel)((FrameworkElement)shell.Tekla.Module("Model Sharing")!.View).DataContext;
        sharing.TeklaBin = @"C:\HydrationFixture\SharingTekla";
        using var workspace = new ConverterWorkspaceModule(runtime.ConverterClient, action => source.Dispatcher.BeginInvoke(action));
        using var tools = new PlatformToolsModule();
        await using var controller = new GraphiteDesktopController(host, runtime, shell, tools.ViewModel, workspace.ViewModel,
            platformConnection: platform);
        await using var view = new GraphiteWebView { Width = 1180, Height = 822 };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        view.Loaded += (_, _) => loaded.TrySetResult();
        view.Configure(controller.HandleAsync, Console.WriteLine, controller.CreateDocumentSnapshot);
        controller.SnapshotChanged += snapshot => _ = view.PublishSnapshotAsync(snapshot);
        source.RootVisual = view;
        view.Measure(new Size(1180, 822)); view.Arrange(new Rect(0, 0, 1180, 822)); view.UpdateLayout();
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        // Reproduce the lost-first-snapshot race: newer incremental state replaces hydration.
        await view.PublishSnapshotAsync(controller.CreateSnapshot());
        await view.PublishSnapshotAsync(controller.CreateSnapshot());
        await view.InitializeAsync(Path.Combine(output, "isolated-settings-webview-profile"));
        var core = ((WebView2)view.FindName("Browser")).CoreWebView2;
        await WaitForDocumentAsync(core);
        await WaitForConditionAsync(core, "document.querySelector('#token').placeholder==='Токен сохранён' && document.querySelector('#token').value===''");
        var json = JsonSerializer.Serialize(controller.CreateDocumentSnapshot());
        if (json.Contains(secret, StringComparison.Ordinal) || json.Contains(host.Settings.TokenCipherBase64, StringComparison.Ordinal) ||
            json.Contains("HydrationFixture", StringComparison.Ordinal))
            throw new InvalidOperationException("Settings hydration disclosed a private path or credential.");
        await core.ExecuteScriptAsync("document.querySelector('#token').value='typed-fixture'; document.querySelector('#token').dispatchEvent(new Event('input',{bubbles:true}))");
        await view.PublishSnapshotAsync(controller.CreateSnapshot());
        await WaitForConditionAsync(core, "document.querySelector('#token').value==='typed-fixture'");
        await core.ExecuteScriptAsync("document.querySelector('#token').value=''; document.querySelector('[data-act=connect]').click()");
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (host.ConnectedCredential != secret) await Task.Delay(50, timeout.Token);
        await core.ExecuteScriptAsync("document.querySelector('[data-page=tekla]').click()");
        await WaitForConditionAsync(core, "document.querySelector('#xs-path').value==='CompanyFirm' && document.querySelector('#ext-path').value==='CompanyExtensions' && document.querySelector('#lib-path').value==='CompanyLibraries'");
        using (var image = File.Create(Path.Combine(output, "settings-restored-firm.png")))
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
        await core.ExecuteScriptAsync("document.querySelector('[data-tab=sharing]').click()");
        await WaitForConditionAsync(core, "document.querySelector('#sharing-bin').value==='SharingTekla'");
        await core.ExecuteScriptAsync("document.querySelector('[data-tab=ifc]').click()");
        await WaitForConditionAsync(core, "document.querySelector('#ifc-bin').value==='SelectedTekla' && document.querySelector('#patch-folder').value==='SelectedPatch'");
        await core.ExecuteScriptAsync("document.querySelector('[data-page=settings]').click()");
        await WaitForConditionAsync(core, "document.querySelector('#autostart').checked===false && document.querySelector('#heartbeat-seconds').value==='73'");
        await core.ExecuteScriptAsync("document.querySelector('#heartbeat-seconds').value='109'; document.querySelector('#heartbeat-seconds').dispatchEvent(new Event('input',{bubbles:true}))");
        await core.ExecuteScriptAsync("document.querySelector('[data-act=settings-store]').click()");
        var preferences = await host.PreferencesSaved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (preferences.HeartbeatSeconds != 109 || preferences.AutoStart != false)
            throw new InvalidOperationException("Settings store did not capture the expected isolated DesktopPreferences DTO.");
        await WaitForConditionAsync(core, "document.querySelector('#heartbeat-seconds').value==='109'");
        core.Reload();
        await WaitForDocumentAsync(core);
        await WaitForConditionAsync(core, "document.querySelector('#heartbeat-seconds')?.value==='73'");
        await core.ExecuteScriptAsync("document.querySelector('[data-mode=platform]').click(); window.__platformHydration=null; window.CONNECTOR_DESKTOP_BRIDGE.invoke('jobs-refresh',{mode:'platform',fields:{}}).then(result=>window.__platformHydration=result)");
        await WaitForConditionAsync(core, "Boolean(window.__platformHydration)");
        core.Reload();
        await WaitForDocumentAsync(core);
        await WaitForConditionAsync(core, "document.querySelector('[data-mode=platform]').getAttribute('aria-pressed')==='true' && document.querySelector('#token')?.placeholder==='Токен сохранён'");
        await core.ExecuteScriptAsync("document.querySelector('#token').value=''; document.querySelector('[data-act=connect]').click()");
        await platform.OperationCalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForConditionAsync(core, "document.body.innerText.includes('Действие не выполнено.')");
        if (host.ConnectedCredential != secret || platform.LastToken is not null ||
            platform.StoredCredential == secret || host.EnsureVpnReadyCalls != 1 ||
            !host.CallOrder.Take(2).SequenceEqual(["vpn", "platform-connect"]))
            throw new InvalidOperationException("Platform connect did not use its isolated saved credential after VPN preparation.");
        await core.ExecuteScriptAsync("document.querySelector('[data-mode=structura]').click(); document.querySelector('[data-page=tekla]').click(); document.querySelector('[data-tab=standard]').click()");
        await WaitForConditionAsync(core, "document.querySelector('#xs-path').value==='CompanyFirm'");
        await File.WriteAllTextAsync(Path.Combine(output, "settings-hydration.json"), JsonSerializer.Serialize(new
        { restoredFirm = true, restoredSharing = true, restoredPatch = true, restoredPreferences = true,
          savedCredentialUsedNatively = true, platformCredentialIsSeparate = true, platformFailureNotSuccess = true,
          secretsAndPathsStayNative = true, reloadHydrated = true, backgroundPreservedEdits = true,
          productScopePreserved = true, noUserSettingsWrites = true }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: isolated settings DTO saves hydrate native WebView fields after reload; Structura and Platform credentials stay separate, VPN precedes fake Platform failure, and no user settings are written.");
    }

    private static async Task RunNativeBoundaryContractsAsync(HwndSource source, string output)
    {
        string publicationRoot = Path.Combine(output, "native-publication-fixture");
        string firm = Directory.CreateDirectory(Path.Combine(publicationRoot, "firm")).FullName;
        string extensions = Directory.CreateDirectory(Path.Combine(publicationRoot, "extensions")).FullName;
        string libraries = Directory.CreateDirectory(Path.Combine(publicationRoot, "libraries")).FullName;
        var host = new IsolatedHost { CanControlBackground = true, CanPublish = true };
        // Platform reconnect requires the same accepted VPN precondition as the
        // native settings flow. Without it, the controller correctly falls back
        // to the absent Structura token before it can reach the fake facade.
        host.Settings.VpnEnabled = true;
        host.Settings.VpnConfigCipherBase64 = "isolated-vpn-config";
        var platform = new FakePlatformConnection(host.CallOrder);
        await using var runtime = new ConnectorRuntimeServices(Path.Combine(output, "boundary-agent"));
        var shell = CreateIsolatedShell(host, runtime, output, "boundary");
        using var workspace = new ConverterWorkspaceModule(runtime.ConverterClient, action => source.Dispatcher.BeginInvoke(action));
        using var tools = new PlatformToolsModule();
        await using var controller = new GraphiteDesktopController(host, runtime, shell, tools.ViewModel, workspace.ViewModel,
            target => target switch
            {
                "publish-firm-path" => firm,
                "publish-ext-path" => extensions,
                "publish-lib-path" => libraries,
                _ => null
            }, platformConnection: platform);
        await using var view = new GraphiteWebView { Width = 1180, Height = 822 };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        view.Loaded += (_, _) => loaded.TrySetResult();
        view.Configure(controller.HandleAsync, Console.WriteLine, controller.CreateDocumentSnapshot);
        controller.SnapshotChanged += snapshot => _ = view.PublishSnapshotAsync(snapshot);
        source.RootVisual = view;
        view.Measure(new Size(1180, 822)); view.Arrange(new Rect(0, 0, 1180, 822)); view.UpdateLayout();
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await view.InitializeAsync(Path.Combine(output, "isolated-boundary-webview-profile"));
        var core = ((WebView2)view.FindName("Browser")).CoreWebView2 ?? throw new InvalidOperationException("Native boundary WebView is unavailable.");
        await WaitForDocumentAsync(core);

        // These are actual rendered controls. Structura uses the host lifecycle;
        // Platform must use only its own facade even when the host supports both.
        await core.ExecuteScriptAsync("document.querySelector('[data-act=agent-start]').click()");
        await WaitForConditionAsync(core, "document.body.innerText.includes('Состояние обновлено.')");
        if (host.BackgroundStartCalls != 1 || host.BackgroundStopCalls != 0)
            throw new InvalidOperationException("Structura start did not reach the native background lifecycle exactly once.");
        await core.ExecuteScriptAsync("document.querySelector('[data-act=agent-stop]').click()");
        await WaitForConditionAsync(core, "document.body.innerText.includes('Состояние обновлено.')");
        if (host.BackgroundStopCalls != 1) throw new InvalidOperationException("Structura stop did not reach the native background lifecycle.");
        host.FailNextBackgroundStart = true;
        await core.ExecuteScriptAsync("document.querySelector('[data-act=agent-start]').click()");
        await WaitForConditionAsync(core, "document.body.innerText.includes('Действие не выполнено.')");
        if (host.BackgroundStartCalls != 2 || host.BackgroundRunning)
            throw new InvalidOperationException("Background lifecycle failure was reported as a successful/running state.");

        var platformStart = platform.WaitForNextOperationAsync();
        var reconnectRelease = platform.HoldNextReconnect();
        var platformStartedNotice = WaitForNoticeAsync(controller, "connect-platform", "Действие запущено.");
        await core.ExecuteScriptAsync("document.querySelector('[data-mode=platform]').click();document.querySelector('[data-act=agent-start]').click()");
        var operation = await platformStart.WaitAsync(TimeSpan.FromSeconds(10));
        if (operation != PlatformOperation.Reconnect || platform.ReconnectCalls != 1 || platform.ConnectCalls != 0 ||
            host.BackgroundStartCalls != 2 || host.BackgroundStopCalls != 1)
            throw new InvalidOperationException("Platform start touched the Structura background lifecycle.");
        await platformStartedNotice;
        var platformFailedNotice = WaitForNoticeAsync(controller, "connect-platform", "Действие не выполнено. Подробности доступны в журнале.");
        reconnectRelease.TrySetResult();
        await platformFailedNotice;
        await core.ExecuteScriptAsync("document.querySelector('[data-act=agent-stop]').click()");
        await WaitForConditionAsync(core, "document.body.innerText.includes('Состояние обновлено.')");
        if (platform.DisconnectCalls != 1 || host.BackgroundStopCalls != 1)
            throw new InvalidOperationException("Platform stop touched the Structura background lifecycle.");

        // The admin page is entered through the same typed bridge context that a
        // rendered navigation action supplies; publication stays at the fake host.
        await InvokeBridgeAsync(core, "jobs-refresh", "{mode:'structura',page:'admin',fields:{}}");
        core.Navigate("https://connector.local/desktop.html?mode=structura&page=admin");
        await WaitForDocumentAsync(core);
        await WaitForConditionAsync(core, "Boolean(document.querySelector('#publish-comment'))");
        await core.ExecuteScriptAsync("document.querySelector('[data-act=choose-folder][data-target=publish-firm-path]').click();document.querySelector('[data-act=choose-folder][data-target=publish-ext-path]').click();document.querySelector('[data-act=choose-folder][data-target=publish-lib-path]').click()");
        using (var savedTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (host.SavedDesktopPreferences.Count < 3) await Task.Delay(50, savedTimeout.Token);
        DesktopPreferences saved = host.SavedDesktopPreferences.Last();
        if (saved.TeklaPublishSourcePath != firm || saved.TeklaExtensionsPublishSourcePath != extensions || saved.TeklaLibrariesPublishSourcePath != libraries)
            throw new InvalidOperationException("Selected native publication paths were not persisted through DesktopPreferences.");
        await core.ExecuteScriptAsync("document.querySelector('#publish-firm').checked=false;document.querySelector('#publish-extensions').checked=true;document.querySelector('#publish-libraries').checked=true;document.querySelector('#publish-comment').value='  Native release note  ';document.querySelector('[data-act=publish-validate]').click()");
        await host.PublicationValidated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await core.ExecuteScriptAsync("document.querySelector('[data-act=publish-start]').click()");
        TeklaPublicationRequest published = await host.PublicationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (host.ValidatedPublication is not { PublishFirm: false, PublishExtensions: true, PublishLibraries: true, Comment: "Native release note" } ||
            published is not { PublishFirm: false, PublishExtensions: true, PublishLibraries: true, Comment: "Native release note" })
            throw new InvalidOperationException("Publication category flags or comment changed across the native boundary.");
        string snapshot = JsonSerializer.Serialize(controller.CreateDocumentSnapshot());
        if (snapshot.Contains(publicationRoot, StringComparison.OrdinalIgnoreCase) || snapshot.Contains("token", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Publication paths or credentials escaped the native document scope.");

        host.CanPublish = false;
        await view.PublishSnapshotAsync(controller.CreateSnapshot());
        await WaitForConditionAsync(core, "[...document.querySelectorAll('[data-act=publish-validate],[data-act=publish-start]')].every(button=>button.disabled||button.getAttribute('aria-disabled')==='true')");
        _ = await InvokeBridgeAsync(core, "publish-start", "{mode:'structura',page:'admin',fields:{'publish-firm':true,'publish-comment':'bypass'}}");
        await WaitForConditionAsync(core, "document.body.innerText.includes('Действие не выполнено.')");
        if (host.PublicationStartCalls != 1)
            throw new InvalidOperationException("Non-admin direct publication RPC was not rejected by the native host.");

        await File.WriteAllTextAsync(Path.Combine(output, "native-boundary-contracts.json"), JsonSerializer.Serialize(new
        {
            productScope = "verified by real shared Host only when engines run",
            structuraLifecycle = new { host.BackgroundStartCalls, host.BackgroundStopCalls, failureTruthful = true },
            platformLifecycle = new { platform.ConnectCalls, platform.ReconnectCalls, platform.DisconnectCalls, structuraUntouched = true },
            publication = new { categoriesAndCommentCaptured = true, selectedPathsPersisted = true, pathsAndTokenNativeOnly = true, nonAdminControlsUnavailable = true, directRpcRejected = true }
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: actual native WebView controls preserve Structura/Platform lifecycle boundaries; publication flags/comment and native-only paths are captured, while non-admin UI/RPC remains unavailable.");
    }

    private static async Task<string> InvokeBridgeAsync(CoreWebView2 core, string command, string payload)
    {
        await core.ExecuteScriptAsync($"window.__smokeResponse=null;window.CONNECTOR_DESKTOP_BRIDGE.invoke('{command}',{payload}).then(value=>window.__smokeResponse=value)");
        await WaitForConditionAsync(core, "window.__smokeResponse!==null");
        return JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("JSON.stringify(window.__smokeResponse)"))!;
    }

    private static async Task RunMainWindowAcceptanceAsync(string output, UserStateFingerprint userStateBefore)
    {
        string fixtureRoot = Path.Combine(output, "main-window-native-acceptance");
        Directory.CreateDirectory(fixtureRoot);
        var settings = new SettingsService(Path.Combine(fixtureRoot, "settings.json"));
        AppSettings seeded = settings.Load();
        seeded.AutoStart = false;
        seeded.HeartbeatSeconds = 73;
        seeded.ConverterOutputDirectory = Directory.CreateDirectory(Path.Combine(fixtureRoot, "results")).FullName;
        seeded.TeklaStandardLocalPath = Directory.CreateDirectory(Path.Combine(fixtureRoot, "CompanyFirm")).FullName;
        settings.Save(seeded);

        var autoStart = new RecordingAutoStartService();
        var runtime = new ConnectorRuntimeServices(Path.Combine(fixtureRoot, "agent"));
        var windowTekla = new TeklaStandardService(new HttpClient { Timeout = TimeSpan.FromSeconds(25) }, Path.Combine(fixtureRoot, "window-tekla-state"));
        var shellTekla = new TeklaStandardService(new HttpClient { Timeout = TimeSpan.FromSeconds(25) }, Path.Combine(fixtureRoot, "shell-tekla-state"));
        var vpn = new VpnProvisioningService(Path.Combine(fixtureRoot, "vpn-state"));
        var modelSharing = new ModelSharingProvisioningService(Path.Combine(fixtureRoot, "model-sharing-state"));
        var ifcPatch = new IfcExportPatchService(Path.Combine(fixtureRoot, "ifc-patch-state"));
        string webViewProfile = Path.Combine(fixtureRoot, "webview-profile");
        var loadedInitialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int applicationWindowsBefore = Application.Current.Windows.Count;
        var window = new MainWindow(new MainWindowServices(settings, autoStart, runtime, windowTekla, shellTekla, vpn,
            modelSharing, ifcPatch, new PackageUpdateService(null),
            trayIconVisible: false,
            allowOwnedFixtureClose: true,
            runLoadedExternalActions: false,
            webViewUserDataDirectory: webViewProfile,
            loadedInitializationCompleted: () => loadedInitialization.TrySetResult()));
        bool applicationWindowRegistered = Application.Current.Windows.Count == applicationWindowsBefore + 1;
        var view = window.FindName("GraphiteWebHost") as GraphiteWebView
            ?? throw new InvalidOperationException("Actual MainWindow Graphite host was not composed.");
        var tray = typeof(MainWindow).GetField("_trayIcon", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(window)
            as System.Windows.Forms.NotifyIcon ?? throw new InvalidOperationException("Actual MainWindow tray icon was not composed.");
        var graphiteController = typeof(MainWindow).GetField("_graphite", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
            as GraphiteDesktopController ?? throw new InvalidOperationException("Actual MainWindow Graphite controller was not composed.");
        var backgroundAgent = typeof(MainWindow).GetField("_backgroundAgent", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
            as DesktopBackgroundAgent ?? throw new InvalidOperationException("Actual MainWindow background Agent was not composed.");
        bool actualGraphiteController = true;
        string expectedApplicationVersion = typeof(GraphiteDesktopController).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+', 2)[0].Trim()
            ?? throw new InvalidOperationException("Connector.Desktop has no informational version.");
        string snapshotJson = JsonSerializer.Serialize(graphiteController.CreateSnapshot());
        using JsonDocument snapshotDocument = JsonDocument.Parse(snapshotJson);
        string snapshotApplicationVersion = snapshotDocument.RootElement.GetProperty("moduleFields").GetProperty("app-version").GetString()
            ?? throw new InvalidOperationException("Graphite snapshot has no application version.");
        if (snapshotApplicationVersion != expectedApplicationVersion)
            throw new InvalidOperationException($"Graphite snapshot version '{snapshotApplicationVersion}' did not match assembly informational version '{expectedApplicationVersion}'.");
        bool windowShown = false;
        bool loadedHooksRun = false;
        bool offscreen = false;
        bool windowNeverForeground = false;
        bool externalLoadedActionsSuppressed = false;
        bool runtimeStartedDuringLoaded = false;
        bool loadedBackgroundAgentStarted = false;
        bool trayHiddenDuringLoaded = false;
        bool cleanupCompleted = false;
        bool seededSettingsHydrated = false;
        bool settingsRpcPersisted = false;
        string installedApplicationVersion = string.Empty;
        string releaseNotesApplicationVersion = string.Empty;

        try
        {
            window.Width = 1180;
            window.Height = 822;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Left = SystemParameters.VirtualScreenLeft - window.Width - 512;
            window.Top = SystemParameters.VirtualScreenTop;
            window.Show();
            windowShown = window.IsVisible;
            await loadedInitialization.Task.WaitAsync(TimeSpan.FromSeconds(30));
            loadedHooksRun = window.IsLoaded;
            IntPtr windowHandle = new WindowInteropHelper(window).Handle;
            windowNeverForeground = !window.IsActive && GetForegroundWindow() != windowHandle;
            offscreen = window.Left + window.ActualWidth <= SystemParameters.VirtualScreenLeft;
            trayHiddenDuringLoaded = !tray.Visible;
            runtimeStartedDuringLoaded = runtime.Host.IsRunning;
            loadedBackgroundAgentStarted = backgroundAgent.IsRunning;
            externalLoadedActionsSuppressed = autoStart.Calls == 0 && !runtimeStartedDuringLoaded &&
                                              !loadedBackgroundAgentStarted;
            if (!windowShown || !loadedHooksRun || !windowNeverForeground || !offscreen ||
                !trayHiddenDuringLoaded || !externalLoadedActionsSuppressed)
            {
                throw new InvalidOperationException("MainWindow Show/Loaded did not preserve the isolated offscreen lifecycle contract.");
            }

            var core = ((WebView2)view.FindName("Browser")).CoreWebView2
                ?? throw new InvalidOperationException("Actual MainWindow WebView2 failed to initialize.");
            await WaitForDocumentAsync(core);
            await core.ExecuteScriptAsync("document.querySelector('[data-page=settings]').click()");
            await WaitForConditionAsync(core, "document.querySelector('#heartbeat-seconds').value==='73' && document.querySelector('#autostart').checked===false");
            installedApplicationVersion = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("[...document.querySelectorAll('#page-content .row')].find(row=>row.querySelector('strong')?.textContent==='Установленная версия')?.querySelector('small')?.textContent??''"))!;
            if (installedApplicationVersion != expectedApplicationVersion)
                throw new InvalidOperationException($"Native settings displayed version '{installedApplicationVersion}' instead of '{expectedApplicationVersion}'.");
            await core.ExecuteScriptAsync("document.querySelector('[data-act=release-notes]').click()");
            await WaitForConditionAsync(core, "document.querySelector('#kit-dialog')?.open===true");
            releaseNotesApplicationVersion = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("[...document.querySelectorAll('#dialog-content .row')].find(row=>row.querySelector('strong')?.textContent==='Версия текущего приложения')?.querySelector('small')?.textContent??''"))!;
            if (releaseNotesApplicationVersion != expectedApplicationVersion)
                throw new InvalidOperationException($"Native release notes displayed version '{releaseNotesApplicationVersion}' instead of '{expectedApplicationVersion}'.");
            await core.ExecuteScriptAsync("document.querySelector('#kit-dialog').close()");
            seededSettingsHydrated = true;

            await core.ExecuteScriptAsync("document.querySelector('#heartbeat-seconds').value='109';document.querySelector('#autostart').checked=true;document.querySelector('[data-act=settings-store]').click()");
            bool requestedAutoStart = await autoStart.Called.Task.WaitAsync(TimeSpan.FromSeconds(10));
            AppSettings persisted;
            using (var saveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                do
                {
                    persisted = settings.Load();
                    if (persisted.AutoStart && persisted.HeartbeatSeconds == 109) break;
                    await Task.Delay(25, saveTimeout.Token);
                } while (true);
            }
            if (!requestedAutoStart || autoStart.Calls != 1 || !persisted.AutoStart || persisted.HeartbeatSeconds != 109)
                throw new InvalidOperationException("Actual MainWindow settings RPC did not persist through the injected native services.");
            settingsRpcPersisted = true;

            core.Reload();
            await WaitForDocumentAsync(core);
            await WaitForConditionAsync(core, "document.querySelector('#heartbeat-seconds')?.value==='109' && document.querySelector('#autostart')?.checked===true");
            using (var image = File.Create(Path.Combine(output, "main-window-native-acceptance.png")))
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
        }
        finally
        {
            await window.CloseOwnedCompositionAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        bool applicationWindowsRestored = Application.Current.Windows.Count == applicationWindowsBefore;
        bool presentationDetached = PresentationSource.FromVisual(window) is null;
        cleanupCompleted = applicationWindowsRestored && presentationDetached && !window.IsVisible;
        UserStateFingerprint userStateAfter = CaptureUserStateFingerprint();
        bool userStateUnchanged = userStateBefore == userStateAfter;
        if (!applicationWindowRegistered || !cleanupCompleted || !userStateUnchanged)
            throw new InvalidOperationException(
                $"Shown MainWindow cleanup or user-state isolation was not confirmed: " +
                $"registered={applicationWindowRegistered}, windowsRestored={applicationWindowsRestored}, " +
                $"presentationDetached={presentationDetached}, visible={window.IsVisible}, " +
                $"userStateUnchanged={userStateUnchanged}, " +
                $"connectorAgentChanged={userStateBefore.ConnectorAgentDesktop != userStateAfter.ConnectorAgentDesktop}, " +
                $"structuraChanged={userStateBefore.StructuraConnector != userStateAfter.StructuraConnector}, " +
                $"startupChanged={userStateBefore.StartupShortcuts != userStateAfter.StartupShortcuts}, " +
                $"runRegistryChanged={userStateBefore.AutoStartRegistry != userStateAfter.AutoStartRegistry}.");
        await File.WriteAllTextAsync(Path.Combine(output, "main-window-native-acceptance.json"), JsonSerializer.Serialize(new
        {
            actualMainWindow = true,
            actualGraphiteController,
            windowShown,
            loadedHooksRun,
            showActivated = window.ShowActivated,
            showInTaskbar = window.ShowInTaskbar,
            offscreen,
            windowNeverForeground,
            trayHiddenDuringLoaded,
            externalLoadedActionsSuppressed,
            runtimeStartedDuringLoaded,
            loadedBackgroundAgentStarted,
            graphiteWebView2Ready = true,
            seededSettingsHydrated,
            settingsRpcPersisted,
            expectedApplicationVersion,
            snapshotApplicationVersion,
            installedApplicationVersion,
            releaseNotesApplicationVersion,
            autoStartRedirectedToInjectedService = autoStart.Calls == 1,
            applicationWindowRegistered,
            applicationWindowsRestored,
            presentationDetached,
            cleanupCompleted,
            isolatedWebViewProfileCreated = Directory.Exists(webViewProfile),
            networkAndUiUpdateWorkflowExecuted = false,
            userSettingsTouched = !userStateUnchanged,
            userStateBefore,
            userStateAfter
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS: actual MainWindow.Show/Loaded completed offscreen without activation; Graphite WebView2/RPC/version and settings reload passed; tray/runtime/external startup stayed disabled; close detached the HWND and real user state was unchanged.");
    }

    private static ShellViewModel CreateIsolatedShell(IsolatedHost host, ConnectorRuntimeServices runtime, string output, string name) =>
        new(host, host, runtime.Platform, runtime.LegacyPartConverter,
            new TeklaStandardService(new HttpClient { Timeout = TimeSpan.FromSeconds(25) }, Path.Combine(output, name + "-tekla-state")),
            new VpnProvisioningService(Path.Combine(output, name + "-vpn-state")),
            new ModelSharingProvisioningService(Path.Combine(output, name + "-model-sharing-state")),
            new IfcExportPatchService(Path.Combine(output, name + "-ifc-patch-state")));

    private static UserStateFingerprint CaptureUserStateFingerprint() => new(
        CaptureDirectoryFingerprint(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConnectorAgentDesktop")),
        CaptureDirectoryFingerprint(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Structura Connector")),
        CaptureDirectoryFingerprint(Environment.GetFolderPath(Environment.SpecialFolder.Startup)),
        CaptureAutoStartRegistryFingerprint());

    private static DirectoryFingerprint CaptureDirectoryFingerprint(string root)
    {
        if (!Directory.Exists(root)) return new DirectoryFingerprint(false, 0, 0, 0, 0, Convert.ToHexString(SHA256.HashData([])));
        var entries = new List<string>();
        int files = 0, directories = 0;
        long bytes = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", options).Order(StringComparer.OrdinalIgnoreCase))
        {
            var attributes = File.GetAttributes(path);
            bool directory = attributes.HasFlag(FileAttributes.Directory);
            long length = directory ? 0 : new FileInfo(path).Length;
            if (directory) directories++; else { files++; bytes += length; }
            entries.Add(string.Join('|', Path.GetRelativePath(root, path), directory ? "d" : "f", length,
                File.GetLastWriteTimeUtc(path).Ticks, (int)attributes));
        }
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries))));
        return new DirectoryFingerprint(true, files, directories, bytes, Directory.GetLastWriteTimeUtc(root).Ticks, hash);
    }

    private static RegistryValueFingerprint CaptureAutoStartRegistryFingerprint()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: false);
        const string valueName = "ConnectorAgentDesktop";
        bool exists = key?.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase) == true;
        if (!exists) return new RegistryValueFingerprint(false, string.Empty, string.Empty);
        object? value = key!.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        string serialized = value is string[] values ? string.Join('\n', values) : value?.ToString() ?? string.Empty;
        return new RegistryValueFingerprint(true, key.GetValueKind(valueName).ToString(), serialized);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private sealed record UserStateFingerprint(
        DirectoryFingerprint ConnectorAgentDesktop,
        DirectoryFingerprint StructuraConnector,
        DirectoryFingerprint StartupShortcuts,
        RegistryValueFingerprint AutoStartRegistry);

    private sealed record RegistryValueFingerprint(bool Exists, string Kind, string Value);

    private sealed record DirectoryFingerprint(
        bool Exists,
        int Files,
        int Directories,
        long Bytes,
        long RootLastWriteUtcTicks,
        string MetadataSha256);

    private sealed class IsolatedHost : IConnectorHost, IShellHost
    {
        private int _ensureVpnReadyCalls;
        private int _backgroundStartCalls, _backgroundStopCalls, _publicationStartCalls;
        public Window OwnerWindow { get; } = new();
        public AppSettings Settings { get; } = new() { AutoStart = false };
        public HeartbeatClient Heartbeat { get; } = new(new HttpClient());
        public bool HasPendingUpdate => false;
        public bool CanControlBackground { get; set; }
        public bool CanPublish { get; set; }
        public bool FailNextBackgroundStart { get; set; }
        public bool BackgroundRunning { get; private set; }
        public int BackgroundStartCalls => Volatile.Read(ref _backgroundStartCalls);
        public int BackgroundStopCalls => Volatile.Read(ref _backgroundStopCalls);
        public int PublicationStartCalls => Volatile.Read(ref _publicationStartCalls);
        public bool CanControlBackgroundConnection => CanControlBackground;
        public bool CanPublishTekla => CanPublish;
        public bool AllowCredentialProbe { get; set; }
        public string? ConnectedCredential { get; private set; }
        public ConcurrentQueue<DesktopPreferences> SavedDesktopPreferences { get; } = new();
        public TaskCompletionSource<DesktopPreferences> PreferencesSaved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> CallOrder { get; } = new();
        public TeklaPublicationRequest? ValidatedPublication { get; private set; }
        public TaskCompletionSource PublicationValidated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<TeklaPublicationRequest> PublicationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int EnsureVpnReadyCalls => Volatile.Read(ref _ensureVpnReadyCalls);
        public bool TeklaCheckInProgress { get; set; }
        public bool IsWindowVisible => false;
        public IReadOnlyList<ReleaseNoteItem> ReleaseNotes => [];
        public void Log(string message) => Console.WriteLine(message);
        public void SaveSettings() => throw new InvalidOperationException("Settings are isolated.");
        public Task SaveDesktopPreferencesAsync(DesktopPreferences preferences)
        {
            SavedDesktopPreferences.Enqueue(preferences);
            PreferencesSaved.TrySetResult(preferences);
            return Task.CompletedTask;
        }
        public Task StartBackgroundConnectionAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _backgroundStartCalls);
            if (FailNextBackgroundStart)
            {
                FailNextBackgroundStart = false;
                throw new InvalidOperationException("SMOKE_BACKGROUND_START_FAILED");
            }
            BackgroundRunning = true;
            return Task.CompletedTask;
        }
        public Task StopBackgroundConnectionAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _backgroundStopCalls);
            BackgroundRunning = false;
            return Task.CompletedTask;
        }
        public Task ValidateTeklaPublicationAsync(TeklaPublicationRequest request, CancellationToken cancellationToken)
        {
            if (!CanPublish) throw new UnauthorizedAccessException("SMOKE_PUBLISH_FORBIDDEN");
            ValidatedPublication = request;
            PublicationValidated.TrySetResult();
            return Task.CompletedTask;
        }
        public Task PublishTeklaAsync(TeklaPublicationRequest request, CancellationToken cancellationToken)
        {
            if (!CanPublish) throw new UnauthorizedAccessException("SMOKE_PUBLISH_FORBIDDEN");
            Interlocked.Increment(ref _publicationStartCalls);
            PublicationStarted.TrySetResult(request);
            return Task.CompletedTask;
        }
        public Task EnsureVpnReadyAsync()
        {
            Interlocked.Increment(ref _ensureVpnReadyCalls);
            CallOrder.Enqueue("vpn");
            return Task.CompletedTask;
        }
        public Task ConnectByTokenAsync(string token, bool showSuccessDialog)
        {
            if (!AllowCredentialProbe) throw new InvalidOperationException("Network disabled in smoke.");
            ConnectedCredential = token;
            return Task.CompletedTask;
        }
        public Task CheckUpdatesAsync(bool showDialogs) => throw new InvalidOperationException("Network disabled in smoke.");
        public Task InstallPendingUpdateAsync(bool confirmBeforeRun) => throw new InvalidOperationException("Updates disabled in smoke.");
        public Task RunTeklaInteractiveSyncAsync() => throw new InvalidOperationException("Tekla modification disabled in smoke.");
        public Task RestartManagedServerAsync(string serviceKey, System.Windows.Controls.Button button, string displayName) => throw new InvalidOperationException("Server modification disabled in smoke.");
        public MessageBoxResult ShowDialog(string message, string title, MessageBoxButton buttons, MessageBoxImage image) => throw new InvalidOperationException("Foreground dialogs disabled in smoke.");
        public void SetServerConnectionFailed(bool failed) { }
        public void UpdateHeaderStatus() { }
        public void OnTeklaStatusChanged(string overallText, System.Windows.Media.Brush overallBrush, string actionButtonContent, bool actionIsSyncStyle, bool inProgress) { }
        public void ShowTrayBalloon(int durationMs, string title, string message, bool isWarning) { }
        public void ResetTeklaPendingBalloon() { }
    }

    private sealed class RecordingAutoStartService : IAutoStartService
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource<bool> Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsEnabled() => false;
        public void SetEnabled(bool enabled)
        {
            Interlocked.Increment(ref _calls);
            Called.TrySetResult(enabled);
        }
    }

    private sealed class FakePlatformConnection(ConcurrentQueue<string> callOrder) : IPlatformConnectionApi
    {
        private readonly ConcurrentQueue<string> _callOrder = callOrder;
        private PlatformConnectionSnapshot _connection = new(false, true, false, true, "https://platform.invalid", ConnectorRuntimePhase.Stopped, null, null);
        private TaskCompletionSource<PlatformOperation> _nextOperation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource? _reconnectRelease;

        public PlatformConnectionSnapshot Connection => _connection;
        public string StoredCredential { get; } = "platform-isolated-credential";
        public string? LastToken { get; private set; }
        public int ConnectCalls { get; private set; }
        public int ReconnectCalls { get; private set; }
        public int DisconnectCalls { get; private set; }
        public TaskCompletionSource OperationCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PlatformOperation> WaitForNextOperationAsync()
            => Volatile.Read(ref _nextOperation).Task;

        public TaskCompletionSource HoldNextReconnect()
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _reconnectRelease, release, null) is not null)
                throw new InvalidOperationException("Reconnect release is already armed.");
            return release;
        }

        private void RecordOperation(PlatformOperation operation)
        {
            Interlocked.Exchange(ref _nextOperation,
                new TaskCompletionSource<PlatformOperation>(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult(operation);
        }

        public Task<PlatformOperationResult> ConnectAsync(string? oneTimeToken = null, CancellationToken cancellationToken = default)
        {
            ConnectCalls++;
            RecordOperation(PlatformOperation.Connect);
            LastToken = oneTimeToken;
            _callOrder.Enqueue("platform-connect");
            OperationCalled.TrySetResult();
            _connection = _connection with { ErrorCode = "SMOKE_PLATFORM_CONNECT_FAILED", Message = "Isolated platform failure." };
            return Task.FromResult(PlatformOperationResult.Fail("SMOKE_PLATFORM_CONNECT_FAILED", "Isolated platform failure."));
        }

        public async Task<PlatformOperationResult> ReconnectAsync(CancellationToken cancellationToken = default)
        {
            ReconnectCalls++;
            RecordOperation(PlatformOperation.Reconnect);
            _callOrder.Enqueue("platform-connect");
            OperationCalled.TrySetResult();
            var release = Interlocked.Exchange(ref _reconnectRelease, null);
            if (release is not null) await release.Task.WaitAsync(cancellationToken);
            _connection = _connection with { ErrorCode = "SMOKE_PLATFORM_RECONNECT_FAILED", Message = "Isolated platform failure." };
            return PlatformOperationResult.Fail("SMOKE_PLATFORM_RECONNECT_FAILED", "Isolated platform failure.");
        }
        public Task<PlatformOperationResult> DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectCalls++;
            return Task.FromResult(PlatformOperationResult.Accept("SMOKE_PLATFORM_DISCONNECTED", "Disconnected."));
        }
        public PlatformOperationResult ImportLegacyCredential() =>
            PlatformOperationResult.Reject("SMOKE_LEGACY_IMPORT_FORBIDDEN", "Platform fixture already owns its credential.");
    }

    private enum PlatformOperation
    {
        Connect,
        Reconnect
    }

    private static async Task WaitForNoticeAsync(GraphiteDesktopController controller, string command, string message)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSnapshot(object snapshot)
        {
            using var json = JsonSerializer.SerializeToDocument(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (json.RootElement.TryGetProperty("notice", out var notice) && notice.ValueKind == JsonValueKind.Object &&
                notice.TryGetProperty("command", out var actualCommand) && actualCommand.GetString() == command &&
                notice.TryGetProperty("message", out var actualMessage) && actualMessage.GetString() == message)
                received.TrySetResult();
        }

        controller.SnapshotChanged += OnSnapshot;
        try { await received.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { controller.SnapshotChanged -= OnSnapshot; }
    }

    private static async Task WaitForDocumentAsync(CoreWebView2 core)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            if (await core.ExecuteScriptAsync("Boolean(document.readyState==='complete' && window.CONNECTOR_DESKTOP_BRIDGE && typeof window.CONNECTOR_DESKTOP_RENDER_SNAPSHOT==='function' && document.querySelector('#navigation [data-page]'))") == "true") return;
            await Task.Delay(50, timeout.Token);
        }
        throw new TimeoutException("Local Graphite document did not load.");
    }

    private static async Task WaitForTextAsync(CoreWebView2 core, string text)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            var actual = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.querySelector('#connection-status').textContent"));
            if (actual == text) return;
            await Task.Delay(50, timeout.Token);
        }
        throw new TimeoutException("Native snapshot was not rendered.");
    }
}
