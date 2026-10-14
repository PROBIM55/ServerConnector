using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Windows.Automation;
using Connector.CommonAccess.Runtime;
using Connector.Upgrade.HelperReleaseTrust;
using Connector.Upgrade.Velopack;

if (args.Length is < 1 or > 2 || !OperatingSystem.IsWindows()) throw new InvalidOperationException("Fixture arguments differ.");
using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) || !identity.IsAuthenticated)
    throw new InvalidOperationException("The native user-stage fixture must run unelevated.");
var output = Path.GetFullPath(args[0]);
try
{
    if (args.Length == 2)
    {
        using var baseline = Process.Start(new ProcessStartInfo(args[1], "--silent") { UseShellExecute = false, CreateNoWindow = true })!;
        if (!baseline.WaitForExit(180000) || baseline.ExitCode != 0) throw new InvalidOperationException("Baseline desktop installation failed.");
    }
    var release = new HelperReleasePinSource().GetVelopackSetupPin();
    var sid = identity.User!.Value;
    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), release.PackageId);
    var probe = new SystemVelopackInstallProbe(sid, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    var pin = new Connector.Upgrade.Velopack.VelopackSetupPin(release.PackageId, release.Version, release.Size, release.Sha256);
    var caller = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "StructuraConnectorInstaller", "Bootstrapper", "Connector.Upgrade.Bootstrapper.exe");
    for (var attempt = 0; attempt < 2; attempt++)
    {
        // Exercise the actual default GUI entrypoint, including its automatic Shown handler.
        // A token prompt, extra Finish click or network preflight makes this bounded test fail.
        using var installation = Process.Start(new ProcessStartInfo(caller) { UseShellExecute = false })!;
        if (!installation.WaitForExit(180000)) { installation.Kill(true); throw new InvalidOperationException("Default installation waited for user input or network."); }
        if (installation.ExitCode is not (0 or 3010) || probe.Inspect(pin, sid).Presence != VelopackInstallPresence.ExactInstalled)
            throw new InvalidOperationException("Default installation did not complete and verify its desktop package.");
    }
    var deployment = CommonAccessDeploymentFactory.OpenProduction(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Structura Unified Connector", "Agent"));
    if (File.Exists(deployment.StateFilePath)) throw new InvalidOperationException("Installation created an enrollment credential.");
    if (Directory.EnumerateFiles(root, "blender.exe", SearchOption.AllDirectories).Any())
        throw new InvalidOperationException("Default installation included Blender.");
    var desktop = Path.Combine(root, "current", UnifiedVelopackApplication.MainExecutable);
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
    File.WriteAllText(output, JsonSerializer.Serialize(new { success = true, version = release.Version,
        tokenRequired = false, enrollmentCreated = false, defaultCallerVerified = true, repeatedInstallVerified = true,
        desktopWebViewVerified = ready, blenderIncluded = false, upgradedPreviousDesktop = args.Length == 2 }));
}
catch (Exception error)
{
    File.WriteAllText(output, JsonSerializer.Serialize(new { success = false, failure = error.Message, type = error.GetType().Name }));
    Environment.ExitCode = 1;
}
