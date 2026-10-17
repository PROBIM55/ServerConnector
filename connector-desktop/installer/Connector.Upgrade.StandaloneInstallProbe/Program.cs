using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows.Automation;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Connector.CommonAccess.Runtime;
using Connector.Network;
using Connector.Upgrade.HelperReleaseTrust;
using Connector.Upgrade.Velopack;

if (args.Length is < 1 or > 2 || !OperatingSystem.IsWindows()) throw new InvalidOperationException("Fixture arguments differ.");
using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) || !identity.IsAuthenticated)
    throw new InvalidOperationException("The native user-stage fixture must run unelevated.");
var output = Path.GetFullPath(args[0]);
void Progress(string stage, string? detail = null) => File.WriteAllText(output + ".progress.json", JsonSerializer.Serialize(new
{
    stage, detail, utc = DateTimeOffset.UtcNow, processId = Environment.ProcessId,
    sessionId = Process.GetCurrentProcess().SessionId
}));
object? postFailureReadOnlySnapshot = null;
try
{
    Progress("started");
    var diagnosticMarker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "PlatformConnectorInstallerDiagnostic", "ready-method.json");
    if (Directory.Exists(Path.GetDirectoryName(diagnosticMarker)))
    {
        // Dedicated non-acceptance diagnosis, invoking the exact ready product DLL.
        Environment.ExitCode = 1;
        DiagnoseReadyInstaller(output, diagnosticMarker, message => Progress("ready-installer-method", message));
        return;
    }
    if (args.Length == 2)
    {
        Progress("baseline-install");
        using var baseline = Process.Start(new ProcessStartInfo(args[1], "--silent") { UseShellExecute = false, CreateNoWindow = true })!;
        if (!baseline.WaitForExit(180000) || baseline.ExitCode != 0) throw new InvalidOperationException("Baseline desktop installation failed.");
    }
    Progress("signed-payload");
    var release = new HelperReleasePinSource().GetVelopackSetupPin();
    var sid = identity.User!.Value;
    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), release.PackageId);
    var probe = new SystemVelopackInstallProbe(sid, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    var pin = new Connector.Upgrade.Velopack.VelopackSetupPin(release.PackageId, release.Version, release.Size, release.Sha256);
    var caller = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "StructuraConnectorInstaller", "Bootstrapper", "Connector.Upgrade.Bootstrapper.exe");
    for (var attempt = 0; attempt < 2; attempt++)
    {
        Progress("default-caller-" + attempt);
        // Exercise the actual default GUI entrypoint, including its automatic Shown handler.
        // A token prompt, extra Finish click or network preflight makes this bounded test fail.
        using var installation = Process.Start(new ProcessStartInfo(caller) { UseShellExecute = false })!;
        var installationTimer = Stopwatch.StartNew();
        var lastStatus = string.Empty;
        var exited = false;
        while (!(exited = installation.WaitForExit(1000)) && installationTimer.Elapsed < TimeSpan.FromMinutes(3))
        {
            installation.Refresh();
            if (installation.MainWindowHandle == IntPtr.Zero) continue;
            string? status = null;
            var errorPage = false;
            try
            {
                var window = AutomationElement.FromHandle(installation.MainWindowHandle);
                // The production token-free form contains only installation status, no account fields.
                var labels = window.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
                status = string.Join(" | ", labels.Cast<AutomationElement>().Select(element => element.Current.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name)).Take(12));
                if (status.Length > 1600) status = status[..1600];
                // Direct form controls exclude the enabled Close button inside the native title bar.
                var close = window.FindFirst(TreeScope.Children, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, "Закрыть")));
                // This exact form enables Close only after InstallAsync throws.
                errorPage = close is not null && close.Current.IsEnabled;
            }
            catch (ElementNotAvailableException) { }
            catch (System.Runtime.InteropServices.COMException) { }
            if (!string.IsNullOrWhiteSpace(status) && status != lastStatus)
            {
                lastStatus = status;
                Progress("default-caller-" + attempt, status);
            }
            if (errorPage)
            {
                postFailureReadOnlySnapshot = ReadOnlyInstallationSnapshot(probe, pin, sid, root);
                installation.Kill(true);
                installation.WaitForExit(5000);
                throw new InvalidOperationException("Default installer displayed an error: " + lastStatus);
            }
        }
        if (!exited)
        {
            installation.Kill(true);
            installation.WaitForExit(5000);
            throw new InvalidOperationException("Default installation exceeded three minutes. Last status: " + lastStatus);
        }
        if (installation.ExitCode is not (0 or 3010) || probe.Inspect(pin, sid).Presence != VelopackInstallPresence.ExactInstalled)
            throw new InvalidOperationException("Default installation did not complete and verify its desktop package.");
    }
    Progress("installed-runtime");
    var deployment = CommonAccessDeploymentFactory.OpenProduction(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Structura Unified Connector", "Agent"));
    Progress("netbird-first-login-readiness");
    if (deployment.Coordinator is null) throw new InvalidOperationException("The installed protected network deployment is unavailable.");
    await deployment.Coordinator.EnsureOverlayAvailableAsync(deployment.NetBirdManagementUri)
        .AsTask().WaitAsync(TimeSpan.FromSeconds(120));
    if (File.Exists(deployment.StateFilePath)) throw new InvalidOperationException("Installation created an enrollment credential.");
    if (Directory.EnumerateFiles(root, "blender.exe", SearchOption.AllDirectories).Any())
        throw new InvalidOperationException("Default installation included Blender.");
    var git = Path.Combine(root, "current", "tools", "git", "cmd", "git.exe");
    if (!File.Exists(git)) git = Path.Combine(root, "current", "tools", "git", "bin", "git.exe");
    using (var gitProbe = Process.Start(new ProcessStartInfo(git, "--version") { UseShellExecute = false, CreateNoWindow = true })!)
    {
        if (!gitProbe.WaitForExit(10000) || gitProbe.ExitCode != 0) throw new InvalidOperationException("The packaged Git runtime is unavailable.");
    }
    var desktop = Path.Combine(root, "current", UnifiedVelopackApplication.MainExecutable);
    Progress("application-name-icons-and-shortcuts");
    using(var registration=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\"+release.PackageId))
        if(registration?.GetValue("DisplayName") as string != "Платформа Коннектор")
            throw new InvalidOperationException("Installed Apps still contains the pilot application name.");
    static byte[] IconBytes(string path)
    {
        using var icon=System.Drawing.Icon.ExtractAssociatedIcon(path) ?? throw new InvalidOperationException("Application icon is absent.");
        using var bitmap=icon.ToBitmap(); using var stream=new MemoryStream();
        bitmap.Save(stream,System.Drawing.Imaging.ImageFormat.Png); return SHA256.HashData(stream.ToArray());
    }
    var expectedIcon=IconBytes(caller);
    foreach(var file in new[] {desktop,Path.Combine(root,UnifiedVelopackApplication.RootLauncher),Path.Combine(root,"Update.exe")})
        if(!expectedIcon.SequenceEqual(IconBytes(file))) throw new InvalidOperationException("Installed executable icon differs from the branded installer icon.");
    object shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
    try
    {
        foreach(var location in new[] {Environment.SpecialFolder.DesktopDirectory,Environment.SpecialFolder.Programs})
        {
            var directory=Environment.GetFolderPath(location);
            var path=Path.Combine(directory,"Платформа Коннектор.lnk");
            if(!File.Exists(path)||File.Exists(Path.Combine(directory,"Structura Connector (новый).lnk"))||File.Exists(Path.Combine(directory,"Structura Connector.lnk")))
                throw new InvalidOperationException("A normal branded shortcut is missing or the pilot shortcut remains.");
            object shortcut=((dynamic)shell).CreateShortcut(path);
            try
            {
                if((string)((dynamic)shortcut).TargetPath != Path.Combine(root,UnifiedVelopackApplication.RootLauncher) ||
                    (string)((dynamic)shortcut).IconLocation != desktop+",0")
                    throw new InvalidOperationException("Shortcut target or application icon differs.");
            }
            finally { Marshal.FinalReleaseComObject(shortcut); }
        }
    }
    finally { Marshal.FinalReleaseComObject(shell); }
    Progress("desktop-start");
    using var app = Process.Start(new ProcessStartInfo(desktop) { UseShellExecute = false })!;
    var ready = false;
    try
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(60) && !app.HasExited)
        {
            app.Refresh();
            if (app.MainWindowHandle != IntPtr.Zero)
            {
                Progress("desktop-webview-automation");
                var window = AutomationElement.FromHandle(app.MainWindowHandle);
                ready = window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "Настройки")) is not null;
                if (ready) break;
            }
            Thread.Sleep(500);
        }
        if (!ready) throw new InvalidOperationException("Installed desktop did not load its actual WebView interface.");
    }
    finally { if (!app.HasExited) { app.Kill(true); app.WaitForExit(5000); } }
    Progress("complete");
    File.WriteAllText(output, JsonSerializer.Serialize(new { success = true, version = release.Version,
        tokenRequired = false, enrollmentCreated = false, defaultCallerVerified = true, repeatedInstallVerified = true,
        desktopWebViewVerified = ready, bundledGitVerified = true, netBirdFirstLoginVerified = true,
        blenderIncluded = false, upgradedPreviousDesktop = args.Length == 2 }));
}
catch (Exception error)
{
    object? preflightDiagnostic = null;
    if (error is NetworkGateException networkFailure)
    {
        Console.WriteLine("NETBIRD_FAILURE_CODE=" + networkFailure.Code);
        try
        {
            Progress("netbird-failure-diagnostics");
            var networkRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Structura Connector", "NetBird");
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                         Path.Combine(networkRoot, "scripts", "netbird_windows_preflight.ps1"), "-MsiPath",
                         Path.Combine(networkRoot, "installer", "netbird_installer_0.79.0_windows_amd64.msi") }) start.ArgumentList.Add(argument);
            var timer = Stopwatch.StartNew();
            using var diagnostic = Process.Start(start)!;
            var stdout = diagnostic.StandardOutput.ReadToEndAsync();
            var stderr = diagnostic.StandardError.ReadToEndAsync();
            try { await diagnostic.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
            catch (TimeoutException) { diagnostic.Kill(true); throw; }
            using var report = JsonDocument.Parse(await stdout);
            var root = report.RootElement;
            preflightDiagnostic = new { elapsedSeconds = timer.Elapsed.TotalSeconds, exitCode = diagnostic.ExitCode,
                state = root.GetProperty("state").Clone(), reasons = root.GetProperty("reasons").Clone(), checks = root.GetProperty("checks").Clone() };
            Console.WriteLine("NETBIRD_READONLY_DIAGNOSTIC=" + JsonSerializer.Serialize(preflightDiagnostic));
            _ = await stderr; // Never emit raw native output or environment data.
        }
        catch (Exception diagnosticError) { preflightDiagnostic = new { failure = diagnosticError.GetType().Name }; }
    }
    File.WriteAllText(output, JsonSerializer.Serialize(new { success = false, failure = error.Message, type = error.GetType().Name,
        diagnosticCode = error is NetworkGateException networkError ? networkError.Code : null, preflightDiagnostic,
        postFailureReadOnlySnapshot }));
    Environment.ExitCode = 1;
}

