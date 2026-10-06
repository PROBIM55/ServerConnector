using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Connector.Upgrade.Velopack;
using Xunit;

namespace Connector.Upgrade.Velopack.Tests;

public sealed class VelopackUnifiedApplicationServiceTests
{
    private const string CurrentSid = "S-1-5-21-100-200-300-1001";

    [Fact]
    public async Task RejectsForeignPreExistingPackIdWithoutStartingSetup()
    {
        using var fixture = new Fixture();
        fixture.Probe.Enqueue(fixture.ExactIdentity());

        await Assert.ThrowsAsync<VelopackInstallInvariantException>(() =>
            fixture.Service.InstallAsync(fixture.Pin, fixture.Lease, CurrentSid).AsTask());

        Assert.Empty(fixture.Runner.Requests);
    }

    [Fact]
    public async Task RejectsWrongSidAndRechecksActualSetupHash()
    {
        using var wrongSid = new Fixture();
        await Assert.ThrowsAsync<VelopackInstallInvariantException>(() =>
            wrongSid.Service.InstallAsync(wrongSid.Pin, wrongSid.Lease, "S-1-5-21-9-9-9-1002").AsTask());
        Assert.Empty(wrongSid.Runner.Requests);

        using var wrongHash = new Fixture(setupBytes: "tampered", pinnedBytes: "expected");
        await Assert.ThrowsAsync<VelopackInstallInvariantException>(() =>
            wrongHash.Service.InstallAsync(wrongHash.Pin, wrongHash.Lease, CurrentSid).AsTask());
        Assert.Empty(wrongHash.Runner.Requests);
        Assert.Equal(0, wrongHash.Probe.InspectionCount);
    }

    [Fact]
    public async Task InstallsPinnedSetupSilentlyAndIssuesExactReceipt()
    {
        using var fixture = new Fixture();
        fixture.Probe.Enqueue(fixture.Absent());
        var exact = fixture.ExactIdentity();
        fixture.Probe.Enqueue(exact);
        fixture.Runner.Enqueue(new VelopackProcessResult(VelopackProcessCompletion.Exited, 0));

        var result = await fixture.Service.InstallAsync(fixture.Pin, fixture.Lease, CurrentSid);

        Assert.Equal(VelopackMutationDisposition.Succeeded, result.Disposition);
        Assert.NotNull(result.Receipt);
        Assert.Equal(exact.Identity, result.Receipt!.InstalledIdentity);
        var request = Assert.Single(fixture.Runner.Requests);
        Assert.Equal(fixture.SetupPath, request.FileName);
        Assert.Equal(["--silent"], request.Arguments);
        Assert.Equal(1, fixture.Lease.LaunchPathVerifications);
    }

    [Fact]
    public async Task SignedManifestHashSetupMayInstallWithoutAuthenticodeEvidence()
    {
        using var fixture = new Fixture();
        var pin = fixture.Pin with { TrustMode = VelopackSetupTrustMode.SignedManifestHash };
        await using var lease = new FakeLease(fixture.SetupPath, pin, evidence: null);
        fixture.Probe.Enqueue(fixture.Absent());
        fixture.Probe.Enqueue(fixture.ExactIdentity());
        fixture.Runner.Enqueue(new VelopackProcessResult(VelopackProcessCompletion.Exited, 0));

        var result = await fixture.Service.InstallAsync(pin, lease, CurrentSid);

        Assert.Equal(VelopackMutationDisposition.Succeeded, result.Disposition);
        Assert.Single(fixture.Runner.Requests);
    }

    [Fact]
    public async Task RejectsTrustModeMismatchOrUnexpectedSignerEvidenceBeforeRunningSetup()
    {
        using var fixture = new Fixture();
        var pin = fixture.Pin with { TrustMode = VelopackSetupTrustMode.SignedManifestHash };
        await Assert.ThrowsAsync<VelopackInstallInvariantException>(() =>
            fixture.Service.InstallAsync(pin, fixture.Lease, CurrentSid).AsTask());
        await using var unexpectedEvidence = new FakeLease(fixture.SetupPath, pin, "unexpected-signer");
        await Assert.ThrowsAsync<VelopackInstallInvariantException>(() =>
            fixture.Service.InstallAsync(pin, unexpectedEvidence, CurrentSid).AsTask());
        Assert.Empty(fixture.Runner.Requests);
    }

