using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Connector.Upgrade.HelperReleaseTrust;
using Connector.Upgrade.Velopack;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Connector.Upgrade.Bootstrapper;

public sealed record DesktopInstallationResult(string InstallRoot, string Version, bool RebootRequired);

public interface IStandaloneDesktopInstaller
{
    Task<DesktopInstallationResult> InstallAsync(IBootstrapperStatusSink status, CancellationToken cancellationToken);
}

/// <summary>
/// Installs the signed desktop payload without enrolling a device, contacting the server,
/// reading credentials, or modifying either legacy application's state.
/// </summary>
public sealed class ProductionStandaloneDesktopInstaller : IStandaloneDesktopInstaller
{
    private const string WebViewClient = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    public async Task<DesktopInstallationResult> InstallAsync(IBootstrapperStatusSink status, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
        if (!identity.IsAuthenticated || identity.User is null || identity.IsSystem ||
            new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new SecurityException("Установка приложения должна выполняться от исходного пользователя без повышения прав.");
        var sid = identity.User.Value;
        using var runGuard = new Semaphore(1, 1, @"Local\Structura.Connector.DesktopInstall." + sid);
        if (!runGuard.WaitOne(0)) throw new InvalidOperationException("Установка коннектора уже выполняется.");
        try
        {
            status.Show("Проверяем компоненты приложения…");
            VerifyPrerequisites();
            var release = new HelperReleasePinSource().GetVelopackSetupPin();
            if (release.PackageId != UnifiedVelopackApplication.PackId)
                throw new SecurityException("Подписанный пакет относится к другому приложению.");
            var setupPin = new Connector.Upgrade.Velopack.VelopackSetupPin(release.PackageId, release.Version,
                release.Size, release.Sha256);
            var localRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var installRoot = Path.Combine(localRoot, release.PackageId);
            var probe = new SystemVelopackInstallProbe(sid, localRoot);
            var before = probe.Inspect(setupPin, sid);
            if (before.Presence == VelopackInstallPresence.Conflicting)
            {
                // A complete same-user installation of an earlier release may be upgraded.
                // Partial installations, redirects and another publisher remain blocked.
                var registration = new CurrentUserVelopackRegistrationReader().Read(release.PackageId, sid);
                var installedVersion = registration?.DisplayVersion;
                if (string.IsNullOrWhiteSpace(installedVersion) ||
                    probe.Inspect(setupPin with { Version = installedVersion }, sid).Presence != VelopackInstallPresence.ExactInstalled)
                    throw new InvalidOperationException("Обнаружена неполная установка коннектора. Требуется восстановление.");
                if (Version.TryParse(installedVersion, out var installed) && Version.TryParse(release.Version, out var candidate) && installed > candidate)
                    throw new InvalidOperationException("Установлена более новая версия коннектора.");
            }
            EnsureApplicationClosed(installRoot);

            // The manifest verifies the ACL-protected source. Hold that exact file against
            // replacement and verify its resolved path and bytes again until Setup exits.
            await using var lease = new FileStream(release.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            AssertPhysicalPath(lease.SafeFileHandle, release.AbsolutePath);
            if (new HelperReleasePinSource().GetVelopackSetupPin() != release)
                throw new SecurityException("Подписанный выпуск приложения изменился во время проверки.");
            if (lease.Length != release.Size ||
                !Convert.ToHexString(await SHA256.HashDataAsync(lease, cancellationToken)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new SecurityException("Пакет приложения не соответствует подписанному выпуску.");
            status.Show("Устанавливаем Structura Connector и создаём ярлыки…");
            var result = await new SystemVelopackProcessRunner().RunAsync(
                new VelopackProcessRequest(release.AbsolutePath, ["--silent"], TimeSpan.FromMinutes(5)), cancellationToken);
            if (result.Completion != VelopackProcessCompletion.Exited || result.ExitCode is not (0 or 3010))
                throw new InvalidOperationException("Установка приложения не завершена. Проверьте журнал установки; повторный запуск автоматически не выполнялся.");
            var after = probe.Inspect(setupPin, sid);
            if (after.Presence != VelopackInstallPresence.ExactInstalled || after.Identity is null)
                throw new InvalidOperationException("Не удалось подтвердить полную установку приложения.");
            using var installedImage = File.Open(Path.Combine(installRoot, "current", UnifiedVelopackApplication.MainExecutable),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            AssertPhysicalPath(installedImage.SafeFileHandle, Path.Combine(installRoot, "current", UnifiedVelopackApplication.MainExecutable));
            status.Show("Structura Connector установлен.");
            return new DesktopInstallationResult(installRoot, release.Version, result.ExitCode == 3010);
        }
        finally { runGuard.Release(); }
    }

    private static void VerifyPrerequisites()
    {
        var netBird = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NetBird", "netbird.exe");
        using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\NetBird");
        if (!File.Exists(netBird) || service is null)
            throw new InvalidOperationException("Не установлен NetBird. Запустите полный EXE установщика коннектора.");
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using var machineClient = machine.OpenSubKey(WebViewClient);
        using var userClient = Registry.CurrentUser.OpenSubKey(WebViewClient);
        static bool Present(object? value) => value is string text && Version.TryParse(text, out var version) && version > new Version(0, 0, 0, 0);
        if (!Present(machineClient?.GetValue("pv")) && !Present(userClient?.GetValue("pv")))
            throw new InvalidOperationException("Не установлен WebView2. Запустите полный EXE установщика коннектора.");
    }

    private static void EnsureApplicationClosed(string installRoot)
    {
        var image = Path.Combine(installRoot, "current", UnifiedVelopackApplication.MainExecutable);
        foreach (var process in Process.GetProcessesByName("Connector.Desktop"))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, image, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Закройте коннектор через значок в области уведомлений и повторите установку.");
                }
                catch (System.ComponentModel.Win32Exception) { } // Another user's process is outside this install root.
            }
        }
    }

    private static void AssertPhysicalPath(SafeFileHandle file, string expected)
    {
        var path = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(file, path, (uint)path.Capacity, 0);
        if (length == 0 || length >= path.Capacity) throw new SecurityException("Не удалось проверить физический путь приложения.");
        var actual = path.ToString();
        if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        if (!string.Equals(actual, Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            throw new SecurityException("Установка перенаправлена в приватный каталог другого приложения. Запустите EXE из Проводника Windows.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
}
