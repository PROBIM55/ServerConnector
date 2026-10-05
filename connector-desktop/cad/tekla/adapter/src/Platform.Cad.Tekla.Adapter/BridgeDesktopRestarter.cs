// Fallback recovery когда Bridge.Desktop ответил 503/BRIDGE_STALE_TEKLA
// или вообще не отвечает: hard-restart Bridge.Desktop процесса.
//
// Цепочка запуска (от предпочтительного к запасному):
//   1. Scheduled Task PlatformBridgeDesktop (schtasks /Run) — штатный путь,
//      процесс живёт под Task Scheduler и переживает выход Connector'а.
//   2. Прямой запуск exe (%LOCALAPPDATA%\Platform\Bridge\app\
//      Platform.Bridge.Desktop.Tekla.exe, скрытое окно) — если задача не
//      зарегистрирована или не подняла процесс.
// Какой механизм сработал — видно в BridgeRestartOutcome.Mechanism (UI
// показывает это пользователю, раньше fallback был молчаливым).
//
// Используется TeklaProviderAdapter автоматически (retry после stale Tekla)
// и BridgeDesktopController'ом из Desktop UI (кнопки Запустить/Перезапустить).
//
// Расчёт когда нужен hard-restart: in-process reconnect в Bridge.Desktop
// (`TeklaWorker.RunOne` + `TeklaConnection.Invalidate`) уже попробовал
// пересоздать `Tekla.Structures.Model.Model()`. Если это не помогло —
// Trimble Remoting сохранил мёртвые TCP-каналы / static service references,
// которые сбрасываются только пере-инициализацией процесса.

using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Platform.Cad.Tekla.Adapter;

/// <summary>Итог перезапуска Bridge.Desktop: жив ли /health и каким механизмом подняли процесс.</summary>
public sealed record BridgeRestartOutcome(bool Healthy, string Mechanism, string? Detail = null)
{
    public const string MechanismScheduledTask = "scheduled-task";
    public const string MechanismDirectExe = "direct-exe";
    public const string MechanismNone = "none";
}

/// <summary>Абстракция для тестов: реальный restarter ходит в Task Scheduler/exe, mock не делает ничего.</summary>
public interface IBridgeDesktopRestarter
{
    /// <summary>True если на платформе доступен (Windows). false → recovery skip.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Stop процесса Bridge.Desktop + запуск (Scheduled Task → fallback exe) +
    /// ожидание /health (до <paramref name="totalTimeout"/>). Возвращает true если
    /// после всего этого Bridge.Desktop отвечает 200 на /health.
    /// </summary>
    Task<bool> RestartAndWaitAsync(
        string bridgeBaseUrl,
        Func<string, HttpClient> httpClientFactory,
        TimeSpan totalTimeout,
        CancellationToken cancellationToken);

    /// <summary>
    /// То же, но с подробным итогом (механизм запуска + причина неудачи) —
    /// для UI. Default-реализация оборачивает <see cref="RestartAndWaitAsync"/>.
    /// </summary>
    async Task<BridgeRestartOutcome> RestartAndWaitDetailedAsync(
        string bridgeBaseUrl,
        Func<string, HttpClient> httpClientFactory,
        TimeSpan totalTimeout,
        CancellationToken cancellationToken)
    {
        var healthy = await RestartAndWaitAsync(bridgeBaseUrl, httpClientFactory, totalTimeout, cancellationToken)
            .ConfigureAwait(false);
        return new BridgeRestartOutcome(
            healthy,
            healthy ? BridgeRestartOutcome.MechanismScheduledTask : BridgeRestartOutcome.MechanismNone);
    }
}

