using Connector.Updates;

namespace Connector.Updater.Tests;

public sealed class UpdaterEngineTests
{
    private const string Alice = "S-1-5-21-1000-2000-3000-1001";
    private const string Bob = "S-1-5-21-1000-2000-3000-1002";
    private static readonly DateTimeOffset Start = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    private sealed class World
    {
        public readonly FakeClock Clock = new(Start);
        public readonly FakeRandom Random = new();
        public readonly FakeLog Log = new();
        public readonly FakeChannel Channel = new();
        public readonly FakeProduct Product = new();
        public readonly FakeFiles Files = new();
        public readonly FakeLauncher Launcher = new();
        public readonly MemoryStore Store = new();
        public readonly StatusList Status = new();
        public readonly FakeSource Source;
        public readonly FakeProcesses Processes;
        public readonly FakeSignals Signals;
        public readonly FakeInstaller Installer;

        public World()
        {
            Source = new FakeSource(Clock);
            Processes = new FakeProcesses(Status);
            Signals = new FakeSignals(Clock);
            Installer = new FakeInstaller(Clock, Product, Processes, Store);
        }

        public UpdaterEngine Engine() => new(Clock, Random, Log, Channel, Product, Source, Files, Processes, Signals, Installer, Launcher, Store, Status);

        public UpdateStatus Last => Status.Items[^1];
    }

    /// <summary>Runs steps until the clock passes <paramref name="until"/>, jumping straight to each due time.</summary>
    private static async Task RunUntil(UpdaterEngine engine, World world, DateTimeOffset until)
    {
        for (var i = 0; i < 10_000 && world.Clock.UtcNow < until; i++)
        {
            var due = await engine.StepAsync(CancellationToken.None);
            var next = due > world.Clock.UtcNow + UpdateSchedule.IdlePoll ? world.Clock.UtcNow + UpdateSchedule.IdlePoll : due;
            world.Clock.Set(next > world.Clock.UtcNow ? next : world.Clock.UtcNow + TimeSpan.FromSeconds(1));
        }
    }

    [Theory]
    [InlineData(0.0, 2)]
    [InlineData(0.5, 6)]
    [InlineData(0.9999999, 10)]
    public async Task First_check_is_two_to_ten_minutes_after_start(double random, int minutes)
    {
        var world = new World();
        world.Random.Value = random;
        var engine = world.Engine();
        await engine.StepAsync(CancellationToken.None);
        Assert.Equal(0, world.Source.Fetches);
        Assert.InRange((engine.State.NextCheckAtUtc!.Value - Start).TotalMinutes, minutes - 0.01, minutes + 0.01);
    }

    [Fact]
    public async Task Regular_checks_are_four_hours_plus_minus_twenty_minutes()
    {
        Assert.Equal(TimeSpan.FromHours(4) - TimeSpan.FromMinutes(20), UpdateSchedule.RegularDelay(0));
        Assert.InRange(UpdateSchedule.RegularDelay(0.9999999), TimeSpan.FromHours(4) + TimeSpan.FromMinutes(19.99), TimeSpan.FromHours(4) + TimeSpan.FromMinutes(20));
        var world = new World();
        world.Source.Release = TestRelease.Description("1.1.15"); // same as installed
        var engine = world.Engine();
        await RunUntil(engine, world, Start + TimeSpan.FromHours(9));
        Assert.Equal(3, world.Source.Fetches); // ~0:06, ~4:06, ~8:06
        Assert.Equal(UpdatePhase.Idle, world.Last.Phase);
        Assert.Null(world.Last.AvailableVersion);
    }

