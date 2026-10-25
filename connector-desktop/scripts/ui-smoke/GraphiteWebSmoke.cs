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
using Connector.Desktop.Features.ConverterWorkspace;
using Connector.Desktop.Features.PlatformTools;
using Connector.Desktop.Models;
using Connector.Desktop.Services;
using Connector.Desktop.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace Connector.UiSmoke;

/// <summary>
/// Native WebView2 smoke of the «Платформа Коннектор» interface (desktop.html, ИТ-А markup). Runs in an invisible
/// owned HWND and an offscreen MainWindow with isolated profiles; no network, no user settings, no foreground.
/// The account is never signed in here, so it covers the sign-in screen, the RPC boundary (theme, window buttons,
/// refusals with their own message), hydration without secrets and the MainWindow lifecycle.
/// </summary>
internal static class GraphiteWebSmoke
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    internal static int Run(string outputDirectory)
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
            try { await RunAsync(output); exitCode = 0; }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { application.Dispatcher.InvokeShutdown(); }
        });
        Dispatcher.Run();
        return exitCode;
    }

    private static async Task RunAsync(string output)
    {
        UserStateFingerprint userStateBefore = CaptureUserStateFingerprint();
        using var source = new HwndSource(new HwndSourceParameters("Connector isolated UI smoke")
        {
            Width = 1280, Height = 840, WindowStyle = unchecked((int)0x80000000),
            PositionX = -32000, PositionY = -32000
        });
        await RunRendererContractAsync(source, output);
        await RunControllerBoundaryAsync(source, output);
        await RunMainWindowAcceptanceAsync(output, userStateBefore);

        UserStateFingerprint userStateAfter = CaptureUserStateFingerprint();
        bool userStateUnchanged = userStateBefore == userStateAfter;
        await File.WriteAllTextAsync(Path.Combine(output, "native-user-state-isolation.json"),
            JsonSerializer.Serialize(new { userStateUnchanged, before = userStateBefore, after = userStateAfter }, Indented));
        if (!userStateUnchanged)
            throw new InvalidOperationException("Native smoke changed the real ConnectorAgentDesktop user-state tree.");
    }

    // A signed-in fixture snapshot in the controller's shape; values are examples.
    private static object SignedInFixture(string status = "Подключено") => new
    {
        reset = true, commonAccessSelected = true, commonAccessReady = true, connectPending = false, signInPending = false,
        hasSavedCredential = true, status,
        allowedPages = new { structura = new[] { "tekla", "folders", "converters" }, platform = new[] { "agr", "bridge", "autocad" } },
        moduleFields = new Dictionary<string, string> { ["app-version"] = "smoke", ["bridge-status"] = "Не проверено" },
        files = Array.Empty<object>(), ifcFiles = Array.Empty<object>(), history = Array.Empty<object>(),
        assignedFolders = Array.Empty<object>(), cadSessions = Array.Empty<object>(), uiTheme = "dark"
    };

    private static async Task RunRendererContractAsync(HwndSource source, string output)
    {
        var view = new GraphiteWebView { Width = 1280, Height = 840 };
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
        view.Measure(new Size(1280, 840)); view.Arrange(new Rect(0, 0, 1280, 840)); view.UpdateLayout();
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await view.InitializeAsync(Path.Combine(output, "isolated-webview-profile"));
        var core = ((WebView2)view.FindName("Browser")).CoreWebView2 ?? throw new InvalidOperationException("Native WebView2 failed to initialize.");
        try
        {
            await WaitForDocumentAsync(core);
            await view.PublishSnapshotAsync(SignedInFixture());
            await WaitForConditionAsync(core, "Boolean(document.querySelector('.side [data-page=\"tekla\"]'))");
            var pages = new List<string>();
            foreach (var page in new[] { "home", "tekla", "converters" })
            {
                await core.ExecuteScriptAsync($"document.querySelector('.side [data-ui=\"go\"][data-page=\"{page}\"]').click()");
                await WaitForConditionAsync(core, $"window.PC.state.ui.page==='{page}'");
                pages.Add(JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(
                    "JSON.stringify({page:PC.state.ui.page,title:document.querySelector('.page-head h1').textContent,sidebar:document.querySelector('.side').getBoundingClientRect().width,manrope:document.fonts.check('600 13px Manrope'),width:innerWidth})"))!);
                using var image = File.Create(Path.Combine(output, "renderer-" + page + ".png"));
                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
            }
            if (pages.Any(page => !page.Contains("\"manrope\":true", StringComparison.Ordinal)))
                throw new InvalidOperationException("Manrope was not loaded from the local ui/fonts folder.");
            if (commands != 0) throw new InvalidOperationException("Rendering executed a business command.");
            string response = await InvokeBridgeAsync(core, "ifc-detect", "{mode:'structura',fields:{}}");
            if (!JsonDocument.Parse(response).RootElement.GetProperty("ok").GetBoolean() || commands != 1)
                throw new InvalidOperationException("Native RPC was not round-tripped.");
            await view.PublishSnapshotAsync(new { reset = true, commonAccessReady = false, hasSavedCredential = false, status = "Smoke: snapshot delivered" });
            await WaitForConditionAsync(core, "Boolean(document.querySelector('.signin')) && document.querySelector('.titlebar .chip').textContent.includes('Smoke: snapshot delivered')");
            core.Navigate("https://example.invalid/");
            await core.ExecuteScriptAsync("Promise.resolve()");
            if (!core.Source.StartsWith("https://connector.local/desktop.html", StringComparison.Ordinal))
                throw new InvalidOperationException("Untrusted navigation was allowed.");
            await File.WriteAllTextAsync(Path.Combine(output, "native-webview-smoke.json"), JsonSerializer.Serialize(new
            { pages, commands, nonClientRegionEnabled = view.NonClientRegionEnabled, noForeground = true, blockedExternalNavigation = true }, Indented));
            Console.WriteLine("PASS: native WebView2 in an invisible HWND: 3 sections from the registry, local Manrope, real JSON RPC, pushed snapshot switches to sign-in, external navigation blocked.");
        }
        finally { await view.DisposeAsync(); await view.DisposeAsync(); }
    }

    private static async Task RunControllerBoundaryAsync(HwndSource source, string output)
    {
        // In-memory settings only; the real settings file is never read or written.
        const string secret = "isolated-settings-credential";
        var host = new IsolatedHost();
        host.Settings.TokenCipherBase64 = SettingsService.EncryptToken(secret);
        host.Settings.TeklaStandardLocalPath = @"C:\HydrationFixture\CompanyFirm";
        bool closingRouted = false;
        // The owner window is never shown here; WPF may close it without Closing, so either event proves the route.
        host.OwnerWindow.Closing += (_, args) => { closingRouted = true; args.Cancel = true; };
        host.OwnerWindow.Closed += (_, _) => closingRouted = true;
        await using var runtime = new ConnectorRuntimeServices(Path.Combine(output, "boundary-agent"));
        var shell = CreateIsolatedShell(host, runtime, output, "boundary");
        using var workspace = new ConverterWorkspaceModule(runtime.ConverterClient, action => source.Dispatcher.BeginInvoke(action));
        using var tools = new PlatformToolsModule();
        // The job journal of the smoke lives in its own output folder, never in the user's profile.
        await using var controller = new GraphiteDesktopController(host, runtime, shell, tools.ViewModel, workspace.ViewModel,
            journalPath: Path.Combine(output, "boundary-journal", "jobs.json"));
        await using var view = new GraphiteWebView { Width = 1280, Height = 840 };
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        view.Loaded += (_, _) => loaded.TrySetResult();
        view.Configure(controller.HandleAsync, Console.WriteLine, controller.CreateDocumentSnapshot);
        controller.SnapshotChanged += snapshot => _ = view.PublishSnapshotAsync(snapshot);
        source.RootVisual = view;
        view.Measure(new Size(1280, 840)); view.Arrange(new Rect(0, 0, 1280, 840)); view.UpdateLayout();
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await view.PublishSnapshotAsync(controller.CreateSnapshot());
        await view.InitializeAsync(Path.Combine(output, "isolated-boundary-webview-profile"));
        var core = ((WebView2)view.FindName("Browser")).CoreWebView2 ?? throw new InvalidOperationException("Boundary WebView is unavailable.");
        await WaitForDocumentAsync(core);

        // Not signed in: the sign-in screen with its service links; no secret or private path in the document state.
        await WaitForConditionAsync(core, "Boolean(document.querySelector('.signin [data-cmd=\"connect\"]')) && Boolean(document.querySelector('.signin [data-cmd=\"settings-log\"]')) && Boolean(document.querySelector('.signin [data-cmd=\"diagnostics\"]'))");
        string hydration = JsonSerializer.Serialize(controller.CreateDocumentSnapshot());
        if (hydration.Contains(secret, StringComparison.Ordinal) || hydration.Contains(host.Settings.TokenCipherBase64, StringComparison.Ordinal) ||
            hydration.Contains("HydrationFixture", StringComparison.Ordinal))
            throw new InvalidOperationException("Document hydration disclosed a private path or credential.");
        using (var image = File.Create(Path.Combine(output, "boundary-sign-in.png")))
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);

        // Theme: saved natively through DesktopPreferences, applied by the document, kept after a reload.
        await InvokeBridgeAsync(core, "ui-theme", "{mode:'structura',page:'settings',fields:{'ui-theme':'light'}}");
        var themePreferences = await host.PreferencesSaved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (themePreferences.UiTheme != "light") throw new InvalidOperationException("Theme was not saved through DesktopPreferences.");
        await WaitForConditionAsync(core, "document.documentElement.dataset.theme==='light'");
        core.Reload();
        await WaitForDocumentAsync(core);
        await WaitForConditionAsync(core, "document.documentElement.dataset.theme==='light'");

        // Refusals reach the user with their own text (no «Команда не выполнена.»).
        string unknownTheme = await InvokeBridgeAsync(core, "ui-theme", "{mode:'structura',fields:{'ui-theme':'sepia'}}");
        if (!unknownTheme.Contains("Неизвестная тема оформления.", StringComparison.Ordinal))
            throw new InvalidOperationException("A user refusal lost its message: " + unknownTheme);
        await WaitForConditionAsync(core, "[...document.querySelectorAll('.toast.error')].some(t=>t.innerText.includes('Неизвестная тема оформления.'))");
        string denied = await InvokeBridgeAsync(core, "xs-open", "{mode:'structura',page:'tekla',fields:{'xs-path':'C:\\\\Windows'}}");
        if (!denied.Contains("Для этого действия нет доступа", StringComparison.Ordinal))
            throw new InvalidOperationException("A folder command ran without a grant: " + denied);

        // Window buttons: maximize toggles the owner window, close goes through Closing (tray behaviour).
        await InvokeBridgeAsync(core, "window-maximize", "{mode:'structura',fields:{}}");
        await WaitForAsync(() => host.OwnerWindow.WindowState == WindowState.Maximized, "window-maximize");
        await InvokeBridgeAsync(core, "window-maximize", "{mode:'structura',fields:{}}");
        await WaitForAsync(() => host.OwnerWindow.WindowState == WindowState.Normal, "window-restore");
        await InvokeBridgeAsync(core, "window-close", "{mode:'structura',fields:{}}");
        await WaitForAsync(() => closingRouted, "window-close");

        await File.WriteAllTextAsync(Path.Combine(output, "native-boundary-contracts.json"), JsonSerializer.Serialize(new
        {
            signInScreenWithServiceLinks = true, secretsAndPathsStayNative = true, themeSavedAndHydrated = true,
            userRefusalMessagePassedThrough = true, permissionGateHeld = true, windowButtons = true, closeRoutedThroughClosing = closingRouted
        }, Indented));
        Console.WriteLine("PASS: real controller in native WebView2: sign-in screen and links, theme saved/hydrated, refusals keep their text, folder command gated, window buttons and close-to-tray path.");
    }

    private static async Task RunMainWindowAcceptanceAsync(string output, UserStateFingerprint userStateBefore)
    {
        string fixtureRoot = Path.Combine(output, "main-window-native-acceptance");
        Directory.CreateDirectory(fixtureRoot);
        var settings = new SettingsService(Path.Combine(fixtureRoot, "settings.json"));
        AppSettings seeded = settings.Load();
        seeded.AutoStart = false;
        seeded.UiTheme = "dark";
        seeded.ConverterOutputDirectory = Directory.CreateDirectory(Path.Combine(fixtureRoot, "results")).FullName;
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
        var tray = typeof(MainWindow).GetField("_trayIcon", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
            as System.Windows.Forms.NotifyIcon ?? throw new InvalidOperationException("Actual MainWindow tray icon was not composed.");
        var graphiteController = typeof(MainWindow).GetField("_graphite", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
            as GraphiteDesktopController ?? throw new InvalidOperationException("Actual MainWindow Graphite controller was not composed.");
        var backgroundAgent = typeof(MainWindow).GetField("_backgroundAgent", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
            as DesktopBackgroundAgent ?? throw new InvalidOperationException("Actual MainWindow background Agent was not composed.");
        string expectedApplicationVersion = typeof(GraphiteDesktopController).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+', 2)[0].Trim()
            ?? throw new InvalidOperationException("Connector.Desktop has no informational version.");
        using JsonDocument snapshotDocument = JsonDocument.Parse(JsonSerializer.Serialize(graphiteController.CreateSnapshot()));
        string snapshotApplicationVersion = snapshotDocument.RootElement.GetProperty("moduleFields").GetProperty("app-version").GetString()
            ?? throw new InvalidOperationException("Graphite snapshot has no application version.");
        if (snapshotApplicationVersion != expectedApplicationVersion)
            throw new InvalidOperationException($"Graphite snapshot version '{snapshotApplicationVersion}' did not match '{expectedApplicationVersion}'.");
        bool windowShown = false, loadedHooksRun = false, offscreen = false, windowNeverForeground = false;
        bool externalLoadedActionsSuppressed = false, trayHiddenDuringLoaded = false, cleanupCompleted = false;
        bool settingsRpcPersisted = false, themePersisted = false, captionMode = false;

        try
        {
            window.Width = 1280;
            window.Height = 840;
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
            externalLoadedActionsSuppressed = autoStart.Calls == 0 && !runtime.Host.IsRunning && !backgroundAgent.IsRunning;
            if (!windowShown || !loadedHooksRun || !windowNeverForeground || !offscreen || !trayHiddenDuringLoaded || !externalLoadedActionsSuppressed)
                throw new InvalidOperationException("MainWindow Show/Loaded did not preserve the isolated offscreen lifecycle contract.");
            // Either the page title bar moves the window, or the system caption came back.
            captionMode = view.NonClientRegionEnabled
                ? System.Windows.Shell.WindowChrome.GetWindowChrome(window) is not null
                : System.Windows.Shell.WindowChrome.GetWindowChrome(window) is null;
            if (!captionMode) throw new InvalidOperationException("The window has neither a draggable page title bar nor a system caption.");

            var core = ((WebView2)view.FindName("Browser")).CoreWebView2
                ?? throw new InvalidOperationException("Actual MainWindow WebView2 failed to initialize.");
            await WaitForDocumentAsync(core);
            await WaitForConditionAsync(core, "Boolean(document.querySelector('.signin [data-cmd=\"connect\"]'))");

            await InvokeBridgeAsync(core, "settings-store", "{mode:'structura',page:'settings',fields:{autostart:true}}");
            bool requestedAutoStart = await autoStart.Called.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await InvokeBridgeAsync(core, "ui-theme", "{mode:'structura',page:'settings',fields:{'ui-theme':'light'}}");
            AppSettings persisted;
            using (var saveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                do
                {
                    persisted = settings.Load();
                    if (persisted.AutoStart && persisted.UiTheme == "light") break;
                    await Task.Delay(25, saveTimeout.Token);
                } while (true);
            }
            settingsRpcPersisted = requestedAutoStart && autoStart.Calls == 1 && persisted.AutoStart;
            themePersisted = persisted.UiTheme == "light";
            if (!settingsRpcPersisted || !themePersisted)
                throw new InvalidOperationException("Actual MainWindow settings RPC did not persist through the injected native services.");

            core.Reload();
            await WaitForDocumentAsync(core);
            await WaitForConditionAsync(core, "document.documentElement.dataset.theme==='light' && Boolean(document.querySelector('.signin'))");
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
                $"Shown MainWindow cleanup or user-state isolation was not confirmed: registered={applicationWindowRegistered}, " +
                $"windowsRestored={applicationWindowsRestored}, presentationDetached={presentationDetached}, visible={window.IsVisible}, " +
                $"userStateUnchanged={userStateUnchanged}.");
        await File.WriteAllTextAsync(Path.Combine(output, "main-window-native-acceptance.json"), JsonSerializer.Serialize(new
        {
            actualMainWindow = true, windowShown, loadedHooksRun, offscreen, windowNeverForeground, trayHiddenDuringLoaded,
            externalLoadedActionsSuppressed, nonClientRegionEnabled = view.NonClientRegionEnabled, captionMode,
            expectedApplicationVersion, snapshotApplicationVersion, settingsRpcPersisted, themePersisted,
            autoStartRedirectedToInjectedService = autoStart.Calls == 1, applicationWindowRegistered, applicationWindowsRestored,
            presentationDetached, cleanupCompleted, isolatedWebViewProfileCreated = Directory.Exists(webViewProfile),
            networkAndUiUpdateWorkflowExecuted = false, userSettingsTouched = !userStateUnchanged
        }, Indented));
        Console.WriteLine("PASS: actual MainWindow.Show/Loaded offscreen without activation; caption mode valid; sign-in screen; autostart and theme persisted through RPC and hydrated after reload; real user state unchanged.");
    }

    private static ShellViewModel CreateIsolatedShell(IsolatedHost host, ConnectorRuntimeServices runtime, string output, string name) =>
        new(host, host, runtime.Platform, runtime.LegacyPartConverter,
            new TeklaStandardService(new HttpClient { Timeout = TimeSpan.FromSeconds(25) }, Path.Combine(output, name + "-tekla-state")),
            new VpnProvisioningService(Path.Combine(output, name + "-vpn-state")),
            new ModelSharingProvisioningService(Path.Combine(output, name + "-model-sharing-state")),
            new IfcExportPatchService(Path.Combine(output, name + "-ifc-patch-state")));

    private static async Task<string> InvokeBridgeAsync(CoreWebView2 core, string command, string payload)
    {
        await core.ExecuteScriptAsync($"window.__smokeResponse=null;window.CONNECTOR_DESKTOP_BRIDGE.invoke('{command}',{payload}).then(value=>window.__smokeResponse=value)");
        await WaitForConditionAsync(core, "window.__smokeResponse!==null");
        return JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("JSON.stringify(window.__smokeResponse)"))!;
    }

    private static async Task WaitForAsync(Func<bool> condition, string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (timeout.IsCancellationRequested) throw new TimeoutException("Native condition not reached: " + name);
            await Task.Delay(25);
        }
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
            Console.WriteLine(await core.ExecuteScriptAsync("JSON.stringify({url:location.href,text:document.querySelector('#app')?.innerText})"));
            throw;
        }
    }

    private static async Task WaitForDocumentAsync(CoreWebView2 core)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            if (await core.ExecuteScriptAsync("Boolean(document.readyState==='complete' && window.CONNECTOR_DESKTOP_BRIDGE && typeof window.CONNECTOR_DESKTOP_RENDER_SNAPSHOT==='function' && document.querySelector('#app .titlebar'))") == "true") return;
            await Task.Delay(50, timeout.Token);
        }
        throw new TimeoutException("Local desktop document did not load.");
    }

    private static UserStateFingerprint CaptureUserStateFingerprint() => new(
        CaptureDirectoryFingerprint(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConnectorAgentDesktop")),
        CaptureDirectoryFingerprint(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Structura Connector")),
        CaptureDirectoryFingerprint(Environment.GetFolderPath(Environment.SpecialFolder.Startup)),
        CaptureAutoStartRegistryFingerprint());

    private static DirectoryFingerprint CaptureDirectoryFingerprint(string root)
    {
        if (!Directory.Exists(root)) return new DirectoryFingerprint(false, 0, 0, 0, 0, Convert.ToHexString(SHA256.HashData([])));
        var entries = new List<string>();
        int files = 0, directories = 0;
        long bytes = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };
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

    private sealed record DirectoryFingerprint(bool Exists, int Files, int Directories, long Bytes, long RootLastWriteUtcTicks, string MetadataSha256);

    private sealed class IsolatedHost : IConnectorHost, IShellHost
    {
        public Window OwnerWindow { get; } = new();
        public AppSettings Settings { get; } = new() { AutoStart = false };
        public HeartbeatClient Heartbeat { get; } = new(new HttpClient());
        public bool HasPendingUpdate => false;
        public ConcurrentQueue<DesktopPreferences> SavedDesktopPreferences { get; } = new();
        public TaskCompletionSource<DesktopPreferences> PreferencesSaved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
        public Task ConnectByTokenAsync(string token, bool showSuccessDialog) => throw new InvalidOperationException("Network disabled in smoke.");
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
        public ILocalJobProgress BeginOperation(string kind, string title, string? subject, string firstStep) =>
            new ConnectorJobJournal(null).BeginLocal(kind, title, subject, firstStep);
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
}
