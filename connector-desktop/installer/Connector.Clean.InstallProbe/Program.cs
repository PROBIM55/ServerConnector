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
    var networkTimer = Stopwatch.StartNew();
    await deployment.Coordinator!.EnsureOverlayAvailableAsync(deployment.NetBirdManagementUri).AsTask().WaitAsync(TimeSpan.FromSeconds(120));
    var protectedNetworkCheckSeconds = networkTimer.Elapsed.TotalSeconds;
    if (File.Exists(deployment.StateFilePath)) throw new InvalidOperationException("Installer created an account credential.");
    Progress("bundled-runtime-and-shortcuts");
    var git = Path.Combine(appRoot, "tools", "git", "cmd", "git.exe");
    if (!File.Exists(git)) git = Path.Combine(appRoot, "tools", "git", "bin", "git.exe");
    using (var check = Process.Start(new ProcessStartInfo(git, "--version") { UseShellExecute = false, CreateNoWindow = true })!)
        if (!check.WaitForExit(10000) || check.ExitCode != 0) throw new InvalidOperationException("Bundled Git failed.");
    var shortcuts = await Native.OnStaAsync(() => Native.VerifyShortcuts(desktop, appRoot)).WaitAsync(TimeSpan.FromSeconds(30));
    Progress("actual-desktop-webview");
    // Exercise the actual installed link, rather than bypassing it with the EXE.
    using var launched = await Native.OnStaAsync(() => Process.Start(new ProcessStartInfo(shortcuts[0].Path)
        { UseShellExecute = true, WorkingDirectory = appRoot })).WaitAsync(TimeSpan.FromSeconds(30));
    Process? app = null;
    var ready = false;
    var singleInstance = false;
    var trayReactivation = false;
    try
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(90))
        {
            if (app is null)
            {
                var matches = new List<Process>();
                foreach (var candidate in Process.GetProcessesByName("Connector.Desktop"))
                {
                    try
                    {
                        if (string.Equals(candidate.MainModule?.FileName, desktop, StringComparison.OrdinalIgnoreCase))
                        { matches.Add(candidate); continue; }
                    }
                    catch (System.ComponentModel.Win32Exception) { }
                    candidate.Dispose();
                }
                if (matches.Count > 1)
                {
                    foreach (var candidate in matches) candidate.Dispose();
                    throw new InvalidOperationException("Shortcut launched an ambiguous application process.");
                }
                app = matches.SingleOrDefault();
            }
            if (app is null) { await Task.Delay(500); continue; }
            if (app.HasExited) break;
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
        Progress("single-instance-and-tray-reactivation");
        var originalWindow = app!.MainWindowHandle;
        var closePattern = (WindowPattern)AutomationElement.FromHandle(originalWindow).GetCurrentPattern(WindowPattern.Pattern);
        closePattern.Close(); // Normal Close must hide the owner in its tray.
        var hiddenBudget = Stopwatch.StartNew();
        while (Native.IsWindowVisible(originalWindow) && hiddenBudget.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(100);
        if (app.HasExited || Native.IsWindowVisible(originalWindow)) throw new InvalidOperationException("The owner did not remain in its tray.");
        using (var second = Process.Start(new ProcessStartInfo(desktop) { UseShellExecute = false, WorkingDirectory = appRoot })!)
        {
            try { await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch { if (!second.HasExited) { second.Kill(true); second.WaitForExit(10000); } throw; }
            if (second.ExitCode != 0 || app.HasExited) throw new InvalidOperationException("The duplicate launch did not exit successfully.");
        }
        var activationBudget = Stopwatch.StartNew();
        while (!Native.IsWindowVisible(originalWindow) && activationBudget.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(100);
        trayReactivation = Native.IsWindowVisible(originalWindow);
        var ownedProcesses = 0;
        foreach (var candidate in Process.GetProcessesByName("Connector.Desktop"))
        {
            using (candidate)
                if (string.Equals(candidate.MainModule?.FileName, desktop, StringComparison.OrdinalIgnoreCase)) ownedProcesses++;
        }
        singleInstance = ownedProcesses == 1;
        if (!singleInstance || !trayReactivation) throw new InvalidOperationException("Single-instance/tray activation verification failed.");
    }
    finally
    {
        if (app is not null)
        {
            try { if (!app.HasExited) { app.Kill(true); app.WaitForExit(10000); } }
            finally { app.Dispose(); }
        }
    }
    if (File.Exists(deployment.StateFilePath)) throw new InvalidOperationException("Opening the app silently enrolled an account.");
    File.WriteAllText(output, JsonSerializer.Serialize(new { success = true, ordinaryUser = true, actualDesktopWebView = ready,
        shortcuts = true, shortcutLaunched = true, msiProductRegistered = true, shortcutProof = shortcuts,
        bundledGit = true, protectedNetworkReady = true, protectedNetworkCheckSeconds, singleInstance, trayReactivation,
        tokenRequired = false, credentialCreated = false,
        legacyImport = false, blenderIncluded = false }));
}
catch (Exception error)
{
    File.WriteAllText(output, JsonSerializer.Serialize(new { success = false, failure = error.Message, type = error.GetType().Name }));
    Environment.ExitCode = 1;
}

internal static class Native
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr window);
    internal sealed record ShortcutProof(string Path, string Resolution, uint MsiResult, string IconPath, string ShellTarget);
    internal static Task<T> OnStaAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var initialized = false;
            try
            {
                var result = CoInitializeEx(IntPtr.Zero, 6); // STA, disable OLE1 DDE.
                if (result < 0) Marshal.ThrowExceptionForHR(result);
                initialized = true;
                completion.TrySetResult(action());
            }
            catch (Exception error) { completion.TrySetException(error); }
            finally { if (initialized) CoUninitialize(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
    internal static ShortcutProof[] VerifyShortcuts(string desktop, string appRoot)
    {
        foreach (var process in Process.GetProcessesByName("Connector.Desktop"))
        {
            process.Dispose();
            throw new InvalidOperationException("Fresh shortcut fixture requires no existing desktop process.");
        }
        var product = new StringBuilder(39);
        var next = new StringBuilder(39);
        if (MsiEnumRelatedProducts("{67B43FCD-4FE8-4D29-9F64-6F190FCD6E51}", 0, 0, product) != 0 ||
            MsiEnumRelatedProducts("{67B43FCD-4FE8-4D29-9F64-6F190FCD6E51}", 0, 1, next) != 259 ||
            MsiQueryProductState(product.ToString()) != 5)
            throw new InvalidOperationException("Exactly one installed clean MSI product is required.");
        var proofs = new List<ShortcutProof>();
        foreach (var location in new[] { Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.CommonPrograms })
        {
            var path = Path.Combine(Environment.GetFolderPath(location), "Платформа Коннектор.lnk");
            if (!File.Exists(path)) throw new InvalidOperationException("Application shortcut is missing.");
            var shortcutProduct = new StringBuilder(39); var feature = new StringBuilder(39); var component = new StringBuilder(39);
            var result = MsiGetShortcutTarget(path, shortcutProduct, feature, component);
            if (result == 0)
            {
                var target = new StringBuilder(32768); uint size = (uint)target.Capacity;
                if (!string.Equals(shortcutProduct.ToString(), product.ToString(), StringComparison.OrdinalIgnoreCase) ||
                    feature.ToString() != "Complete" || !Guid.TryParse(component.ToString(), out _) ||
                    MsiGetComponentPath(product.ToString(), component.ToString(), target, ref size) != 3 ||
                    !string.Equals(target.ToString(), desktop, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Shortcut MSI identity or component path differs.");
            }
            // Microsoft documents IShellLink as the fallback if MSI lookup fails.
            // Load only: Resolve is deliberately not called (it can repair links).
            var shell = (IShellLinkW)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!)!;
            string iconPath;
            string shellTarget;
            try
            {
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)shell).Load(path, 0);
                var target = new StringBuilder(32768); var arguments = new StringBuilder(32768);
                var working = new StringBuilder(32768); var icon = new StringBuilder(32768);
                shell.GetPath(target, target.Capacity, IntPtr.Zero, 0);
                shell.GetArguments(arguments, arguments.Capacity);
                shell.GetWorkingDirectory(working, working.Capacity);
                shell.GetIconLocation(icon, icon.Capacity, out var iconIndex);
                shellTarget = target.ToString();
                // An advertised link identifies its application through the MSI
                // descriptor, not an ordinary filesystem target. The exact
                // product/feature/component target has already been checked.
                if (result != 0 && !string.Equals(shellTarget, desktop, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Shortcut fallback target differs: '{shellTarget}', expected '{desktop}' (MSI {result}).");
                if (arguments.Length != 0 || !string.Equals(working.ToString().TrimEnd('\\'), appRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Shortcut arguments '{arguments}' or working directory '{working}' differs from '{appRoot}' (MSI {result}).");
                var expectedIcon = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer", product.ToString(), "PlatformIcon.exe");
                iconPath = icon.ToString();
                if (iconIndex != 0 || !string.Equals(iconPath, expectedIcon, StringComparison.OrdinalIgnoreCase) || !File.Exists(iconPath))
                    throw new InvalidOperationException("Shortcut icon does not belong to the installed clean MSI.");
            }
            finally { Marshal.FinalReleaseComObject(shell); }
            using var image = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (image is null) throw new InvalidOperationException("Shortcut icon is absent.");
            proofs.Add(new(path, result == 0 ? "msi" : "shell", result, iconPath, shellTarget));
        }
        return proofs.ToArray();
    }
    [DllImport("ole32.dll")] internal static extern int CoInitializeEx(IntPtr reserved, uint model);
    [DllImport("ole32.dll")] internal static extern void CoUninitialize();
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetShortcutTargetW")]
    internal static extern uint MsiGetShortcutTarget(string path, [Out] StringBuilder product, [Out] StringBuilder feature, [Out] StringBuilder component);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetComponentPathW")]
    internal static extern int MsiGetComponentPath(string product, string component, StringBuilder path, ref uint size);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiEnumRelatedProductsW")]
    private static extern uint MsiEnumRelatedProducts(string upgrade, uint reserved, uint index, [Out] StringBuilder product);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiQueryProductStateW")]
    private static extern int MsiQueryProductState(string product);
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int length, IntPtr data, uint flags);
        void GetIDList(out IntPtr value); void SetIDList(IntPtr value);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int length);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int length);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetHotkey(out short value); void SetHotkey(short value); void GetShowCmd(out int value); void SetShowCmd(int value);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int length, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string value, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string value, uint reserved);
        void Resolve(IntPtr window, uint flags); void SetPath([MarshalAs(UnmanagedType.LPWStr)] string value);
    }
}