/// <summary>
/// Windows-only реализация: убивает Platform.Bridge.Desktop.Tekla процесс,
/// запускает Scheduled Task PlatformBridgeDesktop через schtasks.exe, при
/// неудаче — стартует exe напрямую.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsBridgeDesktopRestarter : IBridgeDesktopRestarter
{
    private const string ProcessName = "Platform.Bridge.Desktop.Tekla";
    private const string ExePathEnvVar = "PLATFORM_BRIDGE_EXE_PATH";

    /// <summary>Окно ожидания /health после запуска Scheduled Task, прежде чем пробовать exe напрямую.</summary>
    private static readonly TimeSpan ScheduledTaskHealthWindow = TimeSpan.FromSeconds(12);

    public static string DefaultBridgeExePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Platform", "Bridge", "app", "Platform.Bridge.Desktop.Tekla.exe");

    public bool IsAvailable => OperatingSystem.IsWindows();

    public async Task<bool> RestartAndWaitAsync(
        string bridgeBaseUrl,
        Func<string, HttpClient> httpClientFactory,
        TimeSpan totalTimeout,
        CancellationToken cancellationToken)
    {
        var outcome = await RestartAndWaitDetailedAsync(bridgeBaseUrl, httpClientFactory, totalTimeout, cancellationToken)
            .ConfigureAwait(false);
        return outcome.Healthy;
    }

    public async Task<BridgeRestartOutcome> RestartAndWaitDetailedAsync(
        string bridgeBaseUrl,
        Func<string, HttpClient> httpClientFactory,
        TimeSpan totalTimeout,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            return new BridgeRestartOutcome(false, BridgeRestartOutcome.MechanismNone, "Доступно только на Windows.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(totalTimeout);

        var target = ResolveTarget(bridgeBaseUrl);
        if (target is null)
        {
            return new BridgeRestartOutcome(
                false,
                BridgeRestartOutcome.MechanismNone,
                $"Неизвестный профиль Bridge.Desktop: {bridgeBaseUrl}.");
        }

        KillExistingProcesses(target);

        // ── 1. Прямой СКРЫТЫЙ запуск exe (основной путь) ──
        // Запускаем сами с CreateNoWindow: никаких терминалов у пользователя
        // (schtasks показывал бы консоль до перевода Bridge.Desktop на WinExe,
        // и задача может быть вовсе не зарегистрирована).
        string? directDetail = null;
        var exePath = ResolveBridgeExePath(target);
        if (exePath is null)
        {
            directDetail = $"Exe не найден: {DefaultBridgeExePath}.";
        }
        else if (!TryStartDirectExe(exePath, target.Arguments, out var startError))
        {
            directDetail = $"Прямой запуск exe не удался: {startError}";
        }
        else
        {
            var directHealthy = await WaitForHealthAsync(bridgeBaseUrl, httpClientFactory, cts.Token).ConfigureAwait(false);
            if (directHealthy)
            {
                return new BridgeRestartOutcome(true, BridgeRestartOutcome.MechanismDirectExe);
            }

            directDetail = "Exe запущен напрямую, но /health не ответил за отведённое время.";
        }

        // ── 2. Fallback: Scheduled Task (если зарегистрирована) ──
        if (await StartScheduledTaskAsync(target.TaskName, cts.Token).ConfigureAwait(false))
        {
            using (var taskWindowCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
            {
                taskWindowCts.CancelAfter(ScheduledTaskHealthWindow);
                if (await WaitForHealthAsync(bridgeBaseUrl, httpClientFactory, taskWindowCts.Token).ConfigureAwait(false))
                {
                    return new BridgeRestartOutcome(true, BridgeRestartOutcome.MechanismScheduledTask, directDetail);
                }
            }

            if (IsBridgeProcessAlive(target))
            {
                // Задача подняла процесс, он просто медленно стартует — ждём остаток таймаута.
                var healthy = await WaitForHealthAsync(bridgeBaseUrl, httpClientFactory, cts.Token).ConfigureAwait(false);
                return new BridgeRestartOutcome(
                    healthy,
                    BridgeRestartOutcome.MechanismScheduledTask,
                    healthy ? directDetail : $"{directDetail} Процесс запущен задачей, но /health не ответил.");
            }
        }

        return new BridgeRestartOutcome(
            false,
            BridgeRestartOutcome.MechanismNone,
            $"{directDetail} Scheduled Task {target.TaskName} тоже не помогла (нет задачи или процесс не поднялся). Установите Bridge.Desktop (install_bridge_desktop.ps1).");
    }

    private static BridgeRuntimeTarget? ResolveTarget(string bridgeBaseUrl)
    {
        if (!Uri.TryCreate(bridgeBaseUrl, UriKind.Absolute, out var uri)) return null;
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Platform", "Bridge");
        if (uri.Port == 39420)
        {
            var profileRoot = Path.Combine(root, "profiles", "2020.0");
            return new BridgeRuntimeTarget(
                "PlatformBridgeDesktop2020",
                Path.Combine(profileRoot, "app", "Platform.Bridge.Desktop.Tekla.exe"),
                $"--port 39420 --tekla-version 2020.0 --expose-model-path --tekla-session Console " +
                $"--log \"{Path.Combine(profileRoot, "desktop.jsonl")}\" " +
                $"--object-map \"{Path.Combine(profileRoot, "object-map.json")}\" " +
                $"--idempotency-log \"{Path.Combine(profileRoot, "operation-log.jsonl")}\"");
        }
        if (uri.Port == 39421)
        {
            return new BridgeRuntimeTarget(
                "PlatformBridgeDesktop",
                DefaultBridgeExePath,
                string.Empty);
        }
        return null;
    }

    private static string? ResolveBridgeExePath(BridgeRuntimeTarget target)
    {
        var overridePath = Environment.GetEnvironmentVariable(ExePathEnvVar);
        if (target.TaskName == "PlatformBridgeDesktop" &&
            !string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        return File.Exists(target.ExePath) ? target.ExePath : null;
    }

    private static bool TryStartDirectExe(string exePath, string arguments, out string? error)
    {
        error = null;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exePath, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
            });
            if (process is null)
            {
                error = "Process.Start вернул null.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsBridgeProcessAlive(BridgeRuntimeTarget target)
    {
        try
        {
            var processes = Process.GetProcessesByName(ProcessName);
            var alive = processes.Any(process => IsTargetProcess(process, target));
            foreach (var p in processes)
            {
                try { p.Dispose(); } catch { /* ignore */ }
            }

            return alive;
        }
        catch
        {
            return false;
        }
    }

    private static void KillExistingProcesses(BridgeRuntimeTarget target)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(ProcessName))
            {
                if (!IsTargetProcess(p, target))
                {
                    try { p.Dispose(); } catch { /* ignore */ }
                    continue;
                }
                try { p.Kill(entireProcessTree: true); } catch { /* swallow — могли уже умереть */ }
                try { p.Dispose(); } catch { /* ignore */ }
            }
        }
        catch
        {
            // GetProcessesByName может фейлиться на закрытых сессиях — ничего не делаем.
        }
    }

    private static bool IsTargetProcess(Process process, BridgeRuntimeTarget target)
    {
        try
        {
            var path = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(path) &&
                   string.Equals(Path.GetFullPath(path), Path.GetFullPath(target.ExePath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> StartScheduledTaskAsync(string taskName, CancellationToken ct)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo("schtasks.exe", $"/Run /TN \"{taskName}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };
            if (!p.Start()) return false;
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private sealed record BridgeRuntimeTarget(string TaskName, string ExePath, string Arguments);

    private static async Task<bool> WaitForHealthAsync(
        string bridgeBaseUrl,
        Func<string, HttpClient> httpClientFactory,
        CancellationToken ct)
    {
        var healthUrl = bridgeBaseUrl.TrimEnd('/') + "/health";
        // Poll каждые 500мс пока CTS не истёк или /health не вернёт 200.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var http = httpClientFactory(bridgeBaseUrl);
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(TimeSpan.FromSeconds(2));
                using var resp = await http.GetAsync(healthUrl, probeCts.Token).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return true;
            }
            catch
            {
                // not up yet — продолжаем
            }
            try { await Task.Delay(500, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        return false;
    }
}

/// <summary>No-op реализация для тестов / non-Windows окружений.</summary>
public sealed class NoopBridgeDesktopRestarter : IBridgeDesktopRestarter
{
    public bool IsAvailable => false;
    public Task<bool> RestartAndWaitAsync(string _, Func<string, HttpClient> __, TimeSpan ___, CancellationToken ____)
        => Task.FromResult(false);
}