// These observations are collected only after the real default installer has failed.
// They never install/repair/retry and never count as acceptance of the failed GUI path.
static object ReadOnlyInstallationSnapshot(SystemVelopackInstallProbe probe,
    Connector.Upgrade.Velopack.VelopackSetupPin pin, string sid, string root)
{
    object Read(Func<object> operation)
    {
        try { return new { observed = true, value = operation() }; }
        catch (Exception snapshotError)
        {
            // This describes the independent read, not the exception hidden by the GUI.
            // Omit exception messages, local paths, file contents and environment values.
            return new { observed = false, snapshotReadException = new {
                type = snapshotError.GetType().FullName, hResult = snapshotError.HResult.ToString("X8"),
                methods = new StackTrace(snapshotError, false).GetFrames().Take(8)
                    .Select(frame => frame.GetMethod()).Where(method => method is not null)
                    .Select(method => method!.DeclaringType?.FullName + "." + method.Name).ToArray() } };
        }
    }
    var files = new[] { "current/sq.version", "current/" + UnifiedVelopackApplication.MainExecutable,
        "Update.exe", UnifiedVelopackApplication.RootLauncher, "Structura Connector.exe" }
        .Select(relative => new { relative, observation = Read(() => {
            var path = Path.Combine(root, relative);
            return new { exists = File.Exists(path),
                reparsePoint = File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 };
        }) }).ToArray();
    return new { source = "read-only observations after default installer failure; original exception unavailable",
        exactInstallProbe = Read(() => { var result = probe.Inspect(pin, sid);
            return new { presence = result.Presence.ToString(), identityPresent = result.Identity is not null,
                detail = result.Detail[..Math.Min(result.Detail.Length, 512)] }; }),
        files, shortcuts = new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs }
            .Select(location => new { location = location.ToString(), observation = Read(() => {
                var directory = Environment.GetFolderPath(location);
                return new { knownFolderAvailable = !string.IsNullOrWhiteSpace(directory),
                    currentShortcutExists = !string.IsNullOrWhiteSpace(directory) &&
                        File.Exists(Path.Combine(directory, "Платформа Коннектор.lnk")),
                    oldShortcutExists = !string.IsNullOrWhiteSpace(directory) &&
                        File.Exists(Path.Combine(directory, "Structura Connector.lnk")) };
            }) }).ToArray() };
}

