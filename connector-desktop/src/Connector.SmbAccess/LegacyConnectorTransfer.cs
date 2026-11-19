using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Connector.SmbAccess;

/// <summary>What the desktop composition knows about the new connection (K5 preconditions).</summary>
/// <param name="ConnectionReady">Платформа Коннектор is Ready with a current access profile.</param>
/// <param name="LegacyShareGranted">The current SMB grant of this device contains the exact legacy share.</param>
/// <param name="TeklaUsed">This account has the Tekla module, so Tekla MultiUser depends on the tunnel.</param>
public sealed record LegacyTransferReadiness(bool ConnectionReady, bool LegacyShareGranted, bool TeklaUsed);

/// <summary>Read-only evaluation before the confirmation dialog. Texts are Russian and contain no paths.</summary>
public sealed record LegacyTransferPreflight(
    bool Transferred, bool Incomplete, IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings)
{
    public bool CanTransfer => !Transferred && Blockers.Count == 0;
}

public sealed record LegacyTransferStatus(bool Transferred, bool Incomplete, DateTimeOffset? TransferredAtUtc)
{
    public bool CanRestore => Transferred || Incomplete;
}

public enum LegacyTransferOutcome
{
    Completed,
    AlreadyDone,
    Refused,
    /// <summary>Stopped before completion; every change made so far was undone.</summary>
    Aborted,
    /// <summary>Stopped and the automatic undo failed; the journal stays for «Вернуть прежний коннектор».</summary>
    PartiallyApplied,
    Failed,
}

/// <summary>Result of a transfer or a rollback. <see cref="Code"/> is safe to log; the message is for the user.</summary>
public sealed record LegacyTransferResult(LegacyTransferOutcome Outcome, string Code, string Message);

/// <param name="UserConfirmed">The user confirmed the transfer in the native dialog.</param>
/// <param name="ConfirmForceClose">Asked only when the old client did not exit after the graceful request.</param>
public sealed record LegacyTransferRequest(
    bool UserConfirmed, LegacyTransferReadiness Readiness, Func<Task<bool>>? ConfirmForceClose = null);

/// <summary>
/// K5 «перевод ПК» (owner decision 25.10, variant 1): exactly one connector of a PC talks to the
/// legacy server. The transfer closes the previous Structura Connector 1.0.x of this Windows session,
/// removes its HKCU Run value (saved byte-exact) and its deviceless SMB uses of the legacy host, then
/// writes the marker that turns neighbour mode off. The AmneziaWG tunnel service, the old device token,
/// settings and installation are never touched, and nothing is elevated. Every step is journalled
/// before the next one; a failed step is undone at once, and an undo that fails leaves the journal for
/// the explicit rollback «Вернуть прежний коннектор».
/// </summary>
public sealed class LegacyConnectorTransferService
{
    public static readonly TimeSpan DefaultCloseTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan DefaultKillTimeout = TimeSpan.FromSeconds(10);
    internal const int ErrorNotConnected = 2250;
    internal const int ErrorOpenFiles = 2401;
    internal const int ErrorDeviceInUse = 2404;
    internal const int MaximumStateBytes = 64 * 1024;
    private const string LegacyExecutableName = WindowsLegacyConnectorDetector.LegacyProcessName + ".exe";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ILegacyConnectorProbes _probes;
    private readonly ILegacyTransferPorts _ports;
    private readonly TimeProvider _time;
    private readonly string _markerPath;
    private readonly string _statePath;
    private readonly TimeSpan _closeTimeout;
    private readonly TimeSpan _killTimeout;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _noticeSync = new();
    private readonly HashSet<int> _noticedProcesses = new();

    public LegacyConnectorTransferService()
        : this(new WindowsLegacyConnectorProbes(), new WindowsLegacyTransferPorts(), TimeProvider.System,
            LegacyConnectorTransferMarker.DefaultPath, LegacyConnectorTransferMarker.DefaultStatePath) { }

    internal LegacyConnectorTransferService(
        ILegacyConnectorProbes probes,
        ILegacyTransferPorts ports,
        TimeProvider time,
        string markerPath,
        string statePath,
        TimeSpan? closeTimeout = null,
        TimeSpan? killTimeout = null)
    {
        _probes = probes ?? throw new ArgumentNullException(nameof(probes));
        _ports = ports ?? throw new ArgumentNullException(nameof(ports));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _markerPath = markerPath ?? throw new ArgumentNullException(nameof(markerPath));
        _statePath = statePath ?? throw new ArgumentNullException(nameof(statePath));
        _closeTimeout = closeTimeout ?? DefaultCloseTimeout;
        _killTimeout = killTimeout ?? DefaultKillTimeout;
    }

    /// <summary>Cheap: two file checks and one small read. Never touches processes, registry or SMB.</summary>
    public LegacyTransferStatus ReadStatus()
    {
        var marker = _ports.FileExists(_markerPath);
        var journal = _ports.FileExists(_statePath);
        var state = journal ? TryReadState(_statePath) : null;
        if (marker) state ??= TryReadState(_markerPath);
        return new LegacyTransferStatus(marker, !marker && journal, marker ? state?.CompletedAtUtc : null);
    }

