using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows.Automation;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Connector.CommonAccess.Runtime;
using Connector.Network;
using Connector.Upgrade.HelperReleaseTrust;
using Connector.Upgrade.Velopack;

if (args.Length is < 1 or > 2 || !OperatingSystem.IsWindows()) throw new InvalidOperationException("Fixture arguments differ.");
using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) || !identity.IsAuthenticated)
    throw new InvalidOperationException("The native user-stage fixture must run unelevated.");
var output = Path.GetFullPath(args[0]);
void Progress(string stage) => File.WriteAllText(output + ".progress.json", JsonSerializer.Serialize(new
{
    stage, utc = DateTimeOffset.UtcNow, processId = Environment.ProcessId,
    sessionId = Process.GetCurrentProcess().SessionId
}));
try
{
    Progress("started");
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
        var installTimer = Stopwatch.StartNew();
        while (!installation.WaitForExit(2000))
        {
            var failure = ReadInstallerFailure(installation);
            if (failure is not null)
            {
                installation.Kill(true);
                throw new InvalidOperationException("Default installer reported: " + failure);
            }
            if (installTimer.Elapsed < TimeSpan.FromSeconds(180)) continue;
            installation.Kill(true);
            throw new InvalidOperationException("Default installation waited for user input or network.");
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
    foreach(var location in new[] {Environment.SpecialFolder.DesktopDirectory,Environment.SpecialFolder.Programs})
    {
        var directory=Environment.GetFolderPath(location);
        var path=Path.Combine(directory,"Платформа Коннектор.lnk");
        if(!File.Exists(path))
            throw new InvalidOperationException("A branded application shortcut is missing.");
        var shortcut=NativeShortcutProbe.Read(path);
        if(!string.Equals(shortcut.Target,Path.Combine(root,UnifiedVelopackApplication.RootLauncher),StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(shortcut.Icon,desktop,StringComparison.OrdinalIgnoreCase) || shortcut.IconIndex != 0)
            throw new InvalidOperationException("Shortcut target or application icon differs.");
        foreach (var legacyName in new[] { "Structura Connector.lnk", "Structura Connector (новый).lnk" })
        {
            var legacyPath=Path.Combine(directory,legacyName);
            if(!File.Exists(legacyPath)) continue;
            try
            {
                var legacy=NativeShortcutProbe.Read(legacyPath);
                if(!string.IsNullOrWhiteSpace(legacy.Target) && Path.IsPathFullyQualified(legacy.Target) &&
                   Path.GetFullPath(legacy.Target).StartsWith(Path.TrimEndingDirectorySeparator(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("An owned pilot shortcut remains.");
            }
            catch(Exception error) when (error is COMException or IOException or UnauthorizedAccessException or ArgumentException)
            { /* Unreadable legacy shortcut is not owned proof. */ }
        }
    }
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
        diagnosticCode = error is NetworkGateException networkError ? networkError.Code : null, preflightDiagnostic }));
    Environment.ExitCode = 1;
}

static string? ReadInstallerFailure(Process process)
{
    try
    {
        process.Refresh();
        if (process.MainWindowHandle == IntPtr.Zero) return null;
        var window = AutomationElement.FromHandle(process.MainWindowHandle);
        var close = window.FindFirst(TreeScope.Children, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, "Закрыть")));
        if (close is null || !close.Current.IsEnabled) return null;
        var labels = window.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
        var details = string.Join(" | ", labels.Cast<AutomationElement>()
            .Select(label => label.Current.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name)));
        return details.Length > 0 ? details[..Math.Min(details.Length, 500)] : "окно установки ожидает закрытия";
    }
    catch (ElementNotAvailableException) { return null; }
    catch (InvalidOperationException) { return null; }
}

internal static class NativeShortcutProbe
{
    internal sealed record Shortcut(string Target, string Icon, int IconIndex);

    internal static Shortcut Read(string path)
    {
        object native = new ShellLink();
        try
        {
            ((IPersistFile)native).Load(path, 0);
            var link = (IShellLinkW)native;
            var target = new StringBuilder(32768);
            var icon = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 4);
            link.GetIconLocation(icon, icon.Capacity, out var iconIndex);
            return new Shortcut(target.ToString(), icon.ToString(), iconIndex);
        }
        finally { Marshal.FinalReleaseComObject(native); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int capacity);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int capacity);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int capacity);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