static void DiagnoseReadyInstaller(string output, string markerPath, Action<string> progress)
{
    object? capturedFailure = null;
    var completed = false;
    var attempted = false;
    var taskObtained = false;
    string? actualDllSha256 = null;
    var worker = new Thread(() =>
    {
        try
        {
            AssertDiagnosticFile(markerPath);
            using var markerLease = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var marker = JsonDocument.Parse(markerLease);
            if (marker.RootElement.GetProperty("producerSha").GetString() !=
                "898edbee94060eabbeb19c701d60683167109a28")
                throw new InvalidDataException("Diagnostic runtime source differs.");
            var readyDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "StructuraConnectorInstaller", "Bootstrapper");
            var readyDll = Path.Combine(readyDirectory, "Connector.Upgrade.Bootstrapper.dll");
            using (var image = File.OpenRead(readyDll))
                actualDllSha256 = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
            if (actualDllSha256 != marker.RootElement.GetProperty("bootstrapperDllSha256").GetString())
                throw new InvalidDataException("Installed ready DLL differs from the verified producer artifact.");
            var context = new ReadyInstallerLoadContext(readyDll);
            var assembly = context.LoadFromAssemblyPath(readyDll);
            var installerType = assembly.GetType("Connector.Upgrade.Bootstrapper.ProductionStandaloneDesktopInstaller", true)!;
            var sinkType = assembly.GetType("Connector.Upgrade.Bootstrapper.IBootstrapperStatusSink", true)!;
            var sink = System.Reflection.DispatchProxy.Create(sinkType, typeof(ReadyInstallerStatusProxy));
            ((ReadyInstallerStatusProxy)sink).Show = message => progress(message.Length > 512 ? message[..512] : message);
            var installer = Activator.CreateInstance(installerType)!;
            var method = installerType.GetMethod("InstallAsync")!;
            System.Windows.Forms.Application.EnableVisualStyles();
            using var form = new System.Windows.Forms.Form { ShowInTaskbar = false, Opacity = 0 };
            form.Shown += async (_, _) =>
            {
                try
                {
                    attempted = true;
                    var task = method.Invoke(installer, [sink, CancellationToken.None]) as Task
                        ?? throw new InvalidDataException("Ready installer method did not return a Task.");
                    taskObtained = true;
                    await task;
                    completed = true;
                }
                catch (Exception error) { capturedFailure = Capture(error); }
                finally { form.Close(); }
            };
            System.Windows.Forms.Application.Run(form);
        }
        catch (Exception error) { capturedFailure = Capture(error); }
    });
    worker.SetApartmentState(ApartmentState.STA);
    worker.Start();
    // The existing disposable-user fixture owns the bounded watchdog and process tree.
    worker.Join();
    File.WriteAllText(output, JsonSerializer.Serialize(new { success = false, diagnosticOnly = true,
        failure = "Ready installer method diagnosis; this result cannot accept or publish an installer.",
        // A task-boundary failure can still originate in loader/status instrumentation.
        // Assess captured method names before attributing it to product code.
        failureBoundary = taskObtained ? "await-ready-installer-task" : attempted ? "invoke-ready-installer-method" : "diagnostic-host-setup",
        actualDllSha256, installationCompleted = completed, capturedFailure }));

    static object Capture(Exception error)
    {
        while (error is System.Reflection.TargetInvocationException && error.InnerException is not null)
            error = error.InnerException;
        // InstallAsync performs no enrollment or credential read. No environment dump.
        return new { type = error.GetType().FullName, hResult = error.HResult.ToString("X8"),
            message = error.Message[..Math.Min(error.Message.Length, 1024)],
            methods = new StackTrace(error, false).GetFrames().Take(12).Select(frame => frame.GetMethod())
                .Where(method => method is not null).Select(method => method!.DeclaringType?.FullName + "." + method.Name).ToArray() };
    }
}

