using System.Net.Http;
using Connector.Updates;

namespace Connector.Updater;

/// <summary>
/// The decision loop of the update service. One instance, one thread of control: <see cref="StepAsync"/>
/// is called repeatedly by <see cref="RunAsync"/> and returns when the next step is due.
/// The only inputs it acts on are the signed release description (through <see cref="IUpdateSource"/>)
/// and the installed product; the application's flag files can only postpone an installation, by at most
/// <see cref="UpdateSchedule.MaximumBusyDeferral"/>, or bring a check forward, at most once a minute.
/// An installer is started only for a release whose signed description was fetched and verified in the
/// same step, and only after the downloaded file was re-hashed against that description — never against
/// anything read back from disk.
/// </summary>
internal sealed class UpdaterEngine
{
    private readonly IUpdaterClock _clock;
    private readonly IUpdaterRandom _random;
    private readonly IUpdaterLog _log;
    private readonly IChannelSource _channel;
    private readonly IInstalledProduct _product;
    private readonly IUpdateSource _source;
    private readonly IInstallerFiles _files;
    private readonly IApplicationProcesses _processes;
    private readonly ISignalReader _signals;
    private readonly IInstallerRunner _installer;
    private readonly ISessionLauncher _launcher;
    private readonly IStateStore _store;
    private readonly IStatusSink _status;
    private readonly Dictionary<string, DateTimeOffset> _handledCheckRequests = new(StringComparer.Ordinal);
    private UpdaterState _state = new();
    private bool _initialized;
    private bool _downloading;
    private int? _downloadPercent;
    private DateTimeOffset? _scheduledInstallAtUtc;
    private DateTimeOffset _lastPercentPublishedAtUtc;
    private long _step;
    private ReleaseDescription? _verified;
    private long _verifiedStep = -1;
    private DateTimeOffset? _failingSinceUtc;
    private bool _stalled;

    public UpdaterEngine(IUpdaterClock clock, IUpdaterRandom random, IUpdaterLog log, IChannelSource channel,
        IInstalledProduct product, IUpdateSource source, IInstallerFiles files, IApplicationProcesses processes,
        ISignalReader signals, IInstallerRunner installer, ISessionLauncher launcher, IStateStore store, IStatusSink status)
    {
        _clock = clock; _random = random; _log = log; _channel = channel; _product = product; _source = source;
        _files = files; _processes = processes; _signals = signals; _installer = installer; _launcher = launcher;
        _store = store; _status = status;
    }

