using System.Security;
using System.Security.Principal;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Connector.Upgrade.Core;
using Connector.Upgrade.MachineDispatcher;
using Connector.Upgrade.MachineIpc;
using Connector.Upgrade.MachineJournal;
using Connector.Upgrade.MachineHelper;
using Connector.Upgrade.NetBirdMachine;
using Connector.Upgrade.WindowsHost;
using Connector.Upgrade.WindowsPayload;
using Xunit;

namespace Connector.Upgrade.MachineHelper.Tests;

public sealed class MachineHelperRuntimeTests
{
    private static readonly Guid Operation = Guid.Parse("24c21d461f4e4caba00be96c7954626d");
    private static string SidText => WindowsIdentity.GetCurrent().User!.Value;

    [Fact]
    public async Task Malformed_or_duplicate_arguments_are_rejected_before_opening_caller()
    {
        var opener = new FakeOpener();
        var runtime = Runtime(opener, new FakeTrust(), new FakeSessionHost(), new FakeComposition());
        var valid = Args();

        await Assert.ThrowsAsync<ArgumentException>(async () => await runtime.RunAsync(valid.Take(7).ToArray()));
        await Assert.ThrowsAsync<ArgumentException>(async () => await runtime.RunAsync([.. valid, "--caller-pid", "10"]));
        await Assert.ThrowsAsync<ArgumentException>(async () => await runtime.RunAsync([valid[0], valid[1], "--operation", valid[3], "--operation", valid[3], valid[5], valid[7]]));
        Assert.Equal(0, opener.OpenCount);
    }

    [Fact]
    public async Task Missing_embedded_caller_pin_fails_before_dispatcher_or_netbird_ports_are_created()
    {
        var opener = new FakeOpener();
        var trust = new ProtectedPinsTrust();
        var composition = new FakeComposition();
        var host = new FakeSessionHost();
        var runtime = Runtime(opener, trust, host, composition);

        await Assert.ThrowsAsync<SecurityException>(async () => await runtime.RunAsync(Args()));

        Assert.Equal(1, opener.OpenCount);
        Assert.Equal(0, composition.CreateCount);
        Assert.Equal(0, host.CallCount);
        Assert.Equal(0, composition.Port.InspectCount + composition.Port.PrepareCount + composition.Port.ApplyCount);
    }

    [Fact]
    public async Task Accepted_fake_boundary_routes_only_inspect_and_prepare_without_msi_mutation()
    {
        var composition = new FakeComposition();
        var host = new InspectPrepareSessionHost();
        var runtime = Runtime(new FakeOpener(), new FakeTrust(), host, composition);

        await runtime.RunAsync(Args());

        Assert.Equal([MachineIpcOperation.Inspect, MachineIpcOperation.Prepare], host.Operations);
        Assert.Equal(1, composition.Port.InspectCount);
        Assert.Equal(1, composition.Port.PrepareCount);
        Assert.Equal(0, composition.Port.ApplyCount);
        Assert.Equal(0, composition.Port.ReconcileCount);
    }

    [Fact]
    public async Task Runtime_passes_retained_process_handle_to_factory_only_after_trust_and_session_entry()
    {
        var order = new List<string>();
        var opener = new RecordingOpener(order);
        var composition = new FakeComposition(order);
        var runtime = Runtime(opener, new RecordingTrust(order), new RecordingSessionHost(order), composition);

        await runtime.RunAsync(Args());

        Assert.Equal(["open", "pre-session-trust", "authenticated-session", "create-dispatcher"], order);
        Assert.Same(opener.Handle, composition.RetainedCaller);
    }

    [Fact]
    public async Task Rollback_stager_binds_authenticated_process_token_and_embedded_fixed_source_names()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "rollback-helper-assets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var locked = LegacyUpgradeLock.LoadEmbedded();
            foreach (var pin in locked.Pins)
                File.WriteAllBytes(Path.Combine(root, pin.InstallerName), [1]);
            using var process = Process.GetCurrentProcess();
            var expectedSid = new SecurityIdentifier(SidText);
            string? capturedSid = null;
            WindowsRollbackPayloadSources? capturedSources = null;
            var bridge = new TrustedHelperRollbackPayloadStager(Operation, expectedSid, process.SafeHandle, root,
                (operationId, identity, sid, sources) =>
                {
                    Assert.Equal(Operation, operationId);
                    capturedSid = identity.User?.Value;
                    Assert.Equal(expectedSid, sid);
                    capturedSources = sources;
                    return new ThrowingRollbackPayloadStager();
                });

            await Assert.ThrowsAsync<WindowsRollbackPayloadNeedsManualRecoveryException>(async () =>
                await bridge.StageVerifiedRollbackPayloadAsync(LegacyApplicationKind.StructuraConnector));

