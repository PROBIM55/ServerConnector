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
    await VerifyInstalledShortcutsWithWindowsShellAsync(root, desktop, output + ".shortcuts.json");
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

static async Task VerifyInstalledShortcutsWithWindowsShellAsync(string root, string desktop, string diagnosticPath)
{
    var records = new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs }
        .Select(location => new
        {
            path = Path.Combine(Environment.GetFolderPath(location), "Платформа Коннектор.lnk"),
            target = Path.Combine(root, UnifiedVelopackApplication.RootLauncher),
            icon = desktop, workingDirectory = root
        }).ToArray();
    foreach (var item in records)
    {
        var directory = Path.GetDirectoryName(item.path)!;
        if (!File.Exists(item.path) || File.Exists(Path.Combine(directory, "Structura Connector (новый).lnk")) ||
            File.Exists(Path.Combine(directory, "Structura Connector.lnk")))
            throw new InvalidOperationException("A branded shortcut is missing or an owned pilot shortcut remains.");
    }
    // Independent reader: Windows PowerShell's COM adapter, not the product's
    // IShellLinkW declaration or the C# dynamic binder that caused the failure.
    var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(records)));
    var script = """
        $ErrorActionPreference='Stop'
        [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
        $report=[ordered]@{valid=$false;count=0;stage='decode';items=@();cleanupFailures=@()}
        function Release-ObservedCom($value,[string]$kind) {
            if($null -eq $value){return}
            try {[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($value)}
            catch {
                $failure=$_.Exception
                for($depth=0;$depth -lt 4 -and $null -ne $failure.InnerException;$depth++){$failure=$failure.InnerException}
                $report.cleanupFailures+=@{kind=$kind;type=$failure.GetType().FullName;hResult=$failure.HResult.ToString('X8')}
            }
        }
        $shell=$null
        try {
            $items=([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('PAYLOAD'))|ConvertFrom-Json)
            $report.stage='shell'
            $shell=New-Object -ComObject WScript.Shell
            foreach($item in $items) {
                $report.stage='read-shortcut'
                $link=$null
                try {
                    $link=$shell.CreateShortcut([string]$item.path)
                    $report.stage='read-properties'
                    $target=[string]$link.TargetPath
                    $icon=[string]$link.IconLocation
                    $workingDirectory=[string]$link.WorkingDirectory
                    $arguments=[string]$link.Arguments
                    $description=[string]$link.Description
                    $observation=@{target=$target.Substring(0,[Math]::Min(1024,$target.Length));
                        icon=$icon.Substring(0,[Math]::Min(1024,$icon.Length));
                        workingDirectory=$workingDirectory.Substring(0,[Math]::Min(1024,$workingDirectory.Length));
                        argumentsLength=$arguments.Length}
                    $report.items+=$observation
                    $report.stage='parse-icon'
                    $separator=$icon.LastIndexOf(',')
                    if($separator -lt 1){throw 'Shortcut icon metadata is incomplete'}
                    $iconPath=$icon.Substring(0,$separator).Trim().Trim('"')
                    $index=[int]::Parse($icon.Substring($separator+1),[Globalization.CultureInfo]::InvariantCulture)
                    $matches=[ordered]@{
                        target=[string]::Equals($target,[string]$item.target,[StringComparison]::OrdinalIgnoreCase)
                        icon=[string]::Equals($iconPath,[string]$item.icon,[StringComparison]::OrdinalIgnoreCase)
                        iconIndex=($index -eq 0)
                        workingDirectory=[string]::Equals($workingDirectory,[string]$item.workingDirectory,[StringComparison]::OrdinalIgnoreCase)
                        arguments=($arguments -ceq '')
                        description=($description -ceq 'Платформа Коннектор')
                    }
                    # Only metadata of these two fixture-owned shortcuts; no environment,
                    # credentials, arbitrary stderr, command arguments or exception messages.
                    $observation.matches=$matches
                    $report.stage='compare'
                    if($matches.Values -contains $false){throw 'Shortcut metadata differs'}
                    $report.count++
                } finally {
                    Release-ObservedCom $link 'shortcut'
                }
            }
            $report.stage='complete'
            $report.valid=$true
        } catch {
            $failure=$_.Exception
            for($depth=0;$depth -lt 4 -and $null -ne $failure.InnerException;$depth++){$failure=$failure.InnerException}
            $report['errorType']=$failure.GetType().FullName
            $report['hResult']=$failure.HResult.ToString('X8')
            $report['errorId']=($_.FullyQualifiedErrorId -split ',')[0].Substring(0,[Math]::Min(128,($_.FullyQualifiedErrorId -split ',')[0].Length))
        } finally {
            Release-ObservedCom $shell 'shell'
        }
        if($report.cleanupFailures.Count -gt 0){$report.valid=$false}
        $report|ConvertTo-Json -Depth 6 -Compress
        if(!$report.valid){exit 1}
        """.Replace("PAYLOAD", payload, StringComparison.Ordinal);
    var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                 Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) })
        start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows shortcut verification did not start.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
    catch (TimeoutException)
    {
        process.Kill(entireProcessTree: true);
        throw new InvalidOperationException("Windows shortcut verification exceeded its bound.");
    }
    var result = await stdout;
    _ = await stderr; // Do not emit arbitrary native output.
    if (result.Length > 16384)
        throw new InvalidOperationException("Windows shortcut diagnostic exceeds its bound.");
    using (var boundedReport = JsonDocument.Parse(result))
        File.WriteAllText(diagnosticPath, boundedReport.RootElement.GetRawText());
    if (process.ExitCode != 0)
        throw new InvalidOperationException("Windows rejected the installed shortcut metadata; see the bounded shortcut report.");
    using var report = JsonDocument.Parse(result);
    if (!report.RootElement.GetProperty("valid").GetBoolean() || report.RootElement.GetProperty("count").GetInt32() != 2)
        throw new InvalidOperationException("Both native shortcuts were not independently confirmed.");
}