    [Fact]
    public async Task New_release_is_downloaded_installed_and_the_application_restarted_where_it_ran()
    {
        var world = new World();
        world.Source.Release = TestRelease.Description("1.1.16", notes: ["Быстрее вход"]);
        world.Processes.Running.Add(new FakeProcess(2, Alice, exitsOnRequest: true));
        world.Processes.Running.Add(new FakeProcess(3, Bob, exitsOnRequest: false)); // does not close by itself
        world.Installer.Installs = true;
        var engine = world.Engine();

        await RunUntil(engine, world, Start + TimeSpan.FromMinutes(15));

        Assert.Equal(new ReleaseVersion(1, 1, 16), world.Product.Version);
        // The pending record (with sessions) was saved before the installer started.
        var pendingBeforeStart = world.Installer.PendingAtStart!;
        Assert.Equal(new ReleaseVersion(1, 1, 16), pendingBeforeStart.Version);
        Assert.Equal([new SessionRecord(2, Alice), new SessionRecord(3, Bob)], pendingBeforeStart.Sessions);
        Assert.True(world.Installer.StartedAfterApplicationsGone);
        Assert.True(world.Processes.Running[1].Killed);
        Assert.False(world.Processes.Running[0].Killed);
        Assert.Equal([(2, Alice), (3, Bob)], world.Launcher.Launched);
        Assert.Null(engine.State.Pending);
        Assert.Null(engine.State.Target);
        Assert.Equal(UpdatePhase.Idle, world.Last.Phase);
        Assert.Equal("1.1.16", world.Last.InstalledVersion);
        Assert.Equal("1.1.16", world.Last.LastUpdate!.Version);
        Assert.Equal(["Быстрее вход"], world.Last.LastUpdate.Notes);
        Assert.Contains(world.Status.Items, s => s.Phase == UpdatePhase.Installing && s.AvailableVersion == "1.1.16");
        Assert.Contains(world.Status.Items, s => s.Phase == UpdatePhase.Downloading && s.DownloadPercent is not null);
    }

    [Fact]
    public async Task Busy_application_postpones_the_installation_at_most_eight_hours()
    {
        var world = new World();
        world.Source.Release = TestRelease.Description("1.1.16");
        world.Processes.Running.Add(new FakeProcess(2, Alice, exitsOnRequest: true));
        world.Installer.Installs = true;
        // The application keeps saying «busy for another 10 minutes» (and once, absurdly, for a year).
        world.Signals.Busy = (sid, now) => sid == Alice ? now + TimeSpan.FromMinutes(10) : null;
        var engine = world.Engine();

        await RunUntil(engine, world, Start + TimeSpan.FromHours(2));
        Assert.Equal(0, world.Installer.Starts);
        Assert.Equal(UpdatePhase.Ready, world.Last.Phase);
        Assert.NotNull(world.Last.ScheduledInstallAtUtc);

        world.Signals.Busy = (_, now) => now + TimeSpan.FromDays(365);
        var readyAt = engine.State.Target!.ReadyAtUtc!.Value;
        await RunUntil(engine, world, readyAt + TimeSpan.FromHours(8) - TimeSpan.FromMinutes(1));
        Assert.Equal(0, world.Installer.Starts);
        Assert.Equal(readyAt + TimeSpan.FromHours(8), world.Last.ScheduledInstallAtUtc);

        await RunUntil(engine, world, readyAt + TimeSpan.FromHours(8) + TimeSpan.FromMinutes(2));
        Assert.Equal(1, world.Installer.Starts);
        Assert.Equal(new ReleaseVersion(1, 1, 16), world.Product.Version);
    }

    [Fact]
    public async Task Expired_or_missing_busy_flag_does_not_postpone()
    {
        var world = new World();
        world.Source.Release = TestRelease.Description("1.1.16");
        world.Processes.Running.Add(new FakeProcess(2, Alice, exitsOnRequest: true));
        world.Installer.Installs = true;
        world.Signals.Busy = (_, now) => now - TimeSpan.FromMinutes(1); // the app crashed while busy
        var engine = world.Engine();
        await RunUntil(engine, world, Start + TimeSpan.FromMinutes(15));
        Assert.Equal(1, world.Installer.Starts);
    }

