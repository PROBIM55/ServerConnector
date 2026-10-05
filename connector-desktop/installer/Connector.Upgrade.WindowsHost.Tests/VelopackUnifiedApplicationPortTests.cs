using System.Security.Cryptography;
using System.Text.Json;
using Connector.Upgrade.Velopack;
using Connector.Upgrade.VelopackPayload;
using Connector.Upgrade.WindowsHost;
using Microsoft.Win32.SafeHandles;

namespace Connector.Upgrade.WindowsHost.Tests;

public sealed class VelopackUnifiedApplicationPortTests
{
    [Fact]
    public async Task Install_VerifiesExactLaunchPathAtRunnerBoundary_AndKeepsTypedReceiptForRollback()
    {
        var root = Path.Combine(Path.GetTempPath(), "velopack-host-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var calls = new List<string>();
            var setupPath = Path.Combine(root, "Setup.exe");
            await File.WriteAllBytesAsync(setupPath, [1, 2, 3, 4]);
            var bytes = await File.ReadAllBytesAsync(setupPath);
            var pin = new VelopackSetupPin(
                UnifiedVelopackApplication.PackId,
                "1.0.0",
                bytes.Length,
                Convert.ToHexString(SHA256.HashData(bytes)));
            var localAppData = Path.Combine(root, "LocalAppData");
            Directory.CreateDirectory(localAppData);
            var probe = new MutableVelopackProbe(localAppData, calls);
            var runner = new MutableVelopackRunner(probe, calls);
            var lease = new RecordingSetupLease(setupPath, pin, calls);
            var port = new VelopackUnifiedApplicationPort(
                new SingleSetupStager(lease),
                pin,
                probe,
                runner,
                HostFixture.Sid,
                localAppData,
                TimeSpan.FromSeconds(10));

            var receipt = await port.InstallAsync(CancellationToken.None);

            Assert.Equal(new[] { "probe:absent", "lease:verify-launch", "process:setup", "probe:installed" }, calls);
            var metadata = port.GetRecoveryMetadata(receipt);
            Assert.NotNull(metadata);
            Assert.Equal(receipt.OperationId, metadata!.OperationId);
            Assert.True(await port.IsExactInstallReadyAsync(CancellationToken.None));

            var restartedPort = new VelopackUnifiedApplicationPort(
                new SingleSetupStager(lease),
                pin,
                probe,
                runner,
                HostFixture.Sid,
                localAppData,
                TimeSpan.FromSeconds(10));
            var persistedReceipt = JsonSerializer.Deserialize<Connector.Upgrade.Core.UnifiedApplicationReceipt>(
                JsonSerializer.Serialize(receipt))!;
            await restartedPort.RemoveAsync(persistedReceipt, CancellationToken.None);
            Assert.Equal("process:rollback", calls[^2]);
            Assert.Equal("probe:absent", calls[^1]);

            var tamperedReceipt = persistedReceipt with { RecoveryMetadataJson = "{}" };
            var priorCallCount = calls.Count;
            await Assert.ThrowsAsync<WindowsHostManualRecoveryRequiredException>(async () =>
                await restartedPort.RemoveAsync(tamperedReceipt, CancellationToken.None));
            Assert.Equal(priorCallCount, calls.Count);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

internal sealed class SingleSetupStager(IVerifiedVelopackSetupLease lease) : IWindowsVelopackSetupStager
{
    public ValueTask<IVerifiedVelopackSetupLease> StageAndVerifyAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(lease);

    public ValueTask<IVerifiedVelopackSetupLease> ReacquireAsync(
        ProtectedVelopackSetupReceipt receipt,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed class RecordingSetupLease : IWindowsVerifiedVelopackSetupLease
{
    private readonly List<string> _calls;
    private FileStream? _stream;

    public RecordingSetupLease(string path, VelopackSetupPin pin, List<string> calls)
    {
        _calls = calls;
        StagedPath = path;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Inspection = new VelopackSetupInspection(
            pin.PackId,
            pin.Version,
            pin.SizeBytes,
            pin.Sha256,
            SignatureVerified: true,
            SignatureEvidenceId: "test-signature");
    }

    public string HandleId => "setup-lease";
    public string StagedPath { get; }
    public SafeFileHandle ContentHandle => _stream?.SafeFileHandle ?? throw new ObjectDisposedException(nameof(RecordingSetupLease));
    public VelopackSetupLeaseProtection Protection => VelopackSetupLeaseProtection.ProtectedInstallerStaging;
    public VelopackSetupInspection Inspection { get; }
    public NtfsFileIdentity FileIdentity => new(1, 1);
    public void VerifyLaunchPath() => _calls.Add("lease:verify-launch");
    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _stream = null;
        return ValueTask.CompletedTask;
    }
}

internal sealed class MutableVelopackProbe(string localAppData, List<string> calls) : IVelopackInstallProbe
{
    public bool Installed { get; set; }

    public VelopackInstallObservation Inspect(VelopackSetupPin pin, string targetUserSid)
    {
        calls.Add(Installed ? "probe:installed" : "probe:absent");
        if (!Installed)
            return new VelopackInstallObservation(VelopackInstallPresence.Absent, null, "absent", "absent");
        var root = Path.Combine(localAppData, pin.PackId);
        var identity = new VelopackInstalledIdentity(
            targetUserSid,
            pin.PackId,
            pin.Version,
            root,
            new string('A', 64),
            new string('B', 64),
            new string('C', 64),
            new string('D', 64),
            new VelopackRegistration(targetUserSid, "key", root, "publisher", pin.Version, "uninstall", "quiet"));
        return new VelopackInstallObservation(VelopackInstallPresence.ExactInstalled, identity, "installed", "installed");
    }
}

internal sealed class MutableVelopackRunner(MutableVelopackProbe probe, List<string> calls) : IVelopackProcessRunner
{
    public ValueTask<VelopackProcessResult> RunAsync(VelopackProcessRequest request, CancellationToken cancellationToken)
    {
        var setup = string.Equals(Path.GetFileName(request.FileName), "Setup.exe", StringComparison.OrdinalIgnoreCase);
        calls.Add(setup ? "process:setup" : "process:rollback");
        probe.Installed = setup;
        return ValueTask.FromResult(new VelopackProcessResult(VelopackProcessCompletion.Exited, 0));
    }
}
