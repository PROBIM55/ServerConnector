using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Connector.Desktop;
using Connector.Desktop.Features.GraphiteWeb;
using Connector.Desktop.Features.Tekla.Standard;
using Connector.Desktop.Models;
using Connector.Desktop.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace Connector.UiSmoke;

/// <summary>
/// Acceptance of the «Платформа Коннектор» interface in the REAL MainWindow (WindowChrome frame + WebView2):
/// window behaviour (drag region, resize edges, window buttons, maximize geometry) under the real controller, then
/// fixture states rendered through the production snapshot path and captured with PrintWindow and CapturePreview.
/// Isolated settings/runtime/journal/WebView2 profile, no network (dead proxy for the browser, no external actions),
/// window offscreen and never activated except while maximized for the geometry check.
/// </summary>
internal static class WindowAcceptance
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private const int Inset = 6; // MainWindow: WebView2 margin = WindowChrome.ResizeBorderThickness in the normal state

    // Only the WebView2 browser is network-isolated here; the .NET side relies on runLoadedExternalActions:false and
    // fixtures (no sign-in, no update check, no Tekla/NetBird calls). The browser process of the test host never reaches a network: a dead proxy for every URL (loopback included),
    // no background networking, no reputation checks. connector.local is served from the folder mapping.
    private const string IsolatedBrowserArguments =
        "--proxy-server=http://127.0.0.1:9 --proxy-bypass-list=<-loopback> --disable-background-networking " +
        "--disable-component-update --no-pings --disable-features=msSmartScreenProtection,CalculateNativeWinOcclusion";

    internal static int Run(string outputDirectory, string statesPath, double scale, bool minimumSize = false)
    {
        Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", IsolatedBrowserArguments);
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var resource in new[] { "Theme", "Graphite" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri($"/Connector.Desktop;component/Resources/{resource}.xaml", UriKind.Relative) });
        int exitCode = 1;
        application.Dispatcher.BeginInvoke(async () =>
        {
            try { exitCode = await RunAsync(output, Path.GetFullPath(statesPath), scale, minimumSize); }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { application.Dispatcher.InvokeShutdown(); }
        });
        Dispatcher.Run();
        return exitCode;
    }

    private static async Task<int> RunAsync(string output, string statesPath, double scale, bool minimumSize)
    {
        var report = new Dictionary<string, object?> { ["scale"] = scale, ["browserArguments"] = IsolatedBrowserArguments };
        var fingerprintBefore = Fingerprint();
        string root = Path.Combine(output, "isolated-root");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);

        var settings = new SettingsService(Path.Combine(root, "settings.json"));
        AppSettings seeded = settings.Load();
        seeded.AutoStart = false;
        seeded.UiTheme = "dark";
        seeded.ConverterOutputDirectory = Directory.CreateDirectory(Path.Combine(root, "results")).FullName;
        settings.Save(seeded);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new ConnectorRuntimeServices(Path.Combine(root, "agent"));
        var window = new MainWindow(new MainWindowServices(settings, new NullAutoStart(), runtime,
            new TeklaStandardService(new HttpClient { Timeout = TimeSpan.FromSeconds(5) }, Path.Combine(root, "window-tekla")),
            new TeklaStandardService(new HttpClient { Timeout = TimeSpan.FromSeconds(5) }, Path.Combine(root, "shell-tekla")),
            new VpnProvisioningService(Path.Combine(root, "vpn")),
            new ModelSharingProvisioningService(Path.Combine(root, "model-sharing")),
            new IfcExportPatchService(Path.Combine(root, "ifc-patch")),
            new PackageUpdateService(null),
            trayIconVisible: false, allowOwnedFixtureClose: true, runLoadedExternalActions: false,
            webViewUserDataDirectory: Path.Combine(root, "webview-profile"),
            loadedInitializationCompleted: () => loaded.TrySetResult(),
            jobJournalPath: Path.Combine(root, "journal", "jobs.json")));
        var view = (GraphiteWebView)window.FindName("GraphiteWebHost");
        var browser = (WebView2)view.FindName("Browser");
        var controller = (GraphiteDesktopController)typeof(MainWindow)
            .GetField("_graphite", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

        // scale 1: page 1280×840 CSS px plus the 6 px native resize edge on every side (WindowChrome client).
        // scale 1.5: WebView2 zoom 1.5 (not real monitor DPI) in a window filling the work area, i.e. the CSS viewport
        // of a 1920×1080 screen at 150 %. min: the window at MinWidth×MinHeight.
        double width = scale == 1 ? 1280 + 2 * Inset : SystemParameters.WorkArea.Width;
        double height = scale == 1 ? 840 + 2 * Inset : SystemParameters.WorkArea.Height;
        if (minimumSize) { width = window.MinWidth; height = window.MinHeight; }
        report["windowSize"] = new { width, height, window.MinWidth, window.MinHeight, minimumSize };
        window.Width = width; window.Height = height;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Left = SystemParameters.VirtualScreenLeft - width - 512;
        window.Top = SystemParameters.VirtualScreenTop;
        IntPtr foregroundBefore = GetForegroundWindow();
        window.Show();
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30));
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        var core = browser.CoreWebView2 ?? throw new InvalidOperationException("WebView2 did not initialize.");

        var problems = new ConcurrentQueue<string>();
        var requests = new ConcurrentQueue<string>();
        await AttachDiagnosticsAsync(core, problems, requests);
        core.Reload();
        await WaitForDocumentAsync(core);
        await ApplyScaleAsync(browser, core, scale);

        string log = ((System.Windows.Controls.TextBox)window.FindName("LogTextBox")).Text;
        report["nonClientRegionEnabled"] = view.NonClientRegionEnabled;
        report["windowChromeKept"] = System.Windows.Shell.WindowChrome.GetWindowChrome(window) is not null;
        report["nativeLog"] = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        report["signInPage"] = await PageFactsAsync(core);
        report["hitTest"] = HitTests(window, hwnd, browser, await TitlebarPointsAsync(core), scale);
        // The window buttons and the maximize geometry do not depend on the page zoom; checked in the 100 % run.
        if (scale == 1 && !minimumSize) report["windowButtons"] = await WindowButtonsAsync(window, hwnd, browser, core, output, scale);

        // Fixture states through the production snapshot path; the real controller no longer pushes its own state.
        typeof(GraphiteDesktopController).GetField("SnapshotChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(controller, null);
        var states = JsonNode.Parse(await File.ReadAllTextAsync(statesPath))!.AsArray();
        var stateReports = new List<object>();
        report["windowPhaseProblems"] = problems.ToArray();
        bool anyProblems = !problems.IsEmpty;
        string theme = "dark";
        foreach (var state in states)
        {
            string name = (string)state!["name"]!;
            if ((string)state["theme"]! != theme)
            {
                theme = (string)state["theme"]!;
                await InvokeBridgeAsync(core, "ui-theme", $"{{mode:'structura',page:'settings',fields:{{'ui-theme':'{theme}'}}}}");
                await WaitForAsync(() => window.Background is System.Windows.Media.SolidColorBrush b &&
                    (theme == "light" ? b.Color.R == 0xE6 : b.Color.R == 0x1A), "native window theme " + theme);
            }
            problems.Clear();
            core.Reload();
            await WaitForDocumentAsync(core);
            await ApplyScaleAsync(browser, core, scale);
            await view.PublishSnapshotAsync(ToElement(state["snapshot"]!));
            await WaitForConditionAsync(core, "window.PC && PC.state.snapshotSeen===true");
            // The renderer keeps the last pointer position across reloads; park it where nothing reacts to hover.
            await Cdp(core, "Input.dispatchMouseEvent", new { type = "mouseMoved", x = 2.0, y = 50.0, button = "none" });
            var notes = new List<string>();
            foreach (var step in state["steps"]!.AsArray())
            {
                switch ((string)step!["op"]!)
                {
                    case "click": notes.AddRange(await ClickAsync(core, (string)step["selector"]!)); break;
                    case "emit": await view.PublishSnapshotAsync(ToElement(step["snapshot"]!)); break;
                    case "palette": notes.AddRange(await PaletteAsync(core, (string)step["text"]!)); break;
                }
                await SettleAsync(core);
            }
            await SettleAsync(core);
            var facts = await PageFactsAsync(core);
            await CaptureAsync(core, hwnd, output, name);
            anyProblems |= !problems.IsEmpty || JsonNode.Parse(facts)!["csp"]!.AsArray().Count > 0;
            stateReports.Add(new { name, facts = JsonNode.Parse(facts), notes, problems = problems.ToArray(), frameColor = window.Background.ToString() });
            Console.WriteLine($"{name}: {(problems.IsEmpty ? "ok" : "PROBLEMS " + string.Join(" | ", problems))} {string.Join(' ', notes)}");
        }
        report["states"] = stateReports;
        report["requests"] = requests.Distinct().ToArray();
        report["foregroundStolenAtEnd"] = GetForegroundWindow() == hwnd;
        report["foregroundBefore"] = foregroundBefore.ToString();

        await window.CloseOwnedCompositionAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var fingerprintAfter = Fingerprint();
        var changed = fingerprintBefore.Keys.Union(fingerprintAfter.Keys)
            .Where(key => fingerprintBefore.GetValueOrDefault(key) != fingerprintAfter.GetValueOrDefault(key)).ToArray();
        report["userStateChanged"] = changed;
        report["userStateEntries"] = fingerprintBefore.Count;
        await File.WriteAllTextAsync(Path.Combine(output, "window-report.json"), JsonSerializer.Serialize(report, Indented));
        bool external = requests.Any(uri => !uri.StartsWith("https://connector.local/", StringComparison.Ordinal) && !uri.StartsWith("data:", StringComparison.Ordinal));
        if (requests.IsEmpty) throw new InvalidOperationException("No renderer request was observed; the network record is not trustworthy.");
        Console.WriteLine($"DONE: {states.Count} states; problems={anyProblems}; externalRequests={external}; userStateChanged={changed.Length}");
        // Console errors/warnings, exceptions, CSP violations or failed loads fail the run as well.
        return changed.Length == 0 && !external && !anyProblems ? 0 : 2;
    }

    private static JsonElement ToElement(JsonNode node) => JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

    // ---- diagnostics ---------------------------------------------------------------------------------------------

    private static async Task AttachDiagnosticsAsync(CoreWebView2 core, ConcurrentQueue<string> problems, ConcurrentQueue<string> requests)
    {
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            "window.__csp=[];document.addEventListener('securitypolicyviolation',e=>window.__csp.push(e.violatedDirective+' '+e.blockedURI));");
        await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        await core.CallDevToolsProtocolMethodAsync("Log.enable", "{}");
        await core.CallDevToolsProtocolMethodAsync("Emulation.setFocusEmulationEnabled", "{\"enabled\":true}");
        core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived += (_, e) =>
        {
            var node = JsonNode.Parse(e.ParameterObjectAsJson)!;
            string type = (string)node["type"]!;
            if (type is "error" or "warning" or "assert") problems.Enqueue($"console.{type}: {node["args"]?.ToJsonString()}");
        };
        core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived += (_, e) =>
        {
            var details = JsonNode.Parse(e.ParameterObjectAsJson)!["exceptionDetails"];
            problems.Enqueue("exception: " + ((string?)details?["exception"]?["description"] ?? (string?)details?["text"]));
        };
        core.GetDevToolsProtocolEventReceiver("Log.entryAdded").DevToolsProtocolEventReceived += (_, e) =>
        {
            var entry = JsonNode.Parse(e.ParameterObjectAsJson)!["entry"]!;
            string level = (string)entry["level"]!;
            if (level is "error" or "warning") problems.Enqueue($"log.{level} [{entry["source"]}]: {entry["text"]} {entry["url"]}");
        };
        // Every request the renderer starts (documents, styles, scripts, fonts, data: URLs, fetches).
        await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
        core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent").DevToolsProtocolEventReceived += (_, e) =>
        {
            string url = (string)JsonNode.Parse(e.ParameterObjectAsJson)!["request"]!["url"]!;
            requests.Enqueue(url.StartsWith("data:", StringComparison.Ordinal) ? "data:" + url.Length : url);
        };
        // A load cancelled by the harness's own reload is not a failure; any other failed load is.
        core.GetDevToolsProtocolEventReceiver("Network.loadingFailed").DevToolsProtocolEventReceived += (_, e) =>
        {
            if (JsonNode.Parse(e.ParameterObjectAsJson)!["canceled"]?.GetValue<bool>() != true)
                problems.Enqueue("network.failed: " + e.ParameterObjectAsJson);
        };
    }

    private const string PageFactsScript = """
        JSON.stringify((() => {
          const css = getComputedStyle(document.documentElement);
          const region = el => { if (!el) return null; const s = getComputedStyle(el); return s.getPropertyValue('app-region') || s.getPropertyValue('-webkit-app-region') || s.webkitAppRegion || null; };
          const c = document.createElement('canvas').getContext('2d');
          const sample = 'Платформа Коннектор — Задания 0123456789';
          c.font = '13px Manrope, serif'; const withManrope = c.measureText(sample).width;
          c.font = '13px serif'; const fallback = c.measureText(sample).width;
          c.font = '13px "Segoe UI"'; const segoe = c.measureText(sample).width;
          const h1 = document.querySelector('h1');
          return {
            theme: document.documentElement.dataset.theme,
            vars: Object.fromEntries(['--app','--side','--panel','--text','--accent','--line'].map(v => [v, css.getPropertyValue(v).trim()])),
            bodyBg: getComputedStyle(document.body).backgroundColor,
            appBg: getComputedStyle(document.querySelector('#app')).backgroundColor,
            fontsCheck13: document.fonts.check('13px Manrope'), fontsCheck600: document.fonts.check('600 13px Manrope'),
            manropeFaceLoaded: [...document.fonts].some(f => f.family.replace(/"/g,'') === 'Manrope' && f.status === 'loaded'),
            canvasWidthManrope: Math.round(withManrope * 10) / 10, canvasWidthSerif: Math.round(fallback * 10) / 10, canvasWidthSegoe: Math.round(segoe * 10) / 10,
            bodyFont: getComputedStyle(document.body).fontFamily, h1Font: h1 ? getComputedStyle(h1).fontFamily : null,
            titlebar: Boolean(document.querySelector('.titlebar')), titlebarRegion: region(document.querySelector('.titlebar')),
            buttonRegion: region(document.querySelector('.titlebar .wc')), nativeCaptionClass: Boolean(document.querySelector('.titlebar.native')),
            windowButtons: [...document.querySelectorAll('.titlebar [data-cmd^="window-"]')].map(b => b.dataset.cmd + ':' + b.getAttribute('aria-label')),
            csp: window.__csp || [],
            dpr: devicePixelRatio, w: innerWidth, h: innerHeight,
            overflowX: document.documentElement.scrollWidth > innerWidth, overflowY: document.documentElement.scrollHeight > innerHeight,
            mainOverflowX: (() => { const m = document.querySelector('.main'); if (!m || m.scrollWidth <= m.clientWidth) return null;
              const right = m.getBoundingClientRect().left + m.clientWidth;
              return { by: m.scrollWidth - m.clientWidth, wide: [...m.querySelectorAll('*')].filter(e => e.getBoundingClientRect().right > right + 0.5).slice(-3).map(e => e.tagName + '.' + e.className + ' ' + Math.round(e.getBoundingClientRect().width) + ':' + (e.textContent || '').trim().slice(0, 30)) }; })(),
            page: window.PC?.state?.ui?.page ?? null
          };
        })())
        """;

    private static async Task<string> PageFactsAsync(CoreWebView2 core) =>
        JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(PageFactsScript))!;

    // ---- input ---------------------------------------------------------------------------------------------------

    private static Task Cdp(CoreWebView2 core, string method, object parameters) =>
        core.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(parameters));

    /// <summary>A real mouse click inside the renderer at the element centre (hit-tested by the page).</summary>
    private static async Task<IEnumerable<string>> ClickAsync(CoreWebView2 core, string selector)
    {
        string sel = JsonSerializer.Serialize(selector);
        string json = await core.ExecuteScriptAsync($$"""
            (() => { const el = document.querySelector({{sel}}); if (!el) return null; el.scrollIntoView({block:'nearest',inline:'nearest'});
              const r = el.getBoundingClientRect(); const x = r.left + r.width / 2, y = r.top + r.height / 2;
              const hit = document.elementFromPoint(x, y); return { x, y, covered: !(hit && (hit === el || el.contains(hit))) }; })()
            """);
        if (json == "null") throw new InvalidOperationException("Element not found: " + selector);
        var point = JsonNode.Parse(json)!;
        double x = (double)point["x"]!, y = (double)point["y"]!;
        await Cdp(core, "Input.dispatchMouseEvent", new { type = "mouseMoved", x, y, button = "none" });
        await Cdp(core, "Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", clickCount = 1 });
        await Cdp(core, "Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", clickCount = 1 });
        return (bool)point["covered"]! ? ["covered:" + selector] : [];
    }

    private static async Task<IEnumerable<string>> PaletteAsync(CoreWebView2 core, string text)
    {
        var notes = new List<string>();
        await Cdp(core, "Input.dispatchKeyEvent", new { type = "rawKeyDown", modifiers = 2, key = "k", code = "KeyK", windowsVirtualKeyCode = 75, nativeVirtualKeyCode = 75 });
        await Cdp(core, "Input.dispatchKeyEvent", new { type = "keyUp", modifiers = 2, key = "k", code = "KeyK", windowsVirtualKeyCode = 75, nativeVirtualKeyCode = 75 });
        await WaitForConditionAsync(core, "Boolean(document.getElementById('palette-input'))");
        if (await core.ExecuteScriptAsync("document.activeElement===document.getElementById('palette-input')") != "true")
        {
            notes.Add("palette-input-not-autofocused");
            await core.ExecuteScriptAsync("document.getElementById('palette-input').focus()");
        }
        await Cdp(core, "Input.insertText", new { text });
        await WaitForConditionAsync(core, $"PC.state.ui.palette.query==={JsonSerializer.Serialize(text)}");
        return notes;
    }

    private static async Task SettleAsync(CoreWebView2 core)
    {
        await Task.Delay(80);
        await core.ExecuteScriptAsync("document.fonts.ready.then(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))))");
        await Task.Delay(40);
    }

    private static async Task ApplyScaleAsync(WebView2 browser, CoreWebView2 core, double scale)
    {
        if (scale == 1) return;
        // Zoom 1.5 (not real DPI): the page gets device pixel ratio 1.5, the WPF window stays at the monitor's DPI.
        browser.ZoomFactor = scale;
        await WaitForConditionAsync(core, $"Math.abs(devicePixelRatio-{scale.ToString(System.Globalization.CultureInfo.InvariantCulture)})<0.01");
    }

    // ---- capture -------------------------------------------------------------------------------------------------

    private static async Task CaptureAsync(CoreWebView2 core, IntPtr hwnd, string output, string name)
    {
        using (var image = File.Create(Path.Combine(output, name + ".png")))
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
        SavePng(PrintWindowCapture(hwnd), Path.Combine(output, name + ".window.png"));
    }

    private static BitmapSource PrintWindowCapture(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var rect);
        int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
        IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, w, h);
        IntPtr old = SelectObject(memory, bitmap);
        try
        {
            if (!PrintWindow(hwnd, memory, 2 /* PW_RENDERFULLCONTENT */)) throw new InvalidOperationException("PrintWindow failed.");
            SelectObject(memory, old);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally { DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(IntPtr.Zero, screen); }
    }

    private static void SavePng(BitmapSource source, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    // ---- window behaviour ----------------------------------------------------------------------------------------

    private static async Task<JsonNode> TitlebarPointsAsync(CoreWebView2 core) => JsonNode.Parse(JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("""
        JSON.stringify((() => {
          const bar = document.querySelector('.titlebar').getBoundingClientRect(); const y = bar.top + bar.height / 2; let free = null;
          for (let x = Math.round(innerWidth * 0.45); x < innerWidth - 160; x += 8) { const el = document.elementFromPoint(x, y); if (el && !el.closest('button,.chip,a,input,select,[data-cmd]')) { free = { x, y, el: el.className || el.tagName }; break; } }
          const close = document.querySelector('.titlebar [data-cmd="window-close"]').getBoundingClientRect();
          return { drag: free, button: { x: close.left + close.width / 2, y: close.top + close.height / 2 }, content: { x: innerWidth / 2, y: innerHeight / 2 } };
        })())
        """))!)!;

    private static object HitTests(Window window, IntPtr hwnd, WebView2 browser, JsonNode points, double scale)
    {
        GetWindowRect(hwnd, out var r);
        int midX = (r.Left + r.Right) / 2, midY = (r.Top + r.Bottom) / 2;
        var edges = new List<object>();
        bool edgesPass = true;
        foreach (int d in new[] { 0, Inset - 1, Inset })
        {
            int l = r.Left + d, t = r.Top + d, rr = r.Right - 1 - d, b = r.Bottom - 1 - d;
            var probes = new (string Name, int X, int Y, int Expected)[]
            {
                ("left", l, midY, 10), ("right", rr, midY, 11), ("top", midX, t, 12), ("bottom", midX, b, 15),
                ("topLeft", l, t, 13), ("topRight", rr, t, 14), ("bottomLeft", l, b, 16), ("bottomRight", rr, b, 17)
            };
            foreach (var point in probes)
            {
                int effective = Effective(hwnd, point.X, point.Y);
                // Inside the page (offset = Inset) the WebView answers: client, or caption on the title-bar drag area.
                bool pass = d < Inset ? effective == point.Expected : effective is 1 or 2;
                edgesPass &= pass;
                edges.Add(new { point.Name, offset = d, effective, expected = d < Inset ? point.Expected.ToString() : "1|2", pass });
            }
        }
        var topLevel = new { edgesPass, edges };
        // What the mouse would actually reach: the deepest child window at the point (WebView2 HWNDs of the browser).
        var deep = new List<object>();
        foreach (var (name, cssX, cssY) in new[] { ("dragArea", points["drag"]?["x"], points["drag"]?["y"]), ("closeButton", points["button"]?["x"], points["button"]?["y"]), ("content", points["content"]?["x"], points["content"]?["y"]) })
        {
            if (cssX is null) { deep.Add(new { name, error = "no free titlebar point" }); continue; }
            int x = r.Left + Inset + (int)Math.Round((double)cssX * scale), y = r.Top + Inset + (int)Math.Round((double)cssY! * scale);
            deep.Add(new { name, css = new { x = (double)cssX, y = (double)cssY! }, chain = Chain(hwnd, x, y) });
        }
        var inner = new[] { ("left+5", r.Left + 5, midY), ("left+6", r.Left + 6, midY), ("bottom-6", midX, r.Bottom - 6), ("bottom-7", midX, r.Bottom - 7) }
            .Select(p => new { name = p.Item1, chain = Chain(hwnd, p.Item2, p.Item3) }).ToArray();
        return new { windowRect = new { r.Left, r.Top, r.Right, r.Bottom }, topLevel, deep, insideFrameOverWebView = inner, browserHwnd = browser.Handle.ToString() };
    }

    private static object[] Chain(IntPtr top, int screenX, int screenY)
    {
        var chain = new List<object>();
        IntPtr current = top;
        for (int depth = 0; depth < 8; depth++)
        {
            var client = new POINT { X = screenX, Y = screenY };
            ScreenToClient(current, ref client);
            IntPtr child = ChildWindowFromPointEx(current, client, 0x0001 /* CWP_SKIPINVISIBLE */);
            if (child == IntPtr.Zero || child == current) break;
            current = child;
            var cls = new StringBuilder(128); GetClassName(current, cls, cls.Capacity);
            chain.Add(new { cls = cls.ToString(), hit = HitTest(current, screenX, screenY) });
        }
        chain.Insert(0, new { cls = "top-level", hit = HitTest(top, screenX, screenY) });
        return chain.ToArray();
    }

    /// <summary>The answer for the window the mouse reaches: the deepest child, HTTRANSPARENT passing to the parent.</summary>
    private static int Effective(IntPtr top, int x, int y)
    {
        var windows = new List<IntPtr> { top };
        IntPtr current = top;
        for (int depth = 0; depth < 8; depth++)
        {
            var client = new POINT { X = x, Y = y };
            ScreenToClient(current, ref client);
            IntPtr child = ChildWindowFromPointEx(current, client, 0x0001);
            if (child == IntPtr.Zero || child == current) break;
            windows.Add(current = child);
        }
        for (int i = windows.Count - 1; i >= 0; i--)
        {
            int hit = HitTest(windows[i], x, y);
            if (hit != -1) return hit;
        }
        return 0;
    }

    private static int HitTest(IntPtr hwnd, int x, int y) =>
        (int)SendMessage(hwnd, 0x0084 /* WM_NCHITTEST */, IntPtr.Zero, (IntPtr)((y & 0xFFFF) << 16 | (x & 0xFFFF))).ToInt64();

    private static async Task<object> WindowButtonsAsync(Window window, IntPtr hwnd, WebView2 browser, CoreWebView2 core, string output, double scale)
    {
        // Close (the page ✕) hides to the tray; the process and the window object stay.
        await ClickAsync(core, ".titlebar [data-cmd=\"window-close\"]");
        await WaitForAsync(() => !window.IsVisible, "window-close hides");
        bool closeHidden = !window.IsVisible, closeKeptWindow = Application.Current.Windows.OfType<Window>().Contains(window) && PresentationSource.FromVisual(window) is not null;
        window.Show();
        await WaitForAsync(() => window.IsVisible, "shown after close");
        await WaitForDocumentAsync(core);

        // Minimize also goes to the tray (StateChanged → HideToTray).
        await ClickAsync(core, ".titlebar [data-cmd=\"window-minimize\"]");
        await WaitForAsync(() => window.WindowState == WindowState.Minimized, "window-minimize");
        await Task.Delay(100);
        bool minimizedHidden = !window.IsVisible;
        window.Show();
        ShowWindow(hwnd, 4 /* SW_SHOWNOACTIVATE */);
        await WaitForAsync(() => window.WindowState == WindowState.Normal && window.IsVisible, "restore after minimize");

        // Maximize: the page button, then geometry against the monitor work area, then restore.
        await ClickAsync(core, ".titlebar [data-cmd=\"window-maximize\"]");
        await WaitForAsync(() => window.WindowState == WindowState.Maximized, "window-maximize");
        await WaitForConditionAsync(core, "Boolean(document.querySelector('.titlebar [data-cmd=\"window-maximize\"][aria-label=\"Восстановить\"]'))");
        await Task.Delay(400);
        await SettleAsync(core);
        GetWindowRect(hwnd, out var windowRect);
        GetWindowRect(browser.Handle, out var webRect);
        var monitor = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor);
        var work = monitor.rcWork;
        string viewport = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("JSON.stringify({w:innerWidth,h:innerHeight,dpr:devicePixelRatio,bottom:document.querySelector('#app').getBoundingClientRect().bottom})"))!;
        await CaptureAsync(core, hwnd, output, "W_Maximized");
        bool foregroundWhileMaximized = GetForegroundWindow() == hwnd;
        await ClickAsync(core, ".titlebar [data-cmd=\"window-maximize\"]");
        await WaitForAsync(() => window.WindowState == WindowState.Normal, "window-restore");
        await Task.Delay(200);
        bool restoredOffscreen = window.Left + window.ActualWidth <= SystemParameters.VirtualScreenLeft;
        return new
        {
            closeHidden, closeKeptWindow, minimizedHidden, restoredOffscreen, foregroundWhileMaximized,
            maximized = new
            {
                window = new { windowRect.Left, windowRect.Top, windowRect.Right, windowRect.Bottom },
                webView = new { webRect.Left, webRect.Top, webRect.Right, webRect.Bottom },
                workArea = new { work.Left, work.Top, work.Right, work.Bottom },
                webViewEqualsWorkArea = webRect.Left == work.Left && webRect.Top == work.Top && webRect.Right == work.Right && webRect.Bottom == work.Bottom,
                clipped = webRect.Left < work.Left || webRect.Top < work.Top || webRect.Right > work.Right || webRect.Bottom > work.Bottom,
                viewport = JsonNode.Parse(viewport)
            }
        };
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            while (await core.ExecuteScriptAsync("Boolean(" + condition + ")") != "true")
                await Task.Delay(40, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Page condition not reached: " + condition);
        }
    }

    private static async Task WaitForDocumentAsync(CoreWebView2 core) => await WaitForConditionAsync(core,
        "document.readyState==='complete' && window.CONNECTOR_DESKTOP_BRIDGE && typeof window.CONNECTOR_DESKTOP_RENDER_SNAPSHOT==='function' && document.querySelector('#app .titlebar')");

    /// <summary>Metadata of every user-state location the installed connectors own; compared before/after.</summary>
    private static Dictionary<string, string> Fingerprint()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (string root in new[] { "Platform Connector", "Structura Unified Connector", "ConnectorAgentDesktop", "Structura Connector" }
                     .Select(name => Path.Combine(local, name)).Append(Environment.GetFolderPath(Environment.SpecialFolder.Startup)))
        {
            if (!Directory.Exists(root)) { result[root] = "absent"; continue; }
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var info in new DirectoryInfo(root).EnumerateFileSystemInfos("*", options))
                result[info.FullName] = info is FileInfo file ? $"{file.Length}|{file.LastWriteTimeUtc.Ticks}" : "d";
        }
        using RegistryKey? run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        foreach (string value in run?.GetValueNames() ?? [])
            result[@"HKCU\Run\" + value] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(run!.GetValue(value)?.ToString() ?? "")));
        return result;
    }

    private sealed class NullAutoStart : IAutoStartService
    {
        public bool IsEnabled() => false;
        public void SetEnabled(bool enabled) => throw new InvalidOperationException("Autostart is not changed by the window acceptance.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern IntPtr ChildWindowFromPointEx(IntPtr parent, POINT point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int capacity);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
