using Connector.Updates;

namespace Connector.Updater.Tests;

internal sealed class FakeClock(DateTimeOffset start) : IUpdaterClock
{
    public DateTimeOffset UtcNow { get; private set; } = start;
    public void Set(DateTimeOffset value) => UtcNow = value;
    public void Advance(TimeSpan delay) => UtcNow += delay;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UtcNow += delay;
        return Task.CompletedTask;
    }
}

internal sealed class FakeRandom : IUpdaterRandom
{
    public double Value { get; set; }
    public double NextDouble() => Value;
}

internal sealed class FakeLog : IUpdaterLog
{
    public List<string> Lines { get; } = new();
    public void Info(string message) => Lines.Add("INFO " + message);
    public void Warning(string message) => Lines.Add("WARN " + message);
}

internal sealed class FakeChannel : IChannelSource
{
    public string ReadChannel() => "stable";
}

internal sealed class FakeProduct : IInstalledProduct
{
    public ReleaseVersion? Version { get; set; } = new ReleaseVersion(1, 1, 15);
    public bool Throws { get; set; }
    public ReleaseVersion? InstalledVersion() => Throws ? throw new InvalidOperationException("broken") : Version;
    public string ApplicationExecutable => @"C:\Program Files\Platform Connector\Connector.Desktop.exe";
}

internal sealed class FakeSource(FakeClock clock) : IUpdateSource
{
    public ReleaseDescription? Release { get; set; }
    public Exception? Error { get; set; }
    public int Fetches { get; private set; }
    public int Downloads => DownloadTimes.Count;
    public List<DateTimeOffset> DownloadTimes { get; } = new();
    public int BusyResponses { get; set; }
    public TimeSpan BusyRetryAfter { get; set; } = TimeSpan.FromSeconds(60);
    public int DownloadFailures { get; set; }
    public string DownloadFailureCode { get; set; } = UpdateErrorCodes.Network;

    public Task<ReleaseDescription> FetchVerifiedAsync(string channel, CancellationToken cancellationToken)
    {
        Fetches++;
        if (Error is not null) throw Error;
        return Task.FromResult(Release ?? throw new InvalidOperationException("No release configured."));
    }

    public Task DownloadAsync(ReleaseDescription release, IProgress<int> percent, CancellationToken cancellationToken)
    {
        DownloadTimes.Add(clock.UtcNow);
        if (BusyResponses > 0) { BusyResponses--; throw new DownloadBusyException(BusyRetryAfter); }
        if (DownloadFailures > 0) { DownloadFailures--; throw new UpdateRejectedException(DownloadFailureCode); }
        percent.Report(50);
        percent.Report(100);
        return Task.CompletedTask;
    }
}

internal sealed class FakeFiles : IInstallerFiles
{
    public bool LeaseFails { get; set; }
    public ReleaseInstaller? LeasedFor { get; private set; }
    public bool DeletedAll { get; private set; }

    public IInstallerLease? AcquireVerified(ReleaseVersion version, ReleaseInstaller installer)
    {
        LeasedFor = installer;
        return LeaseFails ? null : new Lease(@"C:\Program Files\Platform Connector\Updater\state\downloads\" + installer.FileName);
    }

    public void DeleteAll() => DeletedAll = true;
    public void DeleteAllExcept(ReleaseVersion keep) { }
    public string InstallerLogPath(ReleaseVersion version, int attempt) => @"C:\logs\install-" + version + "-" + attempt + ".log";

    private sealed class Lease(string path) : IInstallerLease
    {
        public string Path { get; } = path;
        public void Dispose() { }
    }
}

internal sealed class FakeProcess(int sessionId, string userSid, bool exitsOnRequest)
{
    public int SessionId { get; } = sessionId;
    public string UserSid { get; } = userSid;
    public bool Killed { get; set; }
    public StatusList? Status { get; set; }

    /// <summary>A cooperative application closes itself once the status says «installing».</summary>
    public bool HasExited => Killed || (exitsOnRequest && Status?.Items.Any(item => item.Phase == UpdatePhase.Installing) == true);
}

internal sealed class FakeProcesses(StatusList status) : IApplicationProcesses
{
    public List<FakeProcess> Running { get; } = new();

    public IReadOnlyList<IApplicationProcess> List()
    {
        foreach (var process in Running) process.Status = status;
        return Running.Where(process => !process.HasExited).Select(process => (IApplicationProcess)new Handle(process)).ToArray();
    }