    internal UpdaterState State => _state;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            DateTimeOffset next;
            try { next = await StepGuardedAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            var delay = next - _clock.UtcNow;
            if (delay > UpdateSchedule.IdlePoll) delay = UpdateSchedule.IdlePoll;
            if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            try { await _clock.DelayAsync(delay, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// One step that never throws (except for cancellation). A completed step confirms the engine to the heartbeat;
    /// a step that keeps failing for <see cref="UpdateSchedule.EngineStallLimit"/> while an installation is recorded
    /// withdraws «installing» (status «failed», <c>engine-stalled</c>) so no application closes itself for it.
    /// </summary>
    internal async Task<DateTimeOffset> StepGuardedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var next = await StepAsync(cancellationToken).ConfigureAwait(false);
            _failingSinceUtc = null;
            _stalled = false;
            _status.Touch();
            return next;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Never let one unexpected failure stop the service; the type name only, no message text.
            _log.Warning("Unexpected step failure: " + ex.GetType().Name);
            var now = _clock.UtcNow;
            _failingSinceUtc ??= now;
            if (!_stalled && now - _failingSinceUtc.Value >= UpdateSchedule.EngineStallLimit)
            {
                _stalled = true;
                _status.SetActiveInstaller(null);
                _log.Warning("Engine stalled; «installing» withdrawn from the status.");
            }
            if (_stalled) Publish();
            return now + TimeSpan.FromMinutes(1);
        }
    }

    /// <summary>One pass: resolve an installation in progress, check when due, install when ready. Returns the next due time.</summary>
    public async Task<DateTimeOffset> StepAsync(CancellationToken cancellationToken)
    {
        _step++;
        if (!_initialized) Initialize();
        if (_state.Pending is not null)
        {
            await ResolvePendingAsync(cancellationToken).ConfigureAwait(false);
            if (_state.Pending is not null)
            {
                // The installer of a previous attempt is still running: no check, no second installer,
                // no application restart until it ends.
                Publish();
                return _clock.UtcNow + TimeSpan.FromMinutes(1);
            }
        }

        var now = _clock.UtcNow;
        var users = RunningUsers();
        if (TakeCheckRequest(users, now)) _state.NextCheckAtUtc = now;
        if (now >= _state.NextCheckAtUtc) await CheckAsync(cancellationToken).ConfigureAwait(false);

        if (_state.Target is { ReadyAtUtc: not null } && _state.Pending is null)
            await TryInstallAsync(cancellationToken).ConfigureAwait(false);

        Publish();
        var due = _state.NextCheckAtUtc ?? _clock.UtcNow + UpdateSchedule.IdlePoll;
        if (_state.Target?.NextAttemptAtUtc is { } attempt && attempt < due) due = attempt;
        return due;
    }

    private void Initialize()
    {
        _state = _store.Load();
        _initialized = true;
        var now = _clock.UtcNow;
        // Each service start (and so each Windows start) checks after a random 2–10 minutes,
        // unless a pending check is already due sooner. Times far in the future (clock jumps) are pulled back.
        var first = now + UpdateSchedule.FirstCheckDelay(_random.NextDouble());
        if (_state.NextCheckAtUtc is null || _state.NextCheckAtUtc > first) _state.NextCheckAtUtc = first;
        if (_state.Target?.NextAttemptAtUtc is { } attempt && attempt > now + UpdateSchedule.InstallRetryDelays[^1])
            _state.Target.NextAttemptAtUtc = now + UpdateSchedule.InstallRetryDelays[^1];
        _log.Info("Service state loaded; installed " + (_product.InstalledVersion()?.ToString() ?? "unknown") + ".");
        // A recorded installation is resolved first; «installing» is published only while its installer really runs.
        if (_state.Pending is null) Publish();
    }

    // ---- check and download ------------------------------------------------------------------------------

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        _state.LastCheckAtUtc = now;
        _verified = null;
        try
        {
            var installed = _product.InstalledVersion() ?? throw new UpdateRejectedException(UpdateErrorCodes.InstalledVersionUnknown);
            var channel = _channel.ReadChannel();
            var release = await _source.FetchVerifiedAsync(channel, cancellationToken).ConfigureAwait(false);
            _state.ConsecutiveCheckFailures = 0;
            _state.CheckErrorCode = null;
            _state.NextCheckAtUtc = _clock.UtcNow + UpdateSchedule.RegularDelay(_random.NextDouble());
            _state.ProblemVersions.RemoveAll(version => version <= installed);
            _verified = release;
            _verifiedStep = _step;

            switch (ReleasePolicy.Decide(release, installed, _state.ProblemVersions))
            {
                case ReleaseDecision.UpToDate:
                case ReleaseDecision.Paused:
                    if (_state.Target is not null) _log.Info("Release " + release.Version + " needs no installation (" + (release.Paused ? "paused" : "not newer") + ").");
                    DropTarget();
                    _state.FailureCode = null;
                    break;
                case ReleaseDecision.BelowMinimumVersion:
                    DropTarget();
                    _state.FailureCode = UpdateErrorCodes.BelowMinimumVersion;
                    _log.Warning("Release " + release.Version + " requires at least " + release.MinimumVersion + ".");
                    break;
                case ReleaseDecision.MarkedProblem:
                    DropTarget();
                    _state.FailureCode = UpdateErrorCodes.VersionMarkedProblem;
                    break;
                case ReleaseDecision.Install:
                    AdoptTarget(release);
                    if (HashMismatchLimitReached(release))
                    {
                        // The signed hash keeps disagreeing with the served file: stop pulling ~400 MB again and
                        // again until the description changes.
                        _state.FailureCode = UpdateErrorCodes.DownloadHashMismatch;
                        break;
                    }
                    if (_state.Target!.ReadyAtUtc is null) await DownloadAsync(release, cancellationToken).ConfigureAwait(false);
                    break;
            }
            _store.Save(_state);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _downloading = false;
            _store.Save(_state);
            throw;
        }
        catch (DownloadBusyException busy)
        {
            // The catalog's download limit: come back after Retry-After plus up to a minute of jitter and resume
            // from the same offset. Neither an installation attempt nor a failure of the release.
            _state.NextCheckAtUtc = _clock.UtcNow + busy.RetryAfter + UpdateSchedule.BusyJitter(_random.NextDouble());
            _state.CheckErrorCode = UpdateErrorCodes.DownloadBusy;
            _downloading = false;
            _downloadPercent = null;
            _log.Info("Catalog busy; download resumes after " + (int)busy.RetryAfter.TotalSeconds + " s.");
            _store.Save(_state);
        }
        catch (Exception ex)
        {
            var code = CodeOf(ex);
            if (_downloading)
            {
                // 5xx, broken connection, wrong bytes, no disk space: retried with a growing pause;
                // installation attempts are untouched.
                if (code == UpdateErrorCodes.DownloadHashMismatch && _state.Target is { } target) RecordHashMismatch(target);
                _state.ConsecutiveDownloadFailures++;
                _state.NextCheckAtUtc = _clock.UtcNow + UpdateSchedule.DownloadRetryDelay(_state.ConsecutiveDownloadFailures);
                _state.FailureCode = code;
            }
            else
            {
                _state.ConsecutiveCheckFailures++;
                _state.NextCheckAtUtc = _clock.UtcNow + UpdateSchedule.CheckRetryDelay(_state.ConsecutiveCheckFailures, _random.NextDouble());
                _state.CheckErrorCode = code;
            }
            _downloading = false;
            _downloadPercent = null;
            _log.Warning("Check failed: " + code + ".");
            _store.Save(_state);
        }
    }