static void AssertDiagnosticFile(string path)
{
    const System.Security.AccessControl.FileSystemRights mutation =
        (System.Security.AccessControl.FileSystemRights)0x10000000 |
        (System.Security.AccessControl.FileSystemRights)0x40000000 |
        System.Security.AccessControl.FileSystemRights.WriteData |
        System.Security.AccessControl.FileSystemRights.AppendData |
        System.Security.AccessControl.FileSystemRights.WriteAttributes |
        System.Security.AccessControl.FileSystemRights.WriteExtendedAttributes |
        System.Security.AccessControl.FileSystemRights.Delete |
        System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles |
        System.Security.AccessControl.FileSystemRights.ChangePermissions |
        System.Security.AccessControl.FileSystemRights.TakeOwnership;
    var trusted = new HashSet<string>(StringComparer.Ordinal)
    {
        "S-1-5-18", "S-1-5-32-544",
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
    };
    foreach (var item in new FileSystemInfo[] { new DirectoryInfo(Path.GetDirectoryName(path)!), new FileInfo(path) })
    {
        if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Diagnostic marker or parent is redirected.");
        System.Security.AccessControl.FileSystemSecurity security = item is DirectoryInfo directory
            ? System.IO.FileSystemAclExtensions.GetAccessControl(directory)
            : System.IO.FileSystemAclExtensions.GetAccessControl((FileInfo)item);
        var owner = (SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier));
        if (!trusted.Contains(owner.Value)) throw new InvalidDataException("Diagnostic marker owner differs.");
        foreach (System.Security.AccessControl.FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow &&
                (rule.FileSystemRights & mutation) != 0 &&
                !trusted.Contains(((SecurityIdentifier)rule.IdentityReference).Value))
                throw new InvalidDataException("Diagnostic marker or parent permits untrusted replacement.");
    }
}