    private sealed class Handle(FakeProcess process) : IApplicationProcess
    {
        public int SessionId => process.SessionId;
        public string UserSid => process.UserSid;
        public bool HasExited => process.HasExited;
        public void Kill() => process.Killed = true;
        public void Dispose() { }
    }
}

internal sealed class FakeSignals(FakeClock clock) : ISignalReader
{
    public Func<string, DateTimeOffset, DateTimeOffset?> Busy { get; set; } = (_, _) => null;
    public Func<string, DateTimeOffset, DateTimeOffset?> Check { get; set; } = (_, _) => null;
    public HashSet<string> ReadFor { get; } = new();

    public DateTimeOffset? ReadBusyUntil(string userSid) { ReadFor.Add(userSid); return Busy(userSid, clock.UtcNow); }
    public DateTimeOffset? ReadCheckRequest(string userSid) { ReadFor.Add(userSid); return Check(userSid, clock.UtcNow); }
}

internal sealed class TrustAll : IFileTrust
{
    public bool IsTrusted(FileStream stream) => true;
}

internal sealed class TrustNone : IFileTrust
{
    public bool IsTrusted(FileStream stream) => false;
}

internal sealed class FakeInstaller(FakeClock clock, FakeProduct product, FakeProcesses processes, MemoryStore store) : IInstallerRunner
{
    /// <summary>Wait slices the installer keeps running before it exits.</summary>
    public int RunningSlices { get; set; }
    internal FakeProduct Product => product;
    public bool Installs { get; set; }
    public int ExitCode { get; set; }
    public bool StopServiceWhileRunning { get; set; }
    public int Starts => StartTimes.Count;
    public int Failures { get; private set; }
    public List<DateTimeOffset> StartTimes { get; } = new();
    public PendingInstall? PendingAtStart { get; private set; }
    public bool StartedAfterApplicationsGone { get; private set; }

    internal FakeClock Clock => clock;

    public IInstallerProcess Start(string installerPath, string logPath)
    {
        Exited = false;
        StartTimes.Add(clock.UtcNow);
        PendingAtStart = UpdaterStateCodec.TryDeserialize(store.Saved!)!.Pending;
        StartedAfterApplicationsGone = processes.Running.All(process => process.HasExited);
        return new Process(this, PendingAtStart!.Version, clock.UtcNow);
    }

    public IInstallerProcess? TryAttach(int processId, DateTimeOffset startedAtUtc) =>
        processId == 4242 && PendingAtStart is { } pending && !Exited ? new Process(this, pending.Version, startedAtUtc) : null;

    public bool Exited { get; private set; }

    private sealed class Process(FakeInstaller owner, ReleaseVersion target, DateTimeOffset started) : IInstallerProcess
    {
        public int ProcessId => 4242;
        public DateTimeOffset StartedAtUtc => started;
        public bool HasExited => owner.Exited;

        public Task<int?> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (owner.StopServiceWhileRunning) throw new OperationCanceledException("service stopped by the installer");
            if (owner.RunningSlices > 0)
            {
                owner.RunningSlices--;
                owner.Clock.Advance(timeout);
                return Task.FromResult<int?>(null);
            }
            owner.Exited = true;
            if (owner.Installs) { owner.Product.Version = target; return Task.FromResult<int?>(owner.ExitCode); }
            owner.Failures++;
            return Task.FromResult<int?>(owner.ExitCode);
        }

        public void Dispose() { }
    }
}

internal sealed class FakeLauncher : ISessionLauncher
{
    public List<(int, string)> Launched { get; } = new();

    public bool TryLaunch(int sessionId, string userSid, string applicationExecutable)
    {
        Launched.Add((sessionId, userSid));
        return true;
    }
}

internal sealed class MemoryStore : IStateStore
{
    public byte[]? Saved { get; private set; }
    public UpdaterState Load() => Saved is null ? new UpdaterState() : UpdaterStateCodec.TryDeserialize(Saved) ?? new UpdaterState();
    public void Save(UpdaterState state) => Saved = UpdaterStateCodec.Serialize(state);
}

internal sealed class StatusList : IStatusSink
{
    public List<UpdateStatus> Items { get; } = new();
    public int Touches { get; private set; }
    public void Publish(UpdateStatus status) => Items.Add(status);
    public void Touch() => Touches++;
    public void SetActiveInstaller(Func<bool>? isAlive) { }
}
