using System.Security.Principal;
using Connector.Upgrade.HelperLauncher;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Connector.Upgrade.MachineSession.Tests;

public sealed class MachineSessionTransportTests
{
    private static readonly HelperImagePin Image = new(Path.GetFullPath("helper.exe"), new string('a', 64), new string('b', 40));

    [Fact]
    public async Task ProbeCreatesPipeBeforeLaunchAndObservesElevatedAdminWithDifferentSidThenClosesHandles()
    {
        var fixture = new Fixture(helperSid: new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

        var observation = await fixture.Probe.ProbeAsync(Image, TimeSpan.FromSeconds(2));

        Assert.True(observation.PipeConnected);
        Assert.True(observation.BootstrapMarkerReceived);
        Assert.True(observation.PipeClientMatchesLaunchedProcess);
        Assert.True(observation.ElevatedAdministratorToken);
        Assert.False(observation.IsAuthorized);
        Assert.NotEqual(fixture.InitiatingSid, fixture.HelperSid);
        Assert.True(fixture.Pipes.CreatedBeforeLaunch);
        Assert.Equal(fixture.InitiatingSid, fixture.Pipes.LastSid);
        Assert.True(fixture.Identity.Called);
        Assert.Equal(1, fixture.Launcher.LaunchCalls);
        Assert.True(fixture.Pipe.Disposed);
        Assert.True(fixture.Launcher.LastProcess!.IsClosed);
    }

    [Fact]
    public async Task TimeoutDisposesPipeAndRetainedProcess()
    {
        var fixture = new Fixture(pipe: new FakePipe(blockConnection: true));

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await fixture.Probe.ProbeAsync(Image, TimeSpan.FromMilliseconds(25)));

        Assert.True(fixture.Pipe.Disposed);
        Assert.True(fixture.Launcher.LastProcess!.IsClosed);
        Assert.False(fixture.Identity.Called);
    }

    [Fact]
    public async Task WrongMarkerDisposesPipeAndRetainedProcess()
    {
        var fixture = new Fixture(pipe: new FakePipe(marker: 0x01));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Probe.ProbeAsync(Image, TimeSpan.FromSeconds(2)));

