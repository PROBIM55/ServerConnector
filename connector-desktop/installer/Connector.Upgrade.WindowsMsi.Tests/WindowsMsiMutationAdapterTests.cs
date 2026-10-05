using Microsoft.Win32.SafeHandles;
using System.Security.Principal;
using Connector.Upgrade.Core;
using Connector.Upgrade.WindowsMsi;
using Connector.Upgrade.WindowsPayload;
using Xunit;

namespace Connector.Upgrade.WindowsMsi.Tests;

public sealed class WindowsMsiMutationAdapterTests
{
    private const string CurrentSid = "S-1-5-21-100-200-300-1001";
    private static readonly string MsiExecPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "msiexec.exe");

    [Fact]
    public void InspectsOnlyExactTwoLockedProfiles()
    {
        var fixture = new Fixture();
        fixture.Inventory.Enqueue(fixture.ExactInstalled(fixture.StructuraPin));
        fixture.Inventory.Enqueue(fixture.ExactInstalled(fixture.PlatformPin));

        var structura = fixture.Adapter.InspectExact(LegacyApplicationKind.StructuraConnector);
        var platform = fixture.Adapter.InspectExact(LegacyApplicationKind.PlatformConnector);

        Assert.Equal(ExactWindowsMsiPresence.ExactInstalled, structura.Presence);
        Assert.Equal(WindowsMsiRegistrationContext.UserUnmanaged, structura.Registration!.Context);
        Assert.Equal(CurrentSid, structura.Registration.UserSid);
        Assert.Equal(WindowsMsiRegistrationContext.UserUnmanaged, platform.Registration!.Context);
        Assert.Equal(CurrentSid, platform.Registration.UserSid);
    }

    [Theory]
    [InlineData(WindowsMsiRegistrationContext.Machine, null)]
    [InlineData(WindowsMsiRegistrationContext.UserManaged, CurrentSid)]
    [InlineData(WindowsMsiRegistrationContext.UserUnmanaged, "S-1-5-21-9-9-9-1002")]
    public void RejectsWrongStructuraContextOrProfile(
        WindowsMsiRegistrationContext context,
        string? sid)
    {
        var fixture = new Fixture();
        fixture.Inventory.Enqueue(fixture.ExactInstalled(fixture.StructuraPin, context, sid));

        Assert.Throws<WindowsMsiInvariantException>(() =>
            fixture.Adapter.InspectExact(LegacyApplicationKind.StructuraConnector));
        Assert.Empty(fixture.Runner.Requests);
    }

    [Theory]
    [InlineData(WindowsMsiRegistrationContext.Machine, null)]
    [InlineData(WindowsMsiRegistrationContext.UserManaged, CurrentSid)]
    [InlineData(WindowsMsiRegistrationContext.UserUnmanaged, "S-1-5-21-9-9-9-1002")]
    public void RejectsWrongPlatformContextOrProfile(
        WindowsMsiRegistrationContext context,
        string? sid)
    {
        var fixture = new Fixture();
        fixture.Inventory.Enqueue(fixture.ExactInstalled(fixture.PlatformPin, context, sid));

        Assert.Throws<WindowsMsiInvariantException>(() =>
            fixture.Adapter.InspectExact(LegacyApplicationKind.PlatformConnector));
        Assert.Empty(fixture.Runner.Requests);
    }

    [Fact]
    [Trait("Category", "NativeWindowsInstaller")]
    public void NativeInventorySmokeInspectsBothLockedCurrentUserProductsWhenEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("STRUCTURA_RUN_NATIVE_MSI_SMOKE"),
                "1",
                StringComparison.Ordinal))
            return;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The native Windows Installer smoke requires Windows.");

        var currentSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows identity has no SID.");
        var upgradeLock = LegacyUpgradeLock.LoadEmbedded();
        var adapter = WindowsMsiMutationAdapter.CreateForCurrentUser(TimeSpan.FromMinutes(1));

        foreach (var kind in new[]
                 {
                     LegacyApplicationKind.StructuraConnector,
                     LegacyApplicationKind.PlatformConnector,
                 })
        {
            var pin = upgradeLock.Get(kind);
            var inspection = adapter.InspectExact(kind);
            Assert.Equal(ExactWindowsMsiPresence.ExactInstalled, inspection.Presence);
            Assert.Equal(pin.Version, inspection.Version);
            Assert.Equal(WindowsMsiRegistrationContext.UserUnmanaged, inspection.Registration!.Context);
            Assert.Equal(currentSid, inspection.Registration.UserSid, ignoreCase: true);
        }
    }

    [Fact]
    public void RejectsAmbiguousOrForeignRegistration()
    {
        var ambiguous = new Fixture();
        var snapshot = ambiguous.ExactInstalled(ambiguous.StructuraPin);
        ambiguous.Inventory.Enqueue(snapshot with
        {
            Registrations = [snapshot.Registrations[0], snapshot.Registrations[0]],
        });
        Assert.Throws<WindowsMsiInvariantException>(() =>
            ambiguous.Adapter.InspectExact(LegacyApplicationKind.StructuraConnector));

        var foreign = new Fixture();
        foreign.Inventory.Enqueue(snapshot with
        {
            RelatedProductCodes = [foreign.StructuraPin.Identity.ProductCode, Guid.NewGuid()],
        });
        Assert.Throws<WindowsMsiInvariantException>(() =>
            foreign.Adapter.InspectExact(LegacyApplicationKind.StructuraConnector));
    }

    [Fact]
    public async Task RemoveRequiresDrainAndProtectedLiveLease()
    {
        var fixture = new Fixture();
        await using var lease = fixture.CreateLease(fixture.StructuraPin);

        await Assert.ThrowsAsync<WindowsMsiInvariantException>(async () =>
            await fixture.Adapter.RemoveExactAsync(
                fixture.StructuraPin.Kind,
                new LegacyProcessDrainProof(fixture.StructuraPin.Kind, false, "still-running"),
                lease));

        await lease.DisposeAsync();
        await Assert.ThrowsAsync<WindowsMsiInvariantException>(async () =>
            await fixture.Adapter.RemoveExactAsync(
                fixture.StructuraPin.Kind,
                fixture.Drained(fixture.StructuraPin),
                lease));
        Assert.Empty(fixture.Runner.Requests);
    }

    [Fact]
    public async Task RemovesByExactProductCodeWithArgumentListAndReprobesAbsence()
    {
        var fixture = new Fixture();
        fixture.Inventory.Enqueue(fixture.ExactInstalled(fixture.StructuraPin));
        fixture.Inventory.Enqueue(fixture.Absent(fixture.StructuraPin));
        fixture.Runner.Enqueue(new WindowsMsiProcessResult(WindowsMsiProcessCompletion.Exited, 0));
        await using var lease = fixture.CreateLease(fixture.StructuraPin);

        var result = await fixture.Adapter.RemoveExactAsync(
            fixture.StructuraPin.Kind,
            fixture.Drained(fixture.StructuraPin),
            lease);

        Assert.Equal(WindowsMsiMutationDisposition.Succeeded, result.Disposition);
        var request = Assert.Single(fixture.Runner.Requests);
        Assert.Equal(MsiExecPath, request.FileName);
        Assert.Equal(
            ["/x", $"{{{fixture.StructuraPin.Identity.ProductCode:D}}}".ToUpperInvariant(), "/qn", "/norestart"],
            request.Arguments);
    }

    [Fact]
    public async Task RemovalBoundaryRechecksProcessesAfterExactInventoryAndBeforeMsiexec()
    {
        var fixture = new Fixture();
        fixture.Inventory.Enqueue(fixture.ExactInstalled(fixture.StructuraPin));
        await using var lease = fixture.CreateLease(fixture.StructuraPin);
        var checkedAtBoundary = false;
        var adapter = new WindowsMsiMutationAdapter(
            fixture.Inventory,
            fixture.Runner,
            CurrentSid,
            MsiExecPath,
            TimeSpan.FromMinutes(5),
            beforeRemoval: (kind, _) =>
            {
                Assert.Equal(LegacyApplicationKind.StructuraConnector, kind);
                Assert.Equal(1, fixture.Inventory.InspectionCount);
                checkedAtBoundary = true;
                throw new InvalidOperationException("Legacy client restarted.");
            });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await adapter.RemoveExactAsync(fixture.StructuraPin.Kind,
                fixture.Drained(fixture.StructuraPin), lease));
        Assert.True(checkedAtBoundary);
        Assert.Empty(fixture.Runner.Requests);
    }

    [Fact]
    public async Task Exit3010IsSuccessfulOnlyAfterExactDesiredProbe()
    {
        var fixture = new Fixture();
        fixture.Inventory.Enqueue(fixture.ExactInstalled(fixture.PlatformPin));
        fixture.Inventory.Enqueue(fixture.Absent(fixture.PlatformPin));
        fixture.Runner.Enqueue(new WindowsMsiProcessResult(WindowsMsiProcessCompletion.Exited, 3010));
        await using var lease = fixture.CreateLease(fixture.PlatformPin);

        var result = await fixture.Adapter.RemoveExactAsync(
            fixture.PlatformPin.Kind,
            fixture.Drained(fixture.PlatformPin),
            lease);

        Assert.Equal(WindowsMsiMutationDisposition.SucceededRebootRequired, result.Disposition);
        Assert.Equal(ExactWindowsMsiPresence.Absent, result.StatusProbe!.Presence);
    }

    [Fact]
    public async Task Exit1605AcceptsOnlyConfirmedAbsence()
    {
        var fixture = new Fixture();
        fixture.Inventory.Enqueue(fixture.ExactInstalled(fixture.StructuraPin));
        fixture.Inventory.Enqueue(fixture.Absent(fixture.StructuraPin));
        fixture.Runner.Enqueue(new WindowsMsiProcessResult(WindowsMsiProcessCompletion.Exited, 1605));
        await using var lease = fixture.CreateLease(fixture.StructuraPin);

        var result = await fixture.Adapter.RemoveExactAsync(
            fixture.StructuraPin.Kind,
            fixture.Drained(fixture.StructuraPin),
            lease);

        Assert.Equal(WindowsMsiMutationDisposition.AlreadyInDesiredState, result.Disposition);
        Assert.Equal(ExactWindowsMsiPresence.Absent, result.StatusProbe!.Presence);
    }

    [Fact]
    public async Task Exit1618ReturnsRetryOnlyWhenExactStateIsUnchanged()
    {
        var fixture = new Fixture();
        fixture.Inventory.Enqueue(fixture.ExactInstalled(fixture.StructuraPin));
        fixture.Inventory.Enqueue(fixture.ExactInstalled(fixture.StructuraPin));
        fixture.Runner.Enqueue(new WindowsMsiProcessResult(WindowsMsiProcessCompletion.Exited, 1618));
        await using var lease = fixture.CreateLease(fixture.StructuraPin);

        var result = await fixture.Adapter.RemoveExactAsync(
            fixture.StructuraPin.Kind,
            fixture.Drained(fixture.StructuraPin),
            lease);

        Assert.Equal(WindowsMsiMutationDisposition.RetryRequired, result.Disposition);
        Assert.Equal(ExactWindowsMsiPresence.ExactInstalled, result.StatusProbe!.Presence);
    }

    [Theory]
    [InlineData(WindowsMsiProcessCompletion.Cancelled)]
    [InlineData(WindowsMsiProcessCompletion.TimedOut)]
    public async Task CancellationOrTimeoutRequiresManualRecoveryWithExactProbe(
        WindowsMsiProcessCompletion completion)
    {
        var fixture = new Fixture();
        fixture.Inventory.Enqueue(fixture.ExactInstalled(fixture.StructuraPin));
        fixture.Inventory.Enqueue(fixture.Absent(fixture.StructuraPin));
        fixture.Runner.Enqueue(new WindowsMsiProcessResult(completion));
        await using var lease = fixture.CreateLease(fixture.StructuraPin);

        var result = await fixture.Adapter.RemoveExactAsync(
            fixture.StructuraPin.Kind,
            fixture.Drained(fixture.StructuraPin),
            lease);

        Assert.Equal(WindowsMsiMutationDisposition.ManualRecoveryRequired, result.Disposition);
        Assert.Equal(ExactWindowsMsiPresence.Absent, result.StatusProbe!.Presence);
        Assert.Equal(2, fixture.Inventory.InspectionCount);
    }

    [Fact]
    public async Task RestoreUsesExactLiveStagedPathAndKeepsLeasePinnedThroughVerification()
    {
        var fixture = new Fixture();
        fixture.Inventory.Enqueue(fixture.Absent(fixture.StructuraPin));
        fixture.Inventory.Enqueue(fixture.ExactInstalled(fixture.StructuraPin));
        await using var lease = fixture.CreateLease(fixture.StructuraPin);
        fixture.Runner.OnRun = request =>
        {
            Assert.False(lease.ContentHandle.IsClosed);
            Assert.False(lease.ContentHandle.IsInvalid);
            Assert.Equal(lease.StagedPath, request.Arguments[1]);
        };
        fixture.Runner.Enqueue(new WindowsMsiProcessResult(WindowsMsiProcessCompletion.Exited, 0));

        var result = await fixture.Adapter.RestoreExactAsync(fixture.StructuraPin.Kind, lease);

        Assert.Equal(WindowsMsiMutationDisposition.Succeeded, result.Disposition);
        Assert.False(lease.ContentHandle.IsClosed);
        Assert.Equal(["/i", lease.StagedPath, "/qn", "/norestart"],
            Assert.Single(fixture.Runner.Requests).Arguments);
    }

    private sealed class Fixture
    {
        private readonly LegacyUpgradeLock _lock = LegacyUpgradeLock.LoadEmbedded();

        public Fixture()
        {
            StructuraPin = _lock.Get(LegacyApplicationKind.StructuraConnector);
            PlatformPin = _lock.Get(LegacyApplicationKind.PlatformConnector);
            Adapter = new WindowsMsiMutationAdapter(
                Inventory,
                Runner,
                CurrentSid,
                MsiExecPath,
                TimeSpan.FromMinutes(5),
                _lock);
        }

        public LegacyUpgradePin StructuraPin { get; }
        public LegacyUpgradePin PlatformPin { get; }
        public FakeInventory Inventory { get; } = new();
        public FakeRunner Runner { get; } = new();
        public WindowsMsiMutationAdapter Adapter { get; }

        public LegacyProcessDrainProof Drained(LegacyUpgradePin pin) =>
            new(pin.Kind, true, $"fixture-drain:{pin.PackageId}");

        public FakeLease CreateLease(LegacyUpgradePin pin) => new(pin);

        public WindowsMsiInventorySnapshot ExactInstalled(
            LegacyUpgradePin pin,
            WindowsMsiRegistrationContext? context = null,
            string? sid = null)
        {
            var selectedContext = context ?? WindowsMsiRegistrationContext.UserUnmanaged;
            var selectedSid = selectedContext == WindowsMsiRegistrationContext.Machine
                ? null
                : sid ?? CurrentSid;
            return new WindowsMsiInventorySnapshot(
                pin.Identity.UpgradeCode,
                [pin.Identity.ProductCode],
                [new WindowsMsiRegistration(
                    pin.Identity.ProductCode,
                    pin.Identity.UpgradeCode,
                    pin.Version,
                    selectedContext,
                    selectedSid)]);
        }

        public WindowsMsiInventorySnapshot Absent(LegacyUpgradePin pin) =>
            new(pin.Identity.UpgradeCode, [], []);
    }

    private sealed class FakeInventory : IWindowsMsiInventory
    {
        private readonly Queue<WindowsMsiInventorySnapshot> _snapshots = new();
        public int InspectionCount { get; private set; }

        public void Enqueue(WindowsMsiInventorySnapshot snapshot) => _snapshots.Enqueue(snapshot);

        public WindowsMsiInventorySnapshot Inspect(LegacyUpgradePin pin)
        {
            InspectionCount++;
            return _snapshots.Count > 0
                ? _snapshots.Dequeue()
                : throw new InvalidOperationException($"No fake inventory snapshot exists for {pin.Kind}.");
        }
    }

    private sealed class FakeRunner : IWindowsMsiProcessRunner
    {
        private readonly Queue<WindowsMsiProcessResult> _results = new();
        public List<WindowsMsiProcessRequest> Requests { get; } = [];
        public Action<WindowsMsiProcessRequest>? OnRun { get; set; }

        public void Enqueue(WindowsMsiProcessResult result) => _results.Enqueue(result);

        public ValueTask<WindowsMsiProcessResult> RunAsync(
            WindowsMsiProcessRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            OnRun?.Invoke(request);
            return ValueTask.FromResult(_results.Dequeue());
        }
    }

    private sealed class FakeLease : IWindowsVerifiedRollbackPayloadLease
    {
        private SafeFileHandle? _handle = new(new IntPtr(1234), ownsHandle: false);

        public FakeLease(LegacyUpgradePin pin)
        {
            HandleId = $"fixture:{pin.PackageId}";
            StagedPath = Path.Combine("C:\\ProgramData\\StructuraConnectorInstaller\\RollbackPayloads", pin.InstallerName);
            Inspection = new RollbackPayloadInspection(
                pin.Identity,
                pin.PackageId,
                pin.InstallerName,
                pin.Version,
                pin.SizeBytes,
                pin.Sha256,
                TrustedSource: true);
        }

        public string HandleId { get; }
        public RollbackPayloadProtection Protection => RollbackPayloadProtection.ProtectedMachineStaging;
        public RollbackPayloadInspection Inspection { get; }
        public string StagedPath { get; }
        public SafeFileHandle ContentHandle =>
            _handle ?? throw new ObjectDisposedException(nameof(FakeLease));

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _handle, null)?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
