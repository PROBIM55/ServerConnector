using System.ComponentModel;
using Connector.Network;
using Connector.SmbAccess;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class LegacyConnectorDetectorTests
{
    private const string LegacyExe = @"C:\Users\u\AppData\Local\Structura Connector\Connector.Desktop.exe";
    private const string CustomLegacyExe = @"D:\Apps\Old Connector\Connector.Desktop.exe";
    private const string OwnExe = @"C:\Users\u\AppData\Local\Structura.Connector.Desktop\current\Connector.Desktop.exe";
    private const string NewInstallExe = @"C:\Program Files\Platform Connector\Connector.Desktop.exe";

    [Fact]
    public void Clean_pc_is_not_in_neighbour_mode()
    {
        var snapshot = Detect(new FakeProbes());

        Assert.Equal(LegacySignal.Absent, snapshot.Alive);
        Assert.False(snapshot.NeighbourModeActive);
        Assert.Empty(snapshot.UnknownReasons);
    }

    [Theory]
    [InlineData("\"" + LegacyExe + "\"", true, false, true)]
    [InlineData(LegacyExe + " --minimized", true, false, true)]
    [InlineData("\"" + LegacyExe + "\"", true, true, false)]   // transfer marker turns neighbour mode off
    [InlineData("\"" + LegacyExe + "\"", false, false, false)] // stale value: the old exe is gone
    [InlineData("\"" + OwnExe + "\"", true, false, false)]     // never our own executable
    public void Old_autostart_counts_only_without_transfer_marker(
        string command, bool exeExists, bool transferred, bool neighbour)
    {
        var probes = new FakeProbes { AutoStartCommand = command, Transferred = transferred };
        if (exeExists) probes.Files.Add(LegacyExe);
        if (exeExists) probes.Files.Add(OwnExe);

        var snapshot = Detect(probes);

        Assert.Equal(neighbour, snapshot.NeighbourModeActive);
        Assert.Equal(transferred, snapshot.IsTransferred);
    }

    [Fact]
    public void Old_process_from_default_perUser_directory_is_alive()
    {
        var snapshot = Detect(new FakeProbes { Processes = { new(42, 1, LegacyExe) } });

        Assert.Equal(LegacySignal.Present, snapshot.Process);
        Assert.True(snapshot.NeighbourModeActive);
    }

    [Fact]
    public void Old_process_from_custom_directory_is_recognised_through_its_autostart_value()
    {
        var probes = new FakeProbes { AutoStartCommand = $"\"{CustomLegacyExe}\"", Processes = { new(42, 1, CustomLegacyExe) } };
        probes.Files.Add(CustomLegacyExe);

        Assert.Equal(LegacySignal.Present, Detect(probes).Process);
    }

    [Fact]
    public void Unified_shell_with_the_same_image_name_is_never_flagged()
    {
        var probes = new FakeProbes
        {
            Processes = { new(7, 1, LegacyExe), new(43, 1, OwnExe), new(44, 1, NewInstallExe) },
        };

        var snapshot = Detect(probes);

        // Pid 7 is this process (even if started from the old path), the others are new installs.
        Assert.Equal(LegacySignal.Absent, snapshot.Process);
        Assert.False(snapshot.NeighbourModeActive);
    }

    [Fact]
    public void Unreadable_candidate_process_is_unknown_and_fails_closed()
    {
        var snapshot = Detect(new FakeProbes { Processes = { new(42, 1, null) } });

        Assert.Equal(LegacySignal.Unknown, snapshot.Process);
        Assert.Equal(LegacySignal.Unknown, snapshot.Alive);
        Assert.True(snapshot.NeighbourModeActive);
    }

    [Theory]
    [InlineData(null, @"\\62.113.36.107\BIM_Models", true)]  // v1.0.31: deviceless net use
    [InlineData(null, @"\\62.113.36.107\IPC$", true)]
    [InlineData("P:", @"\\62.113.36.107\BIM_Models", false)] // drive letters go to smb_mapping_not_owned
    [InlineData("Y:", @"\\100.64.1.20\Company_A", false)]    // overlay share, another server name
    [InlineData(null, @"\\100.64.1.20\Company_A", false)]
    [InlineData(null, @"\\62.113.36.1070\BIM_Models", false)]
    public void Only_deviceless_connection_to_legacy_host_is_old_client_evidence(
        string? local, string remote, bool alive)
    {
        var snapshot = Detect(new FakeProbes { Connections = { new SmbConnectionEntry(local, remote) } });

        Assert.Equal(alive ? LegacySignal.Present : LegacySignal.Absent, snapshot.ForeignSmbConnection);
        Assert.Equal(alive, snapshot.NeighbourModeActive);
    }

    [Fact]
    public void Own_leftover_drive_mapping_after_a_crash_does_not_blame_the_old_client()
    {
        // A killed earlier process of this connector leaves its lettered NetUseAdd mapping behind.
        var snapshot = Detect(new FakeProbes
        {
            Connections = { new SmbConnectionEntry("Z:", LegacyEncryptedSmbPolicy.ShareUnc) },
        });

        Assert.Equal(LegacySignal.Absent, snapshot.ForeignSmbConnection);
        Assert.False(snapshot.NeighbourModeActive);
    }

    [Fact]
    public void Processes_of_other_windows_sessions_are_ignored_before_their_path_is_judged()
    {
        var snapshot = Detect(new FakeProbes
        {
            Processes = { new(42, 2, null), new(43, 3, LegacyExe) }, // RDS / fast user switching
        });

        Assert.Equal(LegacySignal.Absent, snapshot.Process);
        Assert.False(snapshot.NeighbourModeActive);
        Assert.Empty(snapshot.UnknownReasons);
    }

    [Fact]
    public void Process_with_unreadable_session_is_still_judged_by_path() =>
        Assert.Equal(LegacySignal.Present, Detect(new FakeProbes { Processes = { new(42, null, LegacyExe) } }).Process);

    [Fact]
    public void Foreign_connection_or_process_keeps_alive_but_transfer_marker_disables_neighbour_mode()
    {
        var probes = new FakeProbes
        {
            Transferred = true,
            Processes = { new(42, 1, LegacyExe) },
            Connections = { new SmbConnectionEntry(null, LegacyEncryptedSmbPolicy.ShareUnc) },
        };

        var snapshot = Detect(probes);

        Assert.Equal(LegacySignal.Present, snapshot.Alive);
        Assert.False(snapshot.NeighbourModeActive);
    }

    [Fact]
    public void Running_tunnel_service_alone_is_diagnostic_only()
    {
        var snapshot = Detect(new FakeProbes { TunnelRunning = true });

        Assert.Equal(LegacySignal.Present, snapshot.TunnelService);
        Assert.False(snapshot.NeighbourModeActive);
        Assert.Contains("tunnel=Present", snapshot.ToSafeDiagnostic(), StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_failures_are_unknown_with_exception_type_only()
    {
        var probes = new FakeProbes
        {
            ConnectionFailure = new Win32Exception(5, @"secret \\62.113.36.107 user"),
            ServiceFailure = new InvalidOperationException("account bim_user"),
        };

        var snapshot = Detect(probes);

        Assert.Equal(LegacySignal.Unknown, snapshot.ForeignSmbConnection);
        Assert.Equal(LegacySignal.Unknown, snapshot.TunnelService);
        Assert.True(snapshot.NeighbourModeActive);
        var diagnostic = snapshot.ToSafeDiagnostic();
        Assert.Contains("smb:Win32Exception", diagnostic, StringComparison.Ordinal);
        Assert.Contains("tunnel:InvalidOperationException", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("bim_user", diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"C:\\A B\\Connector.Desktop.exe\" /x", @"C:\A B\Connector.Desktop.exe")]
    [InlineData(@"C:\A\Connector.Desktop.exe /x", @"C:\A\Connector.Desktop.exe")]
    [InlineData("Connector.Desktop.exe", null)]
    [InlineData("\"", null)]
    public void Autostart_command_parsing(string command, string? expected) =>
        Assert.Equal(expected, WindowsLegacyConnectorDetector.ParseExecutable(command));

    [Fact]
    public void Windows_detector_reads_this_pc_without_throwing()
    {
        var snapshot = new WindowsLegacyConnectorDetector().Detect();

        Assert.NotNull(snapshot.ToSafeDiagnostic());
        Assert.DoesNotContain(snapshot.UnknownReasons, reason => reason.StartsWith("detector:", StringComparison.Ordinal));
    }

    [Fact]
    public void Transfer_marker_path_is_per_user_platform_connector_state() =>
        Assert.EndsWith(@"\Platform Connector\legacy-connector-transferred.marker",
            LegacyConnectorTransferMarker.DefaultPath, StringComparison.OrdinalIgnoreCase);

    private static LegacyConnectorSnapshot Detect(FakeProbes probes) =>
        new WindowsLegacyConnectorDetector(probes).Detect();

    private sealed class FakeProbes : ILegacyConnectorProbes
    {
        public int OwnProcessId => 7;
        public int OwnSessionId => 1;
        public string? OwnProcessPath => OwnExe;
        public string LegacyDefaultExecutable => LegacyExe;
        public string? AutoStartCommand { get; init; }
        public bool Transferred { get; init; }
        public bool TunnelRunning { get; init; }
        public Exception? ConnectionFailure { get; init; }
        public Exception? ServiceFailure { get; init; }
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<LegacyProcessImage> Processes { get; } = [];
        public List<SmbConnectionEntry> Connections { get; } = [];

        public string? ReadAutoStartCommand() => AutoStartCommand;
        public bool FileExists(string path) => Files.Contains(path);
        public IReadOnlyList<LegacyProcessImage> ReadProcessImages(string processName)
        {
            Assert.Equal("Connector.Desktop", processName);
            return Processes;
        }
        public bool IsServiceRunning(string serviceName)
        {
            Assert.Equal("AmneziaWGTunnel$structura-vpn", serviceName);
            return ServiceFailure is null ? TunnelRunning : throw ServiceFailure;
        }
        public IReadOnlyList<SmbConnectionEntry> EnumerateSmbConnections() =>
            ConnectionFailure is null ? Connections : throw ConnectionFailure;
        public bool TransferMarkerExists() => Transferred;
    }
}