    [Fact]
    public async Task RefusesChangedLaunchPathBeforeStartingSetup()
    {
        using var fixture = new Fixture();
        fixture.Probe.Enqueue(fixture.Absent());
        fixture.Lease.RejectLaunchPath = true;

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Service.InstallAsync(fixture.Pin, fixture.Lease, CurrentSid));

        Assert.Equal(1, fixture.Lease.LaunchPathVerifications);
        Assert.Empty(fixture.Runner.Requests);
    }

    [Fact]
    public async Task RollbackUsesInstalledUpdateExeOnlyForReceiptIdentity()
    {
        using var fixture = new Fixture();
        var exact = fixture.ExactIdentity();
        fixture.Probe.Enqueue(fixture.Absent());
        fixture.Probe.Enqueue(exact);
        fixture.Probe.Enqueue(exact);
        fixture.Probe.Enqueue(fixture.Absent());
        fixture.Runner.Enqueue(new VelopackProcessResult(VelopackProcessCompletion.Exited, 0));
        fixture.Runner.Enqueue(new VelopackProcessResult(VelopackProcessCompletion.Exited, 0));

        var install = await fixture.Service.InstallAsync(fixture.Pin, fixture.Lease, CurrentSid);
        var rollback = await fixture.Service.RollbackAsync(install.Receipt!);

        Assert.Equal(VelopackMutationDisposition.Succeeded, rollback.Disposition);
        Assert.Equal(2, fixture.Runner.Requests.Count);
        var request = fixture.Runner.Requests[1];
        Assert.Equal(Path.Combine(fixture.InstallRoot, "Update.exe"), request.FileName);
        Assert.Equal(["uninstall", "--silent"], request.Arguments);
        Assert.DoesNotContain(fixture.Runner.Requests, item =>
            item.Arguments.Any(argument => argument.Contains("msiexec", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task RollbackRefusesChangedCurrentIdentity()
    {
        using var fixture = new Fixture();
        var exact = fixture.ExactIdentity();
        fixture.Probe.Enqueue(fixture.Absent());
        fixture.Probe.Enqueue(exact);
        fixture.Runner.Enqueue(new VelopackProcessResult(VelopackProcessCompletion.Exited, 0));
        var install = await fixture.Service.InstallAsync(fixture.Pin, fixture.Lease, CurrentSid);
        fixture.Probe.Enqueue(exact with
        {
            Identity = exact.Identity! with { UpdateExecutableSha256 = new string('f', 64) },
        });

        await Assert.ThrowsAsync<VelopackInstallInvariantException>(() =>
            fixture.Service.RollbackAsync(install.Receipt!).AsTask());

        Assert.Single(fixture.Runner.Requests);
    }

    [Fact]
    public async Task Exit3010SucceedsOnlyWithExactProbe()
    {
        using var fixture = new Fixture();
        fixture.Probe.Enqueue(fixture.Absent());
        fixture.Probe.Enqueue(fixture.ExactIdentity());
        fixture.Runner.Enqueue(new VelopackProcessResult(VelopackProcessCompletion.Exited, 3010));

        var result = await fixture.Service.InstallAsync(fixture.Pin, fixture.Lease, CurrentSid);

        Assert.Equal(VelopackMutationDisposition.SucceededRebootRequired, result.Disposition);
        Assert.NotNull(result.Receipt);
    }

    [Theory]
    [InlineData(VelopackProcessCompletion.TimedOut)]
    [InlineData(VelopackProcessCompletion.Cancelled)]
    [InlineData(VelopackProcessCompletion.StartFailed)]
    public async Task UncertainCompletionAlwaysProbesAndRequiresManualRecovery(
        VelopackProcessCompletion completion)
    {
        using var fixture = new Fixture();
        fixture.Probe.Enqueue(fixture.Absent());
        fixture.Probe.Enqueue(fixture.ExactIdentity());
        fixture.Runner.Enqueue(new VelopackProcessResult(completion, Failure: "uncertain"));

        var result = await fixture.Service.InstallAsync(fixture.Pin, fixture.Lease, CurrentSid);

        Assert.Equal(VelopackMutationDisposition.ManualRecoveryRequired, result.Disposition);
        Assert.Null(result.Receipt);
        Assert.Equal(2, fixture.Probe.InspectionCount);
        Assert.Single(fixture.Runner.Requests);
    }

    [Fact]
    public async Task UnexpectedExitAlsoRequiresManualRecoveryAfterProbe()
    {
        using var fixture = new Fixture();
        fixture.Probe.Enqueue(fixture.Absent());
        fixture.Probe.Enqueue(fixture.ExactIdentity());
        fixture.Runner.Enqueue(new VelopackProcessResult(VelopackProcessCompletion.Exited, 5));

        var result = await fixture.Service.InstallAsync(fixture.Pin, fixture.Lease, CurrentSid);

        Assert.Equal(VelopackMutationDisposition.ManualRecoveryRequired, result.Disposition);
        Assert.Equal(VelopackInstallPresence.ExactInstalled, result.StatusProbe!.Presence);
    }

    [Fact]
    public void SystemProbeVerifiesSqVersionExecutablesAndCurrentUserRegistration()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.InstallRoot, "current"));
        var sqVersionPath = Path.Combine(fixture.InstallRoot, "current", "sq.version");
        File.WriteAllText(sqVersionPath, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd">
              <metadata>
                <id>{fixture.Pin.PackId}</id>
                <version>{fixture.Pin.Version}</version>
                <mainExe>{UnifiedVelopackApplication.MainExecutable}</mainExe>
              </metadata>
            </package>
            """);
        File.WriteAllText(Path.Combine(fixture.InstallRoot, "current", UnifiedVelopackApplication.MainExecutable), "app");
        File.WriteAllText(Path.Combine(fixture.InstallRoot, "Update.exe"), "update");
        File.WriteAllText(Path.Combine(fixture.InstallRoot, UnifiedVelopackApplication.MainExecutable), "launcher");
        var registration = fixture.Registration();
        var probe = new SystemVelopackInstallProbe(
            CurrentSid,
            fixture.LocalApplicationData,
            new FixedRegistrationReader(registration));

        var observation = probe.Inspect(fixture.Pin, CurrentSid);

        Assert.Equal(VelopackInstallPresence.ExactInstalled, observation.Presence);
        Assert.Equal(fixture.Pin.Version, observation.Identity!.Version);
        Assert.Equal(HashFile(sqVersionPath), observation.Identity.SqVersionSha256);
        Assert.Equal(registration, observation.Identity.Registration);
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "velopack-service-tests", Guid.NewGuid().ToString("N"));

        public Fixture(string setupBytes = "expected", string? pinnedBytes = null)
        {
            Directory.CreateDirectory(_root);
            LocalApplicationData = Path.Combine(_root, "LocalAppData");
            Directory.CreateDirectory(LocalApplicationData);
            SetupPath = Path.Combine(_root, "Structura.Connector.Desktop-preview-Setup.exe");
            File.WriteAllText(SetupPath, setupBytes);
            var pinned = pinnedBytes ?? setupBytes;
            Pin = new VelopackSetupPin(
                UnifiedVelopackApplication.PackId,
                "1.1.0-preview.12",
                pinned.Length,
                Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pinned))).ToLowerInvariant());
            Lease = new FakeLease(SetupPath, Pin);
            Probe = new FakeProbe();
            Runner = new FakeRunner();
            Service = new VelopackUnifiedApplicationService(
                Probe,
                Runner,
                CurrentSid,
                LocalApplicationData,
                TimeSpan.FromMinutes(5));
        }

        public string LocalApplicationData { get; }
        public string InstallRoot => Path.Combine(LocalApplicationData, UnifiedVelopackApplication.PackId);
        public string SetupPath { get; }
        public VelopackSetupPin Pin { get; }
        public FakeLease Lease { get; }
        public FakeProbe Probe { get; }
        public FakeRunner Runner { get; }
        public VelopackUnifiedApplicationService Service { get; }

        public VelopackInstallObservation Absent() => new(
            VelopackInstallPresence.Absent,
            null,
            Guid.NewGuid().ToString("N"),
            "absent");

        public VelopackInstallObservation ExactIdentity()
        {
            var identity = new VelopackInstalledIdentity(
                CurrentSid,
                Pin.PackId,
                Pin.Version,
                InstallRoot,
                new string('1', 64),
                new string('2', 64),
                new string('3', 64),
                new string('4', 64),
                Registration());
            return new VelopackInstallObservation(
                VelopackInstallPresence.ExactInstalled,
                identity,
                Guid.NewGuid().ToString("N"),
                "exact");
        }

        public VelopackRegistration Registration()
        {
            var update = Path.Combine(InstallRoot, "Update.exe");
            return new VelopackRegistration(
                CurrentSid,
                $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{Pin.PackId}",
                InstallRoot,
                Pin.PackId,
                "1.1.0",
                $"\"{update}\" --uninstall",
                $"\"{update}\" --uninstall --silent");
        }

        public void Dispose()
        {
            Lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeProbe : IVelopackInstallProbe
    {
        private readonly Queue<VelopackInstallObservation> _observations = new();
        public int InspectionCount { get; private set; }

        public void Enqueue(VelopackInstallObservation observation) => _observations.Enqueue(observation);

        public VelopackInstallObservation Inspect(VelopackSetupPin pin, string targetUserSid)
        {
            InspectionCount++;
            return _observations.Dequeue();
        }
    }

    private sealed class FakeRunner : IVelopackProcessRunner
    {
        private readonly Queue<VelopackProcessResult> _results = new();
        public List<VelopackProcessRequest> Requests { get; } = [];

        public void Enqueue(VelopackProcessResult result) => _results.Enqueue(result);

        public ValueTask<VelopackProcessResult> RunAsync(
            VelopackProcessRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(_results.Dequeue());
        }
    }

    private sealed class FakeLease : IVerifiedVelopackSetupLease
    {
        private SafeFileHandle? _handle = new(new IntPtr(1234), ownsHandle: false);

        public FakeLease(string path, VelopackSetupPin pin, string? evidence = "fixture-signature")
        {
            StagedPath = path;
            Inspection = new VelopackSetupInspection(
                pin.PackId,
                pin.Version,
                pin.SizeBytes,
                pin.Sha256,
                SignatureVerified: true,
                SignatureEvidenceId: evidence,
                TrustMode: pin.TrustMode);
        }

        public string HandleId { get; } = "fixture-setup-lease";
        public string StagedPath { get; }
        public SafeFileHandle ContentHandle =>
            _handle ?? throw new ObjectDisposedException(nameof(FakeLease));
        public VelopackSetupLeaseProtection Protection =>
            VelopackSetupLeaseProtection.ProtectedInstallerStaging;
        public VelopackSetupInspection Inspection { get; }
        public bool RejectLaunchPath { get; set; }
        public int LaunchPathVerifications { get; private set; }

        public void VerifyLaunchPath()
        {
            LaunchPathVerifications++;
            if (RejectLaunchPath) throw new InvalidDataException("Fixture launch path changed.");
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _handle, null)?.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedRegistrationReader(VelopackRegistration registration)
        : IVelopackRegistrationReader
    {
        public VelopackRegistration? Read(string packId, string currentUserSid) => registration;
    }
}