    public LegacyTransferPreflight Preflight(LegacyTransferReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);
        var status = ReadStatus();
        if (status.Transferred) return new LegacyTransferPreflight(true, false, [], []);

        var blockers = new List<string>();
        var warnings = new List<string>();
        if (status.Incomplete)
            blockers.Add("Предыдущий перевод не завершён. Нажмите «Вернуть прежний коннектор», затем повторите перевод.");
        if (!readiness.ConnectionReady)
            blockers.Add("Платформа Коннектор ещё не подключён. Дождитесь состояния «Подключено» и повторите.");
        else if (!readiness.LegacyShareGranted)
            blockers.Add("Этому устройству ещё не выдан доступ к папке BIM_Models. Обратитесь к администратору.");
        if (readiness.TeklaUsed)
        {
            // Read-only SCM query: the tunnel keeps serving Tekla MultiUser and SMB after the transfer.
            bool running;
            try { running = _probes.IsServiceRunning(WindowsLegacyConnectorDetector.TunnelServiceName); }
            catch (Exception) { running = false; }
            if (!running)
                warnings.Add("Защищённый туннель прежнего коннектора сейчас не запущен: без него Tekla MultiUser " +
                    "не получит доступ к серверу. Перевод туннель не включает и не выключает.");
        }
        return new LegacyTransferPreflight(false, status.Incomplete, blockers.AsReadOnly(), warnings.AsReadOnly());
    }

    public async Task<LegacyTransferResult> TransferAsync(
        LegacyTransferRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!request.UserConfirmed)
                return Result(LegacyTransferOutcome.Refused, "legacy_not_confirmed", "Перевод не подтверждён.");
            var preflight = Preflight(request.Readiness);
            if (preflight.Transferred)
                return Result(LegacyTransferOutcome.AlreadyDone, "legacy_already_transferred",
                    "Компьютер уже переведён на Платформа Коннектор.");
            if (preflight.Blockers.Count > 0)
                return Result(LegacyTransferOutcome.Refused, "legacy_preconditions",
                    "Перевод недоступен. " + string.Join(" ", preflight.Blockers));
            // The first native change ignores cancellation: a half-done step is never abandoned.
            cancellationToken.ThrowIfCancellationRequested();
            return await TransferCoreAsync(request).ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task<LegacyTransferResult> TransferCoreAsync(LegacyTransferRequest request)
    {
        // Handles opened right after identification pin exactly those processes: a reused PID can
        // never be asked to close, waited for or killed in their place.
        var processes = new List<ILegacyProcess>();
        try
        {
            return await TransferWithProcessesAsync(request, processes).ConfigureAwait(false);
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private async Task<LegacyTransferResult> TransferWithProcessesAsync(
        LegacyTransferRequest request, List<ILegacyProcess> processes)
    {
        LegacyRunValue? autoStart;
        IReadOnlyList<LegacyProcessImage> images;
        try
        {
            autoStart = _ports.ReadAutoStartValue();
            bool unreadable;
            (images, unreadable) = WindowsLegacyConnectorDetector.FindLegacyProcesses(
                _probes, _probes.OwnProcessPath, autoStart?.Executable);
            if (unreadable)
                return Result(LegacyTransferOutcome.Refused, "legacy_process_unknown",
                    "Не удалось однозначно определить процесс прежнего коннектора. Закройте его через значок " +
                    "в области уведомлений («Закрыть») и повторите перевод.");
            // Read-only precheck before anything is closed: removing a busy session would fail anyway,
            // and a restarted old client would force-delete it (v1.0.31 `net use … /delete /y`).
            if (HasOpenLegacyFiles())
                return Result(LegacyTransferOutcome.Refused, "legacy_smb_files_open", OpenFilesMessage);
            foreach (var image in images)
                if (_ports.OpenProcess(image) is { } process) processes.Add(process);
        }
        catch (Exception)
        {
            return Result(LegacyTransferOutcome.Failed, "legacy_identify_failed",
                "Не удалось проверить прежний коннектор. Перевод не выполнялся.");
        }

        var state = new LegacyTransferState
        {
            StartedAtUtc = _time.GetUtcNow(),
            AutoStart = IsOldClientAutoStart(autoStart) ? LegacyRunValueState.From(autoStart!) : null,
            LegacyExecutable = ResolveExecutable(autoStart?.Executable, images),
            ProcessWasRunning = processes.Count > 0,
        };
        try { SaveState(state); }
        catch (Exception)
        {
            return Result(LegacyTransferOutcome.Failed, "legacy_state_unavailable",
                "Не удалось сохранить состояние перевода. Перевод не выполнялся.");
        }

        try
        {
            // 1. The old client exits first, so nothing re-creates its Run value or SMB session.
            foreach (var process in processes)
            {
                var closed = await CloseAsync(process, request.ConfirmForceClose).ConfigureAwait(false);
                if (closed == CloseOutcome.Declined)
                    return Compensate(state, LegacyTransferOutcome.Aborted, "legacy_close_declined",
                        "Перевод отменён: прежний коннектор продолжает работать.");
                if (closed == CloseOutcome.Failed)
                    return Compensate(state, LegacyTransferOutcome.Aborted, "legacy_close_failed",
                        "Не удалось закрыть прежний коннектор. Закройте его через значок в области уведомлений " +
                        "(«Закрыть») и повторите перевод.");
                state.ClosedProcesses++;
                state.ForcedClose |= closed == CloseOutcome.Forced;
                SaveState(state);
            }

            // 2. Its autostart: the exact current bytes are journalled before the value is deleted.
            if (state.AutoStart is not null && _ports.ReadAutoStartValue() is { } current && IsOldClientAutoStart(current))
            {
                state.AutoStart = LegacyRunValueState.From(current);
                SaveState(state);
                _ports.DeleteAutoStartValue();
                state.AutoStartRemoved = true;
                SaveState(state);
            }

            // 3. Every deviceless use of the legacy host (see LegacyDevicelessConnections).
            //    force=false: files opened after the precheck stop the transfer.
            foreach (var connection in LegacyDevicelessConnections())
            {
                var status = _ports.CancelConnection(connection.RemoteName);
                if (status is ErrorOpenFiles or ErrorDeviceInUse)
                    return Compensate(state, LegacyTransferOutcome.Aborted, "legacy_smb_files_open", OpenFilesMessage);
                if (status is not (0 or ErrorNotConnected))
                    return Compensate(state, LegacyTransferOutcome.Failed, "legacy_smb_cancel_failed",
                        "Не удалось отключить прежний коннектор от папки BIM_Models.");
                state.RemovedConnections.Add(connection.RemoteName);
                SaveState(state);
            }
            if (LegacyDevicelessConnections().Count > 0)
                return Compensate(state, LegacyTransferOutcome.Failed, "legacy_smb_still_connected",
                    "Windows всё ещё сообщает о подключении прежнего коннектора к папке BIM_Models. Повторите перевод.");

            // 4. Completion: journal first, then the marker that turns neighbour mode off.
            state.Phase = LegacyTransferState.Completed;
            state.CompletedAtUtc = _time.GetUtcNow();
            SaveState(state);
            _ports.WriteFileAtomic(_markerPath, Serialize(state));
            ForgetNotices();
            return Result(LegacyTransferOutcome.Completed, "legacy_transferred",
                "Компьютер переведён на Платформа Коннектор. Папка BIM_Models теперь открывается здесь; " +
                "защищённый туннель для Tekla продолжает работать.");
        }
        catch (Exception)
        {
            return Compensate(state, LegacyTransferOutcome.Failed, "legacy_transfer_failed",
                "Перевод не выполнен. Подробности доступны в журнале.");
        }
    }

    /// <summary>
    /// «Вернуть прежний коннектор»: releases only this connector's own drive mappings of the legacy share,
    /// restores the saved Run value exactly, removes the marker (neighbour mode resumes), starts the old
    /// client and removes the journal. Idempotent; a missing journal falls back to the marker copy and
    /// then to the v1.0.31 default autostart command.
    /// </summary>
    public async Task<LegacyTransferResult> RestoreAsync(
        Func<CancellationToken, Task> releaseOwnLegacyDrives, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releaseOwnLegacyDrives);
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var markerExists = _ports.FileExists(_markerPath);
            var journalExists = _ports.FileExists(_statePath);
            if (!markerExists && !journalExists)
                return Result(LegacyTransferOutcome.AlreadyDone, "legacy_not_transferred",
                    "Компьютер не переведён: прежний коннектор работает как раньше.");
            var state = (journalExists ? TryReadState(_statePath) : null) ??
                (markerExists ? TryReadState(_markerPath) : null);
            cancellationToken.ThrowIfCancellationRequested();

            try { await releaseOwnLegacyDrives(CancellationToken.None).ConfigureAwait(false); }
            catch (CommonSmbAccessException exception) when (exception.Code == "smb_mapping_busy")
            {
                return Result(LegacyTransferOutcome.Refused, "legacy_own_drive_busy",
                    "На диске с папкой BIM_Models открыты файлы. Сохраните и закройте их, затем повторите.");
            }
            catch (Exception)
            {
                return Result(LegacyTransferOutcome.Failed, "legacy_own_drive_failed",
                    "Не удалось отключить диск с папкой BIM_Models. Подробности доступны в журнале.");
            }

            try
            {
                // Any drive letter still mapped to the legacy host (e.g. a temporary mapping left by an
                // earlier run of this connector, which survives restarts and updates) would make the
                // old client hit 1219 and force-delete every connection to the server. Never claim a
                // restore while one remains; nothing has been changed yet except our own release.
                var mapped = _probes.EnumerateSmbConnections()
                    .Where(connection => !string.IsNullOrEmpty(connection.LocalName) &&
                        WindowsLegacyConnectorDetector.IsLegacyHost(connection.RemoteName))
                    .Select(connection => connection.LocalName!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (mapped.Length > 0)
                    return Result(LegacyTransferOutcome.Refused, "legacy_drive_mapped",
                        $"{(mapped.Length == 1 ? "Диск" : "Диски")} {string.Join(", ", mapped)} всё ещё " +
                        $"{(mapped.Length == 1 ? "подключён" : "подключены")} к серверу папки BIM_Models " +
                        "(например, прежним запуском Платформа Коннектор). Отключите в проводнике («Отключить») " +
                        "и повторите возврат: иначе прежний коннектор при запуске сам отключит все подключения к серверу.");

                var autoStartPresent = RestoreAutoStart(state);
                // Neighbour mode resumes before the old client starts, so it is never reported as foreign.
                DeleteIfExists(_markerPath);
                ForgetNotices();
                var executable = ResolveRestoreExecutable(state);
                var started = executable is not null && StartLegacyIfStopped(executable);
                DeleteIfExists(_statePath);
                if (started)
                    return Result(LegacyTransferOutcome.Completed, "legacy_restored",
                        "Прежний Structura Connector возвращён и запущен. Папку BIM_Models снова обслуживает он.");
                return Result(LegacyTransferOutcome.Completed, "legacy_restored_not_started", autoStartPresent
                    ? "Автозапуск прежнего коннектора восстановлен, но сам он не найден на этом компьютере. " +
                      "Установите Structura Connector заново."
                    : "Прежний коннектор не найден на этом компьютере, и сохранённого автозапуска нет. " +
                      "Установите Structura Connector заново.");
            }
            catch (Exception)
            {
                return Result(LegacyTransferOutcome.Failed, "legacy_restore_failed",
                    "Возврат прежнего коннектора не завершён. Повторите действие; подробности доступны в журнале.");
            }
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>
    /// While transferred: old-client processes the user started by hand, each reported once per
    /// process. Nothing is closed automatically. Skipped while a transfer or rollback runs.
    /// </summary>
    public IReadOnlyList<int> TakeNewlyStartedLegacyProcesses()
    {
        if (!_ports.FileExists(_markerPath))
        {
            ForgetNotices();
            return [];
        }
        if (!_serial.Wait(0)) return [];
        try
        {
            var state = TryReadState(_statePath) ?? TryReadState(_markerPath);
            var executable = state?.AutoStart?.ToValue()?.Executable ?? state?.LegacyExecutable;
            var (matches, _) = WindowsLegacyConnectorDetector.FindLegacyProcesses(
                _probes, _probes.OwnProcessPath, executable);
            var fresh = new List<int>();
            lock (_noticeSync)
            {
                _noticedProcesses.IntersectWith(matches.Select(match => match.ProcessId));
                foreach (var match in matches)
                    if (_noticedProcesses.Add(match.ProcessId)) fresh.Add(match.ProcessId);
            }
            return fresh.AsReadOnly();
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task<CloseOutcome> CloseAsync(ILegacyProcess process, Func<Task<bool>>? confirmForceClose)
    {
        // The shared identification already excludes this process; never rely on that alone here.
        if (process.ProcessId == _probes.OwnProcessId) return CloseOutcome.Failed;

        // v1.0.31 hides to the tray on WM_CLOSE (MainWindow_Closing cancels unless the tray menu
        // «Закрыть» set _allowClose), so the only graceful exit is the session-end request handled by
        // the WPF Application: WM_QUERYENDSESSION → Application.Shutdown → windows close ignoring Cancel.
        // Per-window answers or timeouts do not matter: only the exit of the pinned process counts.
        // Without any window an exit is unlikely; a short wait only covers one already under way.
        var asked = _ports.RequestGracefulClose(process);
        var wait = asked ? _closeTimeout : TimeSpan.FromTicks(Math.Min(_closeTimeout.Ticks, TimeSpan.TicksPerSecond * 2));
        if (await _ports.WaitForExitAsync(process, wait).ConfigureAwait(false))
            return CloseOutcome.Graceful;

        if (confirmForceClose is null || !await confirmForceClose().ConfigureAwait(false))
            return CloseOutcome.Declined;
        // The dialog may stay open for minutes; the handle still refers to this very process.
        if (await _ports.WaitForExitAsync(process, TimeSpan.Zero).ConfigureAwait(false)) return CloseOutcome.Graceful;
        try { _ports.Kill(process); }
        catch (Exception) { return CloseOutcome.Failed; }
        return await _ports.WaitForExitAsync(process, _killTimeout).ConfigureAwait(false)
            ? CloseOutcome.Forced
            : CloseOutcome.Failed;
    }

    /// <summary>
    /// Undoes a stopped transfer: the Run value comes back exactly, the journal goes. The old client is
    /// deliberately NOT started again: on start it auto-connects and, when its tunnel is not ready,
    /// runs <c>net use \\host\share /delete /y</c>, which would force-close the very files the
    /// transfer stopped for (and may open Explorer or ask for UAC). The user starts it or retries.
    /// </summary>
    private LegacyTransferResult Compensate(
        LegacyTransferState state, LegacyTransferOutcome outcome, string code, string message)
    {
        try
        {
            if (state.AutoStartRemoved && state.AutoStart?.ToValue() is { } saved && _ports.ReadAutoStartValue() is null)
                _ports.WriteAutoStartValue(saved);
            DeleteIfExists(_statePath);
            var suffix = state.ClosedProcesses > 0
                ? " Прежний коннектор закрыт, его автозапуск сохранён. Повторите перевод или запустите прежний " +
                  "коннектор вручную (меню «Пуск» → Structura Connector)."
                : string.Empty;
            return Result(outcome, code, message + suffix);
        }
        catch (Exception)
        {
            state.CompensationFailed = true;
            try { SaveState(state); }
            catch (Exception) { /* The journal written by the last completed step stays. */ }
            return Result(LegacyTransferOutcome.PartiallyApplied, "legacy_transfer_partial:" + code,
                "Перевод прерван, и прежнее состояние не удалось вернуть автоматически. " +
                "Нажмите «Вернуть прежний коннектор».");
        }
    }

    /// <returns>Whether an autostart value of the old client exists afterwards.</returns>
    private bool RestoreAutoStart(LegacyTransferState? state)
    {
        // A value written after the transfer (the old client sets it again on connect) is newer; keep it.
        if (_ports.ReadAutoStartValue() is not null) return true;
        if (state is not null)
        {
            if (state.AutoStart?.ToValue() is not { } saved) return false;
            _ports.WriteAutoStartValue(saved);
            return true;
        }
        // Marker without a readable journal: v1.0.31 AutoStartService writes exactly "\"<exe>\"".
        var executable = _probes.LegacyDefaultExecutable;
        if (!_ports.FileExists(executable)) return false;
        _ports.WriteAutoStartValue(LegacyRunValue.FromCommand($"\"{executable}\""));
        return true;
    }

    private const string OpenFilesMessage =
        "На сетевой папке BIM_Models открыты файлы или папки. Сохраните и закройте их (Tekla, AutoCAD, окна " +
        "проводника с этой папкой) и повторите перевод.";

    /// <summary>
    /// Read-only: NetUseEnum level 1 reference counts (open files, directories) of the deviceless uses
    /// that the transfer would remove. A busy use cannot be removed with force=false.
    /// </summary>
    private bool HasOpenLegacyFiles() => _ports.EnumerateSmbUsage().Any(use => use.OpenReferences > 0 &&
        WindowsLegacyConnectorDetector.IsLegacyDevicelessConnection(new SmbConnectionEntry(use.LocalName, use.RemoteName)));

    private string? ResolveRestoreExecutable(LegacyTransferState? state) =>
        new[] { state?.LegacyExecutable, state?.AutoStart?.ToValue()?.Executable, _probes.LegacyDefaultExecutable }
            .FirstOrDefault(path => IsStartableLegacyExecutable(path) && _ports.FileExists(path!));

    private bool StartLegacyIfStopped(string executable)
    {
        if (!IsStartableLegacyExecutable(executable) || !_ports.FileExists(executable)) return false;
        var (matches, _) = WindowsLegacyConnectorDetector.FindLegacyProcesses(_probes, _probes.OwnProcessPath, executable);
        if (matches.Count == 0) _ports.StartProcess(executable);
        return true;
    }

    private string? ResolveExecutable(string? autoStartExecutable, IReadOnlyList<LegacyProcessImage> processes) =>
        new[] { autoStartExecutable, processes.FirstOrDefault()?.ImagePath, _probes.LegacyDefaultExecutable }
            .FirstOrDefault(IsStartableLegacyExecutable);

    /// <summary>Only the old client's image (never this connector's executable) is ever started.</summary>
    private bool IsStartableLegacyExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) &&
        string.Equals(Path.GetFileName(path), LegacyExecutableName, StringComparison.OrdinalIgnoreCase) &&
        !(_probes.OwnProcessPath is { } own && WindowsLegacyConnectorDetector.SamePath(path, own));

    /// <summary>A string Run value that does not start this connector. Anything else is not the old client's.</summary>
    private bool IsOldClientAutoStart(LegacyRunValue? value) =>
        value is { IsString: true } &&
        !(value.Executable is { } executable && _probes.OwnProcessPath is { } own &&
          WindowsLegacyConnectorDetector.SamePath(executable, own));

    /// <summary>
    /// Exactly the K4 old-client evidence: ALL deviceless uses of the legacy host, including its IPC$
    /// and implicit UNC uses of other programs (Tekla, AutoCAD, Explorer), which are indistinguishable
    /// from the old client's. All of them carry the old account's credentials in this logon session and
    /// would refuse this connector's mapping with Windows error 1219 anyway; idle ones are re-created on
    /// the next access. The share session goes first and IPC$ last. Drive-letter mappings, overlay
    /// hosts and this connector's own (always lettered) mappings are never touched.
    /// </summary>
    private IReadOnlyList<SmbConnectionEntry> LegacyDevicelessConnections() => _probes.EnumerateSmbConnections()
        .Where(WindowsLegacyConnectorDetector.IsLegacyDevicelessConnection)
        .OrderBy(connection => connection.RemoteName.EndsWith(@"\IPC$", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
        .ToArray();

    private void ForgetNotices()
    {
        lock (_noticeSync) _noticedProcesses.Clear();
    }

    private void SaveState(LegacyTransferState state) => _ports.WriteFileAtomic(_statePath, Serialize(state));

    private static string Serialize(LegacyTransferState state) => JsonSerializer.Serialize(state, JsonOptions);

    private LegacyTransferState? TryReadState(string path)
    {
        try
        {
            var text = _ports.ReadFile(path);
            if (string.IsNullOrWhiteSpace(text)) return null;
            var state = JsonSerializer.Deserialize<LegacyTransferState>(text, JsonOptions);
            return state is { SchemaVersion: 1 } &&
                state.Phase is LegacyTransferState.InProgress or LegacyTransferState.Completed &&
                (state.AutoStart is null || state.AutoStart.ToValue() is not null) &&
                (state.LegacyExecutable is null || IsStartableLegacyExecutable(state.LegacyExecutable))
                ? state
                : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or
                                              InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    private void DeleteIfExists(string path)
    {
        if (_ports.FileExists(path)) _ports.DeleteFile(path);
    }

    private static LegacyTransferResult Result(LegacyTransferOutcome outcome, string code, string message) =>
        new(outcome, code, message);

    private enum CloseOutcome
    {
        Graceful,
        Forced,
        Declined,
        Failed,
    }
}

/// <summary>Exact HKCU Run value: registry type and raw bytes, restored without re-encoding.</summary>
internal sealed class LegacyRunValue
{
    public const uint RegSz = 1;
    public const uint RegExpandSz = 2;
    public const int MaximumBytes = 32 * 1024;

    public LegacyRunValue(uint kind, byte[] data)
    {
        Kind = kind;
        Data = data ?? throw new ArgumentNullException(nameof(data));
    }

    public uint Kind { get; }
    public byte[] Data { get; }
    public bool IsString => Kind is RegSz or RegExpandSz;

    public string? Command
    {
        get
        {
            if (!IsString) return null;
            var text = Encoding.Unicode.GetString(Data);
            var end = text.IndexOf('\0');
            if (end >= 0) text = text[..end];
            return Kind == RegExpandSz ? Environment.ExpandEnvironmentVariables(text) : text;
        }
    }

    public string? Executable => Command is { } command ? WindowsLegacyConnectorDetector.ParseExecutable(command) : null;

    public bool SameAs(LegacyRunValue? other) =>
        other is not null && Kind == other.Kind && Data.AsSpan().SequenceEqual(other.Data);

    public static LegacyRunValue FromCommand(string command) => new(RegSz, Encoding.Unicode.GetBytes(command + "\0"));
}

internal sealed class LegacyRunValueState
{
    public string Name { get; set; } = WindowsLegacyConnectorDetector.AutoStartValueName;
    public uint Kind { get; set; }
    /// <summary>Base64 of the exact registry bytes; the only source used for the restore.</summary>
    public string Data { get; set; } = string.Empty;
    /// <summary>Readable copy of the command for support; never used for the restore.</summary>
    public string? Command { get; set; }

    public static LegacyRunValueState From(LegacyRunValue value) => new()
    {
        Kind = value.Kind,
        Data = Convert.ToBase64String(value.Data),
        Command = value.Command,
    };

    public LegacyRunValue? ToValue()
    {
        if (!string.Equals(Name, WindowsLegacyConnectorDetector.AutoStartValueName, StringComparison.Ordinal) ||
            Kind is not (LegacyRunValue.RegSz or LegacyRunValue.RegExpandSz))
            return null;
        try
        {
            var data = Convert.FromBase64String(Data);
            return data.Length is > 0 and <= LegacyRunValue.MaximumBytes && data.Length % 2 == 0
                ? new LegacyRunValue(Kind, data)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>Journal of one transfer; the marker holds the same JSON once the transfer completed.</summary>
internal sealed class LegacyTransferState
{
    public const string InProgress = "in-progress";
    public const string Completed = "completed";

    public int SchemaVersion { get; set; } = 1;
    public string Phase { get; set; } = InProgress;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public LegacyRunValueState? AutoStart { get; set; }
    public bool AutoStartRemoved { get; set; }
    public string? LegacyExecutable { get; set; }
    public bool ProcessWasRunning { get; set; }
    public int ClosedProcesses { get; set; }
    public bool ForcedClose { get; set; }
    public List<string> RemovedConnections { get; set; } = [];
    public bool CompensationFailed { get; set; }
}

/// <summary>An open handle to one identified old-client process; PID reuse cannot redirect it.</summary>
internal interface ILegacyProcess : IDisposable
{
    int ProcessId { get; }
}

/// <summary>One SMB use of this logon session with its open file/directory references (never the account).</summary>
internal readonly record struct LegacySmbUse(string? LocalName, string RemoteName, uint OpenReferences);

internal interface ILegacyTransferPorts
{
    LegacyRunValue? ReadAutoStartValue();
    void WriteAutoStartValue(LegacyRunValue value);
    void DeleteAutoStartValue();
    /// <summary>Opens the identified process and re-checks its image; null when it is already gone.</summary>
    ILegacyProcess? OpenProcess(LegacyProcessImage image);
    /// <summary>Asks the process to end as at a session end; true when at least one window was asked.</summary>
    bool RequestGracefulClose(ILegacyProcess process);
    /// <summary>True when the process exited within the timeout.</summary>
    Task<bool> WaitForExitAsync(ILegacyProcess process, TimeSpan timeout);
    void Kill(ILegacyProcess process);
    void StartProcess(string executable);
    IReadOnlyList<LegacySmbUse> EnumerateSmbUsage();
    /// <summary>WNetCancelConnection2 with force=false; returns the Win32 status.</summary>
    int CancelConnection(string remoteName);
    bool FileExists(string path);
    string? ReadFile(string path);
    void WriteFileAtomic(string path, string content);
    void DeleteFile(string path);
}

internal sealed class WindowsLegacyTransferPorts : ILegacyTransferPorts
{
    private const uint WmQueryEndSession = 0x0011;
    private const uint EndSessionCloseApp = 0x00000001;
    private const uint SendMessageAbortIfHung = 0x0002;
    private const long GracefulRequestBudgetMilliseconds = 3000;
    private const long PerWindowTimeoutMilliseconds = 1000;
    private const uint MaxPreferredLength = 0xFFFFFFFF;
    private const int NoError = 0;
    private const int ErrorFileNotFound = 2;
    private const int ErrorMoreData = 234;

    public LegacyRunValue? ReadAutoStartValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(WindowsLegacyConnectorDetector.AutoStartRunKey, writable: false);
        if (key is null) return null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            uint size = 0;
            var status = RegQueryValueEx(key.Handle, WindowsLegacyConnectorDetector.AutoStartValueName,
                IntPtr.Zero, out _, null, ref size);
            if (status == ErrorFileNotFound) return null;
            if (status != NoError && status != ErrorMoreData) throw new Win32Exception(status);
            if (size > LegacyRunValue.MaximumBytes) throw new InvalidDataException("The autostart value is too large.");
            var data = new byte[size];
            status = RegQueryValueEx(key.Handle, WindowsLegacyConnectorDetector.AutoStartValueName,
                IntPtr.Zero, out var kind, data, ref size);
            if (status == ErrorFileNotFound) return null;
            if (status == ErrorMoreData) continue; // Changed between the two reads.
            if (status != NoError) throw new Win32Exception(status);
            return new LegacyRunValue(kind, data.AsSpan(0, (int)size).ToArray());
        }
        throw new InvalidOperationException("The autostart value kept changing while it was read.");
    }

    public void WriteAutoStartValue(LegacyRunValue value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(WindowsLegacyConnectorDetector.AutoStartRunKey, writable: true) ??
            throw new InvalidOperationException("Cannot open the HKCU Run key.");
        var status = RegSetValueEx(key.Handle, WindowsLegacyConnectorDetector.AutoStartValueName, 0,
            value.Kind, value.Data, (uint)value.Data.Length);
        if (status != NoError) throw new Win32Exception(status);
    }

    public void DeleteAutoStartValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(WindowsLegacyConnectorDetector.AutoStartRunKey, writable: true);
        key?.DeleteValue(WindowsLegacyConnectorDetector.AutoStartValueName, throwOnMissingValue: false);
    }

    public ILegacyProcess? OpenProcess(LegacyProcessImage image)
    {
        Process process;
        try { process = Process.GetProcessById(image.ProcessId); }
        catch (ArgumentException) { return null; }
        try
        {
            // Opening the handle pins this process object; a later PID reuse gets another object.
            var handle = process.SafeHandle;
            if (HasExited(process)) return Dispose(process);
            var capacity = 32768;
            var path = new StringBuilder(capacity);
            if (!QueryFullProcessImageName(handle, 0, path, ref capacity))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            // The PID now belongs to another image: the identified old client has already exited.
            if (image.ImagePath is null || !WindowsLegacyConnectorDetector.SamePath(path.ToString(), image.ImagePath))
                return Dispose(process);
            return new WindowsLegacyProcess(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public bool RequestGracefulClose(ILegacyProcess process)
    {
        // Top-level windows (also hidden ones, such as the WPF application window that receives
        // session messages) of exactly this process, re-enumerated before every round because
        // windows appear and disappear during shutdown. Only WM_QUERYENDSESSION is sent: WPF acts on
        // it alone (Application.Shutdown). ENDSESSION_CLOSEAPP is the Restart Manager form: only
        // this application is asked. The whole request is bounded by a short total budget.
        var target = ((WindowsLegacyProcess)process).Process;
        var budget = Stopwatch.StartNew();
        var asked = new HashSet<IntPtr>();
        while (budget.ElapsedMilliseconds < GracefulRequestBudgetMilliseconds && !HasExited(target))
        {
            var pending = TopLevelWindows(process.ProcessId).Where(window => !asked.Contains(window)).ToArray();
            if (pending.Length == 0) break;
            foreach (var window in pending)
            {
                var remaining = GracefulRequestBudgetMilliseconds - budget.ElapsedMilliseconds;
                if (remaining <= 0 || HasExited(target)) break;
                asked.Add(window);
                SendMessageTimeout(window, WmQueryEndSession, IntPtr.Zero, (IntPtr)EndSessionCloseApp,
                    SendMessageAbortIfHung, (uint)Math.Min(PerWindowTimeoutMilliseconds, remaining), out _);
            }
        }
        return asked.Count > 0;
    }

    public async Task<bool> WaitForExitAsync(ILegacyProcess process, TimeSpan timeout)
    {
        var target = ((WindowsLegacyProcess)process).Process;
        if (HasExited(target)) return true;
        if (timeout <= TimeSpan.Zero) return false;
        try
        {
            using var deadline = new CancellationTokenSource(timeout);
            await target.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { return HasExited(target); }
    }

    public void Kill(ILegacyProcess process) => ((WindowsLegacyProcess)process).Process.Kill(entireProcessTree: false);

    public IReadOnlyList<LegacySmbUse> EnumerateSmbUsage()
    {
        // USE_INFO_1: local, remote, (password: never returned by enumeration), status, type, refcount,
        // usecount. ui1_refcount counts open files, directories and processes on the use. No account.
        var result = new List<LegacySmbUse>();
        uint resume = 0;
        int status;
        do
        {
            status = NetUseEnum(null, 1, out var buffer, MaxPreferredLength, out var read, out _, ref resume);
            try
            {
                if (status != NoError && status != ErrorMoreData) throw new Win32Exception(status);
                var size = Marshal.SizeOf<UseInfo1>();
                for (var index = 0; index < read; index++)
                {
                    var entry = Marshal.PtrToStructure<UseInfo1>(buffer + index * size);
                    if (!string.IsNullOrEmpty(entry.Remote))
                        result.Add(new LegacySmbUse(string.IsNullOrEmpty(entry.Local) ? null : entry.Local,
                            entry.Remote, entry.ReferenceCount));
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
            }
        }
        while (status == ErrorMoreData);
        return result;
    }

    private static List<IntPtr> TopLevelWindows(int processId)
    {
        var windows = new List<IntPtr>();
        EnumWindowsProc collect = (window, _) =>
        {
            if (GetWindowThreadProcessId(window, out var owner) != 0 && owner == (uint)processId) windows.Add(window);
            return true;
        };
        EnumWindows(collect, IntPtr.Zero);
        GC.KeepAlive(collect);
        return windows;
    }

    private static ILegacyProcess? Dispose(Process process)
    {
        process.Dispose();
        return null;
    }

    private sealed class WindowsLegacyProcess(Process process) : ILegacyProcess
    {
        public Process Process { get; } = process;
        public int ProcessId => Process.Id;
        public void Dispose() => Process.Dispose();
    }

    public void StartProcess(string executable)
    {
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        }) ?? throw new InvalidOperationException("The previous connector did not start.");
    }

    public int CancelConnection(string remoteName) => WNetCancelConnection2(remoteName, 0, force: false);

    public bool FileExists(string path) => File.Exists(path);

    public string? ReadFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        if (info.Length > LegacyConnectorTransferService.MaximumStateBytes)
            throw new InvalidDataException("The transfer state is too large.");
        return File.ReadAllText(path, Encoding.UTF8);
    }

    public void WriteFileAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void DeleteFile(string path) => File.Delete(path);

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UseInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Local;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Remote;
        public IntPtr Password;
        public uint Status;
        public uint AssignmentType;
        public uint ReferenceCount;
        public uint UseCount;
    }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUseEnum(string? serverName, uint level, out IntPtr buffer,
        uint preferredMaximumLength, out uint entriesRead, out uint totalEntries, ref uint resumeHandle);

    [DllImport("Netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMilliseconds, out IntPtr result);

    [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueEx(SafeRegistryHandle key, string valueName, IntPtr reserved,
        out uint kind, byte[]? data, ref uint size);

    [DllImport("advapi32.dll", EntryPoint = "RegSetValueExW", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueEx(SafeRegistryHandle key, string valueName, int reserved,
        uint kind, byte[] data, uint size);

    [DllImport("mpr.dll", EntryPoint = "WNetCancelConnection2W", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, uint flags, [MarshalAs(UnmanagedType.Bool)] bool force);
}