    [Fact]
    public async Task Failed_installations_retry_after_10_minutes_1_hour_6_hours_then_mark_the_version()
    {
        var world = new World();
        world.Source.Release = TestRelease.Description("1.1.16");
        world.Installer.Installs = false;
        world.Installer.ExitCode = 1603;
        var engine = world.Engine();

        await RunUntil(engine, world, Start + TimeSpan.FromMinutes(10));
        Assert.Equal(1, world.Installer.Starts);
        var firstFailure = world.Installer.StartTimes[0];
        Assert.Equal(UpdatePhase.Failed, world.Last.Phase);
        Assert.Equal(UpdateErrorCodes.InstallerFailed, world.Last.LastErrorCode);

        await RunUntil(engine, world, Start + TimeSpan.FromHours(12));
        Assert.Equal(4, world.Installer.Starts);
        Assert.InRange(world.Installer.StartTimes[1] - firstFailure, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(11));
        Assert.InRange(world.Installer.StartTimes[2] - world.Installer.StartTimes[1], TimeSpan.FromHours(1), TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1));
        Assert.InRange(world.Installer.StartTimes[3] - world.Installer.StartTimes[2], TimeSpan.FromHours(6), TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1));
        Assert.Contains(new ReleaseVersion(1, 1, 16), engine.State.ProblemVersions);
        Assert.Null(engine.State.Target);
        Assert.Equal(UpdateErrorCodes.VersionMarkedProblem, world.Last.LastErrorCode);

        // The problematic version is not tried again; the next release is.
        await RunUntil(engine, world, Start + TimeSpan.FromHours(24));
        Assert.Equal(4, world.Installer.Starts);
        world.Source.Release = TestRelease.Description("1.1.17");
        world.Installer.Installs = true;
        await RunUntil(engine, world, Start + TimeSpan.FromHours(30));
        Assert.Equal(5, world.Installer.Starts);
        Assert.Equal(new ReleaseVersion(1, 1, 17), world.Product.Version);
        Assert.Empty(engine.State.ProblemVersions);
    }

    [Fact]
    public async Task Busy_catalog_waits_retry_after_and_never_counts_as_an_installation_attempt()
    {
        var world = new World();
        world.Source.Release = TestRelease.Description("1.1.16");
        world.Source.BusyResponses = 3;
        world.Source.BusyRetryAfter = TimeSpan.FromSeconds(45);
        world.Random.Value = 0.5; // +30 s jitter, first check at 6 min
        world.Installer.Installs = true;
        var engine = world.Engine();

        await RunUntil(engine, world, Start + TimeSpan.FromMinutes(6) + TimeSpan.FromSeconds(1));
        Assert.Equal(1, world.Source.Downloads);
        Assert.Equal(UpdateErrorCodes.DownloadBusy, world.Last.LastErrorCode);
        Assert.NotEqual(UpdatePhase.Failed, world.Last.Phase);
        var retryAt = engine.State.NextCheckAtUtc!.Value;
        Assert.Equal(TimeSpan.FromSeconds(75), retryAt - world.Source.DownloadTimes[0]);

        await RunUntil(engine, world, Start + TimeSpan.FromMinutes(20));
        Assert.Equal(4, world.Source.Downloads); // three busy answers, then the resumed download completes
        Assert.Equal(1, world.Installer.Starts);
        Assert.Equal(new ReleaseVersion(1, 1, 16), world.Product.Version);
        Assert.Empty(engine.State.ProblemVersions);
    }

    [Fact]
    public async Task Broken_downloads_retry_with_growing_pause_without_touching_installation_attempts()
    {
        var world = new World();
        world.Source.Release = TestRelease.Description("1.1.16");
        world.Source.DownloadFailures = 3;
        world.Installer.Installs = true;
        var engine = world.Engine();
        await RunUntil(engine, world, Start + TimeSpan.FromMinutes(40));
        var times = world.Source.DownloadTimes;
        Assert.Equal(4, times.Count);
        Assert.Equal(TimeSpan.FromMinutes(1), times[1] - times[0]);
        Assert.Equal(TimeSpan.FromMinutes(5), times[2] - times[1]);
        Assert.Equal(TimeSpan.FromMinutes(15), times[3] - times[2]);
        Assert.Equal(1, world.Installer.Starts);
        Assert.Equal(0, world.Installer.Failures);
    }

    [Fact]
    public async Task Check_request_is_honoured_once_and_at_most_once_a_minute()
    {
        var world = new World();
        world.Source.Release = TestRelease.Description("1.1.15");
        world.Processes.Running.Add(new FakeProcess(2, Alice, exitsOnRequest: true));
        var engine = world.Engine();
        await engine.StepAsync(CancellationToken.None);
        Assert.Equal(0, world.Source.Fetches);

        var pressed = world.Clock.UtcNow;
        world.Signals.Check = (_, _) => pressed;
        await engine.StepAsync(CancellationToken.None);
        Assert.Equal(1, world.Source.Fetches);
        await engine.StepAsync(CancellationToken.None); // same request again: ignored
        Assert.Equal(1, world.Source.Fetches);

        world.Clock.Advance(TimeSpan.FromSeconds(20));
        var again = world.Clock.UtcNow;
        world.Signals.Check = (_, _) => again;
        await engine.StepAsync(CancellationToken.None); // within a minute of the last check
        Assert.Equal(1, world.Source.Fetches);
        world.Clock.Advance(TimeSpan.FromSeconds(45));
        await engine.StepAsync(CancellationToken.None);
        Assert.Equal(2, world.Source.Fetches);
    }

    [Fact]
    public async Task Stale_future_and_foreign_user_check_requests_are_ignored()
    {
        var world = new World();
        world.Source.Release = TestRelease.Description("1.1.15");
        world.Processes.Running.Add(new FakeProcess(2, Alice, exitsOnRequest: true));
        var engine = world.Engine();
        await engine.StepAsync(CancellationToken.None);
        world.Signals.Check = (_, now) => now - TimeSpan.FromHours(1);
        await engine.StepAsync(CancellationToken.None);
        world.Signals.Check = (_, now) => now + TimeSpan.FromDays(1);
        await engine.StepAsync(CancellationToken.None);
        // A request of a user whose application is not running is never even read.
        world.Signals.Check = (sid, now) => sid == Bob ? now : null;
        await engine.StepAsync(CancellationToken.None);
        Assert.Equal(0, world.Source.Fetches);
        Assert.DoesNotContain(Bob, world.Signals.ReadFor);
    }

    [Fact]
    public async Task Pending_installation_is_resolved_by_the_next_service_instance()
    {
        var world = new World();
        world.Source.Release = TestRelease.Description("1.1.16");
        world.Processes.Running.Add(new FakeProcess(4, Alice, exitsOnRequest: true));
        world.Installer.Installs = true;
        world.Installer.StopServiceWhileRunning = true; // the installer stops the old service
        var engine = world.Engine();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunUntil(engine, world, Start + TimeSpan.FromMinutes(15)));
        Assert.NotNull(world.Store.Saved);
        Assert.Contains("\"pending\": {", System.Text.Encoding.UTF8.GetString(world.Store.Saved!));

        // New service (new version) starts with the saved state, re-attaches, sees the installer gone and the new version.
        world.Installer.StopServiceWhileRunning = false;
        var next = world.Engine();
        await next.StepAsync(CancellationToken.None);
        Assert.Null(next.State.Pending);
        Assert.Equal("1.1.16", next.State.LastUpdate!.Version);
        Assert.Equal([(4, Alice)], world.Launcher.Launched);
    }

    [Fact]
    public async Task Rolled_back_installation_counts_as_a_failure_and_still_restarts_the_application()
    {
        var world = new World();
        world.Source.Release = TestRelease.Description("1.1.16");
        world.Processes.Running.Add(new FakeProcess(2, Alice, exitsOnRequest: true));
        world.Installer.Installs = false;
        world.Installer.ExitCode = 0; // reports success but the version did not change
        var engine = world.Engine();
        await RunUntil(engine, world, Start + TimeSpan.FromMinutes(10));
        Assert.Equal(UpdateErrorCodes.InstalledVersionMismatch, world.Last.LastErrorCode);
        Assert.Equal(1, engine.State.Target!.Failures);
        Assert.Equal([(2, Alice)], world.Launcher.Launched);
    }

    [Fact]
    public async Task Paused_or_older_release_cancels_a_prepared_update()
    {
        var world = new World();
        world.Source.Release = TestRelease.Description("1.1.16");
        world.Signals.Busy = (_, now) => now + TimeSpan.FromMinutes(10);
        world.Processes.Running.Add(new FakeProcess(2, Alice, exitsOnRequest: true));
        var engine = world.Engine();
        await RunUntil(engine, world, Start + TimeSpan.FromMinutes(15));
        Assert.NotNull(engine.State.Target);
        world.Source.Release = TestRelease.Description("1.1.16", paused: true);
        await RunUntil(engine, world, Start + TimeSpan.FromHours(5));
        Assert.Null(engine.State.Target);
        Assert.True(world.Files.DeletedAll);
        Assert.Equal(0, world.Installer.Starts);
        Assert.Equal(UpdatePhase.Idle, world.Last.Phase);
    }

    [Fact]
    public async Task Rejected_descriptions_and_network_errors_never_install_and_are_reported_by_code()
    {
        var world = new World();
        world.Source.Error = new UpdateRejectedException(UpdateErrorCodes.SignatureInvalid);
        var engine = world.Engine();
        await RunUntil(engine, world, Start + TimeSpan.FromMinutes(5));
        Assert.Equal(UpdateErrorCodes.SignatureInvalid, world.Last.LastErrorCode);
        Assert.Equal(UpdatePhase.Idle, world.Last.Phase);
        Assert.Equal(0, world.Source.Downloads);
        Assert.Equal(0, world.Installer.Starts);
        // Retried after 10 minutes (then after an hour, then at the regular cadence).
        Assert.Equal(TimeSpan.FromMinutes(10), engine.State.NextCheckAtUtc - engine.State.LastCheckAtUtc);
    }

    [Fact]
    public async Task Planted_state_and_installer_never_start_without_a_signed_description_of_this_run()
    {
        var world = new World();
        // Somebody left a «ready» target of their own EXE in the state; the catalog says 1.1.15 is current.
        var planted = new UpdaterState
        {
            Target = new UpdateTarget { Version = new ReleaseVersion(9, 9, 9), Channel = "stable", ReadyAtUtc = Start - TimeSpan.FromDays(1),
                Installer = new ReleaseInstaller("PlatformConnector-9.9.9.exe", 4096, new string('e', 64)), Notes = [] }
        };
        world.Store.Save(planted);
        world.Source.Release = TestRelease.Description("1.1.15");
        world.Installer.Installs = true;
        var engine = world.Engine();
        await RunUntil(engine, world, Start + TimeSpan.FromMinutes(30));
        Assert.Equal(0, world.Installer.Starts);
        Assert.Null(engine.State.Target);
        Assert.True(world.Files.DeletedAll);

        // Catalog unreachable at installation time: still nothing starts, and no attempt is counted.
        var offline = new World();
        offline.Store.Save(planted);
        offline.Source.Error = new HttpRequestException("offline");
        var stalled = offline.Engine();
        await RunUntil(stalled, offline, Start + TimeSpan.FromHours(2));
        Assert.Equal(0, offline.Installer.Starts);
        Assert.Equal(0, stalled.State.Target!.Failures);
    }

    [Fact]
    public async Task Installer_is_verified_against_the_description_fetched_now_not_the_stored_one()
    {
        var world = new World();
        var stored = TestRelease.Description("1.1.16");
        world.Store.Save(new UpdaterState
        {
            Target = new UpdateTarget { Version = stored.Version, Channel = "stable", ReadyAtUtc = Start - TimeSpan.FromHours(1),
                Installer = new ReleaseInstaller(stored.Installer.FileName, stored.Installer.Size, new string('d', 64)), Notes = [] }
        });
        world.Source.Release = stored; // signed hash differs from the stored one
        world.Installer.Installs = true;
        var engine = world.Engine();
        await RunUntil(engine, world, Start + TimeSpan.FromMinutes(15));
        Assert.Equal(stored.Installer, world.Files.LeasedFor);
        Assert.Equal(new ReleaseVersion(1, 1, 16), world.Product.Version);
    }

    [Fact]
    public async Task Pause_published_after_download_stops_the_installation()
    {
        var world = new World();
        world.Source.Release = TestRelease.Description("1.1.16");
        world.Processes.Running.Add(new FakeProcess(2, Alice, exitsOnRequest: true));
        var busyUntil = Start + TimeSpan.FromMinutes(30);
        world.Signals.Busy = (_, now) => now < busyUntil ? busyUntil : null;
        world.Installer.Installs = true;
        var engine = world.Engine();
        await RunUntil(engine, world, Start + TimeSpan.FromMinutes(20));
        Assert.NotNull(engine.State.Target!.ReadyAtUtc);
        world.Source.Release = TestRelease.Description("1.1.16", paused: true); // stop-cock pulled meanwhile
        await RunUntil(engine, world, Start + TimeSpan.FromHours(1));
        Assert.Equal(0, world.Installer.Starts);
        Assert.Null(engine.State.Target);
    }

    [Fact]
    public async Task A_running_installer_is_awaited_without_a_second_one_or_an_application_restart()
    {
        var world = new World();
        world.Source.Release = TestRelease.Description("1.1.16");
        world.Processes.Running.Add(new FakeProcess(2, Alice, exitsOnRequest: true));
        world.Installer.Installs = true;
        world.Installer.RunningSlices = 20; // 20 × 10 min: past the 3 h limit
        var engine = world.Engine();
        await RunUntil(engine, world, Start + TimeSpan.FromHours(2));
        Assert.Equal(1, world.Installer.Starts);
        Assert.NotNull(engine.State.Pending);
        Assert.Empty(world.Launcher.Launched);
        Assert.Equal(UpdatePhase.Installing, world.Last.Phase);

        await RunUntil(engine, world, Start + TimeSpan.FromHours(3) + TimeSpan.FromMinutes(20));
        Assert.Equal(1, world.Installer.Starts);
        Assert.True(engine.State.Pending!.FailureCounted);
        Assert.Equal(UpdatePhase.Failed, world.Last.Phase);
        Assert.Equal(UpdateErrorCodes.InstallerTimeout, world.Last.LastErrorCode);
        Assert.Empty(world.Launcher.Launched);

        await RunUntil(engine, world, Start + TimeSpan.FromHours(5));
        Assert.Equal(1, world.Installer.Starts);
        Assert.Null(engine.State.Pending);
        Assert.Equal(new ReleaseVersion(1, 1, 16), world.Product.Version);
        Assert.Equal([(2, Alice)], world.Launcher.Launched);
    }

    [Theory]
    [InlineData(1618, false)]   // another installation was running: not an attempt against the release
    [InlineData(1603, true)]
    public async Task Busy_windows_installer_is_retried_without_counting(int exitCode, bool counted)
    {
        var world = new World();
        world.Source.Release = TestRelease.Description("1.1.16");
        world.Installer.Installs = false;
        world.Installer.ExitCode = exitCode;
        var engine = world.Engine();
        await RunUntil(engine, world, Start + TimeSpan.FromMinutes(10));
        Assert.Equal(1, world.Installer.Starts);
        Assert.Equal(counted ? 1 : 0, engine.State.Target!.Failures);
    }

    [Theory]
    [InlineData(3010)]
    [InlineData(1641)]
    public async Task Success_that_needs_a_restart_of_windows_is_success(int exitCode)
    {
        var world = new World();
        world.Source.Release = TestRelease.Description("1.1.16");
        world.Installer.Installs = true;
        world.Installer.ExitCode = exitCode;
        var engine = world.Engine();
        await RunUntil(engine, world, Start + TimeSpan.FromMinutes(10));
        Assert.Equal("1.1.16", engine.State.LastUpdate!.Version);
        Assert.Null(engine.State.FailureCode);
    }

    [Fact]
    public async Task Repeated_hash_mismatch_stops_downloading_until_the_description_changes()
    {
        var world = new World();
        world.Source.Release = TestRelease.Description("1.1.16");
        world.Source.DownloadFailures = 100;
        world.Source.DownloadFailureCode = UpdateErrorCodes.DownloadHashMismatch;
        var engine = world.Engine();
        await RunUntil(engine, world, Start + TimeSpan.FromHours(12));
        Assert.Equal(UpdateSchedule.MaximumHashMismatches, world.Source.Downloads);
        Assert.Equal(UpdateErrorCodes.DownloadHashMismatch, world.Last.LastErrorCode);
        // A corrected description (new hash) is downloaded again.
        world.Source.Release = TestRelease.Description("1.1.16", payload: "fixed build"u8.ToArray());
        world.Source.DownloadFailures = 0;
        world.Installer.Installs = true;
        await RunUntil(engine, world, Start + TimeSpan.FromHours(17));
        Assert.Equal(new ReleaseVersion(1, 1, 16), world.Product.Version);
    }

    [Fact]
    public async Task Stale_installing_is_cleared_at_start_when_its_installer_is_gone()
    {
        var world = new World();
        world.Store.Save(new UpdaterState
        {
            Pending = new PendingInstall { Version = new ReleaseVersion(1, 1, 16), Attempt = 1, StartedAtUtc = Start - TimeSpan.FromDays(2),
                Sessions = [], InstallerProcessId = 999, InstallerStartedAtUtc = Start - TimeSpan.FromDays(2) }
        });
        world.Source.Release = TestRelease.Description("1.1.15");
        var engine = world.Engine();
        await engine.StepAsync(CancellationToken.None);
        Assert.Null(engine.State.Pending);
        Assert.DoesNotContain(world.Status.Items, status => status.Phase == UpdatePhase.Installing);
    }

    [Fact]
    public async Task Engine_failing_in_every_step_withdraws_installing_and_lets_it_go_stale()
    {
        var world = new World();
        world.Store.Save(new UpdaterState
        {
            Pending = new PendingInstall { Version = new ReleaseVersion(1, 1, 16), Attempt = 1, StartedAtUtc = Start, Sessions = [] }
        });
        world.Product.Throws = true;                                  // e.g. a broken new release throws while resolving
        var writes = new List<UpdateStatus>();
        using var sink = new FileStatusSink("status.json", () => world.Clock.UtcNow,
            (_, bytes) => writes.Add(UpdateStatusCodec.TryDeserialize(bytes)!), heartbeat: false);
        // What the previous service instance left: a fresh «installing».
        sink.Publish(new UpdateStatus("1.1.15", "1.1.16", UpdatePhase.Installing, null, null, null, null, null, [], null, Start));
        var engine = new UpdaterEngine(world.Clock, world.Random, world.Log, world.Channel, world.Product, world.Source, world.Files,
            world.Processes, world.Signals, world.Installer, world.Launcher, world.Store, sink);
        for (var minute = 0; minute < 15; minute++)
        {
            await engine.StepGuardedAsync(CancellationToken.None);
            world.Clock.Advance(TimeSpan.FromMinutes(1));
            sink.Beat();
        }
        // No heartbeat ever refreshed «installing»; after the stall limit the status says failed / engine-stalled.
        Assert.DoesNotContain(writes.Skip(1), status => status.Phase == UpdatePhase.Installing);
        Assert.Equal(UpdatePhase.Failed, writes[^1].Phase);
        Assert.Equal(UpdateErrorCodes.EngineStalled, writes[^1].LastErrorCode);
        Assert.True(writes[^1].WrittenAtUtc >= Start + UpdateSchedule.EngineStallLimit);
    }

    [Fact]
    public async Task Altered_download_is_not_installed()
    {
        var world = new World();
        world.Source.Release = TestRelease.Description("1.1.16");
        world.Files.LeaseFails = true;
        var engine = world.Engine();
        await RunUntil(engine, world, Start + TimeSpan.FromMinutes(7));
        Assert.Equal(0, world.Installer.Starts);
        Assert.Null(engine.State.Target!.ReadyAtUtc);
        Assert.Equal(UpdateErrorCodes.DownloadHashMismatch, engine.State.FailureCode);
    }
}

