using System.ServiceProcess;
using Connector.Updates;

namespace Connector.Updater;

/// <summary>
/// Entry point. Started by the Service Control Manager it runs the update service. The only other mode is
/// <c>--check-once &lt;folder&gt;</c>: one read-only check of the production catalog by whoever runs it,
/// writing status and journal into that folder. It never downloads, stops, installs or launches anything,
/// and it uses the same pinned key, HTTPS origin and verification as the service.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            ServiceBase.Run(new UpdaterWindowsService());
            return 0;
        }
        if (args.Length is 2 or 4 && args[0] == "--check-once")
            return CheckOnce(args[1], args.Length == 4 && args[2] == "--installed" ? args[3] : null);
        Console.Error.WriteLine("Usage: PlatformConnectorUpdater --check-once <empty folder> [--installed <x.y.z>]");
        return 2;
    }

    private static int CheckOnce(string folder, string? installed)
    {
        if (!Path.IsPathFullyQualified(folder)) { Console.Error.WriteLine("The folder must be a full path."); return 2; }
        ReleaseVersion? assumed = null;
        if (installed is not null)
        {
            if (!ReleaseVersion.TryParse(installed, out var parsed)) { Console.Error.WriteLine("Invalid version."); return 2; }
            assumed = parsed;
        }
        var root = Path.GetFullPath(folder);
        var locations = new UpdateLocations(root);
        var paths = new ServicePaths(root);
        Directory.CreateDirectory(locations.UpdatesRoot);
        var log = new FileUpdaterLog(paths.Logs, echoToConsole: true);
        var product = new WindowsInstalledProduct(log);
        var version = assumed ?? product.InstalledVersion();
        using var client = new ReleaseCatalogClient(ReleaseDescriptionVerifier.CreatePinned(),
            new InstallerFiles(paths.Downloads, paths.Logs, new WindowsFileTrust()), UserAgentVersion());
        try
        {
            var channel = new RegistryChannelSource().ReadChannel();
            var release = client.FetchVerifiedAsync(channel, CancellationToken.None).GetAwaiter().GetResult();
            var decision = version is { } v ? ReleasePolicy.Decide(release, v, Array.Empty<ReleaseVersion>()).ToString() : "installed-version-unknown";
            log.Info("Channel " + channel + ": release " + release.Version + " (paused " + release.Paused + ", minimum " + release.MinimumVersion +
                     ", installer " + release.Installer.Size + " bytes); decision " + decision + ".");
            using var sink = new FileStatusSink(locations.StatusFile, heartbeat: false);
            sink.Publish(new UpdateStatus(version?.ToString(), release.Version.ToString(), UpdatePhase.Idle,
                null, null, null, DateTimeOffset.UtcNow, null, release.Notes, null, DateTimeOffset.UtcNow));
            return 0;
        }
        catch (Exception ex)
        {
            var code = ex is UpdateRejectedException rejected ? rejected.Code : ex.GetType().Name;
            log.Warning("Check failed: " + code + ".");
            return 1;
        }
    }

    internal static string UserAgentVersion() =>
        typeof(Program).Assembly.GetName().Version is { } version ? version.ToString(3) : "0.0.0";
}

/// <summary>
/// The Windows service. <see cref="OnStart"/> never fails: everything that depends on the disk, the network or
/// the service's own files happens in the background, retried every few minutes. Windows Installer starts this
/// service with <c>Wait="yes"</c>; a failing start would roll back the whole update.
/// </summary>
internal sealed class UpdaterWindowsService : ServiceBase
{
    private static readonly TimeSpan PrepareRetry = TimeSpan.FromMinutes(10);
    private readonly CancellationTokenSource _stop = new();
    private Task? _run;

    public UpdaterWindowsService()
    {
        ServiceName = UpdateLocations.ServiceName;
        CanStop = true;
        CanShutdown = true;
        CanPauseAndContinue = false;
        AutoLog = false;
    }

    protected override void OnStart(string[] args) => _run = Task.Run(() => RunGuardedAsync(_stop.Token));

    private static async Task RunGuardedAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            FileUpdaterLog? log = null;
            ReleaseCatalogClient? client = null;
            FileStatusSink? status = null;
            try
            {
                var locations = UpdateLocations.Machine();
                var paths = ServicePaths.ForThisService();
                var trust = new WindowsFileTrust();
                var notes = new List<string>();
                // Folders first: nothing is read from or written to them before they are proven trustworthy.
                if (!SecureDirectories.Ensure(locations, paths, trust, notes.Add))
                {
                    // The reason must be visible although the service's own journal may be the untrusted part:
                    // Windows event log, the journal if its folder was proven, and a code in the status if possible.
                    ReportPreparationFailure(notes, paths);
                    SecureDirectories.TryReportUntrusted(locations);
                    await Task.Delay(PrepareRetry, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                log = new FileUpdaterLog(paths.Logs, echoToConsole: false);
                foreach (var note in notes) log.Warning(note);
                log.Info("Service " + Program.UserAgentVersion() + " started.");
                var product = new WindowsInstalledProduct(log);
                var files = new InstallerFiles(paths.Downloads, paths.Logs, trust);
                client = new ReleaseCatalogClient(ReleaseDescriptionVerifier.CreatePinned(), files, Program.UserAgentVersion());
                status = new FileStatusSink(locations.StatusFile);
                var engine = new UpdaterEngine(
                    new SystemUpdaterClock(), new SharedUpdaterRandom(), log, new RegistryChannelSource(), product, client, files,
                    new WindowsApplicationProcesses(product), new WindowsSignalReader(locations.Signals), new WindowsInstallerRunner(),
                    new WtsSessionLauncher(log), new FileStateStore(paths.StateFile, log, trust), status);
                await engine.RunAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                log?.Warning("Service preparation failed: " + ex.GetType().Name + "; retrying.");
                try { await Task.Delay(PrepareRetry, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
            finally
            {
                status?.Dispose();
                client?.Dispose();
            }
        }
    }

    private static void ReportPreparationFailure(IReadOnlyList<string> notes, ServicePaths paths)
    {
        var text = "PlatformConnectorUpdater: " + UpdateErrorCodes.StorageUntrusted + ". " + string.Join(" ", notes);
        try { System.Diagnostics.EventLog.WriteEntry(UpdateLocations.ServiceName, text, System.Diagnostics.EventLogEntryType.Warning, 1001); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException or ArgumentException) { }
        try
        {
            if (WindowsFileTrust.IsTrustedDirectory(new DirectoryInfo(paths.Logs)))
                new FileUpdaterLog(paths.Logs, echoToConsole: false).Warning(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    protected override void OnStop() => StopEngine();

    protected override void OnShutdown() => StopEngine();

    private void StopEngine()
    {
        // Never waits for or ends an installer: it may be the installer that is stopping this service.
        _stop.Cancel();
        try { _run?.Wait(TimeSpan.FromSeconds(20)); }
        catch (AggregateException) { }
    }
}