public class ReadyInstallerStatusProxy : System.Reflection.DispatchProxy
{
    public Action<string>? Show { get; set; }
    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name != "Show" || args is not [string message])
            throw new InvalidOperationException("Unexpected ready installer status method.");
        Show?.Invoke(message);
        return null;
    }
}

public sealed class ReadyInstallerLoadContext : System.Runtime.Loader.AssemblyLoadContext
{
    private readonly string _directory;
    private readonly System.Runtime.Loader.AssemblyDependencyResolver _resolver;
    public ReadyInstallerLoadContext(string readyDll) : base("ExactReadyInstallerDiagnostic")
    {
        _directory = Path.GetDirectoryName(readyDll)!;
        _resolver = new System.Runtime.Loader.AssemblyDependencyResolver(readyDll);
    }
    protected override System.Reflection.Assembly? Load(System.Reflection.AssemblyName name)
    {
        if (name.Name is "mscorlib" or "netstandard" or "Microsoft.CSharp" ||
            name.Name?.StartsWith("System", StringComparison.Ordinal) == true ||
            name.Name?.StartsWith("Microsoft.Win32", StringComparison.Ordinal) == true) return null;
        var path = _resolver.ResolveAssemblyToPath(name);
        if (path is null)
            throw new InvalidDataException("Ready dependency is unavailable; probe-copy fallback is forbidden.");
        if (!Path.GetFullPath(path).StartsWith(_directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ready dependency is outside the protected runtime.");
        return LoadFromAssemblyPath(path);
    }
}
