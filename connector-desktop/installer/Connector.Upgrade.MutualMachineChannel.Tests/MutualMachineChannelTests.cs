using System.Security;
using System.Security.Principal;
using Connector.Upgrade.MutualMachineChannel;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Connector.Upgrade.MutualMachineChannel.Tests;

public sealed class MutualMachineChannelTests
{
    private static readonly SecurityIdentifier UserSid = WindowsIdentity.GetCurrent().User!;
    private static readonly Guid Operation = Guid.NewGuid();

    [Fact]
    public async Task CallerAuthenticatesAThenRequiresFreshNonceEchoFromB()
    {
        var platform = new FakePlatform();
        var a = new FakePipe();
        var b = new FakePipe();
        platform.Server = a;
        platform.Client = b;
        a.OnWrite = bytes => b.Input = bytes.ToArray();
        var channel = new MutualMachineChannel(platform, new AllowCallerImage());

        await channel.AuthenticateCallerAsync(Operation, UserSid, new MutualChannelPipe(a), Handle(), TimeSpan.FromSeconds(2));

        Assert.True(platform.HelperIdentityChecked);
        Assert.True(a.Disposed);
        Assert.True(b.Disposed);
        Assert.Equal(32, a.LastWrite!.Length);
    }

    [Fact]
    public async Task OptInCallerHandshakeRetainsAuthenticatedAEndpointAndPeerHandle()
    {
        var platform = new FakePlatform();
        var a = new FakePipe();
        var b = new FakePipe();
        platform.Server = a;
        platform.Client = b;
        a.OnWrite = bytes => b.Input = bytes.ToArray();
        var process = Handle();
        var channel = new MutualMachineChannel(platform, new AllowCallerImage());

        using var session = await channel.AuthenticateCallerSessionAsync(
            Operation, UserSid, new MutualChannelPipe(a), process, TimeSpan.FromSeconds(2));

        Assert.True(platform.HelperIdentityChecked);
        Assert.False(a.Disposed);
        Assert.True(b.Disposed);
        Assert.Same(a.Stream, session.Stream);
        Assert.Same(process, session.Peer);
        session.Dispose();
        Assert.True(a.Disposed);
    }

    [Fact]
    public async Task CallerSessionPinsTheActualCurrentProcessAndSidBeforeGrantingAuthority()
    {
        var platform = new FakePlatform();
        var a = new FakePipe();
        var b = new FakePipe();
        platform.Server = a;
        platform.Client = b;
        a.OnWrite = bytes => b.Input = bytes.ToArray();
        var trust = new CaptureCallerImage();
        var channel = new MutualMachineChannel(platform, trust);

        using var session = await channel.AuthenticateCallerSessionAsync(
            Operation, UserSid, new MutualChannelPipe(a), Handle(), TimeSpan.FromSeconds(2));

        Assert.Equal(UserSid, trust.ExpectedUserSid);
        Assert.True(trust.ReceivedCurrentProcessHandle);
        Assert.False(a.Disposed);
        Assert.True(b.Disposed);
    }

    [Fact]
    public async Task MissingCallerImagePinFailsBeforeSessionAuthorityOrPipeHandshake()
    {
        var platform = new FakePlatform();
        var a = new FakePipe { WaitForever = true };
        platform.Server = a;
        var channel = new MutualMachineChannel(platform, UnavailableCallerImagePinSource.Instance);

        await Assert.ThrowsAsync<SecurityException>(async () =>
            await channel.AuthenticateCallerSessionAsync(Operation, UserSid,
                new MutualChannelPipe(a), Handle(), TimeSpan.FromSeconds(2)));

        Assert.Equal(0, platform.ConnectCount);
        Assert.True(a.Disposed);
    }

    [Fact]
    public async Task SpoofedAPeerIsRejectedBeforeNonceIsSentAndPipeCloses()
    {
        var platform = new FakePlatform { RejectHelper = true };
        var a = new FakePipe();
        platform.Server = a;
        var channel = new MutualMachineChannel(platform, new AllowCallerImage());

        await Assert.ThrowsAsync<SecurityException>(async () =>
            await channel.AuthenticateCallerAsync(Operation, UserSid, new MutualChannelPipe(a), Handle(), TimeSpan.FromSeconds(2)));

        Assert.Null(a.LastWrite);
        Assert.True(a.Disposed);
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("sid")]
    public async Task HelperDoesNotEchoNonceToWrongBClient(string rejection)
    {
        var platform = new FakePlatform { RejectCaller = rejection };
        var a = new FakePipe { Input = Enumerable.Repeat((byte)0x51, 32).ToArray() };
        var b = new FakePipe();
        platform.Client = a;
        platform.Server = b;
        var channel = new MutualMachineChannel(platform, new AllowCallerImage());

        await Assert.ThrowsAsync<SecurityException>(async () =>
            await channel.RunHelperAsync(Operation, UserSid, Handle(), TimeSpan.FromSeconds(2)));

        Assert.Null(b.LastWrite);
        Assert.True(a.Disposed);
        Assert.True(b.Disposed);
    }

