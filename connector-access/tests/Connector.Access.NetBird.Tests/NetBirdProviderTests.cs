using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Connector.Access.Contracts;
using Connector.Access.NetBird;
using Microsoft.AspNetCore.DataProtection;

namespace Connector.Access.NetBird.Tests;

public sealed class NetBirdProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "connector-netbird-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task UnknownPeer_KeepsOneEncryptedSetupKeyAcrossDispatchRetries()
    {
        var fixture = new NetBirdFixtureHandler();
        var provider = CreateProvider(fixture);
        var command = Apply(1);

        await Assert.ThrowsAsync<NetBirdPeerNotReadyException>(() => provider.ApplyAsync(command, default).AsTask());
        await Assert.ThrowsAsync<NetBirdPeerNotReadyException>(() => provider.ApplyAsync(command, default).AsTask());
        var bootstrap = await provider.GetAsync(Device(1), default);

        Assert.True(bootstrap.IsSuccess);
        Assert.Equal("fixture-setup-key", bootstrap.Value!.SetupKey);
        Assert.Equal(1, fixture.SetupKeyCreateCount);
        Assert.Equal(new[] { "g1" }, fixture.SetupKeyAutoGroups);
        var stateJson = Assert.Single(Directory.GetFiles(_root, "*.json"));
        Assert.DoesNotContain("fixture-setup-key", await File.ReadAllTextAsync(stateJson));
    }

    [Fact]
    public async Task Receipt_IsReturnedOnlyAfterConnectedPeerIpAndPolicyAreConfirmed()
    {
        var fixture = new NetBirdFixtureHandler();
        var provider = CreateProvider(fixture);
        var command = Apply(1);
        await Assert.ThrowsAsync<NetBirdPeerNotReadyException>(() => provider.ApplyAsync(command, default).AsTask());
        fixture.PeerExists = true;
        fixture.PeerConnected = true;

        var receipt = await provider.ApplyAsync(command, default);

        Assert.Equal(command.CommandId, receipt.CommandId);
        Assert.Equal(1, receipt.AppliedRevision);
        Assert.True(fixture.PolicyExists);
        Assert.True(fixture.SetupKeyRevoked);
        var bootstrap = await provider.GetAsync(Device(1), default);
        Assert.Equal("vpn_bootstrap_consumed", bootstrap.Failure!.Code);
    }

    [Fact]
    public async Task State_IsReadyOnlyForFreshOwnedPeerGroupAndPolicy()
    {
        var fixture = new NetBirdFixtureHandler();
        var provider = CreateProvider(fixture);
        await Assert.ThrowsAsync<NetBirdPeerNotReadyException>(() => provider.ApplyAsync(Apply(1), default).AsTask());
        fixture.PeerExists = true;
        fixture.PeerConnected = true;
        await provider.ApplyAsync(Apply(1), default);

        var ready = await provider.GetStateAsync(Device(1), default);

        Assert.True(ready.IsSuccess);
        Assert.Equal("ready", ready.Value!.Status);
        Assert.Equal("peer-1", ready.Value.PeerId);
        Assert.Equal("https://netbird.test", ready.Value.ManagementUri);
        Assert.Equal("100.90.0.22", Assert.Single(ready.Value.ExpectedAssignedAddresses));

        fixture.PeerConnected = false;
        var unknown = await provider.GetStateAsync(Device(1), default);
        Assert.Equal("unknown", unknown.Value!.Status);
        Assert.Null(unknown.Value.PeerId);
        Assert.Empty(unknown.Value.ExpectedAssignedAddresses);
    }

    [Fact]
    public async Task State_RejectsAddressReassignmentUntilProviderReconcilesThePeer()
    {
        var fixture = new NetBirdFixtureHandler
        {
            PeerExists = true,
            PeerConnected = true,
            PeerIpv6 = "fd00::22",
        };
        var provider = CreateProvider(fixture);
        await provider.ApplyAsync(Apply(1), default);
        Assert.Equal("ready", (await provider.GetStateAsync(Device(1), default)).Value!.Status);

        fixture.PeerIpv4 = "100.90.0.23";
        var changedIpv4 = await provider.GetStateAsync(Device(1), default);
        Assert.Equal("unknown", changedIpv4.Value!.Status);
        Assert.Null(changedIpv4.Value.PeerId);
        Assert.Empty(changedIpv4.Value.ExpectedAssignedAddresses);

        fixture.PeerIpv4 = "100.90.0.22";
        fixture.PeerIpv6 = "fd00::23";
        var changedIpv6 = await provider.GetStateAsync(Device(1), default);
        Assert.Equal("unknown", changedIpv6.Value!.Status);
        Assert.Null(changedIpv6.Value.PeerId);
        Assert.Empty(changedIpv6.Value.ExpectedAssignedAddresses);

        fixture.PeerIpv6 = "fd00::22";
        Assert.Equal("ready", (await provider.GetStateAsync(Device(1), default)).Value!.Status);

        fixture.ResponsePeerId = "peer-2";
        var changedPeerId = await provider.GetStateAsync(Device(1), default);
        Assert.Equal("unknown", changedPeerId.Value!.Status);
        Assert.Null(changedPeerId.Value.PeerId);
        Assert.Empty(changedPeerId.Value.ExpectedAssignedAddresses);
    }

    [Fact]
    public async Task State_RechecksPermissiveAllPolicyAndCannotReportReady()
    {
        var fixture = new NetBirdFixtureHandler();
        var provider = CreateProvider(fixture);
        await Assert.ThrowsAsync<NetBirdPeerNotReadyException>(() => provider.ApplyAsync(Apply(1), default).AsTask());
        fixture.PeerExists = true;
        fixture.PeerConnected = true;
        await provider.ApplyAsync(Apply(1), default);
        fixture.UnsafeAllPolicy = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetStateAsync(Device(1), default).AsTask());
    }

    [Theory]
    [InlineData("ports")]
    [InlineData("protocol")]
    [InlineData("bidirectional")]
    [InlineData("destination")]
    [InlineData("revision")]
    public async Task State_RejectsAnyMutationOfAppliedPolicy(string mutation)
    {
        var fixture = new NetBirdFixtureHandler();
        var provider = CreateProvider(fixture);
        await Assert.ThrowsAsync<NetBirdPeerNotReadyException>(() => provider.ApplyAsync(Apply(1), default).AsTask());
        fixture.PeerExists = true;
        fixture.PeerConnected = true;
        await provider.ApplyAsync(Apply(1), default);
        fixture.MutatePolicy(mutation);

        var state = await provider.GetStateAsync(Device(1), default);

        Assert.True(state.IsSuccess);
        Assert.Equal("unknown", state.Value!.Status);
        Assert.Null(state.Value.PeerId);
        Assert.Empty(state.Value.ExpectedAssignedAddresses);
    }

    [Fact]
    public async Task RevokeDeletesPolicyAndPeer_AndStaleApplyCannotRestoreEither()
    {
        var fixture = new NetBirdFixtureHandler();
        var provider = CreateProvider(fixture);
        await Assert.ThrowsAsync<NetBirdPeerNotReadyException>(() => provider.ApplyAsync(Apply(1), default).AsTask());
        fixture.PeerExists = true;
        fixture.PeerConnected = true;
        await provider.ApplyAsync(Apply(1), default);
        var policyWritesBeforeRevoke = fixture.PolicyWriteCount;

        var receipt = await provider.RevokeAsync(Revoke(2), default);
        await Assert.ThrowsAsync<NetBirdRevisionRejectedException>(() => provider.ApplyAsync(Apply(1), default).AsTask());

        Assert.Equal(DeviceAccessProviderCommandKind.Revoke, receipt.Kind);
        Assert.False(fixture.PolicyExists);
        Assert.False(fixture.PeerExists);
        Assert.Equal(policyWritesBeforeRevoke, fixture.PolicyWriteCount);
    }

    [Fact]
    public async Task ManagementTimeout_CannotProduceAnAppliedReceipt()
    {
        var provider = CreateProvider(new TimeoutHandler());

        await Assert.ThrowsAsync<TaskCanceledException>(() => provider.ApplyAsync(Apply(1), default).AsTask());
    }

    [Fact]
    public async Task PermissiveAllPolicy_DeniesBootstrapBeforeASetupKeyIsCreated()
    {
        var fixture = new NetBirdFixtureHandler { UnsafeAllPolicy = true };
        var provider = CreateProvider(fixture);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ApplyAsync(Apply(1), default).AsTask());

        Assert.Equal(0, fixture.SetupKeyCreateCount);
        Assert.False(fixture.PeerExists);
    }

    [Fact]
    public async Task ConfiguredDnsGroup_IsAddedOnlyToSetupKeyAndNotDeviceTrafficPolicy()
    {
        var fixture = new NetBirdFixtureHandler { DnsGroupId = "gdns" };
        var provider = CreateProvider(fixture, "gdns");
        var command = Apply(1);

        await Assert.ThrowsAsync<NetBirdPeerNotReadyException>(() => provider.ApplyAsync(command, default).AsTask());
        await Assert.ThrowsAsync<NetBirdPeerNotReadyException>(() => provider.ApplyAsync(command, default).AsTask());
        Assert.Equal(1, fixture.SetupKeyCreateCount);
        Assert.Equal(new[] { "g1", "gdns" }, fixture.SetupKeyAutoGroups);

        fixture.PeerExists = true;
        fixture.PeerConnected = true;
        await provider.ApplyAsync(command, default);

        var policy = JsonNode.Parse(fixture.AppliedPolicyJson!)!;
        var sources = policy["rules"]![0]!["sources"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "g1" }, sources);
        Assert.Equal(new[] { "g1", "gdns" }, fixture.RevokedSetupKeyAutoGroups);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("bidirectional-destination")]
    [InlineData("one-way-destination")]
    public async Task ConfiguredDnsGroup_EnabledAcceptTrafficPolicyDeniesBootstrap(string reference)
    {
        var fixture = new NetBirdFixtureHandler
        {
            DnsGroupId = "gdns",
            UnsafeDnsGroupPolicyReference = reference,
        };
        var provider = CreateProvider(fixture, "gdns");

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ApplyAsync(Apply(1), default).AsTask());

        Assert.Equal(0, fixture.SetupKeyCreateCount);
        Assert.False(fixture.PeerExists);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfiguredDnsGroup_MustExistExactlyOnce(bool duplicate)
    {
        var fixture = new NetBirdFixtureHandler
        {
            DnsGroupId = "gdns",
            DuplicateDnsGroup = duplicate,
            DnsGroupAvailable = duplicate,
        };
        var provider = CreateProvider(fixture, "gdns");

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ApplyAsync(Apply(1), default).AsTask());

        Assert.Equal(0, fixture.SetupKeyCreateCount);
    }

    private NetBirdDeviceAccessGrantProvider CreateProvider(HttpMessageHandler handler, string? dnsDistributionGroupId = null)
    {
        Directory.CreateDirectory(_root);
        var options = new NetBirdOptions
        {
            ManagementUri = new Uri("https://netbird.test/"),
            AccessToken = "fixture-service-token",
            StateDirectory = _root,
            AccessBindings =
            [
                new NetBirdAccessBinding
                {
                    Product = ConnectorProduct.Structura,
                    ModuleId = "sync",
                    DestinationGroupId = "structura-services",
                    Protocol = "tcp",
                    Ports = [443],
                },
            ],
            DnsDistributionGroupId = dnsDistributionGroupId,
        };
        return new NetBirdDeviceAccessGrantProvider(
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) },
            options,
            DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys"))));
    }

    private static DeviceAccessProviderCommand Apply(long revision) => new(
        "apply-" + revision,
        "vpn",
        DeviceAccessProviderCommandKind.Apply,
        "device-1",
        "user-1",
        "company-1",
        revision,
        [new ModuleGrant(ConnectorProduct.Structura, "sync", [ConnectorPermission.Read])],
        []);

    private static DeviceAccessProviderCommand Revoke(long revision) => new(
        "revoke-" + revision,
        "vpn",
        DeviceAccessProviderCommandKind.Revoke,
        "device-1",
        "user-1",
        "company-1",
        revision,
        [],
        []);

    private static AuthenticatedDevice Device(long revision) => new("device-1", "user-1", "company-1", "cert", revision);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("fixture timeout"));
    }

    private sealed class NetBirdFixtureHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private string? _groupName;
        private string? _keyName;
        private string? _policyJson;
        public bool GroupExists { get; private set; }
        public bool PeerExists { get; set; }
        public bool PeerConnected { get; set; }
        public string PeerIpv4 { get; set; } = "100.90.0.22";
        public string? PeerIpv6 { get; set; }
        public string ResponsePeerId { get; set; } = "peer-1";
        public bool SetupKeyExists { get; private set; }
        public bool SetupKeyRevoked { get; private set; }
        public IReadOnlyList<string> RevokedSetupKeyAutoGroups { get; private set; } = [];
        public bool PolicyExists { get; private set; }
        public bool UnsafeAllPolicy { get; set; }
        public string? DnsGroupId { get; set; }
        public bool DnsGroupAvailable { get; set; } = true;
        public bool DuplicateDnsGroup { get; set; }
        public string? UnsafeDnsGroupPolicyReference { get; set; }
        private string[] _setupAutoGroups = ["g1"];
        public IReadOnlyList<string> SetupKeyAutoGroups => _setupAutoGroups;
        public string? AppliedPolicyJson => _policyJson;
        public int SetupKeyCreateCount { get; private set; }
        public int PolicyWriteCount { get; private set; }

        public void MutatePolicy(string mutation)
        {
            var policy = JsonNode.Parse(_policyJson ?? throw new InvalidOperationException("Policy is absent."))!.AsObject();
            var rule = policy["rules"]!.AsArray()[0]!.AsObject();
            switch (mutation)
            {
                case "ports":
                    rule["ports"]!.AsArray().Add("8443");
                    break;
                case "protocol":
                    rule["protocol"] = "udp";
                    break;
                case "bidirectional":
                    rule["bidirectional"] = true;
                    break;
                case "destination":
                    rule["destinations"]!.AsArray().Add("unexpected-services");
                    break;
                case "revision":
                    policy["description"] = "Connector-managed device access; revision 999";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation));
            }
            _policyJson = policy.ToJsonString(JsonOptions);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var method = request.Method;
            if (method == HttpMethod.Get && path == "/api/groups")
            {
                if (request.RequestUri.Query.Contains("name=All", StringComparison.Ordinal))
                    return Json(new[] { new { id = "all", name = "All", peers = Array.Empty<object>(), resources = Array.Empty<object>() } });
                if (request.RequestUri.Query.Length > 0)
                    return Json(GroupExists ? new[] { Group() } : Array.Empty<object>());
                var groups = new List<object>();
                if (GroupExists) groups.Add(Group());
                if (DnsGroupId is not null && DnsGroupAvailable)
                {
                    groups.Add(DnsGroup());
                    if (DuplicateDnsGroup) groups.Add(DnsGroup());
                }
                return Json(groups);
            }
            if (method == HttpMethod.Post && path == "/api/groups")
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                _groupName = body["name"]!.GetValue<string>();
                GroupExists = true;
                return Json(Group());
            }
            if (path == "/api/groups/g1" && method == HttpMethod.Get)
                return GroupExists ? Json(Group()) : Missing();
            if (DnsGroupId is not null && path == "/api/groups/" + DnsGroupId && method == HttpMethod.Get)
                return DnsGroupAvailable ? Json(DnsGroup()) : Missing();
            if (path == "/api/groups/g1" && method == HttpMethod.Delete)
            {
                GroupExists = false;
                return Empty();
            }
            if (method == HttpMethod.Get && path == "/api/setup-keys")
                return Json(SetupKeyExists ? new[] { SetupKey(masked: true) } : Array.Empty<object>());
            if (method == HttpMethod.Post && path == "/api/setup-keys")
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                _keyName = body["name"]!.GetValue<string>();
                _setupAutoGroups = body["auto_groups"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
                SetupKeyExists = true;
                SetupKeyRevoked = false;
                SetupKeyCreateCount++;
                return Json(SetupKey(masked: false));
            }
            if (path == "/api/setup-keys/k1" && method == HttpMethod.Get)
                return SetupKeyExists ? Json(SetupKey(masked: true)) : Missing();
            if (path == "/api/setup-keys/k1" && method == HttpMethod.Put)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                RevokedSetupKeyAutoGroups = body["auto_groups"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
                SetupKeyRevoked = true;
                return Json(SetupKey(masked: true));
            }
            if (path == "/api/peers/peer-1" && method == HttpMethod.Get)
                return PeerExists ? Json(Peer()) : Missing();
            if (path == "/api/peers/peer-1" && method == HttpMethod.Delete)
            {
                PeerExists = false;
                return Empty();
            }
            if (method == HttpMethod.Get && path == "/api/policies")
            {
                var policies = new List<JsonNode>();
                if (UnsafeAllPolicy)
                {
                    policies.Add(JsonNode.Parse("""
                        {"id":"default","name":"Default","enabled":true,"rules":[{"name":"Default","enabled":true,"action":"accept","bidirectional":true,"protocol":"all","sources":[{"id":"all"}],"destinations":[{"id":"all"}]}]}
                        """)!);
                }
                if (UnsafeDnsGroupPolicyReference is { } unsafeReference)
                {
                    var sources = unsafeReference == "source" ? new[] { DnsGroupId ?? "gdns" } : new[] { "g1" };
                    var destinations = unsafeReference is "bidirectional-destination" or "one-way-destination" ? new[] { DnsGroupId ?? "gdns" } : new[] { "g1" };
                    var bidirectional = unsafeReference == "bidirectional-destination";
                    policies.Add(JsonNode.Parse(JsonSerializer.Serialize(new
                    {
                        id = "dns-policy",
                        name = "DNS traffic policy",
                        enabled = true,
                        rules = new[] { new { name = "dns-rule", enabled = true, action = "accept", bidirectional, protocol = "tcp", sources, destinations } },
                    }, JsonOptions))!);
                }
                if (PolicyExists && _policyJson is not null) policies.Add(JsonNode.Parse(_policyJson)!);
                return Json(policies);
            }
            if ((method == HttpMethod.Post && path == "/api/policies") ||
                (method == HttpMethod.Put && path == "/api/policies/p1"))
            {
                var policy = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                policy["id"] = "p1";
                _policyJson = policy.ToJsonString(JsonOptions);
                PolicyExists = true;
                PolicyWriteCount++;
                return Json(policy);
            }
            if (path == "/api/policies/p1" && method == HttpMethod.Get)
                return PolicyExists && _policyJson is not null ? Json(JsonNode.Parse(_policyJson)) : Missing();
            if (path == "/api/policies/p1" && method == HttpMethod.Delete)
            {
                PolicyExists = false;
                return Empty();
            }
            throw new InvalidOperationException($"Unexpected fixture request: {method} {request.RequestUri.PathAndQuery}");
        }

        private object Group() => new
        {
            id = "g1",
            name = _groupName ?? "pending",
            peers = PeerExists ? new[] { new { id = "peer-1", name = "desktop" } } : Array.Empty<object>(),
            resources = Array.Empty<object>(),
        };

        private object DnsGroup() => new
        {
            id = DnsGroupId,
            name = "connector-dns-clients",
            peers = Array.Empty<object>(),
            resources = Array.Empty<object>(),
        };

        private object SetupKey(bool masked) => new
        {
            id = "k1",
            name = _keyName ?? "pending",
            expires = DateTimeOffset.UtcNow.AddDays(1),
            valid = !SetupKeyRevoked,
            revoked = SetupKeyRevoked,
            used_times = PeerExists ? 1 : 0,
            auto_groups = _setupAutoGroups,
            key = masked ? "********" : "fixture-setup-key",
        };

        private object Peer() => new
        {
            id = ResponsePeerId,
            name = "desktop",
            ip = PeerIpv4,
            ipv6 = PeerIpv6,
            connected = PeerConnected,
            last_seen = DateTimeOffset.UtcNow,
        };

        private static HttpResponseMessage Json(object? value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json"),
        };
        private static HttpResponseMessage Missing() => new(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        private static HttpResponseMessage Empty() => new(HttpStatusCode.NoContent);
    }
}
