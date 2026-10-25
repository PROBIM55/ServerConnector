using System.Text;
using System.Text.Json;
using Connector.Network;
using Connector.SmbAccess;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class LegacyConnectorTransferTests
{
    private const string LegacyExe = @"C:\Users\u\AppData\Local\Structura Connector\Connector.Desktop.exe";
    private const string CustomLegacyExe = @"D:\Apps\Old Connector\Connector.Desktop.exe";
    private const string OwnExe = @"C:\Users\u\AppData\Local\Structura.Connector.Desktop\current\Connector.Desktop.exe";
    private const string Marker = @"C:\Users\u\AppData\Local\Platform Connector\legacy-connector-transferred.marker";
    private const string State = @"C:\Users\u\AppData\Local\Platform Connector\legacy-connector-transfer.json";
    private const string Tunnel = "AmneziaWGTunnel$structura-vpn";
    private const string LegacyShare = @"\\62.113.36.107\BIM_Models";
    private const string LegacyIpc = @"\\62.113.36.107\IPC$";
    private const string OverlayShare = @"\\100.64.1.20\Company_A";
    private static readonly LegacyTransferReadiness Ready = new(true, true, true);

    [Fact]
    public async Task Transfer_closes_the_old_client_saves_its_autostart_and_removes_only_its_deviceless_connections()
    {
        var world = OldClientPc();
        var service = Service(world);

        var result = await service.TransferAsync(new LegacyTransferRequest(true, Ready));

        Assert.Equal(LegacyTransferOutcome.Completed, result.Outcome);
        Assert.Equal("legacy_transferred", result.Code);
        // Graceful request only, autostart, the share session before IPC$, the marker last.
        Assert.Equal(["close:42", "run-delete", "smb-cancel:" + LegacyShare, "smb-cancel:" + LegacyIpc, "write:marker"],
            world.Calls);
        Assert.Null(world.RunValue);
        Assert.Contains(world.Connections, connection => connection == new SmbConnectionEntry("Z:", LegacyShare));
        Assert.Contains(world.Connections, connection => connection == new SmbConnectionEntry(null, OverlayShare));
        var state = JsonDocument.Parse(world.Files[State]).RootElement;
        Assert.Equal("completed", state.GetProperty("phase").GetString());
        Assert.Equal(Convert.ToBase64String(OldRunValue().Data), state.GetProperty("autoStart").GetProperty("data").GetString());
        Assert.True(state.GetProperty("autoStartRemoved").GetBoolean());
        Assert.Equal(1, state.GetProperty("closedProcesses").GetInt32());
        Assert.False(state.GetProperty("forcedClose").GetBoolean());
        Assert.Equal([LegacyShare, LegacyIpc], state.GetProperty("removedConnections").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(world.Files[State], world.Files[Marker]);
        // The tunnel is only ever queried, never controlled; neighbour mode (K4) is now off.
        Assert.All(world.ServiceQueries, name => Assert.Equal(Tunnel, name));
        Assert.DoesNotContain(world.Calls, call => call.Contains("AmneziaWG", StringComparison.OrdinalIgnoreCase));
        Assert.False(new WindowsLegacyConnectorDetector(world).Detect().NeighbourModeActive);
        Assert.True(service.ReadStatus().Transferred);
    }

    [Fact]
    public async Task Old_client_that_is_not_running_is_transferred_and_started_again_on_rollback()
    {
        var world = OldClientPc(running: false);
        var service = Service(world);

        var result = await service.TransferAsync(new LegacyTransferRequest(true, Ready));

        Assert.Equal(LegacyTransferOutcome.Completed, result.Outcome);
        Assert.DoesNotContain(world.Calls, call => call.StartsWith("close:") || call.StartsWith("kill:"));
        Assert.Contains("run-delete", world.Calls);
        Assert.False(JsonDocument.Parse(world.Files[State]).RootElement.GetProperty("processWasRunning").GetBoolean());

        var restore = await service.RestoreAsync(world.ReleaseOwnDrives);

        Assert.Equal("legacy_restored", restore.Code);
        Assert.Contains("start:" + LegacyExe, world.Calls);
        Assert.True(OldRunValue().SameAs(world.RunValue));
    }

    [Fact]
    public async Task Absent_autostart_value_is_neither_deleted_nor_created()
    {
        var world = OldClientPc();
        world.RunValue = null;
        var service = Service(world);

        var result = await service.TransferAsync(new LegacyTransferRequest(true, Ready));

        Assert.Equal(LegacyTransferOutcome.Completed, result.Outcome);
        Assert.Equal("close:42", world.Calls[0]);
        Assert.DoesNotContain("run-delete", world.Calls);
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(world.Files[State]).RootElement.GetProperty("autoStart").ValueKind);

        await service.RestoreAsync(world.ReleaseOwnDrives);

        Assert.DoesNotContain("run-write", world.Calls);
        Assert.Null(world.RunValue);
        Assert.Contains("start:" + LegacyExe, world.Calls);
    }

    [Theory]
    [InlineData(LegacyShare)]
    [InlineData(LegacyIpc)]
    public async Task Open_files_found_by_the_precheck_refuse_before_the_old_client_is_touched(string busyUse)
    {
        var world = OldClientPc();
        world.OpenFiles.Add(busyUse);
        world.OpenFiles.Add(@"\\100.64.1.20\Company_A"); // other servers do not matter
        var service = Service(world);

        var result = await service.TransferAsync(new LegacyTransferRequest(true, Ready));

        Assert.Equal(LegacyTransferOutcome.Refused, result.Outcome);
        Assert.Equal("legacy_smb_files_open", result.Code);
        Assert.Contains("открыты файлы", result.Message, StringComparison.Ordinal);
        Assert.Empty(world.Calls);
        Assert.Empty(world.Files);
        Assert.True(OldRunValue().SameAs(world.RunValue));
        Assert.Contains(world.Processes, process => process.ProcessId == 42);
    }

    [Fact]
    public async Task Files_opened_after_the_precheck_abort_restores_autostart_but_never_restarts_the_old_client()
    {
        var world = OldClientPc();
        world.LateOpenFiles.Add(LegacyShare);
        var service = Service(world);

        var result = await service.TransferAsync(new LegacyTransferRequest(true, Ready));

        Assert.Equal(LegacyTransferOutcome.Aborted, result.Outcome);
        Assert.Equal("legacy_smb_files_open", result.Code);
        Assert.Contains("запустите прежний коннектор вручную", result.Message, StringComparison.Ordinal);
        Assert.Equal(["close:42", "run-delete", "smb-cancel:" + LegacyShare, "run-write",
            "delete:legacy-connector-transfer.json"], world.Calls);
        Assert.True(OldRunValue().SameAs(world.RunValue));
        Assert.DoesNotContain(world.Processes, process => process.ImagePath == LegacyExe);
        Assert.Contains(world.Connections, connection => connection == new SmbConnectionEntry(null, LegacyShare));
        Assert.Empty(world.Files);
        Assert.False(service.ReadStatus().CanRestore);
        // Autostart without a marker keeps neighbour mode on: BIM_Models is not mounted here meanwhile.
        Assert.True(new WindowsLegacyConnectorDetector(world).Detect().NeighbourModeActive);
    }

    [Fact]
    public async Task Failed_undo_keeps_the_journal_and_the_rollback_completes_it()
    {
        var world = OldClientPc();
        world.LateOpenFiles.Add(LegacyShare);
        world.FailRunWrites = 1;
        var service = Service(world);

        var result = await service.TransferAsync(new LegacyTransferRequest(true, Ready));

        Assert.Equal(LegacyTransferOutcome.PartiallyApplied, result.Outcome);
        Assert.Equal("legacy_transfer_partial:legacy_smb_files_open", result.Code);
        Assert.False(world.Files.ContainsKey(Marker)); // neighbour mode stays on: nothing is mounted here
        Assert.True(service.ReadStatus().Incomplete);
        Assert.True(JsonDocument.Parse(world.Files[State]).RootElement.GetProperty("compensationFailed").GetBoolean());
        var retry = await service.TransferAsync(new LegacyTransferRequest(true, Ready));
        Assert.Equal("legacy_preconditions", retry.Code);
        Assert.Contains("Вернуть прежний коннектор", retry.Message, StringComparison.Ordinal);

        var restore = await service.RestoreAsync(world.ReleaseOwnDrives);

        Assert.Equal("legacy_restored", restore.Code);
        Assert.True(OldRunValue().SameAs(world.RunValue));
        Assert.Contains(world.Processes, process => process.ImagePath == LegacyExe);
        Assert.Empty(world.Files);
    }

    [Fact]
    public async Task Rollback_releases_own_drives_restores_the_exact_value_and_starts_the_old_client_in_order()
    {
        var world = OldClientPc();
        var service = Service(world);
        await service.TransferAsync(new LegacyTransferRequest(true, Ready));
        world.Calls.Clear();

        var result = await service.RestoreAsync(token =>
        {
            world.Calls.Add("release-own-drives");
            return world.ReleaseOwnDrives(token);
        });

        Assert.Equal(LegacyTransferOutcome.Completed, result.Outcome);
        Assert.Equal("legacy_restored", result.Code);
        Assert.Equal(["release-own-drives", "run-write", "delete:legacy-connector-transferred.marker",
            "start:" + LegacyExe, "delete:legacy-connector-transfer.json"], world.Calls);
        Assert.True(OldRunValue().SameAs(world.RunValue));
        Assert.False(service.ReadStatus().CanRestore);
        Assert.True(new WindowsLegacyConnectorDetector(world).Detect().NeighbourModeActive);
    }

    [Fact]
    public async Task Custom_install_is_closed_and_restored_from_the_marker_when_the_journal_is_missing()
    {
        var custom = OldRunValue(CustomLegacyExe);
        var world = OldClientPc(running: false);
        world.RunValue = custom;
        world.Executables.Add(CustomLegacyExe);
        world.Processes.Add(new LegacyProcessImage(42, 1, CustomLegacyExe));
        var service = Service(world);
        await service.TransferAsync(new LegacyTransferRequest(true, Ready));
        Assert.Contains("close:42", world.Calls);
        world.Files.Remove(State);

        var result = await service.RestoreAsync(world.ReleaseOwnDrives);

        Assert.Equal("legacy_restored", result.Code);
        Assert.True(custom.SameAs(world.RunValue));
        Assert.Contains("start:" + CustomLegacyExe, world.Calls);
        Assert.Empty(world.Files);
    }

    [Fact]
    public async Task Unreadable_marker_without_journal_restores_the_v1_0_31_default_autostart()
    {
        var world = OldClientPc(running: false);
        world.RunValue = null;
        world.Files[Marker] = "transferred";

        var result = await Service(world).RestoreAsync(world.ReleaseOwnDrives);

        Assert.Equal("legacy_restored", result.Code);
        Assert.True(LegacyRunValue.FromCommand($"\"{LegacyExe}\"").SameAs(world.RunValue));
        Assert.Contains("start:" + LegacyExe, world.Calls);
        Assert.Empty(world.Files);
    }

    [Fact]
    public async Task Double_transfer_and_double_rollback_are_idempotent()
    {
        var world = OldClientPc();
        var service = Service(world);
        Assert.Equal(LegacyTransferOutcome.Completed, (await service.TransferAsync(new LegacyTransferRequest(true, Ready))).Outcome);
        world.Calls.Clear();

        var second = await service.TransferAsync(new LegacyTransferRequest(true, Ready));

        Assert.Equal(LegacyTransferOutcome.AlreadyDone, second.Outcome);
        Assert.Empty(world.Calls);
        Assert.Equal("legacy_restored", (await service.RestoreAsync(world.ReleaseOwnDrives)).Code);
        world.Calls.Clear();
        var releases = 0;

        var again = await service.RestoreAsync(_ =>
        {
            releases++;
            return Task.CompletedTask;
        });

        Assert.Equal(LegacyTransferOutcome.AlreadyDone, again.Outcome);
        Assert.Equal("legacy_not_transferred", again.Code);
        Assert.Empty(world.Calls);
        Assert.Equal(0, releases);
        Assert.Single(world.Processes, process => process.ImagePath == LegacyExe);
    }

    [Fact]
    public async Task Busy_own_drive_stops_the_rollback_before_any_change_and_a_retry_completes_it()
    {
        var world = OldClientPc();
        var service = Service(world);
        await service.TransferAsync(new LegacyTransferRequest(true, Ready));
        world.Calls.Clear();

        var busy = await service.RestoreAsync(_ =>
            throw new CommonSmbAccessException("smb_mapping_busy", "Files are open on the mapped legacy share."));

        Assert.Equal(LegacyTransferOutcome.Refused, busy.Outcome);
        Assert.Equal("legacy_own_drive_busy", busy.Code);
        Assert.Empty(world.Calls);
        Assert.True(service.ReadStatus().Transferred);
        Assert.Equal("legacy_restored", (await service.RestoreAsync(world.ReleaseOwnDrives)).Code);
    }

    [Fact]
    public async Task Drive_left_by_an_earlier_run_of_this_connector_blocks_the_rollback_before_any_change()
    {
        var world = OldClientPc();
        var service = Service(world);
        await service.TransferAsync(new LegacyTransferRequest(true, Ready));
        world.Connections.Add(new SmbConnectionEntry("Q:", LegacyShare)); // CONNECT_TEMPORARY survives a restart
        world.Calls.Clear();

        var blocked = await service.RestoreAsync(world.ReleaseOwnDrives);

        Assert.Equal(LegacyTransferOutcome.Refused, blocked.Outcome);
        Assert.Equal("legacy_drive_mapped", blocked.Code);
        Assert.Contains("Q:", blocked.Message, StringComparison.Ordinal);
        Assert.Empty(world.Calls);
        Assert.True(service.ReadStatus().Transferred);
        Assert.Null(world.RunValue);
        world.Connections.RemoveAll(connection => connection.LocalName == "Q:");

        Assert.Equal("legacy_restored", (await service.RestoreAsync(world.ReleaseOwnDrives)).Code);
    }

    [Fact]
    public async Task Missing_old_client_without_saved_autostart_does_not_claim_the_autostart_was_restored()
    {
        var world = OldClientPc(running: false);
        world.RunValue = null;
        var service = Service(world);
        await service.TransferAsync(new LegacyTransferRequest(true, Ready));
        world.Executables.Remove(LegacyExe); // uninstalled meanwhile

        var result = await service.RestoreAsync(world.ReleaseOwnDrives);

        Assert.Equal("legacy_restored_not_started", result.Code);
        Assert.Contains("сохранённого автозапуска нет", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("восстановлен", result.Message, StringComparison.Ordinal);
        Assert.Null(world.RunValue);
        Assert.Empty(world.Files);
    }

    [Fact]
    public async Task Pid_reused_before_the_handle_is_opened_is_never_asked_to_close_or_killed()
    {
        var world = OldClientPc();
        world.ReusedProcessIds.Add(42);

        var result = await Service(world).TransferAsync(new LegacyTransferRequest(true, Ready, () => Task.FromResult(true)));

        Assert.Equal(LegacyTransferOutcome.Completed, result.Outcome);
        Assert.DoesNotContain(world.Calls, call => call.StartsWith("close:") || call.StartsWith("kill:"));
        Assert.Contains(world.Processes, process => process.ProcessId == 42);
    }

    [Fact]
    public async Task Old_client_ignoring_the_graceful_request_is_killed_only_after_confirmation()
    {
        var world = OldClientPc();
        world.IgnoresGracefulClose = true;
        var service = Service(world);

        var declined = await service.TransferAsync(new LegacyTransferRequest(true, Ready, () => Task.FromResult(false)));

        Assert.Equal(LegacyTransferOutcome.Aborted, declined.Outcome);
        Assert.Equal("legacy_close_declined", declined.Code);
        Assert.Equal(["close:42", "delete:legacy-connector-transfer.json"], world.Calls);
        Assert.True(OldRunValue().SameAs(world.RunValue));
        Assert.Contains(world.Processes, process => process.ProcessId == 42);
        Assert.Empty(world.Files);
        world.Calls.Clear();

        var forced = await service.TransferAsync(new LegacyTransferRequest(true, Ready, () => Task.FromResult(true)));

        Assert.Equal(LegacyTransferOutcome.Completed, forced.Outcome);
        Assert.Equal(["close:42", "kill:42"], world.Calls.Take(2));
        Assert.True(JsonDocument.Parse(world.Files[State]).RootElement.GetProperty("forcedClose").GetBoolean());
    }

    [Fact]
    public async Task This_process_its_executable_and_its_drive_mappings_are_never_touched()
    {
        var world = OldClientPc(running: false);
        world.RunValue = OldRunValue(OwnExe); // not the old client's autostart
        world.Executables.Add(OwnExe);
        world.Processes.Add(new LegacyProcessImage(7, 1, LegacyExe));  // this process, even from the old path
        world.Processes.Add(new LegacyProcessImage(43, 1, OwnExe));
        world.Processes.Add(new LegacyProcessImage(44, 2, LegacyExe)); // another Windows session

        var result = await Service(world).TransferAsync(new LegacyTransferRequest(true, Ready));

        Assert.Equal(LegacyTransferOutcome.Completed, result.Outcome);
        Assert.DoesNotContain(world.Calls, call => call.StartsWith("close:") || call.StartsWith("kill:"));
        Assert.DoesNotContain("run-delete", world.Calls);
        Assert.DoesNotContain(world.Calls, call => call.Contains("Z:") || call.Contains("100.64.1.20"));
        Assert.Equal(3, world.Processes.Count);
        Assert.Contains(world.Connections, connection => connection == new SmbConnectionEntry("Z:", LegacyShare));
    }

    [Theory]
    [InlineData(false, true, "ещё не подключён")]
    [InlineData(true, false, "доступ к папке BIM_Models")]
    public async Task Unready_connection_or_missing_grant_refuses_without_any_native_call(
        bool connectionReady, bool shareGranted, string reason)
    {
        var world = OldClientPc();

        var result = await Service(world).TransferAsync(
            new LegacyTransferRequest(true, new LegacyTransferReadiness(connectionReady, shareGranted, false)));

        Assert.Equal(LegacyTransferOutcome.Refused, result.Outcome);
        Assert.Equal("legacy_preconditions", result.Code);
        Assert.Contains(reason, result.Message, StringComparison.Ordinal);
        Assert.Empty(world.Calls);
        Assert.Empty(world.Files);
    }

    [Fact]
    public async Task Unconfirmed_transfer_and_unreadable_process_change_nothing()
    {
        var world = OldClientPc();
        world.Processes.Add(new LegacyProcessImage(45, 1, null));
        var service = Service(world);

        Assert.Equal("legacy_not_confirmed", (await service.TransferAsync(new LegacyTransferRequest(false, Ready))).Code);
        Assert.Equal("legacy_process_unknown", (await service.TransferAsync(new LegacyTransferRequest(true, Ready))).Code);

        Assert.Empty(world.Calls);
        Assert.Empty(world.Files);
        Assert.True(OldRunValue().SameAs(world.RunValue));
    }

    [Fact]
    public void Stopped_tunnel_on_a_tekla_pc_is_a_warning_read_without_touching_the_service()
    {
        var world = OldClientPc();
        world.TunnelRunning = false;
        var service = Service(world);

        var tekla = service.Preflight(new LegacyTransferReadiness(true, true, true));
        var noTekla = service.Preflight(new LegacyTransferReadiness(true, true, false));

        Assert.True(tekla.CanTransfer);
        Assert.Contains(tekla.Warnings, warning => warning.Contains("Tekla MultiUser", StringComparison.Ordinal));
        Assert.Empty(noTekla.Warnings);
        Assert.Equal([Tunnel], world.ServiceQueries);
        Assert.Empty(world.Calls);
    }

    [Fact]
    public async Task Old_client_started_by_hand_after_the_transfer_is_reported_once_per_process_and_left_running()
    {
        var world = OldClientPc();
        var service = Service(world);
        Assert.Empty(service.TakeNewlyStartedLegacyProcesses());
        await service.TransferAsync(new LegacyTransferRequest(true, Ready));
        world.Calls.Clear();
        Assert.Empty(service.TakeNewlyStartedLegacyProcesses());

        world.Processes.Add(new LegacyProcessImage(50, 1, LegacyExe));
        world.Processes.Add(new LegacyProcessImage(7, 1, LegacyExe));
        Assert.Equal([50], service.TakeNewlyStartedLegacyProcesses());
        Assert.Empty(service.TakeNewlyStartedLegacyProcesses());
        world.Processes.RemoveAll(process => process.ProcessId == 50);
        world.Processes.Add(new LegacyProcessImage(51, 1, LegacyExe));
        Assert.Equal([51], service.TakeNewlyStartedLegacyProcesses());

        Assert.Empty(world.Calls);
    }

    [Fact]
    public void Default_paths_are_per_user_platform_connector_state()
    {
        Assert.EndsWith(@"\Platform Connector\legacy-connector-transfer.json",
            LegacyConnectorTransferMarker.DefaultStatePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetDirectoryName(LegacyConnectorTransferMarker.DefaultPath),
            Path.GetDirectoryName(LegacyConnectorTransferMarker.DefaultStatePath));
    }

    // v1.0.31 AutoStartService writes REG_SZ "\"<exe>\""; a trailing double NUL proves the restore is byte-exact.
    private static LegacyRunValue OldRunValue(string executable = LegacyExe) =>
        new(LegacyRunValue.RegSz, Encoding.Unicode.GetBytes($"\"{executable}\"\0\0"));

    private static FakeWorld OldClientPc(bool running = true)
    {
        var world = new FakeWorld { RunValue = OldRunValue() };
        if (running) world.Processes.Add(new LegacyProcessImage(42, 1, LegacyExe));
        world.Connections.Add(new SmbConnectionEntry(null, LegacyIpc));
        world.Connections.Add(new SmbConnectionEntry(null, LegacyShare));
        world.Connections.Add(new SmbConnectionEntry("Z:", LegacyShare));      // this connector's drive
        world.Connections.Add(new SmbConnectionEntry(null, OverlayShare));     // another server name
        world.Connections.Add(new SmbConnectionEntry("Y:", OverlayShare));
        return world;
    }

    private static LegacyConnectorTransferService Service(FakeWorld world) =>
        new(world, world, new FixedTime(), Marker, State, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    private sealed class FakeProcess(int processId) : ILegacyProcess
    {
        public int ProcessId { get; } = processId;
        public void Dispose() { }
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    }

    /// <summary>One fake Windows user session: the K4 read-only probes and the K5 mutating ports.</summary>
    private sealed class FakeWorld : ILegacyConnectorProbes, ILegacyTransferPorts
    {
        private int _nextProcessId = 100;

        public int OwnProcessId => 7;
        public int OwnSessionId => 1;
        public string? OwnProcessPath => OwnExe;
        public string LegacyDefaultExecutable => LegacyExe;
        public LegacyRunValue? RunValue { get; set; }
        public HashSet<string> Executables { get; } = new(StringComparer.OrdinalIgnoreCase) { LegacyExe };
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<LegacyProcessImage> Processes { get; } = [];
        public List<SmbConnectionEntry> Connections { get; } = [];
        /// <summary>Open files visible to the read-only NetUseEnum precheck.</summary>
        public HashSet<string> OpenFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Files opened after the precheck: only the cancel itself sees them.</summary>
        public HashSet<string> LateOpenFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IgnoresGracefulClose { get; set; }
        public bool TunnelRunning { get; set; } = true;
        public int FailRunWrites { get; set; }
        public List<string> Calls { get; } = [];
        public List<string> ServiceQueries { get; } = [];

        public string? ReadAutoStartCommand() => RunValue?.Command;
        public bool FileExists(string path) => Executables.Contains(path) || Files.ContainsKey(path);
        public IReadOnlyList<LegacyProcessImage> ReadProcessImages(string processName)
        {
            Assert.Equal("Connector.Desktop", processName);
            return Processes.ToArray();
        }
        public bool IsServiceRunning(string serviceName)
        {
            ServiceQueries.Add(serviceName);
            return TunnelRunning;
        }
        public IReadOnlyList<SmbConnectionEntry> EnumerateSmbConnections() => Connections.ToArray();
        public bool TransferMarkerExists() => Files.ContainsKey(Marker);

        public LegacyRunValue? ReadAutoStartValue() => RunValue;
        public void WriteAutoStartValue(LegacyRunValue value)
        {
            Calls.Add("run-write");
            if (FailRunWrites > 0)
            {
                FailRunWrites--;
                throw new UnauthorizedAccessException("registry");
            }
            RunValue = new LegacyRunValue(value.Kind, value.Data.ToArray());
        }
        public void DeleteAutoStartValue()
        {
            Calls.Add("run-delete");
            RunValue = null;
        }
        /// <summary>PIDs that already belong to another image when the handle is opened.</summary>
        public HashSet<int> ReusedProcessIds { get; } = [];
        public ILegacyProcess? OpenProcess(LegacyProcessImage image) =>
            !ReusedProcessIds.Contains(image.ProcessId) && Processes.Any(process => process == image)
                ? new FakeProcess(image.ProcessId) : null;
        public bool RequestGracefulClose(ILegacyProcess process)
        {
            Calls.Add($"close:{process.ProcessId}");
            if (!IgnoresGracefulClose) Processes.RemoveAll(running => running.ProcessId == process.ProcessId);
            return true;
        }
        public Task<bool> WaitForExitAsync(ILegacyProcess process, TimeSpan timeout) =>
            Task.FromResult(Processes.All(running => running.ProcessId != process.ProcessId));
        public void Kill(ILegacyProcess process)
        {
            Calls.Add($"kill:{process.ProcessId}");
            Processes.RemoveAll(running => running.ProcessId == process.ProcessId);
        }
        public IReadOnlyList<LegacySmbUse> EnumerateSmbUsage() => Connections
            .Select(connection => new LegacySmbUse(connection.LocalName, connection.RemoteName,
                OpenFiles.Contains(connection.RemoteName) ? 1u : 0u))
            .ToArray();
        /// <summary>The composition's CommonSmbAccessService.ReleaseOwnedLegacyShareAsync: our own Z: only.</summary>
        public Task ReleaseOwnDrives(CancellationToken cancellationToken)
        {
            Connections.RemoveAll(connection => connection.LocalName == "Z:");
            return Task.CompletedTask;
        }
        public void StartProcess(string executable)
        {
            Calls.Add($"start:{executable}");
            Processes.Add(new LegacyProcessImage(_nextProcessId++, OwnSessionId, executable));
        }
        public int CancelConnection(string remoteName)
        {
            Calls.Add($"smb-cancel:{remoteName}");
            if (OpenFiles.Contains(remoteName) || LateOpenFiles.Contains(remoteName)) return 2401;
            Connections.RemoveAll(connection => connection.LocalName is null &&
                string.Equals(connection.RemoteName, remoteName, StringComparison.OrdinalIgnoreCase));
            return 0;
        }
        public string? ReadFile(string path) => Files.GetValueOrDefault(path);
        public void WriteFileAtomic(string path, string content)
        {
            if (string.Equals(path, Marker, StringComparison.OrdinalIgnoreCase)) Calls.Add("write:marker");
            Files[path] = content;
        }
        public void DeleteFile(string path)
        {
            Calls.Add($"delete:{Path.GetFileName(path)}");
            Files.Remove(path);
        }
    }
}