    private void AdoptTarget(ReleaseDescription release)
    {
        var current = _state.Target;
        if (current is not null && SameRelease(current, release)) return;
        if (current is not null) _log.Info("Target " + current.Version + " replaced by " + release.Version + ".");
        _state.Target = new UpdateTarget
        {
            Version = release.Version,
            Channel = release.Channel,
            Installer = release.Installer,
            Notes = release.Notes.ToArray()
        };
        _state.FailureCode = null;
        _scheduledInstallAtUtc = null;
        _files.DeleteAllExcept(release.Version);
        _log.Info("Release " + release.Version + " accepted for installation.");
    }

    private static bool SameRelease(UpdateTarget target, ReleaseDescription release) =>
        target.Version == release.Version && target.Channel == release.Channel && target.Installer == release.Installer;

    private void DropTarget()
    {
        if (_state.Target is null) return;
        _state.Target = null;
        _scheduledInstallAtUtc = null;
        _files.DeleteAll();
    }

    private bool HashMismatchLimitReached(ReleaseDescription release) =>
        _state.HashMismatch is { } mismatch && mismatch.Version == release.Version &&
        mismatch.Sha256 == release.Installer.Sha256 && mismatch.Count >= UpdateSchedule.MaximumHashMismatches;

    private void RecordHashMismatch(UpdateTarget target)
    {
        var current = _state.HashMismatch;
        _state.HashMismatch = current is not null && current.Version == target.Version && current.Sha256 == target.Installer.Sha256
            ? current with { Count = current.Count + 1 }
            : new HashMismatchRecord(target.Version, target.Installer.Sha256, 1);
        _log.Warning("Installer " + target.Version + " does not match its signed hash (" + _state.HashMismatch.Count + ").");
    }

    private async Task DownloadAsync(ReleaseDescription release, CancellationToken cancellationToken)
    {
        _downloading = true;
        _downloadPercent = 0;
        Publish();
        var progress = new SynchronousProgress(percent =>
        {
            _downloadPercent = percent;
            var now = _clock.UtcNow;
            if (now - _lastPercentPublishedAtUtc >= TimeSpan.FromSeconds(2))
            {
                _lastPercentPublishedAtUtc = now;
                Publish();
            }
        });
        await _source.DownloadAsync(release, progress, cancellationToken).ConfigureAwait(false);
        _downloading = false;
        _downloadPercent = null;
        _state.Target!.ReadyAtUtc = _clock.UtcNow;
        _state.FailureCode = null;
        _state.ConsecutiveDownloadFailures = 0;
        _state.HashMismatch = null;
        _log.Info("Installer " + release.Version + " downloaded and verified.");
    }

    // ---- installation ------------------------------------------------------------------------------------

