using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using System.Text;

namespace Connector.Desktop.Services;

// Brings up the firm AmneziaWG VPN tunnel on the user's PC using the bundled AmneziaWG client
// (tools/awg/{amneziawg.exe, awg.exe, wintun.dll}). The connector runs per-user without admin,
// so installing the tunnel service needs a one-time UAC elevation; afterwards the service is
// SYSTEM auto-start and survives reboots (no further prompts).
//
// Everything is opt-in / gated by the server (cfg vpn.enabled + a config delivered via bootstrap).
// If no VPN config is delivered, none of this runs and the connector behaves exactly as before.
public sealed class VpnProvisioningService
{
    private readonly string _awgExe;
    private readonly string _confDir;

    public string LogFilePath { get; }

    public VpnProvisioningService()
    {
        var baseDir = AppContext.BaseDirectory;
        _awgExe = Path.Combine(baseDir, "tools", "awg", "amneziawg.exe");

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConnectorAgentDesktop");
        _confDir = Path.Combine(root, "vpn");
        Directory.CreateDirectory(_confDir);
        LogFilePath = Path.Combine(root, "vpn.log");
    }

    public bool BundledClientPresent => File.Exists(_awgExe);

    private static string ServiceName(string tunnel) => "AmneziaWGTunnel$" + tunnel;

    // In-process service queries (no sc.exe spawn) — safe to call from the UI thread.
    public bool IsTunnelInstalled(string tunnel)
    {
        try
        {
            using var sc = new ServiceController(ServiceName(tunnel));
            _ = sc.Status; // throws InvalidOperationException if the service does not exist
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    public bool IsTunnelRunning(string tunnel)
    {
        try
        {
            using var sc = new ServiceController(ServiceName(tunnel));
            return sc.Status == ServiceControllerStatus.Running || sc.Status == ServiceControllerStatus.StartPending;
        }
        catch
        {
            return false;
        }
    }

    // Install (or reinstall) the tunnel from the given config and start it. One-time UAC.
    public VpnResult Enable(string configContent, string tunnel)
    {
        if (!BundledClientPresent)
        {
            return VpnResult.Fail("Встроенный клиент AmneziaWG не найден в составе коннектора. Переустановите коннектор.");
        }
        if (string.IsNullOrWhiteSpace(configContent))
        {
            return VpnResult.Fail("Сервер не передал конфигурацию VPN.");
        }

        var confPath = Path.Combine(_confDir, tunnel + ".conf");
        try
        {
            // tunnel name must match the file name (AmneziaWG derives it from the .conf filename)
            File.WriteAllText(confPath, configContent.Replace("\r\n", "\n"), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            AppendLog("write conf failed: " + ex.Message);
            return VpnResult.Fail("Не удалось сохранить конфигурацию VPN: " + ex.Message);
        }

        // If already installed, uninstall first so the new config/keys take effect.
        if (IsTunnelInstalled(tunnel))
        {
            var unCode = RunElevated("/uninstalltunnelservice " + tunnel, out _);
            if (unCode == ElevationCancelled)
            {
                return VpnResult.Fail("Переустановка VPN отменена (не подтверждён запрос прав администратора).");
            }
            // SCM keeps the service in DELETE_PENDING until handles close; install would fail otherwise.
            if (!WaitUntil(() => !IsTunnelInstalled(tunnel), TimeSpan.FromSeconds(10)))
            {
                AppendLog("previous tunnel still deleting");
                return VpnResult.Fail("Предыдущий VPN-туннель ещё удаляется. Повторите через несколько секунд.");
            }
        }

        AppendLog($"installtunnelservice {tunnel} from {confPath}");
        var code = RunElevated("/installtunnelservice \"" + confPath + "\"", out var elevErr);
        if (code == ElevationCancelled)
        {
            return VpnResult.Fail("Установка VPN отменена (не подтверждён запрос прав администратора). Нажмите ещё раз и подтвердите.");
        }

        // /installtunnelservice runs elevated with UseShellExecute, so we can't read its stdout;
        // verify by service state instead.
        var running = WaitUntil(() => IsTunnelRunning(tunnel), TimeSpan.FromSeconds(12));
        // The tunnel service has copied the config into its own protected store; do not leave the
        // plaintext private key lying in LocalAppData.
        if (running)
        {
            TryDeleteConf(confPath);
            AppendLog("tunnel running");
            return VpnResult.Success("VPN-доступ к общей папке включён.");
        }

        var hint = string.IsNullOrWhiteSpace(elevErr) ? "" : " " + elevErr;
        AppendLog("tunnel did not reach RUNNING." + hint);
        return VpnResult.Fail("VPN установлен, но туннель не поднялся. Проверьте лог VPN." + hint);
    }

    public VpnResult Disable(string tunnel)
    {
        var confPath = Path.Combine(_confDir, tunnel + ".conf");
        if (!IsTunnelInstalled(tunnel))
        {
            TryDeleteConf(confPath);
            return VpnResult.Success("VPN уже отключён.");
        }
        var code = RunElevated("/uninstalltunnelservice " + tunnel, out _);
        if (code == ElevationCancelled)
        {
            return VpnResult.Fail("Отключение VPN отменено (не подтверждён запрос прав администратора).");
        }
        WaitUntil(() => !IsTunnelInstalled(tunnel), TimeSpan.FromSeconds(8));
        TryDeleteConf(confPath);
        AppendLog("tunnel uninstalled");
        return VpnResult.Success("VPN-доступ отключён.");
    }

    private void TryDeleteConf(string confPath)
    {
        try
        {
            if (File.Exists(confPath))
            {
                File.Delete(confPath);
            }
        }
        catch (Exception ex)
        {
            AppendLog("conf cleanup skipped: " + ex.Message);
        }
    }

    public void AppendLog(string message)
    {
        try
        {
            File.AppendAllText(LogFilePath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // logging must never break VPN control
        }
    }

    private const int ElevationCancelled = -1223;

    private int RunElevated(string arguments, out string error)
    {
        error = string.Empty;
        try
        {
            var psi = new ProcessStartInfo(_awgExe, arguments)
            {
                UseShellExecute = true,   // required for Verb=runas (UAC)
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                error = "не удалось запустить процесс AmneziaWG";
                return -1;
            }
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — user declined the UAC prompt
            return ElevationCancelled;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return -1;
        }
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            Thread.Sleep(400);
        }
        return condition();
    }
}

public sealed class VpnResult
{
    public bool IsSuccess { get; init; }
    public string Message { get; init; } = "";

    public static VpnResult Success(string message) => new() { IsSuccess = true, Message = message };
    public static VpnResult Fail(string message) => new() { IsSuccess = false, Message = message };
}
