using System.Security;
using System.Security.Principal;
using Connector.Upgrade.HelperLauncher;
using Connector.Upgrade.MutualMachineChannel;
using MutualChannel = global::Connector.Upgrade.MutualMachineChannel.MutualMachineChannel;
using Xunit;

namespace Connector.Upgrade.OriginalUserCaller.Tests;

public sealed class OriginalUserCallerFlowTests
{
    private static readonly Guid Operation = Guid.Parse("d07ca329-c9a1-4c5f-ae44-6b4b2f3318cf");
    private static SecurityIdentifier CurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return identity.User ?? throw new InvalidOperationException("Test token has no SID.");
    }

    [Fact]
    public async Task OpensInTrustOrderAndBindsPipeAndLaunchToTokenSidAndOperation()
    {
        var sid = CurrentSid();
        var events = new List<string>();
        var pin = new HelperImagePin(Path.Combine(Path.GetTempPath(), "helper.exe"), new string('a', 64), new string('b', 40));
        var pipe = new Resource(events, "pipe");
        var process = new Resource(events, "process");
        var session = new Resource(events, "session");
        object client = new();
        var result = await Flow(sid, events, pin, pipe, process, session, client,
            (name, gotSid, operation) => Assert.Equal(MutualChannel.GetPipeName("A", Operation, sid), name),
            (gotPin, name, operation, gotSid) =>
            {
                Assert.Same(pin, gotPin);
                Assert.Equal(MutualChannel.GetPipeName("A", Operation, sid), name);
                Assert.Equal(sid, gotSid);
                Assert.Equal(Operation, operation);
            },
            (operation, gotSid, gotPipe, gotProcess, _, _) =>
            {
                Assert.Equal(sid, gotSid);
                Assert.Same(pipe, gotPipe);
                Assert.Same(process, gotProcess);
                return ValueTask.FromResult(session);
            });

        Assert.Same(client, result);
        Assert.Equal(new[] { "sid", "pin", "pipe", "launch", "authenticate", "client", "process.dispose" }, events);
        Assert.Equal(0, pipe.DisposeCount); // The authenticated session owns A.
    }

    [Fact]
    public async Task RefusesUnpinnedReleaseBeforePipeOrHelperLaunch()
    {
        var events = new List<string>();
        var error = await Assert.ThrowsAsync<SecurityException>(async () => await OriginalUserCallerFlow.OpenAsync<Resource, Resource, Resource, object>(
            () => { events.Add("sid"); return CurrentSid(); },
            () => { events.Add("pin"); throw new SecurityException("blank embedded key"); },
            (_, _, _) => { events.Add("pipe"); return new Resource(events, "pipe"); },
            (_, _, _, _) => { events.Add("launch"); return new Resource(events, "process"); },
            (_, _, _, _, _, _) => throw new InvalidOperationException(),
            _ => new object(), Operation, TimeSpan.FromSeconds(5), CancellationToken.None));

        Assert.Contains("blank embedded key", error.Message);
        Assert.Equal(new[] { "sid", "pin" }, events);
    }

    [Fact]
    public async Task TimeoutDisposesPipeAndRetainedProcess()
    {
        var events = new List<string>();
        var pipe = new Resource(events, "pipe");
        var process = new Resource(events, "process");
        await Assert.ThrowsAsync<TimeoutException>(async () => await Flow(CurrentSid(), events,
            new HelperImagePin(Path.Combine(Path.GetTempPath(), "helper.exe"), new string('a', 64), new string('b', 40)),
            pipe, process, new Resource(events, "session"), new object(), (_, _, _) => { }, (_, _, _, _) => { },
            (_, _, _, _, _, _) => throw new TimeoutException("handshake timeout")));

        Assert.Equal(1, pipe.DisposeCount);
        Assert.Equal(1, process.DisposeCount);
        Assert.Contains("pipe.dispose", events);
        Assert.Contains("process.dispose", events);
    }

    [Fact]
    public async Task AlreadyCancelledRequestNeverCreatesPipeOrOpensUac()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var events = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await OriginalUserCallerFlow.OpenAsync<Resource, Resource, Resource, object>(
                () => { events.Add("sid"); return CurrentSid(); },
                () => { events.Add("pin"); throw new InvalidOperationException(); },
                (_, _, _) => { events.Add("pipe"); return new Resource(events, "pipe"); },
                (_, _, _, _) => { events.Add("launch"); return new Resource(events, "process"); },
                (_, _, _, _, _, _) => throw new InvalidOperationException(),
                _ => new object(), Operation, TimeSpan.FromSeconds(5), cancelled.Token));
        Assert.Empty(events);
    }

    [Fact]
    public async Task CancellationAfterPipeCreationDisposesPipeWithoutOpeningUac()
    {
        using var cancelled = new CancellationTokenSource();
        var events = new List<string>();
        var pipe = new Resource(events, "pipe");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await OriginalUserCallerFlow.OpenAsync<Resource, Resource, Resource, object>(
                () => { events.Add("sid"); return CurrentSid(); },
                () => { events.Add("pin"); return new HelperImagePin(Path.Combine(Path.GetTempPath(), "helper.exe"), new string('a', 64), new string('b', 40)); },
                (_, _, _) => { events.Add("pipe"); cancelled.Cancel(); return pipe; },
                (_, _, _, _) => { events.Add("launch"); return new Resource(events, "process"); },
                (_, _, _, _, _, _) => throw new InvalidOperationException(),
                _ => new object(), Operation, TimeSpan.FromSeconds(5), cancelled.Token));
        Assert.Equal(new[] { "sid", "pin", "pipe", "pipe.dispose" }, events);
    }

    private static ValueTask<object> Flow(SecurityIdentifier sid, List<string> events, HelperImagePin pin,
        Resource pipe, Resource process, Resource session, object client,
        Action<string, SecurityIdentifier, Guid> onPipe,
        Action<HelperImagePin, string, Guid, SecurityIdentifier> onLaunch,
        Func<Guid, SecurityIdentifier, Resource, Resource, TimeSpan, CancellationToken, ValueTask<Resource>> onAuthenticate) =>
        OriginalUserCallerFlow.OpenAsync<Resource, Resource, Resource, object>(
            () => { events.Add("sid"); return sid; },
            () => { events.Add("pin"); return pin; },
            (name, gotSid, operation) => { events.Add("pipe"); onPipe(name, gotSid, operation); return pipe; },
            (gotPin, name, operation, gotSid) => { events.Add("launch"); onLaunch(gotPin, name, operation, gotSid); return process; },
            (operation, gotSid, gotPipe, gotProcess, timeout, token) =>
            { events.Add("authenticate"); return onAuthenticate(operation, gotSid, gotPipe, gotProcess, timeout, token); },
            value => { events.Add("client"); Assert.Same(session, value); return client; },
            Operation, TimeSpan.FromSeconds(20), CancellationToken.None);

    private sealed class Resource(List<string> events, string name) : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() { DisposeCount++; events.Add(name + ".dispose"); }
    }
}