    private async Task TryInstallAsync(CancellationToken cancellationToken)
    {
        var target = _state.Target!;
        var now = _clock.UtcNow;
        if (target.NextAttemptAtUtc is { } next && now < next)
        {
            _scheduledInstallAtUtc = next;
            return;
        }

        var deadline = target.ReadyAtUtc!.Value + UpdateSchedule.MaximumBusyDeferral;
        var busyUntil = BusyUntil(RunningUsers(), now);
        if (busyUntil is { } busy && now < deadline)
        {
            _scheduledInstallAtUtc = busy < deadline ? busy : deadline;
            return;
        }

        // Re-read and re-verify the signed description right before installing: a pause, a channel change by the
        // administrator or a replaced release stops an already downloaded update, and the file is checked only
        // against what was signed and fetched now.
        if (_verifiedStep != _step) await CheckAsync(cancellationToken).ConfigureAwait(false);
        if (_state.Target is not { ReadyAtUtc: not null } current) return;
        if (_verifiedStep != _step || _verified is not { } release || !SameRelease(current, release))
        {
            // Not verifiable now (network, catalog): postpone without counting an attempt.
            current.NextAttemptAtUtc = _state.NextCheckAtUtc;
            _scheduledInstallAtUtc = current.NextAttemptAtUtc;
            _store.Save(_state);
            return;
        }

        _scheduledInstallAtUtc = _clock.UtcNow;
        await InstallAsync(current, release, cancellationToken).ConfigureAwait(false);
    }

    private async Task InstallAsync(UpdateTarget target, ReleaseDescription release, CancellationToken cancellationToken)
    {
        var lease = _files.AcquireVerified(release.Version, release.Installer);
        if (lease is null)
        {
            // Missing, altered or not trustworthy on disk: it was deleted; download it again after the download pause.
            target.ReadyAtUtc = null;
            RecordHashMismatch(target);
            _state.FailureCode = UpdateErrorCodes.DownloadHashMismatch;
            _state.ConsecutiveDownloadFailures++;
            _state.NextCheckAtUtc = _clock.UtcNow + UpdateSchedule.DownloadRetryDelay(_state.ConsecutiveDownloadFailures);
            _store.Save(_state);
            return;
        }

        using (lease)
        {
            var running = _processes.List();
            try
            {
                var pending = new PendingInstall
                {
                    Version = target.Version,
                    PreviousVersion = _product.InstalledVersion(),
                    Attempt = target.Failures + 1,
                    StartedAtUtc = _clock.UtcNow,
                    Sessions = running.Select(process => new SessionRecord(process.SessionId, process.UserSid)).Distinct().ToArray()
                };
                // The record exists before anything irreversible happens: whichever service instance runs next
                // (this one, the new version, or the old one after a rollback) resolves it.
                _state.Pending = pending;
                _store.Save(_state);
                Publish();
                _log.Info("Installing " + target.Version + " (attempt " + pending.Attempt + ", " + pending.Sessions.Count + " application session(s)).");

                await WaitForApplicationsAsync(running, cancellationToken).ConfigureAwait(false);
                foreach (var process in running)
                {
                    if (process.HasExited) continue;
                    try { process.Kill(); }
                    catch (Exception ex) { _log.Warning("Application process could not be stopped: " + ex.GetType().Name); }
                }

                IInstallerProcess installer;
                try { installer = _installer.Start(lease.Path, _files.InstallerLogPath(target.Version, pending.Attempt)); }
                catch (Exception ex)
                {
                    _log.Warning("Installer did not start: " + ex.GetType().Name);
                    await ResolvePendingAsync(cancellationToken, UpdateErrorCodes.InstallerStartFailed).ConfigureAwait(false);
                    return;
                }
                using (installer)
                {
                    pending.InstallerProcessId = installer.ProcessId;
                    pending.InstallerStartedAtUtc = installer.StartedAtUtc;
                    _store.Save(_state);
                    // When the installer stops this service, cancellation ends the wait; the installer keeps running
                    // and the next service instance resolves the pending record.
                    await ResolvePendingAsync(cancellationToken, live: installer).ConfigureAwait(false);
                }
            }
            finally
            {
                foreach (var process in running) process.Dispose();
            }
        }
    }