    [Fact]
    public async Task ReplayedOrAlteredNonceIsRejected()
    {
        var platform = new FakePlatform();
        var a = new FakePipe();
        var b = new FakePipe { Input = new byte[32] };
        platform.Server = a;
        platform.Client = b;
        var channel = new MutualMachineChannel(platform, new AllowCallerImage());

        await Assert.ThrowsAsync<SecurityException>(async () =>
            await channel.AuthenticateCallerAsync(Operation, UserSid, new MutualChannelPipe(a), Handle(), TimeSpan.FromSeconds(2)));

        Assert.True(a.Disposed);
        Assert.True(b.Disposed);
    }

    [Fact]
    public async Task TimeoutClosesAWithoutReconnection()
    {
        var platform = new FakePlatform();
        var a = new FakePipe { WaitForever = true };
        platform.Server = a;
        var channel = new MutualMachineChannel(platform, new AllowCallerImage());

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await channel.AuthenticateCallerAsync(Operation, UserSid, new MutualChannelPipe(a), Handle(), TimeSpan.FromMilliseconds(30)));

        Assert.True(a.Disposed);
        Assert.Equal(0, platform.ConnectCount);
    }

    [Fact]
    public async Task ParentExitClosesAAndBImmediately()
    {
        var platform = new FakePlatform { PeerExited = true };
        var a = new FakePipe { WaitForever = true };
        platform.Server = a;
        var channel = new MutualMachineChannel(platform, new AllowCallerImage());

        await Assert.ThrowsAsync<SecurityException>(async () =>
            await channel.AuthenticateCallerAsync(Operation, UserSid, new MutualChannelPipe(a), Handle(), TimeSpan.FromSeconds(2)));

        Assert.True(a.Disposed);
    }

    [Fact]
    public async Task ProcessMonitorFaultCancelsPendingHandshake()
    {
        var platform = new FakePlatform { MonitorFault = true };
        var a = new FakePipe { WaitForever = true };
        platform.Server = a;
        var channel = new MutualMachineChannel(platform, new AllowCallerImage());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await channel.AuthenticateCallerAsync(Operation, UserSid, new MutualChannelPipe(a), Handle(), TimeSpan.FromSeconds(2)));

        Assert.True(a.Disposed);
    }

    [Fact]
    public async Task DefaultProductionPathFailsClosedWhenCallerImagePinSourceIsMissing()
    {
        var channel = new MutualMachineChannel();
        await Assert.ThrowsAsync<SecurityException>(async () =>
            await channel.RunHelperAsync(Operation, UserSid, Handle(), TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(WellKnownSidType.WorldSid)]
    [InlineData(WellKnownSidType.AuthenticatedUserSid)]
    [InlineData(WellKnownSidType.LocalSystemSid)]
    public void BroadAndSystemSidsAreRejectedBeforePipeCreation(WellKnownSidType sidType)
    {
        var sid = new SecurityIdentifier(sidType, null);
        Assert.Throws<ArgumentException>(() => MutualMachineChannel.GetPipeName("A", Operation, sid));
        Assert.Throws<ArgumentException>(() => WindowsMutualPipeFactory.Create("validation-only", sid));
    }

    [Fact]
    public void GuestAccountSidIsRejectedAsInitiatorAndAsPipePeer()
    {
        var guestSid = new SecurityIdentifier("S-1-5-21-100-200-300-501");

        Assert.Throws<ArgumentException>(() => MutualMachineChannel.GetPipeName("A", Operation, guestSid));
        Assert.Throws<SecurityException>(() => WindowsMutualNative.AssertClientTokenIdentity(
            guestSid, TokenImpersonationLevel.Impersonation, [], guestSid));
    }

    [Theory]
    [InlineData(WellKnownSidType.BuiltinGuestsSid)]
    [InlineData(WellKnownSidType.NetworkSid)]
    public void GuestAndNetworkTokenGroupsAreRejected(WellKnownSidType rejectedGroup)
    {
        var group = new SecurityIdentifier(rejectedGroup, null);
        IdentityReference[] groups = [group];

        Assert.Throws<SecurityException>(() => WindowsMutualNative.AssertClientTokenIdentity(
            UserSid, TokenImpersonationLevel.Impersonation, groups, UserSid));
    }

    [Fact]
    public void MatchingNonGuestCallerSidWithoutRejectedGroupsPassesTokenPolicy()
    {
        WindowsMutualNative.AssertClientTokenIdentity(
            UserSid, TokenImpersonationLevel.Impersonation, [], UserSid);
    }

    [Fact]
    public async Task InvalidHandshakeArgumentsStillCloseCallerPipe()
    {
        var pipe = new FakePipe();
        var channel = new MutualMachineChannel(new FakePlatform(), new AllowCallerImage());

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await channel.AuthenticateCallerAsync(Guid.Empty, UserSid, new MutualChannelPipe(pipe), Handle(), TimeSpan.FromSeconds(1)));

        Assert.True(pipe.Disposed);
    }

    private static SafeProcessHandle Handle() => new(new IntPtr(123), ownsHandle: false);

    private sealed class AllowCallerImage : ITrustedCallerImagePinSource
    {
        public void AssertTrustedCaller(SafeProcessHandle retainedCaller, SecurityIdentifier expectedUserSid) { }
    }

    private sealed class CaptureCallerImage : ITrustedCallerImagePinSource
    {
        public SecurityIdentifier? ExpectedUserSid { get; private set; }
        public bool ReceivedCurrentProcessHandle { get; private set; }
        public void AssertTrustedCaller(SafeProcessHandle retainedCaller, SecurityIdentifier expectedUserSid)
        {
            ExpectedUserSid = expectedUserSid;
            ReceivedCurrentProcessHandle = WindowsMutualNative.IsSameRunningProcess((uint)Environment.ProcessId, retainedCaller);
        }
    }

    private sealed class FakePlatform : IMutualMachineChannelPlatform
    {
        public FakePipe? Server { get; set; }
        public FakePipe? Client { get; set; }
        public string? RejectCaller { get; init; }
        public bool RejectHelper { get; init; }
        public bool PeerExited { get; init; }
        public bool MonitorFault { get; init; }
        public bool HelperIdentityChecked { get; private set; }
        public int ConnectCount { get; private set; }
        public IMutualChannelPipe CreateServer(string name, SecurityIdentifier userSid) => Server ??= new FakePipe();
        public ValueTask<IMutualChannelPipe> ConnectClientAsync(string name, CancellationToken cancellationToken)
        {
            ConnectCount++;
            return ValueTask.FromResult<IMutualChannelPipe>(Client ??= new FakePipe());
        }
        public ValueTask WaitForProcessExitAsync(SafeProcessHandle process, CancellationToken cancellationToken) =>
            MonitorFault ? ValueTask.FromException(new InvalidOperationException("monitor failed")) :
            PeerExited ? ValueTask.CompletedTask : new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        public void AssertConnectedHelper(SafePipeHandle connectedPipe, SafeProcessHandle launchedHelper)
        {
            HelperIdentityChecked = true;
            if (RejectHelper) throw new SecurityException("wrong helper pid");
        }
        public void AssertConnectedCaller(SafePipeHandle connectedPipe, SafeProcessHandle retainedCaller,
            SecurityIdentifier expectedUserSid, ITrustedCallerImagePinSource callerTrust)
        {
            if (RejectCaller == "pid") throw new SecurityException("wrong caller pid");
            if (RejectCaller == "sid") throw new SecurityException("wrong caller sid");
            callerTrust.AssertTrustedCaller(retainedCaller, expectedUserSid);
        }
    }

    private sealed class FakePipe : IMutualChannelPipe
    {
        public Stream Stream { get; } = new MemoryStream();
        public SafePipeHandle Handle { get; } = new(IntPtr.Zero, ownsHandle: false);
        public bool Disposed { get; private set; }
        public bool WaitForever { get; init; }
        public byte[] Input { get; set; } = [];
        public byte[]? LastWrite { get; private set; }
        public Action<byte[]>? OnWrite { get; set; }
        public ValueTask WaitForConnectionAsync(CancellationToken cancellationToken) =>
            WaitForever ? new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)) : ValueTask.CompletedTask;
        public ValueTask ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Input.Length != destination.Length) throw new EndOfStreamException();
            Input.CopyTo(destination);
            return ValueTask.CompletedTask;
        }
        public ValueTask WriteExactlyAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastWrite = source.ToArray();
            OnWrite?.Invoke(LastWrite);
            return ValueTask.CompletedTask;
        }
        public void Dispose() => Disposed = true;
    }
}