        Assert.True(fixture.Pipe.Disposed);
        Assert.True(fixture.Launcher.LastProcess!.IsClosed);
        Assert.False(fixture.Identity.Called);
    }

    [Fact]
    public async Task PidMismatchReturnsNonAuthorizingObservationAndDisposesResources()
    {
        var fixture = new Fixture(identity: new FakeIdentity { PipePidMatches = false });

        var observation = await fixture.Probe.ProbeAsync(Image, TimeSpan.FromSeconds(2));

        Assert.False(observation.PipeClientMatchesLaunchedProcess);
        Assert.False(observation.ElevatedAdministratorToken);
        Assert.False(observation.IsAuthorized);
        Assert.True(fixture.Pipe.Disposed);
        Assert.True(fixture.Launcher.LastProcess!.IsClosed);
    }

    [Fact]
    public async Task ExitedHelperReturnsFailedIdentityObservationAndClosesResources()
    {
        var fixture = new Fixture(identity: new FakeIdentity { ProcessRunning = false });

        var observation = await fixture.Probe.ProbeAsync(Image, TimeSpan.FromSeconds(2));

        Assert.True(fixture.Identity.Called);
        Assert.False(observation.PipeClientMatchesLaunchedProcess);
        Assert.False(observation.ElevatedAdministratorToken);
        Assert.False(observation.IsAuthorized);
        Assert.True(fixture.Pipe.Disposed);
        Assert.True(fixture.Launcher.LastProcess!.IsClosed);
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsTimeout()
    {
        var fixture = new Fixture(pipe: new FakePipe(blockConnection: true));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Probe.ProbeAsync(Image, TimeSpan.FromSeconds(2), cancellation.Token));

        Assert.Equal(0, fixture.Launcher.LaunchCalls);
        Assert.False(fixture.Pipes.Created);
    }

    [Fact]
    public async Task HandshakeTimeoutStartsAfterLauncherReturns()
    {
        var fixture = new Fixture(launcherDelay: TimeSpan.FromMilliseconds(80));

        var observation = await fixture.Probe.ProbeAsync(Image, TimeSpan.FromMilliseconds(20));

        Assert.True(observation.BootstrapMarkerReceived);
        Assert.False(observation.IsAuthorized);
        Assert.True(fixture.Pipe.Disposed);
        Assert.True(fixture.Launcher.LastProcess!.IsClosed);
    }

    private sealed class Fixture
    {
        public SecurityIdentifier InitiatingSid { get; } = WindowsIdentity.GetCurrent().User!;
        public SecurityIdentifier? HelperSid { get; }
        public FakePipe Pipe { get; }
        public FakePipeFactory Pipes { get; }
        public FakeLauncher Launcher { get; }
        public FakeIdentity Identity { get; }
        public MachineSessionProbe Probe { get; }
        public TimeSpan LauncherDelay { get; }

        public Fixture(
            FakePipe? pipe = null,
            FakeIdentity? identity = null,
            SecurityIdentifier? helperSid = null,
            TimeSpan? launcherDelay = null)
        {
            Pipe = pipe ?? new FakePipe();
            Identity = identity ?? new FakeIdentity();
            HelperSid = helperSid;
            LauncherDelay = launcherDelay ?? TimeSpan.Zero;
            Pipes = new FakePipeFactory(this);
            Launcher = new FakeLauncher(this);
            Probe = new MachineSessionProbe(Pipes, Launcher, Identity, () => InitiatingSid);
        }
    }

    private sealed class FakePipeFactory(Fixture fixture) : IMachineSessionPipeFactory
    {
        public bool Created { get; private set; }
        public bool CreatedBeforeLaunch { get; private set; }
        public SecurityIdentifier? LastSid { get; private set; }
        public IMachineSessionPipe Create(Guid operationId, SecurityIdentifier initiatingSid)
        {
            Created = true;
            CreatedBeforeLaunch = fixture.Launcher.LaunchCalls == 0;
            LastSid = initiatingSid;
            return fixture.Pipe;
        }
    }

    private sealed class FakeLauncher(Fixture fixture) : IMachineSessionLauncher
    {
        public int LaunchCalls { get; private set; }
        public SafeProcessHandle? LastProcess { get; private set; }

        public SafeProcessHandle Launch(HelperImagePin image, string pipeName, Guid operationId)
        {
            LaunchCalls++;
            if (!fixture.Pipes.Created)
                throw new InvalidOperationException("Pipe must be created before launching the helper.");
            if (fixture.LauncherDelay > TimeSpan.Zero)
                Thread.Sleep(fixture.LauncherDelay);
            LastProcess = new SafeProcessHandle(new IntPtr(31337), ownsHandle: false);
            return LastProcess;
        }
    }

    private sealed class FakeIdentity : IMachineSessionIdentity
    {
        public bool PipePidMatches { get; init; } = true;
        public bool ProcessRunning { get; init; } = true;
        public bool ElevatedAdministrator { get; init; } = true;
        public bool Called { get; private set; }

        public MachineSessionIdentityEvidence ObserveLaunchedElevatedPeer(SafePipeHandle connectedPipe, SafeProcessHandle launchedProcess)
        {
            Called = true;
            var processAndPeerMatch = PipePidMatches && ProcessRunning && !connectedPipe.IsClosed && !launchedProcess.IsClosed;
            return new MachineSessionIdentityEvidence(
                processAndPeerMatch,
                processAndPeerMatch && ElevatedAdministrator);
        }
    }

    private sealed class FakePipe : IMachineSessionPipe
    {
        private readonly byte _marker;
        private readonly bool _blockConnection;
        private readonly SafePipeHandle _handle = new(new IntPtr(41237), ownsHandle: false);

        public FakePipe(byte marker = MachineSessionProbe.BootstrapMarker, bool blockConnection = false)
        {
            _marker = marker;
            _blockConnection = blockConnection;
        }

        public SafePipeHandle Handle => _handle;
        public bool Disposed { get; private set; }

        public async ValueTask WaitForConnectionAsync(CancellationToken cancellationToken)
        {
            if (_blockConnection)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            buffer.Span[0] = _marker;
            return ValueTask.FromResult(1);
        }

        public void Dispose() => Disposed = true;
    }
}