    private async Task WaitForApplicationsAsync(IReadOnlyList<IApplicationProcess> running, CancellationToken cancellationToken)
    {
        var until = _clock.UtcNow + UpdateSchedule.ApplicationCloseGrace;
        while (running.Any(process => !process.HasExited) && _clock.UtcNow < until)
            await _clock.DelayAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Settles a recorded installation. While its installer is alive the record stays (no second installer, no
    /// application restart); past <see cref="UpdateSchedule.InstallerAbsoluteLimit"/> the attempt is counted as
    /// failed once, but the record still waits for that installer to end. Success only when Windows Installer
    /// reports exactly the target version.
    /// </summary>
    private async Task ResolvePendingAsync(CancellationToken cancellationToken, string? startFailure = null, IInstallerProcess? live = null)
    {
        var pending = _state.Pending!;
        if (startFailure is null && pending.ExitCode is null && pending.InstallerProcessId is { } pid && pending.InstallerStartedAtUtc is { } started)
        {
            var attached = live ?? _installer.TryAttach(pid, started);
            try
            {
                if (attached is not null)
                {
                    var elapsed = _clock.UtcNow - pending.StartedAtUtc;
                    var slice = UpdateSchedule.InstallerWaitSlice;
                    var untilLimit = UpdateSchedule.InstallerAbsoluteLimit - elapsed;
                    if (untilLimit > TimeSpan.Zero && untilLimit < slice) slice = untilLimit;
                    // While this wait runs, the heartbeat of «installing» follows exactly this installer process.
                    _status.SetActiveInstaller(() => !attached.HasExited);
                    try { pending.ExitCode = await attached.WaitForExitAsync(slice, cancellationToken).ConfigureAwait(false); }
                    finally { _status.SetActiveInstaller(null); }
                    _status.Touch();
                    if (pending.ExitCode is null)
                    {
                        if (!pending.FailureCounted && _clock.UtcNow - pending.StartedAtUtc >= UpdateSchedule.InstallerAbsoluteLimit)
                        {
                            pending.FailureCounted = true;
                            CountFailure(pending.Version, UpdateErrorCodes.InstallerTimeout);
                            _log.Warning("Installer of " + pending.Version + " exceeded " + UpdateSchedule.InstallerAbsoluteLimit.TotalHours + " h; still waiting for it to end.");
                        }
                        _store.Save(_state);
                        return;
                    }
                }
            }
            finally
            {
                if (live is null) attached?.Dispose();
            }
        }

        var installed = _product.InstalledVersion();
        if (installed == pending.Version)
        {
            var target = _state.Target;
            _state.LastUpdate = new CompletedUpdate(pending.Version.ToString(), _clock.UtcNow,
                target is not null && target.Version == pending.Version ? target.Notes : Array.Empty<string>());
            _state.Target = null;
            _state.FailureCode = null;
            _state.ProblemVersions.RemoveAll(version => version <= pending.Version);
            _files.DeleteAll();
            // 3010/1641: installed, a restart completes it; the service never restarts Windows itself.
            var reboot = pending.ExitCode is 3010 or 1641 ? " (restart of Windows pending)" : "";
            _log.Info("Installed " + pending.Version + " (exit " + (pending.ExitCode?.ToString() ?? "unknown") + ")" + reboot + ".");
        }
        else
        {
            // Not attempts against the release: the previous instance stopped before the installer started, or
            // another Windows Installer transaction was running (1618).
            var interrupted = startFailure is null && (pending.InstallerProcessId is null || pending.ExitCode == 1618);
            if (interrupted)
            {
                if (_state.Target is { } target && target.Version == pending.Version)
                    target.NextAttemptAtUtc = _clock.UtcNow + UpdateSchedule.InstallRetryDelays[0];
                _log.Info("Installation of " + pending.Version + " interrupted (exit " + (pending.ExitCode?.ToString() ?? "none") + "); retried later.");
            }
            else if (!pending.FailureCounted)
            {
                var code = startFailure
                           ?? (pending.ExitCode is { } exit && exit is not (0 or 3010 or 1641) ? UpdateErrorCodes.InstallerFailed
                               : UpdateErrorCodes.InstalledVersionMismatch);
                CountFailure(pending.Version, code);
            }
            _log.Warning("Installation of " + pending.Version + " not completed (installed " + (installed?.ToString() ?? "unknown") +
                         ", exit " + (pending.ExitCode?.ToString() ?? "unknown") + ").");
        }

        _state.Pending = null;
        _store.Save(_state);
        Publish();

        // The application was closed for the installation and the installer has ended: start it again where it ran.
        foreach (var session in pending.Sessions)
        {
            try
            {
                if (!_launcher.TryLaunch(session.SessionId, session.UserSid, _product.ApplicationExecutable))
                    _log.Info("Application not restarted in session " + session.SessionId + " (user signed out or changed).");
            }
            catch (Exception ex) { _log.Warning("Application restart failed in session " + session.SessionId + ": " + ex.GetType().Name); }
        }
    }

    private void CountFailure(ReleaseVersion version, string code)
    {
        var target = _state.Target;
        if (target is not null && target.Version == version)
        {
            target.Failures++;
            if (target.Failures > UpdateSchedule.InstallRetryDelays.Length)
            {
                _state.ProblemVersions.Add(target.Version);
                _state.Target = null;
                _files.DeleteAll();
                code = UpdateErrorCodes.VersionMarkedProblem;
                _log.Warning("Version " + version + " marked problematic after " + target.Failures + " failed installations.");
            }
            else target.NextAttemptAtUtc = _clock.UtcNow + UpdateSchedule.InstallRetryDelays[target.Failures - 1];
        }
        _state.FailureCode = code;
    }

    // ---- application signals -----------------------------------------------------------------------------

    private IReadOnlyList<string> RunningUsers()
    {
        var processes = _processes.List();
        try { return processes.Select(process => process.UserSid).Distinct(StringComparer.Ordinal).ToArray(); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    /// <summary>Latest valid «занят до» of the running users' applications, capped to the deferral limit; null when none is in the future.</summary>
    private DateTimeOffset? BusyUntil(IReadOnlyList<string> users, DateTimeOffset now)
    {
        DateTimeOffset? latest = null;
        foreach (var user in users)
        {
            if (_signals.ReadBusyUntil(user) is not { } until || until <= now) continue;
            var capped = until > now + UpdateSchedule.MaximumBusyDeferral ? now + UpdateSchedule.MaximumBusyDeferral : until;
            if (latest is null || capped > latest) latest = capped;
        }
        return latest;
    }

    /// <summary>True for a fresh, not yet honoured «проверь сейчас», at most once per minute.</summary>
    private bool TakeCheckRequest(IReadOnlyList<string> users, DateTimeOffset now)
    {
        if (_state.LastCheckAtUtc is { } last && now - last < UpdateSchedule.CheckRequestMinimumSpacing) return false;
        foreach (var user in users)
        {
            if (_signals.ReadCheckRequest(user) is not { } requested) continue;
            if (requested < now - UpdateSchedule.CheckRequestMaximumAge || requested > now + UpdateSchedule.ClockSkewAllowance) continue;
            if (_handledCheckRequests.TryGetValue(user, out var handled) && handled == requested) continue;
            if (_handledCheckRequests.Count > 256) _handledCheckRequests.Clear();
            _handledCheckRequests[user] = requested;
            _log.Info("Check requested by the application.");
            return true;
        }
        return false;
    }

    // ---- status ------------------------------------------------------------------------------------------

    private void Publish()
    {
        var target = _state.Target;
        var pending = _state.Pending;
        UpdatePhase phase;
        if (_stalled) phase = UpdatePhase.Failed;
        else if (pending is not null && !pending.FailureCounted) phase = UpdatePhase.Installing;
        else if (_downloading) phase = UpdatePhase.Downloading;
        else if (_state.FailureCode is not null) phase = UpdatePhase.Failed;
        else if (target?.ReadyAtUtc is not null) phase = UpdatePhase.Ready;
        else phase = UpdatePhase.Idle;

        string? installed;
        try { installed = _product.InstalledVersion()?.ToString(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { installed = null; }
        var status = new UpdateStatus(
            installed,
            target?.Version.ToString() ?? pending?.Version.ToString(),
            phase,
            phase == UpdatePhase.Downloading ? _downloadPercent : null,
            target?.ReadyAtUtc is not null ? _scheduledInstallAtUtc ?? target.NextAttemptAtUtc : null,
            _stalled ? UpdateErrorCodes.EngineStalled : _state.FailureCode ?? _state.CheckErrorCode,
            _state.LastCheckAtUtc,
            target?.NextAttemptAtUtc ?? _state.NextCheckAtUtc,
            target?.Notes ?? Array.Empty<string>(),
            _state.LastUpdate,
            _clock.UtcNow);
        try { _status.Publish(status); }
        catch (Exception ex) { _log.Warning("Status not written: " + ex.GetType().Name); }
    }

    private static string CodeOf(Exception exception) => exception switch
    {
        UpdateRejectedException rejected => rejected.Code,
        HttpRequestException => UpdateErrorCodes.Network,
        TaskCanceledException => UpdateErrorCodes.Network, // HTTP timeout, not our cancellation
        IOException or UnauthorizedAccessException => UpdateErrorCodes.Storage,
        _ => UpdateErrorCodes.Internal
    };

    /// <summary>Progress that reports on the calling thread (no captured context, no reordering).</summary>
    private sealed class SynchronousProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
