using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Connector.Access.Contracts;
using Microsoft.AspNetCore.DataProtection;

namespace Connector.Access.Smb.Tests;

public sealed class SmbProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "connector-smb-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Apply_ReturnsReceiptOnlyAfterExactObservation_AndStateHidesPassword()
    {
        var helper = new HelperHandler();
        var provider = CreateProvider(helper);

        var receipt = await provider.ApplyAsync(Apply(1), default);

        Assert.Equal(1, receipt.AppliedRevision);
        Assert.Equal(DeviceAccessProviderCommandKind.Apply, receipt.Kind);
        var state = Assert.Single(Directory.GetFiles(Path.Combine(_root, "state"), "*.json"));
        var json = await File.ReadAllTextAsync(state);
        Assert.DoesNotContain(helper.ObservedPassword!, json);
        Assert.StartsWith("cnb_", helper.LastRequest!.LocalUserName);
        Assert.Equal(20, helper.LastRequest.LocalUserName.Length);
    }

    [Fact]
    public async Task RevokeFence_PreventsStaleApplyFromCallingWindowsHelper()
    {
        var helper = new HelperHandler();
        var provider = CreateProvider(helper);
        await provider.ApplyAsync(Apply(1), default);
        await provider.RevokeAsync(Revoke(2), default);
        var calls = helper.CallCount;

        await Assert.ThrowsAsync<SmbRevisionRejectedException>(() => provider.ApplyAsync(Apply(1), default).AsTask());

        Assert.Equal(calls, helper.CallCount);
        Assert.Equal(SmbHelperProtocol.Revoke, helper.LastRequest!.Action);
        Assert.Null(helper.LastRequest.Password);
    }

    [Fact]
    public async Task MismatchedHelperReply_CannotProduceReceipt()
    {
        var helper = new HelperHandler { MutateCommandId = true };
        var provider = CreateProvider(helper);

        await Assert.ThrowsAsync<SmbHelperProtocolException>(() => provider.ApplyAsync(Apply(1), default).AsTask());
    }

    [Fact]
    public async Task HelperFailure_CannotProduceReceiptOrAppliedRetryShortcut()
    {
        var helper = new HelperHandler { Fail = true };
        var provider = CreateProvider(helper);
        await Assert.ThrowsAsync<SmbHelperProtocolException>(() => provider.ApplyAsync(Apply(1), default).AsTask());
        helper.Fail = false;

        var receipt = await provider.ApplyAsync(Apply(1), default);

        Assert.Equal(1, receipt.AppliedRevision);
        Assert.Equal(2, helper.CallCount);
    }

    [Fact]
    public async Task AccessRead_RequiresCurrentReceiptAndFreshExactReadback_AndReturnsOnlyBoundGrants()
    {
        var helper = new HelperHandler();
        var provider = CreateProvider(helper);
        await provider.ApplyAsync(Apply(1), default);
        var expires = DateTimeOffset.UtcNow.AddMinutes(3);

        var result = await provider.GetAsync(Identity(1), Profile(1, expires,
        [
            new ResourceGrant("models", "smb-folder", "project-1", [ConnectorPermission.Publish]),
            new ResourceGrant("project-1", "project", "project-1", [ConnectorPermission.Execute]),
        ]), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, helper.CallCount);
        Assert.NotNull(result.Value!.Password);
        Assert.StartsWith("SMBHOST\\cnb_", result.Value.UserName);
        Assert.InRange(result.Value.ExpiresAtUtc, DateTimeOffset.UtcNow, expires);
        var grant = Assert.Single(result.Value.Resources);
        Assert.Equal("project-1", grant.ProjectId);
        Assert.Equal(@"\\10.77.0.1\company-models-share", grant.ShareUnc);
        Assert.Equal([ConnectorPermission.Publish], grant.Permissions);
    }

    [Fact]
    public async Task AccessRead_RejectsExpiredForeignAndWrongRevisionProfilesWithoutReadback()
    {
        var helper = new HelperHandler();
        var provider = CreateProvider(helper);
        await provider.ApplyAsync(Apply(1), default);
        var calls = helper.CallCount;

        Assert.False((await provider.GetAsync(Identity(1), Profile(1, DateTimeOffset.UtcNow.AddSeconds(-1)), default)).IsSuccess);
        Assert.False((await provider.GetAsync(Identity(1) with { DeviceId = "device-2" }, Profile(1, DateTimeOffset.UtcNow.AddMinutes(1)), default)).IsSuccess);
        Assert.False((await provider.GetAsync(Identity(2), Profile(2, DateTimeOffset.UtcNow.AddMinutes(1)), default)).IsSuccess);
        Assert.False((await provider.GetAsync(Identity(1) with { CompanyId = "company-2" }, Profile(1, DateTimeOffset.UtcNow.AddMinutes(1)), default)).IsSuccess);

        Assert.Equal(calls, helper.CallCount);
    }

    [Fact]
    public async Task AccessRead_RejectsRevokedOrMismatchedFreshReadback()
    {
        var helper = new HelperHandler();
        var provider = CreateProvider(helper);
        await provider.ApplyAsync(Apply(1), default);
        helper.MutateShareName = true;

        var mismatched = await provider.GetAsync(Identity(1), Profile(1, DateTimeOffset.UtcNow.AddMinutes(1)), default);
        Assert.False(mismatched.IsSuccess);

        helper.MutateShareName = false;
        helper.MutateAuthority = true;
        var wrongAuthority = await provider.GetAsync(Identity(1), Profile(1, DateTimeOffset.UtcNow.AddMinutes(1)), default);
        Assert.False(wrongAuthority.IsSuccess);

        helper.MutateAuthority = false;
        helper.MutateSid = true;
        var wrongSid = await provider.GetAsync(Identity(1), Profile(1, DateTimeOffset.UtcNow.AddMinutes(1)), default);
        Assert.False(wrongSid.IsSuccess);

        helper.MutateSid = false;
        await provider.RevokeAsync(Revoke(2), default);
        var calls = helper.CallCount;
        var revoked = await provider.GetAsync(Identity(2), Profile(2, DateTimeOffset.UtcNow.AddMinutes(1)), default);
        Assert.False(revoked.IsSuccess);
        Assert.Equal(calls, helper.CallCount);
    }

    [Fact]
    public async Task AccessRead_EmptySmbGrantReturnsNoCredential()
    {
        var helper = new HelperHandler();
        var provider = CreateProvider(helper);
        var command = Apply(1) with { Resources = [new ResourceGrant("project-1", "project", "project-1", [ConnectorPermission.Execute])] };
        await provider.ApplyAsync(command, default);

        var result = await provider.GetAsync(Identity(1), Profile(1, DateTimeOffset.UtcNow.AddMinutes(1), command.Resources), default);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.UserName);
        Assert.Null(result.Value.Password);
        Assert.Empty(result.Value.Resources);
    }

    private SmbDeviceAccessGrantProvider CreateProvider(HttpMessageHandler handler)
    {
        Directory.CreateDirectory(_root);
        var options = new SmbProviderOptions
        {
            HelperBaseUri = new Uri("https://smb-helper.test/"),
            StateDirectory = Path.Combine(_root, "state"),
            DataProtectionKeyDirectory = Path.Combine(_root, "keys"),
            DataProtectionCertificatePfxPath = "",
            DataProtectionCertificatePasswordEnvironmentVariable = "",
            ClientCertificatePfxPath = "",
            ClientCertificatePasswordEnvironmentVariable = "",
            ResourceBindings =
            [
                new SmbResourceBinding { ResourceId = "models", HelperResourceId = "company-models", ClientShareUnc = @"\\10.77.0.1\company-models-share" },
                new SmbResourceBinding { ResourceId = "publish", HelperResourceId = "published-models", ClientShareUnc = @"\\10.77.0.1\published-models-share" },
            ],
        };
        return new SmbDeviceAccessGrantProvider(
            new HttpClient(handler), options,
            DataProtectionProvider.Create(new DirectoryInfo(options.DataProtectionKeyDirectory)));
    }

    private static DeviceAccessProviderCommand Apply(long revision) => new(
        "apply-" + revision, "smb", DeviceAccessProviderCommandKind.Apply,
        "device-1", "user-1", "company-1", revision, [],
        [new ResourceGrant("models", "smb-folder", null, [ConnectorPermission.Publish])]);

    private static DeviceAccessProviderCommand Revoke(long revision) => new(
        "revoke-" + revision, "smb", DeviceAccessProviderCommandKind.Revoke,
        "device-1", "user-1", "company-1", revision, [], []);

    private static AuthenticatedDevice Identity(long revision) =>
        new("device-1", "user-1", "company-1", "certificate", revision);

    private static DeviceAccessProfile Profile(
        long revision,
        DateTimeOffset expires,
        IReadOnlyList<ResourceGrant>? resources = null) =>
        new(DeviceAccessProtocol.Version, "device-1", "user-1", "company-1", revision, revision, expires, [],
            resources ?? [new ResourceGrant("models", "smb-folder", "project-1", [ConnectorPermission.Publish])]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class HelperHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        public bool MutateCommandId { get; init; }
        public bool Fail { get; set; }
        public bool MutateShareName { get; set; }
        public bool MutateSid { get; set; }
        public bool MutateAuthority { get; set; }
        public int CallCount { get; private set; }
        public string? ObservedPassword { get; private set; }
        public SmbHelperRequest? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = await request.Content!.ReadFromJsonAsync<SmbHelperRequest>(JsonOptions, cancellationToken);
            ObservedPassword = LastRequest!.Password;
            if (Fail) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var desired = LastRequest.Grants.ToDictionary(grant => grant.ResourceId, grant => grant.Permission);
            var enabled = LastRequest.Action == SmbHelperProtocol.Apply && desired.Count > 0;
            var response = new SmbHelperResponse(
                SmbHelperProtocol.Version,
                MutateCommandId ? "wrong-command" : LastRequest.CommandId,
                LastRequest.DeviceId,
                LastRequest.Revision,
                LastRequest.Action,
                LastRequest.LocalUserName,
                MutateAuthority ? "OTHERHOST" : "SMBHOST",
                MutateSid ? "S-1-5-21-9-9-9-9999" : LastRequest.ExpectedLocalUserSid ?? "S-1-5-21-1-2-3-1001",
                enabled,
                0,
                [
                    Observe("company-models", desired, MutateShareName),
                    Observe("published-models", desired, false),
                ]);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response, JsonOptions), Encoding.UTF8, "application/json"),
            };
        }

        private static SmbHelperResourceObservation Observe(
            string id,
            IReadOnlyDictionary<string, string> desired,
            bool mutateShareName)
        {
            var permission = desired.GetValueOrDefault(id, SmbHelperProtocol.None);
            return new SmbHelperResourceObservation(id, mutateShareName ? "wrong-share" : id + "-share", "D:\\" + id, permission, permission);
        }
    }
}
