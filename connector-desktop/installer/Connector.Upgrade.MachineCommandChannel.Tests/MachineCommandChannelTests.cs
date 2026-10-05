using System.Threading.Channels;
using Connector.Upgrade.MachineCommandChannel;
using Connector.Upgrade.MachineDispatcher;
using Connector.Upgrade.MachineIpc;
using Connector.Upgrade.MutualMachineChannel;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Connector.Upgrade.MachineCommandChannel.Tests;

public sealed class MachineCommandChannelTests
{
    [Fact]
    public async Task SequentialRequestsUseCodecsAndReturnCorrelatedDispatcherResults()
    {
        var (clientPipe, serverPipe) = Pair();
        using var clientSession = Session(clientPipe);
        var serverSession = Session(serverPipe);
        var seen = new List<MachineIpcOperation>();
        var server = MachineCommandServer.ServeCoreAsync(serverSession, (request, _) =>
        {
            seen.Add(request.Operation);
            var code = request.Operation is MachineIpcOperation.StageStructuraRollback or MachineIpcOperation.StagePlatformRollback
                ? MachineDispatcherCode.UnsupportedOperation
                : MachineDispatcherCode.OperationFailed;
            return ValueTask.FromResult(Blocked(request, code));
        }, TimeSpan.FromSeconds(3)).AsTask();
        using var command = new MachineCommandClient(clientSession);

        foreach (var operation in new[] { MachineIpcOperation.Inspect, MachineIpcOperation.Prepare, MachineIpcOperation.Apply,
                      MachineIpcOperation.Reconcile, MachineIpcOperation.Remove, MachineIpcOperation.Restore,
                      MachineIpcOperation.StageVelopack, MachineIpcOperation.PrepareUserStateStage,
                      MachineIpcOperation.StageStructuraRollback, MachineIpcOperation.StagePlatformRollback,
                      MachineIpcOperation.Rollover })
        {
            var request = operation == MachineIpcOperation.Rollover
                ? new MachineIpcRequest(MachineIpcFrameCodec.CurrentVersion, Guid.NewGuid(), operation,
                    Guid.NewGuid(), Guid.NewGuid())
                : Request(operation);
            var result = await command.SendAsync(request, TimeSpan.FromSeconds(2));
            Assert.Equal(request.CorrelationId, result.CorrelationId);
            Assert.Equal(operation, result.Operation);
            var expectedCode = operation is MachineIpcOperation.StageStructuraRollback or MachineIpcOperation.StagePlatformRollback
                ? MachineDispatcherCode.UnsupportedOperation
                : MachineDispatcherCode.OperationFailed;
            Assert.Equal(expectedCode, result.Code);
        }
        var replay = Request(MachineIpcOperation.Inspect);
        _ = await command.SendAsync(replay, TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await command.SendAsync(replay, TimeSpan.FromSeconds(2)));
        command.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => server);
        Assert.Equal(new[] { MachineIpcOperation.Inspect, MachineIpcOperation.Prepare, MachineIpcOperation.Apply,
            MachineIpcOperation.Reconcile, MachineIpcOperation.Remove, MachineIpcOperation.Restore,
            MachineIpcOperation.StageVelopack, MachineIpcOperation.PrepareUserStateStage,
            MachineIpcOperation.StageStructuraRollback, MachineIpcOperation.StagePlatformRollback,
            MachineIpcOperation.Rollover,
            MachineIpcOperation.Inspect }, seen);
    }

    [Fact]
    public async Task ClientRejectsUnsupportedAndReplayedRequestsAndAbortsAfterUseFailure()
    {
        var (clientPipe, _) = Pair();
        using var command = new MachineCommandClient(Session(clientPipe));
        var unsupported = Request((MachineIpcOperation)999);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await command.SendAsync(unsupported, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ServerRejectsReplayedCorrelationAndOnlyDispatchesAllowedOperations()
    {
        var (clientPipe, serverPipe) = Pair();
        using var clientSession = Session(clientPipe);
        var serverSession = Session(serverPipe);
        var count = 0;
        var server = MachineCommandServer.ServeCoreAsync(serverSession, (request, _) =>
        {
            count++;
            return ValueTask.FromResult(Blocked(request, MachineDispatcherCode.OperationFailed));
        }, TimeSpan.FromSeconds(2)).AsTask();
        var first = Request(MachineIpcOperation.Inspect);
        await MachineIpcFrameCodec.WriteAsync(clientPipe.Stream, first);
        Assert.Equal(first.CorrelationId, (await MachineDispatcherResultFrameCodec.ReadAsync(clientPipe.Stream))!.CorrelationId);
        await MachineIpcFrameCodec.WriteAsync(clientPipe.Stream, first);
        await Assert.ThrowsAsync<InvalidDataException>(() => server);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ServerFailsClosedAtThePerSessionCommandLimit()
    {
        var (clientPipe, serverPipe) = Pair();
        using var clientSession = Session(clientPipe);
        var serverSession = Session(serverPipe);
        var count = 0;
        var server = MachineCommandServer.ServeCoreAsync(serverSession, (request, _) =>
        {
            count++;
            return ValueTask.FromResult(Blocked(request, MachineDispatcherCode.OperationFailed));
        }, TimeSpan.FromSeconds(2)).AsTask();

        for (var i = 0; i < MachineCommandServer.MaximumCommandsPerSession; i++)
        {
            var request = Request(MachineIpcOperation.Inspect);
            await MachineIpcFrameCodec.WriteAsync(clientPipe.Stream, request);
            Assert.Equal(request.CorrelationId, (await MachineDispatcherResultFrameCodec.ReadAsync(clientPipe.Stream))!.CorrelationId);
        }

        var overflow = Request(MachineIpcOperation.Inspect);
        await MachineIpcFrameCodec.WriteAsync(clientPipe.Stream, overflow);
        await Assert.ThrowsAsync<InvalidDataException>(() => server);
        Assert.Equal(MachineCommandServer.MaximumCommandsPerSession, count);
        Assert.True(serverPipe.Disposed);
    }

    [Fact]
    public async Task TimeoutAndCancellationAbortTheCommandSession()
    {
        var (timeoutPipe, _) = Pair();
        using (var client = new MachineCommandClient(Session(timeoutPipe)))
        {
            await Assert.ThrowsAsync<TimeoutException>(async () =>
                await client.SendAsync(Request(MachineIpcOperation.Inspect), TimeSpan.FromMilliseconds(30)));
            Assert.True(timeoutPipe.Disposed);
        }

        var (cancelPipe, _) = Pair();
        using var cancelled = new MachineCommandClient(Session(cancelPipe));
        using var source = new CancellationTokenSource();
        var pending = cancelled.SendAsync(Request(MachineIpcOperation.Inspect), TimeSpan.FromSeconds(2), source.Token).AsTask();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(cancelPipe.Disposed);
    }

    [Fact]
    public async Task SharedAuthenticatedSessionRejectsConcurrentClients()
    {
        var (clientPipe, peerPipe) = Pair();
        var session = Session(clientPipe);
        using var first = new MachineCommandClient(session);
        using var second = new MachineCommandClient(session);
        var request = Request(MachineIpcOperation.Inspect);
        var pending = first.SendAsync(request, TimeSpan.FromSeconds(2)).AsTask();
        var received = await MachineIpcFrameCodec.ReadAsync(peerPipe.Stream);
        Assert.Equal(request.CorrelationId, received!.CorrelationId);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await second.SendAsync(Request(MachineIpcOperation.Prepare), TimeSpan.FromSeconds(2)));

        await MachineDispatcherResultFrameCodec.WriteAsync(peerPipe.Stream, Blocked(request, MachineDispatcherCode.OperationFailed));
        Assert.Equal(request.CorrelationId, (await pending).CorrelationId);
        peerPipe.Dispose();
    }

    private static (TestPipe A, TestPipe B) Pair()
    {
        var aToB = Channel.CreateUnbounded<byte>();
        var bToA = Channel.CreateUnbounded<byte>();
        return (new TestPipe(new ChannelStream(bToA.Reader, aToB.Writer)), new TestPipe(new ChannelStream(aToB.Reader, bToA.Writer)));
    }

    private static MutualMachineAuthenticatedSession Session(TestPipe pipe) =>
        new(pipe, new SafeProcessHandle(new IntPtr(123), ownsHandle: false), (operation, token) => operation(token));

    private static MachineIpcRequest Request(MachineIpcOperation operation) => new(MachineIpcFrameCodec.CurrentVersion, Guid.NewGuid(), operation);

    private static MachineDispatcherResult Blocked(MachineIpcRequest request, MachineDispatcherCode code) =>
        new(MachineDispatcherResultFrameCodec.CurrentVersion, request.CorrelationId, request.Operation,
            MachineDispatcherStatus.Blocked, code);

    private sealed class TestPipe(Stream stream) : IMutualChannelPipe
    {
        public Stream Stream { get; } = stream;
        public bool Disposed { get; private set; }
        public SafePipeHandle Handle { get; } = new(IntPtr.Zero, ownsHandle: false);
        public ValueTask WaitForConnectionAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken) => Stream.ReadExactlyAsync(destination, cancellationToken);
        public ValueTask WriteExactlyAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken) => Stream.WriteAsync(source, cancellationToken);
        public void Dispose() { Disposed = true; Stream.Dispose(); }
    }

    private sealed class ChannelStream(ChannelReader<byte> reader, ChannelWriter<byte> writer) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ReadCoreAsync(buffer, cancellationToken);
        }
        private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var count = 0;
            while (count < buffer.Length && reader.TryRead(out var b)) buffer.Span[count++] = b;
            if (count != 0) return count;
            if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
            while (count < buffer.Length && reader.TryRead(out var b)) buffer.Span[count++] = b;
            return count;
        }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var value in buffer.Span) writer.TryWrite(value);
            return ValueTask.CompletedTask;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) writer.TryComplete(); base.Dispose(disposing); }
    }
}
