using System.Net;
using System.Net.Http.Json;
using Connector.Access.Contracts;
using Connector.Network;
using Connector.SmbAccess;
using Platform.Connector.Core;
using Xunit;

namespace Connector.Upgrade.PlatformAccess.Tests;

public sealed class WindowsSmbAccessProbeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly IPAddress LocalOverlay = IPAddress.Parse("100.64.0.10");
    private const string DeviceId = "dev_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Share = @"\\100.64.0.20\projects";

    [Fact]
    public async Task Exact_grant_is_read_and_only_owned_mapping_is_removed()
    {
        var port = new FakeMappingPort();
        string? readRoot = null;
        var probe = CreateProbe(port, Receipt(), (root, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            readRoot = root;
            return ValueTask.FromResult(true);
        });

        var verified = await probe.VerifyAuthenticatedReadAsync(Request(), CancellationToken.None);

        Assert.True(verified);
        Assert.Equal("Z:" + Path.DirectorySeparatorChar, readRoot);
        Assert.Equal(Share, port.MountedShare);
        Assert.Equal(@"SMBHOST\cnb_device", port.ObservedUserName);
        Assert.Equal("temporary-secret", port.ObservedPassword);
        Assert.Equal(1, port.ExactUnmountCalls);
        Assert.Null(port.Target);
    }

    [Fact]
    public async Task Grant_without_readable_resource_fails_closed_without_mapping()
    {
        var port = new FakeMappingPort();
        var profile = Profile() with { Resources = [] };
        var receipt = Receipt() with { UserName = null, Password = null, Resources = [] };
        var probe = CreateProbe(port, receipt, (_, _) => ValueTask.FromResult(true));

        var verified = await probe.VerifyAuthenticatedReadAsync(
            Request() with { Profile = profile }, CancellationToken.None);

        Assert.False(verified);
        Assert.Equal(0, port.MountCalls);
        Assert.Equal(0, port.ExactUnmountCalls);
    }

    [Fact]
    public async Task Cancellation_after_mapping_still_performs_exact_cleanup()
    {
        var port = new FakeMappingPort();
        using var cancellation = new CancellationTokenSource();
        var probe = CreateProbe(port, Receipt(), (_, cancellationToken) =>
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(true);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await probe.VerifyAuthenticatedReadAsync(Request(), cancellation.Token));

        Assert.Equal(1, port.ExactUnmountCalls);
        Assert.Null(port.Target);
    }

    [Fact]
    public async Task Foreign_replacement_is_never_unmounted()
    {
        var port = new FakeMappingPort();
        var probe = CreateProbe(port, Receipt(), (_, _) =>
        {
            port.ReplaceWithForeign();
            return ValueTask.FromResult(true);
        });

        var verified = await probe.VerifyAuthenticatedReadAsync(Request(), CancellationToken.None);

        Assert.False(verified);
        Assert.Equal(0, port.ExactUnmountCalls);
        Assert.Equal(@"\\100.64.0.99\foreign", port.Target);
    }

    [Fact]
    public async Task Unconfirmed_cleanup_is_propagated_instead_of_reported_as_safe_failure()
    {
        var port = new FakeMappingPort { FailCleanup = true };
        var probe = CreateProbe(port, Receipt(), (_, _) => ValueTask.FromResult(true));

        var error = await Assert.ThrowsAsync<CommonSmbAccessException>(async () =>
            await probe.VerifyAuthenticatedReadAsync(Request(), CancellationToken.None));

        Assert.Equal("smb_route_cleanup_incomplete", error.Code);
        Assert.Equal(1, port.ExactUnmountCalls);
        Assert.Equal(Share, port.Target);
    }

    [Fact]
    public async Task Mismatched_device_is_rejected_before_credential_request()
    {
        var port = new FakeMappingPort();
        var transport = new FakeTransport(Receipt());
        var probe = CreateProbe(port, transport, (_, _) => ValueTask.FromResult(true));

        var verified = await probe.VerifyAuthenticatedReadAsync(
            Request() with { DeviceId = "dev_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
            CancellationToken.None);

        Assert.False(verified);
        Assert.Equal(0, transport.Calls);
        Assert.Equal(0, port.MountCalls);
    }

    [Fact]
    public async Task Stale_overlay_is_rejected_before_credential_request()
    {
        var port = new FakeMappingPort();
        var transport = new FakeTransport(Receipt());
        var probe = CreateProbe(port, transport, (_, _) => ValueTask.FromResult(true));

        var verified = await probe.VerifyAuthenticatedReadAsync(
            Request() with { Overlay = Overlay() with { ObservedAtUtc = Now.AddMinutes(-3) } },
            CancellationToken.None);

        Assert.False(verified);
        Assert.Equal(0, transport.Calls);
        Assert.Equal(0, port.MountCalls);
    }

    [Fact]
    public async Task Overlay_losing_readiness_after_read_fails_and_removes_only_owned_mapping()
    {
        var port = new FakeMappingPort();
        var live = new SequencedOverlay(
            Overlay(),
            Overlay(),
            Overlay() with { Status = NetworkServiceStatus.Disconnected, ManagementConnected = false });
        var probe = CreateProbe(
            port,
            new FakeTransport(Receipt()),
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(true);
            },
            live.GetAsync);

        var verified = await probe.VerifyAuthenticatedReadAsync(Request(), CancellationToken.None);

        Assert.False(verified);
        Assert.Equal(3, live.Calls);
        Assert.Equal(1, port.ExactUnmountCalls);
        Assert.Null(port.Target);
    }

    private static WindowsSmbAccessProbe CreateProbe(
        FakeMappingPort port,
        ConnectorSmbAccess receipt,
        Func<string, CancellationToken, ValueTask<bool>> read) =>
        CreateProbe(port, new FakeTransport(receipt), read);

    private static WindowsSmbAccessProbe CreateProbe(
        FakeMappingPort port,
        ICommonConnectorRequestTransport transport,
        Func<string, CancellationToken, ValueTask<bool>> read,
        Func<CancellationToken, ValueTask<NetworkOverlaySnapshot>>? liveOverlay = null) =>
        new(
            transport,
            address => address.Equals(IPAddress.Parse("100.64.0.20")),
            new FixedTimeProvider(Now),
            liveOverlay ?? (_ => ValueTask.FromResult(Overlay())),
            liveness =>
            {
                port.LiveOverlayReader = liveness;
                return port;
            },
            read);

    private static ExactSmbAccessProbeRequest Request() =>
        new(DeviceId, 7, Profile(), Overlay());

    private static DeviceAccessProfile Profile() => new(
        DeviceAccessProtocol.Version,
        DeviceId,
        "user-1",
        "company-1",
        7,
        7,
        Now.AddMinutes(30),
        [new ModuleGrant(ConnectorProduct.Platform, "files", [ConnectorPermission.Read])],
        [new ResourceGrant("projects", "smb", null, [ConnectorPermission.Read])]);

    private static NetworkOverlaySnapshot Overlay() => new(
        NetworkServiceStatus.Ready,
        true,
        true,
        true,
        [LocalOverlay],
        Now,
        "ready",
        new Uri("https://netbird.example.test/"));

    private static ConnectorSmbAccess Receipt() => new(
        DeviceAccessProtocol.Version,
        DeviceId,
        7,
        Now.AddMinutes(2),
        @"SMBHOST\cnb_device",
        "temporary-secret",
        [new ConnectorSmbResourceAccess(
            "projects",
            "smb",
            null,
            [ConnectorPermission.Read],
            Share)]);

    private sealed class FakeTransport(ConnectorSmbAccess receipt) : ICommonConnectorRequestTransport
    {
        public int Calls { get; private set; }

        public Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string relativeUri,
            object? body,
            IReadOnlyDictionary<string, string> managedHeaders,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Get, method);
            Assert.Equal("smb/access", relativeUri);
            Assert.Null(body);
            Assert.Empty(managedHeaders);
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(receipt),
            });
        }
    }

    private sealed class FakeMappingPort : IWindowsSmbMappingPort
    {
        public event Action? NetworkChanged { add { } remove { } }
        public NetworkOverlaySnapshot LiveOverlay { get; set; } = Overlay();
        public Func<CancellationToken, ValueTask<NetworkOverlaySnapshot>>? LiveOverlayReader { get; set; }
        public string? Target { get; private set; }
        public string? MountedShare { get; private set; }
        public string? ObservedUserName { get; private set; }
        public string? ObservedPassword { get; private set; }
        public int MountCalls { get; private set; }
        public int ExactUnmountCalls { get; private set; }
        public bool FailCleanup { get; init; }

        public ValueTask<NetworkOverlaySnapshot> GetLiveOverlayAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return LiveOverlayReader is null
                ? ValueTask.FromResult(LiveOverlay)
                : LiveOverlayReader(cancellationToken);
        }

        public ValueTask<string?> GetTargetAsync(string drive, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Target);
        }

        public ValueTask<bool> IsRouteAllowedAsync(
            IPAddress destination,
            IReadOnlyList<IPAddress> expectedOverlayAddresses,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                destination.Equals(IPAddress.Parse("100.64.0.20")) &&
                expectedOverlayAddresses.SequenceEqual([LocalOverlay]));
        }

        public ValueTask MountAsync(
            string drive,
            string shareUnc,
            string userName,
            string password,
            IPAddress destination,
            IReadOnlyList<IPAddress> expectedOverlayAddresses,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("Z:", drive);
            Assert.Null(Target);
            MountCalls++;
            MountedShare = shareUnc;
            ObservedUserName = userName;
            ObservedPassword = password;
            Target = shareUnc;
            return ValueTask.CompletedTask;
        }

        public ValueTask UnmountAsync(string drive, CancellationToken cancellationToken) =>
            ValueTask.FromException(new NotSupportedException());

        public ValueTask<SmbExactMappingRemoval> TryUnmountExactAsync(
            string drive,
            string expectedShareUnc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Target is null) return ValueTask.FromResult(SmbExactMappingRemoval.Missing);
            if (!string.Equals(Target, expectedShareUnc, StringComparison.OrdinalIgnoreCase))
                return ValueTask.FromResult(SmbExactMappingRemoval.TargetChanged);
            ExactUnmountCalls++;
            if (FailCleanup)
                return ValueTask.FromException<SmbExactMappingRemoval>(new InvalidOperationException("cleanup refused"));
            Target = null;
            return ValueTask.FromResult(SmbExactMappingRemoval.Removed);
        }

        public void ReplaceWithForeign() => Target = @"\\100.64.0.99\foreign";
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SequencedOverlay(params NetworkOverlaySnapshot[] snapshots)
    {
        private readonly Queue<NetworkOverlaySnapshot> _snapshots = new(snapshots);
        public int Calls { get; private set; }

        public ValueTask<NetworkOverlaySnapshot> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            if (_snapshots.Count == 0) throw new InvalidOperationException("No live overlay snapshot remains.");
            return ValueTask.FromResult(_snapshots.Dequeue());
        }
    }
}
