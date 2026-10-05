using System.Buffers.Binary;
using System.Text;
using Connector.Upgrade.MachineIpc;
using Xunit;

namespace Connector.Upgrade.MachineIpc.Tests;

public sealed class MachineIpcFrameCodecTests
{
    [Theory]
    [InlineData(MachineIpcOperation.Inspect)]
    [InlineData(MachineIpcOperation.Prepare)]
    [InlineData(MachineIpcOperation.Apply)]
    [InlineData(MachineIpcOperation.Reconcile)]
    [InlineData(MachineIpcOperation.Remove)]
    [InlineData(MachineIpcOperation.Restore)]
    [InlineData(MachineIpcOperation.StageVelopack)]
    [InlineData(MachineIpcOperation.PrepareUserStateStage)]
    [InlineData(MachineIpcOperation.StageStructuraRollback)]
    [InlineData(MachineIpcOperation.StagePlatformRollback)]
    public async Task Request_round_trips_over_short_read_stream(MachineIpcOperation operation)
    {
        var expected = new MachineIpcRequest(1, Guid.NewGuid(), operation);
        var frame = MachineIpcFrameCodec.Encode(expected);
        await using var stream = new ChunkedReadStream(frame, maxChunk: 2);

        Assert.Equal(expected, await MachineIpcFrameCodec.ReadAsync(stream));
        Assert.Null(await MachineIpcFrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task Writer_emits_little_endian_length_and_complete_payload()
    {
        var request = new MachineIpcRequest(1, Guid.NewGuid(), MachineIpcOperation.Inspect);
        await using var stream = new MemoryStream();
        await MachineIpcFrameCodec.WriteAsync(stream, request);
        var bytes = stream.ToArray();

        Assert.Equal(bytes.Length - 4, BinaryPrimitives.ReadInt32LittleEndian(bytes));
        Assert.Equal(request, await MachineIpcFrameCodec.ReadAsync(new MemoryStream(bytes)));
    }

    [Fact]
    public async Task Rollover_requires_both_distinct_operation_ids()
    {
        var previous = Guid.NewGuid();
        var next = Guid.NewGuid();
        var request = new MachineIpcRequest(1, Guid.NewGuid(), MachineIpcOperation.Rollover, previous, next);
        var frame = MachineIpcFrameCodec.Encode(request);
        Assert.Equal(request, await MachineIpcFrameCodec.ReadAsync(new MemoryStream(frame)));
        Assert.Throws<InvalidDataException>(() => MachineIpcFrameCodec.Encode(request with { NextOperationId = null }));
        Assert.Throws<InvalidDataException>(() => MachineIpcFrameCodec.Encode(request with { NextOperationId = previous }));
        Assert.Throws<InvalidDataException>(() => MachineIpcFrameCodec.Encode(request with { Operation = MachineIpcOperation.Inspect }));
    }

    [Theory]
    [InlineData(MachineIpcOperation.Remove, "remove")]
    [InlineData(MachineIpcOperation.Restore, "restore")]
    public void Compensation_frames_contain_only_the_operation_identity(MachineIpcOperation operation, string operationName)
    {
        var request = new MachineIpcRequest(1, Guid.Parse("11111111-1111-1111-1111-111111111111"), operation);
        var frame = MachineIpcFrameCodec.Encode(request);
        var payload = Encoding.UTF8.GetString(frame, 4, frame.Length - 4);

        Assert.Equal("{\"version\":1,\"correlationId\":\"11111111-1111-1111-1111-111111111111\",\"operation\":\"" + operationName + "\"}", payload);
        Assert.DoesNotContain("path", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("receipt", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plan", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void User_state_stage_frame_contains_only_fixed_command_identity()
    {
        var request = new MachineIpcRequest(1, Guid.Parse("11111111-1111-1111-1111-111111111111"), MachineIpcOperation.PrepareUserStateStage);
        var frame = MachineIpcFrameCodec.Encode(request);
        var payload = Encoding.UTF8.GetString(frame, 4, frame.Length - 4);
        Assert.Equal("{\"version\":1,\"correlationId\":\"11111111-1111-1111-1111-111111111111\",\"operation\":\"prepareUserStateStage\"}", payload);
        Assert.DoesNotContain("path", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(MachineIpcOperation.StageStructuraRollback, "stageStructuraRollback")]
    [InlineData(MachineIpcOperation.StagePlatformRollback, "stagePlatformRollback")]
    public void Rollback_stage_frame_is_zero_payload_and_contains_only_fixed_command_identity(
        MachineIpcOperation operation, string operationName)
    {
        var request = new MachineIpcRequest(1, Guid.Parse("11111111-1111-1111-1111-111111111111"), operation);
        var frame = MachineIpcFrameCodec.Encode(request);
        var payload = Encoding.UTF8.GetString(frame, 4, frame.Length - 4);

        Assert.Equal("{\"version\":1,\"correlationId\":\"11111111-1111-1111-1111-111111111111\",\"operation\":\"" + operationName + "\"}", payload);
        Assert.DoesNotContain("path", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("receipt", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Empty_stream_is_clean_eof_but_partial_header_is_truncated()
    {
        Assert.Null(await MachineIpcFrameCodec.ReadAsync(new MemoryStream()));
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await MachineIpcFrameCodec.ReadAsync(new MemoryStream([1, 0])));
    }

    [Fact]
    public async Task Truncated_payload_is_rejected()
    {
        var frame = MachineIpcFrameCodec.Encode(new MachineIpcRequest(1, Guid.NewGuid(), MachineIpcOperation.Apply));
        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await MachineIpcFrameCodec.ReadAsync(new MemoryStream(frame[..^1])));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65537)]
    [InlineData(int.MaxValue)]
    public async Task Invalid_lengths_are_rejected_before_payload_read(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await MachineIpcFrameCodec.ReadAsync(new MemoryStream(header)));
    }

    [Theory]
    [InlineData("{\"version\":1,\"correlationId\":\"00000000-0000-0000-0000-000000000001\",\"operation\":\"Bogus\"}")]
    [InlineData("{\"version\":2,\"correlationId\":\"00000000-0000-0000-0000-000000000001\",\"operation\":\"inspect\"}")]
    [InlineData("{\"version\":1,\"correlationId\":\"00000000-0000-0000-0000-000000000000\",\"operation\":\"inspect\"}")]
    [InlineData("{\"version\":1,\"correlationId\":\"00000000-0000-0000-0000-000000000001\",\"operation\":\"inspect\",\"token\":\"secret\"}")]
    [InlineData("{\"version\":1,\"correlationId\":\"00000000-0000-0000-0000-000000000001\",\"operation\":\"inspect\",\"extra\":true}")]
    [InlineData("{\"version\":1,\"correlationId\":\"00000000-0000-0000-0000-000000000001\"}")]
    [InlineData("{\"version\":1,\"version\":1,\"correlationId\":\"00000000-0000-0000-0000-000000000001\",\"operation\":\"inspect\"}")]
    [InlineData("{\"version\":1,\"correlationId\":\"00000000-0000-0000-0000-000000000001\",\"operation\":\"inspect\"} trailing")]
    [InlineData("{\"version\":1,\"correlationId\":\"00000000-0000-0000-0000-000000000001\",\"operation\":\"stageStructuraRollback\",\"path\":\"C:\\\\setup.msi\"}")]
    [InlineData("{\"version\":1,\"correlationId\":\"00000000-0000-0000-0000-000000000001\",\"operation\":\"stagePlatformRollback\",\"receipt\":\"opaque\"}")]
    [InlineData("{\"version\":1,\"correlationId\":\"00000000-0000-0000-0000-000000000001\",\"operation\":\"stagePlatformRollbak\"}")]
    public async Task Invalid_or_extended_envelopes_are_rejected(string json)
    {
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await MachineIpcFrameCodec.ReadAsync(new MemoryStream(Frame(Encoding.UTF8.GetBytes(json)))));
    }

    [Fact]
    public async Task Cancellation_is_propagated_during_read_and_write()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await MachineIpcFrameCodec.ReadAsync(new BlockingReadStream(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await MachineIpcFrameCodec.WriteAsync(new MemoryStream(),
                new MachineIpcRequest(1, Guid.NewGuid(), MachineIpcOperation.Apply), cancellation.Token));
    }

    private static byte[] Frame(byte[] payload)
    {
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(4));
        return frame;
    }

    private sealed class ChunkedReadStream(byte[] data, int maxChunk) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, maxChunk)], cancellationToken);
    }

    private sealed class BlockingReadStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromCanceled<int>(cancellationToken);
    }
}