            Assert.Equal(expectedSid.Value, capturedSid);
            Assert.Equal(locked.Get(LegacyApplicationKind.StructuraConnector).InstallerName,
                Path.GetFileName(capturedSources!.StructuraConnectorMsiPath));
            Assert.Equal(locked.Get(LegacyApplicationKind.PlatformConnector).InstallerName,
                Path.GetFileName(capturedSources.PlatformConnectorMsiPath));
            Assert.All(new[] { capturedSources.StructuraConnectorMsiPath, capturedSources.PlatformConnectorMsiPath },
                path => Assert.Equal(root, Path.GetDirectoryName(path), StringComparer.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Rollback_stager_fails_closed_on_missing_embedded_msi_before_token_or_root_mutation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "rollback-helper-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var factoryCalls = 0;
        try
        {
            var bridge = new TrustedHelperRollbackPayloadStager(Operation, new SecurityIdentifier(SidText),
                new SafeProcessHandle(IntPtr.Zero, ownsHandle: false), root,
                (_, _, _, _) =>
                {
                    factoryCalls++;
                    return new ThrowingRollbackPayloadStager();
                });

            await Assert.ThrowsAsync<WindowsRollbackPayloadNeedsManualRecoveryException>(async () =>
                await bridge.StageVerifiedRollbackPayloadAsync(LegacyApplicationKind.StructuraConnector));

            Assert.Equal(0, factoryCalls);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static IReadOnlyList<string> Args()
    {
        var sid = new SecurityIdentifier(SidText);
        return ["--pipe", MutualPipe(Operation, sid),
            "--operation", Operation.ToString("N"), "--caller-sid", SidText, "--caller-pid", "41"];
    }

    private static string MutualPipe(Guid operation, SecurityIdentifier sid) =>
        global::Connector.Upgrade.MutualMachineChannel.MutualMachineChannel.GetPipeName("A", operation, sid);

    private static MachineHelperRuntime Runtime(IHelperCallerProcessOpener opener, IHelperPreSessionTrust trust,
        IHelperCommandSessionHost host, FakeComposition composition) => new(opener, trust, host, composition);

    private sealed class FakeOpener : IHelperCallerProcessOpener
    {
        public int OpenCount { get; private set; }
        public SafeProcessHandle Open(int processId) { OpenCount++; return Handle = new SafeProcessHandle(new IntPtr(42), ownsHandle: false); }
        public SafeProcessHandle? Handle { get; private set; }
    }

    private sealed class RecordingOpener(List<string> order) : IHelperCallerProcessOpener
    {
        public SafeProcessHandle Handle { get; private set; } = null!;
        public SafeProcessHandle Open(int processId)
        {
            order.Add("open");
            return Handle = new SafeProcessHandle(new IntPtr(43), ownsHandle: false);
        }
    }

    private sealed class RecordingTrust(List<string> order) : IHelperPreSessionTrust
    {
        public void AssertTrustedCaller(SafeProcessHandle retainedCaller, SecurityIdentifier expectedSid) => order.Add("pre-session-trust");
    }

    private sealed class RecordingSessionHost(List<string> order) : IHelperCommandSessionHost
    {
        public async ValueTask RunAsync(HelperArguments arguments, SafeProcessHandle retainedCaller,
            Func<MachineUpgradeDispatcher> createDispatcher, CancellationToken cancellationToken)
        {
            order.Add("authenticated-session");
            using var dispatcher = createDispatcher();
            var result = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Inspect), cancellationToken);
            Assert.Equal(MachineDispatcherStatus.Completed, result.Status);
        }
    }

    private sealed class FakeTrust : IHelperPreSessionTrust
    {
        public Exception? Failure { get; init; }
        public int CallCount { get; private set; }
        public void AssertTrustedCaller(SafeProcessHandle retainedCaller, SecurityIdentifier expectedSid)
        {
            CallCount++;
            if (Failure is not null) throw Failure;
        }
    }

    private sealed class ProtectedPinsTrust : IHelperPreSessionTrust
    {
        private readonly Connector.Upgrade.ProtectedCallerImage.ProtectedCallerImagePinSource _pins = new();
        public void AssertTrustedCaller(SafeProcessHandle retainedCaller, SecurityIdentifier expectedSid) =>
            _pins.AssertTrustedCaller(retainedCaller, expectedSid);
    }

    private class FakeSessionHost : IHelperCommandSessionHost
    {
        public int CallCount { get; protected set; }
        public virtual async ValueTask RunAsync(HelperArguments arguments, SafeProcessHandle retainedCaller,
            Func<MachineUpgradeDispatcher> createDispatcher, CancellationToken cancellationToken)
        {
            CallCount++;
            using var dispatcher = createDispatcher();
            await dispatcher.DispatchAsync(Request(MachineIpcOperation.Inspect), cancellationToken);
        }
    }

    private sealed class InspectPrepareSessionHost : FakeSessionHost
    {
        public List<MachineIpcOperation> Operations { get; } = [];
        public override async ValueTask RunAsync(HelperArguments arguments, SafeProcessHandle retainedCaller,
            Func<MachineUpgradeDispatcher> createDispatcher, CancellationToken cancellationToken)
        {
            CallCount++;
            using var dispatcher = createDispatcher();
            foreach (var operation in new[] { MachineIpcOperation.Inspect, MachineIpcOperation.Prepare })
            {
                Operations.Add(operation);
                var result = await dispatcher.DispatchAsync(Request(operation), cancellationToken);
                Assert.Equal(MachineDispatcherStatus.Completed, result.Status);
            }
        }
    }

    private sealed class FakeComposition : IHelperMachineComposition
    {
        private readonly List<string>? _order;
        public FakeComposition(List<string>? order = null) => _order = order;
        public readonly FakeNetBirdPort Port = new();
        public int CreateCount { get; private set; }
        public SafeProcessHandle? RetainedCaller { get; private set; }
        public MachineUpgradeDispatcher CreateDispatcher(Guid operationId, SecurityIdentifier callerSid, SafeProcessHandle retainedCaller)
        {
            _order?.Add("create-dispatcher");
            RetainedCaller = retainedCaller;
            CreateCount++;
            return new MachineUpgradeDispatcher(operationId, callerSid.Value, new FakeJournal(), Port);
        }
    }

    private sealed class ThrowingRollbackPayloadStager : IWindowsOperationRollbackPayloadStager
    {
        public ValueTask<IWindowsVerifiedRollbackPayloadLease> StageVerifiedRollbackPayloadAsync(
            LegacyApplicationKind kind, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IWindowsVerifiedRollbackPayloadLease>(
                new WindowsRollbackPayloadNeedsManualRecoveryException(kind));
    }

    private sealed class FakeJournal : IMachineUpgradeJournal
    {
        private MachineUpgradeJournalDocument? _document;
        public ValueTask<IAsyncDisposable> AcquireLeaseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IAsyncDisposable>(new Lease());
        public ValueTask<MachineUpgradeJournalDocument?> LoadAsync(Guid operationId, string initiatingSid, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_document);
        public ValueTask InitializeAsync(MachineUpgradeJournalDocument document, CancellationToken cancellationToken = default)
        { _document = document; return ValueTask.CompletedTask; }
        public ValueTask SaveAsync(Guid operationId, string initiatingSid, long expectedRevision, MachineUpgradeJournalDocument next, CancellationToken cancellationToken = default)
        { _document = next; return ValueTask.CompletedTask; }
        public void Dispose() { }
        private sealed class Lease : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }

    private sealed class FakeNetBirdPort : IWindowsHostNetBirdPort
    {
        private static readonly NetBirdAssessment Assessment = new(NetBirdOwnership.Absent, null);
        private static readonly NetBirdMsiPackageIdentity Package = new(Guid.Parse("4792212369224a45a631b567e19f9640"),
            Guid.Parse("9a1cb54aeb3a4b0998a2235d104a21d6"), "0.40.1", "NetBird", "NetBird");
        private static readonly NetBirdMutationPlan Plan = new(NetBirdChangeKind.InstalledThisRun, Operation.ToString("D"),
            Assessment, new ProtectedNetBirdPackageReceipt("handle", RollbackPayloadProtection.ProtectedMachineStaging,
                "netbird.msi", Package, 1024, new string('a', 64), "NetBird", new string('b', 40)), null);
        public int InspectCount { get; private set; }
        public int PrepareCount { get; private set; }
        public int ApplyCount { get; private set; }
        public int ReconcileCount { get; private set; }
        public ValueTask<NetBirdAssessment> InspectAsync(CancellationToken cancellationToken) { InspectCount++; return ValueTask.FromResult(Assessment); }
        public ValueTask<NetBirdMutationPlan> PrepareAsync(NetBirdAssessment assessment, Guid operationId, CancellationToken cancellationToken)
        { PrepareCount++; return ValueTask.FromResult(Plan with { OperationId = operationId.ToString("D") }); }
        public ValueTask<NetBirdMutationReceipt> ApplyAsync(NetBirdMutationPlan plan, CancellationToken cancellationToken)
        { ApplyCount++; throw new InvalidOperationException("Test boundary must not run MSI."); }
        public ValueTask<NetBirdInterruptedRecoveryResult> ReconcileAsync(NetBirdMutationPlan plan, CancellationToken cancellationToken)
        { ReconcileCount++; throw new InvalidOperationException("Test boundary must not reconcile."); }
        public ValueTask RemoveInstalledThisRunAsync(NetBirdInstalledThisRunReceipt receipt, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public ValueTask RestoreUpdatedThisRunAsync(NetBirdUpdatedThisRunReceipt receipt, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    private static MachineIpcRequest Request(MachineIpcOperation operation) =>
        new(MachineIpcFrameCodec.CurrentVersion, Guid.NewGuid(), operation);
}
