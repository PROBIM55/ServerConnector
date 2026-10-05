using System.Buffers.Binary;
using System.Text;
using Connector.Upgrade.Core;
using Connector.Upgrade.MachineDispatcher;
using Connector.Upgrade.MachineIpc;
using Connector.Upgrade.MachineJournal;
using Xunit;

namespace Connector.Upgrade.MachineDispatcher.Tests;

public sealed class MachineDispatcherResultFrameCodecTests
{
    private static readonly Guid Correlation = Guid.Parse("42e3bd01-5acb-45c9-b990-3846d9953a4c");

    [Fact]
    public async Task Result_round_trips_through_length_prefixed_stream()
    {
        var expected = new MachineDispatcherResult(1, Correlation, MachineIpcOperation.Inspect,
            MachineDispatcherStatus.Completed, MachineDispatcherCode.None, MachineUpgradeState.InProgress,
            MachineUpgradePhase.AssessNetBird, 4, NetBirdOwnership.Absent);
        var frame = MachineDispatcherResultFrameCodec.Encode(expected);
        Assert.Equal(frame.Length - MachineDispatcherResultFrameCodec.HeaderLength,
            BinaryPrimitives.ReadInt32LittleEndian(frame));
        using var stream = new MemoryStream(frame);
        Assert.Equal(expected, await MachineDispatcherResultFrameCodec.ReadAsync(stream));
        Assert.Null(await MachineDispatcherResultFrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task Blocked_and_cancelled_results_round_trip_without_snapshots()
    {
        var results = new[]
        {
            new MachineDispatcherResult(1, Correlation, MachineIpcOperation.Prepare,
                MachineDispatcherStatus.Blocked, MachineDispatcherCode.AssessmentRequired),
            new MachineDispatcherResult(1, Correlation, MachineIpcOperation.Inspect,
                MachineDispatcherStatus.Cancelled, MachineDispatcherCode.Cancelled),
            new MachineDispatcherResult(1, Correlation, MachineIpcOperation.Apply,
                MachineDispatcherStatus.Blocked, MachineDispatcherCode.InvalidRequest)
        };
        foreach (var expected in results)
        {
            using var stream = new MemoryStream(MachineDispatcherResultFrameCodec.Encode(expected));
            Assert.Equal(expected, await MachineDispatcherResultFrameCodec.ReadAsync(stream));
        }
    }

    [Fact]
    public async Task Apply_and_reconcile_results_round_trip_with_bounded_typed_recovery_evidence()
    {
        const string evidence = "netbird-windows-v1:" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var results = new[]
        {
            new MachineDispatcherResult(1, Correlation, MachineIpcOperation.Apply,
                MachineDispatcherStatus.Completed, MachineDispatcherCode.None, MachineUpgradeState.InProgress,
                MachineUpgradePhase.MutateNetBird, 5, NetBirdOwnership.Absent, NetBirdChangeKind.InstalledThisRun),
            new MachineDispatcherResult(1, Correlation, MachineIpcOperation.Reconcile,
                MachineDispatcherStatus.Completed, MachineDispatcherCode.None, MachineUpgradeState.RollingBack,
                MachineUpgradePhase.Rollback, 6, NetBirdOwnership.Absent, NetBirdChangeKind.InstalledThisRun,
                NetBirdInterruptedRecoveryAction.RemovedInstalledThisRun, evidence),
        };
        foreach (var expected in results)
        {
            using var stream = new MemoryStream(MachineDispatcherResultFrameCodec.Encode(expected));
            Assert.Equal(expected, await MachineDispatcherResultFrameCodec.ReadAsync(stream));
        }
    }

    [Fact]
    public async Task Apply_result_round_trips_reboot_required_receipt()
    {
        var expected = new MachineDispatcherResult(1, Correlation, MachineIpcOperation.Apply,
            MachineDispatcherStatus.Completed, MachineDispatcherCode.None, MachineUpgradeState.InProgress,
            MachineUpgradePhase.MutateNetBird, 5, NetBirdOwnership.Absent, NetBirdChangeKind.InstalledThisRun,
            RebootRequired: true);
        using var stream = new MemoryStream(MachineDispatcherResultFrameCodec.Encode(expected));
        Assert.Equal(expected, await MachineDispatcherResultFrameCodec.ReadAsync(stream));
    }

    [Fact]
    public void Reconciliation_result_rejects_missing_or_path_like_evidence()
    {
        var missingEvidence = new MachineDispatcherResult(1, Correlation, MachineIpcOperation.Reconcile,
            MachineDispatcherStatus.Completed, MachineDispatcherCode.None, MachineUpgradeState.RollingBack,
            MachineUpgradePhase.Rollback, 6, NetBirdOwnership.Absent, NetBirdChangeKind.InstalledThisRun,
            NetBirdInterruptedRecoveryAction.RemovedInstalledThisRun);
        var pathEvidence = missingEvidence with { ReconciliationEvidenceId = @"C:\\secret\\token" };

        Assert.Throws<InvalidDataException>(() => MachineDispatcherResultFrameCodec.Encode(missingEvidence));
        Assert.Throws<InvalidDataException>(() => MachineDispatcherResultFrameCodec.Encode(pathEvidence));
    }

    [Theory]
    [InlineData("{\"version\":1,\"version\":1,\"correlationId\":\"42e3bd01-5acb-45c9-b990-3846d9953a4c\",\"operation\":\"inspect\",\"status\":\"blocked\",\"code\":\"invalidRequest\",\"state\":null,\"phase\":null,\"revision\":null,\"netBirdOwnership\":null,\"netBirdChange\":null}")]
    [InlineData("{\"version\":1,\"correlationId\":\"42e3bd01-5acb-45c9-b990-3846d9953a4c\",\"operation\":\"inspect\",\"status\":\"blocked\",\"code\":\"invalidRequest\",\"state\":null,\"phase\":null,\"revision\":null,\"netBirdOwnership\":null,\"netBirdChange\":null,\"path\":\"C:\\\\secret\"}")]
    [InlineData("{\"version\":1,\"correlationId\":\"42e3bd01-5acb-45c9-b990-3846d9953a4c\",\"operation\":\"inspect\",\"status\":\"blocked\",\"code\":\"invalidRequest\",\"state\":null,\"phase\":null,\"revision\":null,\"netBirdOwnership\":null}")]
    [InlineData("{\"version\":1,\"correlationId\":\"42e3bd01-5acb-45c9-b990-3846d9953a4c\",\"operation\":0,\"status\":\"blocked\",\"code\":\"invalidRequest\",\"state\":null,\"phase\":null,\"revision\":null,\"netBirdOwnership\":null,\"netBirdChange\":null}")]
    [InlineData("{\"version\":1,\"correlationId\":\"42e3bd01-5acb-45c9-b990-3846d9953a4c\",\"operation\":\"INSPECT\",\"status\":\"blocked\",\"code\":\"invalidRequest\",\"state\":null,\"phase\":null,\"revision\":null,\"netBirdOwnership\":null,\"netBirdChange\":null}")]
    [InlineData("{\"version\":2,\"correlationId\":\"42e3bd01-5acb-45c9-b990-3846d9953a4c\",\"operation\":\"inspect\",\"status\":\"blocked\",\"code\":\"invalidRequest\",\"state\":null,\"phase\":null,\"revision\":null,\"netBirdOwnership\":null,\"netBirdChange\":null}")]
    [InlineData("{\"version\":1,\"correlationId\":\"00000000-0000-0000-0000-000000000000\",\"operation\":\"inspect\",\"status\":\"blocked\",\"code\":\"invalidRequest\",\"state\":null,\"phase\":null,\"revision\":null,\"netBirdOwnership\":null,\"netBirdChange\":null}")]
    [InlineData("{\"version\":1,\"correlationId\":\"42e3bd01-5acb-45c9-b990-3846d9953a4c\",\"operation\":\"inspect\",\"status\":\"completed\",\"code\":\"none\",\"state\":null,\"phase\":null,\"revision\":null,\"netBirdOwnership\":null,\"netBirdChange\":null}")]
    [InlineData("{\"version\":1,\"correlationId\":\"42e3bd01-5acb-45c9-b990-3846d9953a4c\",\"operation\":\"inspect\",\"status\":\"blocked\",\"code\":\"cancelled\",\"state\":null,\"phase\":null,\"revision\":null,\"netBirdOwnership\":null,\"netBirdChange\":null}")]
    public async Task Decoder_rejects_invalid_envelopes(string json)
    {
        using var stream = Frame(json);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await MachineDispatcherResultFrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task Reader_rejects_oversized_length_and_truncated_payload()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, MachineDispatcherResultFrameCodec.MaximumFrameLength + 1);
        using (var oversized = new MemoryStream(header))
            await Assert.ThrowsAsync<InvalidDataException>(async () => await MachineDispatcherResultFrameCodec.ReadAsync(oversized));
        BinaryPrimitives.WriteInt32LittleEndian(header, 5);
        using var truncated = new MemoryStream(header);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await MachineDispatcherResultFrameCodec.ReadAsync(truncated));
    }

