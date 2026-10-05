using Connector.Upgrade.Core;
using Connector.Upgrade.MachineDispatcher;
using Connector.Upgrade.MachineIpc;
using Connector.Upgrade.MachineJournal;
using Connector.Upgrade.WindowsHost;
using Xunit;

namespace Connector.Upgrade.NetBirdMachine.Tests;

public sealed class MachineUpgradeDispatcherNoChangeContractTests
{
    private static readonly Guid OperationId = Guid.Parse("7c995af0-b056-4d50-a258-af805fa0b991");
    private const string Sid = "S-1-5-18";
    private const string SourceMsi = "C:\\PackageSource\\netbird_installer_0.79.0_windows_amd64.msi";

    [Fact]
    public async Task Dispatcher_prepares_and_applies_service_no_change_plan_without_msi_mutation()
    {
        var fixture = new NetBirdMachineFixture();
        var exact = NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.TargetState, NetBirdMachineFixture.TargetPackage, fixture.Pin.InstallerSha256);
        Enqueue(fixture, exact, 4);
        var journal = new InMemoryJournal();
        var port = new WindowsHostNetBirdPort(fixture.Service, fixture.State, SourceMsi);
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);

        var prepared = await PrepareAsync(dispatcher);
        var applied = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Apply));

        Assert.Equal(MachineDispatcherStatus.Completed, prepared.Status);
        Assert.Equal(NetBirdChangeKind.NoChange, prepared.NetBirdChange);
        Assert.Null(journal.Document!.NetBirdMutationPlan!.OperationId);
        Assert.Equal(MachineDispatcherStatus.Completed, applied.Status);
        Assert.Equal(NetBirdChangeKind.NoChange, journal.Document.RecoveryReceipt!.Change);
        Assert.Equal(0, fixture.Msi.InstallCalls);
        Assert.Equal(0, fixture.Msi.UninstallCalls);
    }

    [Fact]
    public async Task Dispatcher_rejects_tampered_operation_id_on_service_no_change_plan_before_apply()
    {
        var fixture = new NetBirdMachineFixture();
        var exact = NetBirdMachineFixture.OwnedInspection(
            NetBirdMachineFixture.TargetState, NetBirdMachineFixture.TargetPackage, fixture.Pin.InstallerSha256);
        Enqueue(fixture, exact, 3);
        var journal = new InMemoryJournal();
        var port = new WindowsHostNetBirdPort(fixture.Service, fixture.State, SourceMsi);
        using var dispatcher = new MachineUpgradeDispatcher(OperationId, Sid, journal, port);
        await PrepareAsync(dispatcher);
        journal.ReplaceForTest(journal.Document! with
        {
            Revision = journal.Document!.Revision + 1,
            NetBirdMutationPlan = journal.Document.NetBirdMutationPlan! with { OperationId = OperationId.ToString("D") },
        });

        var applied = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Apply));

        Assert.Equal(MachineDispatcherStatus.Blocked, applied.Status);
        Assert.Equal(MachineDispatcherCode.InvalidMutationPlan, applied.Code);
        Assert.Equal(0, fixture.Msi.InstallCalls);
        Assert.Equal(0, fixture.Msi.UninstallCalls);
    }

    private static async Task<MachineDispatcherResult> PrepareAsync(MachineUpgradeDispatcher dispatcher)
    {
        var inspected = await dispatcher.DispatchAsync(Request(MachineIpcOperation.Inspect));
        Assert.Equal(MachineDispatcherStatus.Completed, inspected.Status);
        return await dispatcher.DispatchAsync(Request(MachineIpcOperation.Prepare));
    }

    private static MachineIpcRequest Request(MachineIpcOperation operation) =>
        new(MachineIpcFrameCodec.CurrentVersion, Guid.NewGuid(), operation);

    private static void Enqueue(NetBirdMachineFixture fixture, NetBirdMachineInspection inspection, int count)
    {
        for (var index = 0; index < count; index++) fixture.State.Inspections.Enqueue(inspection);
    }

    private sealed class InMemoryJournal : IMachineUpgradeJournal
    {
        public MachineUpgradeJournalDocument? Document { get; private set; }

        public void ReplaceForTest(MachineUpgradeJournalDocument document) => Document = document;

        public ValueTask<IAsyncDisposable> AcquireLeaseAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IAsyncDisposable>(new Lease());

        public ValueTask<MachineUpgradeJournalDocument?> LoadAsync(Guid operationId, string initiatingSid,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(Document);

        public ValueTask InitializeAsync(MachineUpgradeJournalDocument document, CancellationToken cancellationToken = default)
        {
            Document = document;
            return ValueTask.CompletedTask;
        }

        public ValueTask SaveAsync(Guid operationId, string initiatingSid, long expectedRevision,
            MachineUpgradeJournalDocument next, CancellationToken cancellationToken = default)
        {
            if (Document is null || Document.Revision != expectedRevision || next.Revision != expectedRevision + 1)
                throw new InvalidOperationException();
            Document = next;
            return ValueTask.CompletedTask;
        }

        public void Dispose() { }

        private sealed class Lease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
