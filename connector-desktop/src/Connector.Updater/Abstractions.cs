using Connector.Updates;

namespace Connector.Updater;

// Every effect of the service on the machine goes through one of these seams, so the decision logic
// (UpdaterEngine) is tested without time, network, processes, sessions or services.

internal interface IUpdaterClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>Uniform value in [0, 1) for the schedule jitter.</summary>
internal interface IUpdaterRandom
{
    double NextDouble();
}

internal interface IUpdaterLog
{
    void Info(string message);
    void Warning(string message);
}

/// <summary>The update channel chosen by the administrator (HKLM); throws <see cref="UpdateRejectedException"/> when invalid.</summary>
internal interface IChannelSource
{
    string ReadChannel();
}

/// <summary>The installed product, as Windows Installer registers it.</summary>
internal interface IInstalledProduct
{
    /// <summary>Version of the one installed MSI of the product, or null when it cannot be determined exactly.</summary>
    ReleaseVersion? InstalledVersion();

    /// <summary>The installed application executable (InstallRoot from HKLM, cross-checked with the service location).</summary>
    string ApplicationExecutable { get; }
}

/// <summary>Fetches and verifies the release description of a channel and downloads its installer.</summary>
internal interface IUpdateSource
{
    Task<ReleaseDescription> FetchVerifiedAsync(string channel, CancellationToken cancellationToken);

    /// <summary>
    /// Brings the installer of <paramref name="release"/> into the protected download directory (resuming a
    /// partial file), verifying size and SHA-256. Returns only when the complete verified file is in place.
    /// </summary>
    Task DownloadAsync(ReleaseDescription release, IProgress<int> percent, CancellationToken cancellationToken);
}

/// <summary>An open, re-verified installer file. While the lease is held the file cannot be written or deleted.</summary>
internal interface IInstallerLease : IDisposable
{
    string Path { get; }
}

/// <summary>
/// Whether an open file is one only SYSTEM or Administrators could have written: owner SYSTEM, Administrators or
/// TrustedInstaller, no access control entry granting anyone else write, delete or ownership rights, exactly one
/// link and no reparse point. Anything else in the service's folders is never believed.
/// </summary>
internal interface IFileTrust
{
    bool IsTrusted(FileStream stream);
}

internal interface IInstallerFiles
{
    /// <summary>Opens the downloaded installer deny-write, re-hashes it through that handle and returns the lease; null when absent or wrong (the wrong file is deleted).</summary>
    IInstallerLease? AcquireVerified(ReleaseVersion version, ReleaseInstaller installer);

    /// <summary>Removes every downloaded or partial installer (after success, a paused release or a replaced target).</summary>
    void DeleteAll();

    /// <summary>Removes installers and partial files of every version except <paramref name="keep"/>.</summary>
    void DeleteAllExcept(ReleaseVersion keep);

    string InstallerLogPath(ReleaseVersion version, int attempt);
}

/// <summary>A running instance of the installed application (opened by handle, so its PID cannot be reused while held).</summary>
internal interface IApplicationProcess : IDisposable
{
    int SessionId { get; }
    string UserSid { get; }
    bool HasExited { get; }
    void Kill();
}

internal interface IApplicationProcesses
{
    /// <summary>Running processes whose image is exactly the installed application executable.</summary>
    IReadOnlyList<IApplicationProcess> List();
}

/// <summary>The application's two flag files, read with the restrictions of <see cref="UpdateSignalFiles"/>.</summary>
internal interface ISignalReader
{
    DateTimeOffset? ReadBusyUntil(string userSid);
    DateTimeOffset? ReadCheckRequest(string userSid);
}

internal interface IInstallerProcess : IDisposable
{
    int ProcessId { get; }
    DateTimeOffset StartedAtUtc { get; }
    bool HasExited { get; }

    /// <summary>Exit code, or null when it did not exit within <paramref name="timeout"/>.</summary>
    Task<int?> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

internal interface IInstallerRunner
{
    /// <summary>Starts the verified installer quietly as a detached process that outlives this service.</summary>
    IInstallerProcess Start(string installerPath, string logPath);

    /// <summary>Re-attaches to an installer started by a previous instance of the service; null when it is gone.</summary>
    IInstallerProcess? TryAttach(int processId, DateTimeOffset startedAtUtc);
}

internal interface ISessionLauncher
{
    /// <summary>Starts the installed application in the session with the rights of its logged-on user, only if that user is still <paramref name="userSid"/>.</summary>
    bool TryLaunch(int sessionId, string userSid, string applicationExecutable);
}

/// <summary>
/// Status output with a heartbeat that confirms only progress of the engine: <see cref="Touch"/> after each completed
/// step, and — while installing — only while the installer started or re-attached by this service is alive.
/// </summary>
internal interface IStatusSink
{
    void Publish(UpdateStatus status);

    /// <summary>The engine completed a step (or a wait slice) just now.</summary>
    void Touch();

    /// <summary>The installer being awaited (null when none); the heartbeat of «installing» follows its liveness.</summary>
    void SetActiveInstaller(Func<bool>? isAlive);
}

internal interface IStateStore
{
    UpdaterState Load();
    void Save(UpdaterState state);
}