    [Fact]
    public void Encoder_rejects_inconsistent_optional_semantics()
    {
        var result = new MachineDispatcherResult(1, Correlation, MachineIpcOperation.Inspect,
            MachineDispatcherStatus.Blocked, MachineDispatcherCode.InvalidRequest, Revision: 0,
            NetBirdChange: NetBirdChangeKind.NoChange);
        Assert.Throws<InvalidDataException>(() => MachineDispatcherResultFrameCodec.Encode(result));
    }

    [Theory]
    [InlineData(MachineDispatcherStatus.Completed, MachineDispatcherCode.None)]
    [InlineData(MachineDispatcherStatus.Cancelled, MachineDispatcherCode.Cancelled)]
    public void Encoder_rejects_unsupported_operation_outcomes(MachineDispatcherStatus status, MachineDispatcherCode code)
    {
        var result = new MachineDispatcherResult(1, Correlation, (MachineIpcOperation)999,
            status, code, status == MachineDispatcherStatus.Completed ? MachineUpgradeState.InProgress : null,
            status == MachineDispatcherStatus.Completed ? MachineUpgradePhase.Preflight : null,
            status == MachineDispatcherStatus.Completed ? 0 : null);
        Assert.Throws<InvalidDataException>(() => MachineDispatcherResultFrameCodec.Encode(result));
    }

    [Fact]
    public void Encoder_rejects_unsupported_operation_code_for_supported_operation()
    {
        var result = new MachineDispatcherResult(1, Correlation, MachineIpcOperation.Inspect,
            MachineDispatcherStatus.Blocked, MachineDispatcherCode.UnsupportedOperation);
        Assert.Throws<InvalidDataException>(() => MachineDispatcherResultFrameCodec.Encode(result));
    }

    private static MemoryStream Frame(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var bytes = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, payload.Length);
        payload.CopyTo(bytes, 4);
        return new MemoryStream(bytes);
    }
}