public sealed class StatusAndSignalTests
{
    [Fact]
    public void Status_round_trips_and_garbage_reads_as_nothing()
    {
        var status = new UpdateStatus("1.1.15", "1.1.16", UpdatePhase.Downloading, 42, null, UpdateErrorCodes.DownloadBusy,
            new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero), null, ["Новое"],
            new CompletedUpdate("1.1.15", new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero), ["Было"]),
            new DateTimeOffset(2026, 11, 25, 9, 1, 0, TimeSpan.Zero));
        var read = UpdateStatusCodec.TryDeserialize(UpdateStatusCodec.Serialize(status))!;
        Assert.Equal("1.1.16", read.AvailableVersion);
        Assert.Equal(UpdatePhase.Downloading, read.Phase);
        Assert.Equal(42, read.DownloadPercent);
        Assert.Equal(UpdateErrorCodes.DownloadBusy, read.LastErrorCode);
        Assert.Equal(["Новое"], read.AvailableNotes);
        Assert.Equal("1.1.15", read.LastUpdate!.Version);

        Assert.Null(UpdateStatusCodec.TryDeserialize("not json"u8));
        Assert.Null(UpdateStatusCodec.TryDeserialize("{\"schemaVersion\":2,\"phase\":\"idle\"}"u8));
        Assert.Null(UpdateStatusCodec.TryDeserialize("{\"schemaVersion\":1,\"phase\":\"exploded\"}"u8));
        var odd = UpdateStatusCodec.TryDeserialize("{\"schemaVersion\":1,\"phase\":\"failed\",\"lastErrorCode\":\"<script>\",\"availableVersion\":\"1.1.16<b>\"}"u8)!;
        Assert.Null(odd.LastErrorCode);
        Assert.Null(odd.AvailableVersion);
    }

    [Fact]
    public void Status_heartbeat_follows_the_installer_and_the_engine_not_the_process()
    {
        var now = new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);
        var writes = new List<UpdateStatus>();
        using var sink = new FileStatusSink("status.json", () => now, (_, bytes) => writes.Add(UpdateStatusCodec.TryDeserialize(bytes)!), heartbeat: false);
        UpdateStatus At(UpdatePhase phase, DateTimeOffset time) =>
            new("1.1.15", "1.1.16", phase, null, null, null, null, null, [], null, time);
        var installerAlive = true;
        sink.Publish(At(UpdatePhase.Installing, now));
        sink.Publish(At(UpdatePhase.Installing, now.AddSeconds(30))); // same content, fresh enough: not rewritten
        Assert.Single(writes);
        sink.SetActiveInstaller(() => installerAlive);                // engine blocked waiting for its installer
        now = now.AddSeconds(61);
        sink.Beat();
        Assert.Equal(2, writes.Count);
        Assert.Equal(now, writes[^1].WrittenAtUtc);
        installerAlive = false;                                       // installer gone, engine not back: no more «installing»
        now = now.AddSeconds(61);
        sink.Beat();
        Assert.Equal(2, writes.Count);
        sink.SetActiveInstaller(null);
        now = now.AddMinutes(5);
        sink.Beat();                                                  // no installer, no engine step: still nothing
        Assert.Equal(2, writes.Count);

        sink.Publish(At(UpdatePhase.Idle, now));
        Assert.Equal(3, writes.Count);
        now = now.AddMinutes(16);
        sink.Beat();                                                  // idle, but the engine made no step for 16 min
        Assert.Equal(3, writes.Count);
        sink.Touch();
        sink.Beat();                                                  // engine alive: every 15 minutes
        Assert.Equal(4, writes.Count);
    }

    [Fact]
    public void Signal_files_carry_one_timestamp_and_nothing_else()
    {
        var time = new DateTimeOffset(2026, 11, 25, 9, 30, 0, TimeSpan.Zero);
        Assert.Equal(time, UpdateSignalFiles.TryDecode(UpdateSignalFiles.Encode(time)));
        Assert.Equal(time, UpdateSignalFiles.TryDecode("2026-11-25T09:30:00Z\r\n"u8));
        Assert.Null(UpdateSignalFiles.TryDecode("\uFEFF2026-11-25T09:30:00Z"u8.ToArray()));
        Assert.Null(UpdateSignalFiles.TryDecode("2026-11-25T09:30:00Z C:\\Windows\\evil.exe"u8));
        Assert.Null(UpdateSignalFiles.TryDecode("2026-11-25T09:30:00+03"u8));
        Assert.Null(UpdateSignalFiles.TryDecode("https://evil.test/x"u8));
        Assert.Null(UpdateSignalFiles.TryDecode(new byte[2048]));
        Assert.Null(UpdateSignalFiles.TryDecode(ReadOnlySpan<byte>.Empty));
        Assert.Null(UpdateSignalFiles.TryDecode("2026-13-45T99:30:00Z"u8));
    }

    [Theory]
    [InlineData("S-1-5-21-1000-2000-3000-1001", true)]
    [InlineData("S-1-5-18", true)]
    [InlineData("S-1-5-21-..\\x", false)]
    [InlineData("S-1-5--21", false)]
    [InlineData("S-1-5-21-", false)]
    [InlineData("s-1-5-21", false)]
    public void Signal_file_names_take_only_canonical_sids(string sid, bool valid)
    {
        Assert.Equal(valid, UpdateSignalFiles.IsSid(sid));
        if (valid) Assert.Equal("busy-" + sid + ".flag", UpdateSignalFiles.BusyFileName(sid));
        else Assert.Throws<ArgumentException>(() => UpdateSignalFiles.BusyFileName(sid));
    }

    [Fact]
    public void Service_state_round_trips_including_the_pending_installation()
    {
        var state = new UpdaterState
        {
            LastCheckAtUtc = DateTimeOffset.UtcNow,
            ConsecutiveDownloadFailures = 2,
            Target = new UpdateTarget { Version = new ReleaseVersion(1, 1, 16), Channel = "pilot",
                Installer = new ReleaseInstaller("PlatformConnector-1.1.16.exe", 10, new string('b', 64)), Notes = ["x"], Failures = 1 },
            Pending = new PendingInstall { Version = new ReleaseVersion(1, 1, 16), Attempt = 2, StartedAtUtc = DateTimeOffset.UtcNow,
                Sessions = [new SessionRecord(3, "S-1-5-21-1-2-3-1001")], InstallerProcessId = 77, InstallerStartedAtUtc = DateTimeOffset.UtcNow }
        };
        state.ProblemVersions.Add(new ReleaseVersion(1, 1, 14));
        var read = UpdaterStateCodec.TryDeserialize(UpdaterStateCodec.Serialize(state))!;
        Assert.Equal(2, read.ConsecutiveDownloadFailures);
        Assert.Equal("pilot", read.Target!.Channel);
        Assert.Equal(1, read.Target.Failures);
        Assert.Equal(77, read.Pending!.InstallerProcessId);
        Assert.Equal(state.Pending.InstallerStartedAtUtc, read.Pending.InstallerStartedAtUtc);
        Assert.Equal([new SessionRecord(3, "S-1-5-21-1-2-3-1001")], read.Pending.Sessions);
        Assert.Equal([new ReleaseVersion(1, 1, 14)], read.ProblemVersions);
        Assert.Null(UpdaterStateCodec.TryDeserialize("{\"schemaVersion\":1,\"target\":{\"version\":\"1.1.16\",\"fileName\":\"..\\\\x.exe\"}}"u8));
    }
}
