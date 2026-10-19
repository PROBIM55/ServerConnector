using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;
using Connector.CommonAccess.Runtime;

if (args.Length != 1) throw new ArgumentException("One evidence path is required.");
var output = Path.GetFullPath(args[0]);
void Progress(string stage) => File.WriteAllText(output + ".progress.json", JsonSerializer.Serialize(new { stage, utc = DateTimeOffset.UtcNow }));
try
{
    using var identity = WindowsIdentity.GetCurrent();
    if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new InvalidOperationException("Probe must run as an ordinary user.");
    var appRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Platform Connector");
    var desktop = Path.Combine(appRoot, "Connector.Desktop.exe");
    if (!File.Exists(desktop) || !File.Exists(Path.Combine(appRoot, "connector-install.json"))) throw new InvalidOperationException("Fresh machine application missing.");
    if (Directory.EnumerateFiles(appRoot, "blender.exe", SearchOption.AllDirectories).Any()) throw new InvalidOperationException("Blender must be optional.");
    Progress("protected-network-readiness");
    var pinnedMsi = Path.Combine(appRoot, "Network", "installer", "netbird_installer_0.79.0_windows_amd64.msi");
    if (!File.Exists(pinnedMsi) || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pinnedMsi))) !=
        "50F822C0F5F6E54E7618096CDD36B63BF4B169E125C9DA06052FD4FD96115210") throw new InvalidOperationException("Installed pinned NetBird payload differs.");
    var userRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Platform Connector");
    var deployment = CommonAccessDeploymentFactory.OpenProduction(Path.Combine(userRoot, "Agent"));
    if (!deployment.IsConfigured) throw new InvalidOperationException("Protected production configuration was rejected.");
    await deployment.Coordinator!.EnsureOverlayAvailableAsync(deployment.NetBirdManagementUri).AsTask().WaitAsync(TimeSpan.FromSeconds(120));
    if (File.Exists(deployment.StateFilePath)) throw new InvalidOperationException("Installer created an account credential.");
    Progress("bundled-runtime-and-shortcuts");
    var git = Path.Combine(appRoot, "tools", "git", "cmd", "git.exe");
    if (!File.Exists(git)) git = Path.Combine(appRoot, "tools", "git", "bin", "git.exe");
    using (var check = Process.Start(new ProcessStartInfo(git, "--version") { UseShellExecute = false, CreateNoWindow = true })!)
        if (!check.WaitForExit(10000) || check.ExitCode != 0) throw new InvalidOperationException("Bundled Git failed.");
    // Windows Installer resolves advertised links through the shell's COM APIs.
    // This continuation can be on a pool thread that has not initialized COM.
    var com = Native.CoInitializeEx(IntPtr.Zero, 0);
    if (com < 0 && com != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(com);
    try
    {
      foreach (var location in new[] { Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.CommonPrograms })
      {
        var path = Path.Combine(Environment.GetFolderPath(location), "Платформа Коннектор.lnk");
        if (!File.Exists(path)) throw new InvalidOperationException("Application shortcut is missing.");
        var product = new StringBuilder(39); var feature = new StringBuilder(256); var component = new StringBuilder(39);
        var shortcutResult = Native.MsiGetShortcutTarget(path, product, feature, component);
        if (shortcutResult != 0) throw new InvalidOperationException($"Shortcut MSI lookup failed: {shortcutResult}, {path}.");
        var target = new StringBuilder(32768); uint size = (uint)target.Capacity;
        var componentState = Native.MsiGetComponentPath(product.ToString(), component.ToString(), target, ref size);
        if (componentState != 3 || !string.Equals(target.ToString(), desktop, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Shortcut component lookup differs: {componentState}, {product}, {component}, {target}.");
        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
        if (icon is null) throw new InvalidOperationException("Shortcut icon is absent.");
      }
    }
    finally { if (com >= 0) Native.CoUninitialize(); }
    Progress("actual-desktop-webview");
    using var app = Process.Start(new ProcessStartInfo(desktop) { UseShellExecute = false, WorkingDirectory = appRoot })!;
    var ready = false;
    try
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(90) && !app.HasExited)
        {
            app.Refresh();
            if (app.MainWindowHandle != IntPtr.Zero)
            {
                var window = AutomationElement.FromHandle(app.MainWindowHandle);
                ready = window.Current.Name == "Платформа Коннектор" && window.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "Войти через Платформу")) is not null;
                if (ready) break;
            }
            await Task.Delay(500);
        }
        if (!ready) throw new InvalidOperationException("Actual installed WebView did not expose the signed-out login screen.");
    }
    finally { if (!app.HasExited) { app.Kill(true); app.WaitForExit(10000); } }
    if (File.Exists(deployment.StateFilePath)) throw new InvalidOperationException("Opening the app silently enrolled an account.");
    File.WriteAllText(output, JsonSerializer.Serialize(new { success = true, ordinaryUser = true, actualDesktopWebView = ready,
        shortcuts = true, bundledGit = true, protectedNetworkReady = true, tokenRequired = false, credentialCreated = false,
        legacyImport = false, blenderIncluded = false }));
}
catch (Exception error)
{
    File.WriteAllText(output, JsonSerializer.Serialize(new { success = false, failure = error.Message, type = error.GetType().Name }));
    Environment.ExitCode = 1;
}

internal static class Native
{
    [DllImport("ole32.dll")] internal static extern int CoInitializeEx(IntPtr reserved, uint model);
    [DllImport("ole32.dll")] internal static extern void CoUninitialize();
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetShortcutTargetW")]
    internal static extern uint MsiGetShortcutTarget(string path, StringBuilder product, StringBuilder feature, StringBuilder component);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetComponentPathW")]
    internal static extern int MsiGetComponentPath(string product, string component, StringBuilder path, ref uint size);
}
