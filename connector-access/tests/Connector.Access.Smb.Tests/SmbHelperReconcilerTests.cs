using Connector.Access.Smb.Helper;

namespace Connector.Access.Smb.Tests;

public sealed class SmbHelperReconcilerTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "smb-helper-tests-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task SharePathMismatch_IsRejectedEvenWhenAclClaimsSuccess()
    {
        var reconciler = Create(new ObservationExecutor(response => response with
        {
            Resources = [response.Resources[0] with { CanonicalRootPath = "D:\\Other" }],
        }));

        await Assert.ThrowsAsync<SmbHelperPostconditionException>(() => reconciler.ReconcileAsync(Request(), default).AsTask());
    }

    [Theory]
    [InlineData("share")]
    [InlineData("ntfs")]
    [InlineData("session")]
    [InlineData("disabled")]
    public async Task FailingWindowsPostcondition_CannotBeAccepted(string failure)
    {
        var reconciler = Create(new ObservationExecutor(response => failure switch
        {
            "share" => response with { Resources = [response.Resources[0] with { ShareAccess = "read" }] },
            "ntfs" => response with { Resources = [response.Resources[0] with { NtfsAccess = "mismatch" }] },
            "session" => response with { Action = SmbHelperProtocol.Revoke, AccountEnabled = false, ActiveSessionCount = 1,
                Resources = [response.Resources[0] with { ShareAccess = "none", NtfsAccess = "none" }] },
            "disabled" => response with { AccountEnabled = false },
            _ => response,
        }));
        var request = failure == "session" ? Request() with { Action = SmbHelperProtocol.Revoke, Password = null, Grants = [] } : Request();

        await Assert.ThrowsAsync<SmbHelperPostconditionException>(() => reconciler.ReconcileAsync(request, default).AsTask());
    }

    [Fact]
    public async Task ExactReadback_IsAccepted()
    {
        var reconciler = Create(new ObservationExecutor(response => response));

        var response = await reconciler.ReconcileAsync(Request(), default);

        Assert.True(response.AccountEnabled);
        Assert.Equal("change", Assert.Single(response.Resources).NtfsAccess);
    }

    [Fact]
    public async Task DurableHelperFence_RejectsStaleCommandBeforeWindowsIo()
    {
        var executor = new CountingExecutor();
        var reconciler = Create(executor);
        var revoke = Request() with
        {
            CommandId = "revoke-2", Revision = 2, Action = SmbHelperProtocol.Revoke, Password = null, Grants = [],
        };
        await reconciler.ReconcileAsync(revoke, default);

        await Assert.ThrowsAsync<SmbHelperValidationException>(() => reconciler.ReconcileAsync(Request(), default).AsTask());

        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public void PackagedScript_UsesFixedStdinAndScopedAclOperations()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "smb-helper", "SmbAclReconcile.ps1"));
        var script = File.ReadAllText(path);

        Assert.Contains("[Console]::In.ReadToEnd()", script);
        Assert.Contains("ReparsePoint", script);
        Assert.Contains("RemoveAccessRuleSpecific", script);
        Assert.Contains("Get-SmbShareAccess", script);
        Assert.DoesNotContain("Remove-SmbShare", script);
        Assert.DoesNotContain("FullControl", script);
        Assert.DoesNotContain("$args", script);
    }

    private SmbHelperReconciler Create(ISmbAclExecutor executor) => new(
        new SmbHelperHostOptions
        {
            Resources = [new SmbHelperResourceOptions { ResourceId = "company-models", ShareName = "BIM_Models", RootPath = "D:\\BIM_Models" }],
            StateDirectory = _state,
        }, executor);

    public void Dispose()
    {
        if (Directory.Exists(_state)) Directory.Delete(_state, true);
    }

    private static SmbHelperRequest Request() => new(
        SmbHelperProtocol.Version, "command-1", "device-1", 1, SmbHelperProtocol.Apply, "cnb_0123456789abcdef", null,
        "password", [new SmbHelperGrant("company-models", SmbHelperProtocol.Change)]);

    private sealed class ObservationExecutor(Func<SmbHelperResponse, SmbHelperResponse> mutate) : ISmbAclExecutor
    {
        public ValueTask<SmbHelperResponse> ReconcileAsync(
            SmbHelperRequest request,
            IReadOnlyList<ValidatedHelperResource> resources,
            CancellationToken cancellationToken)
        {
            var desired = request.Grants.ToDictionary(grant => grant.ResourceId, grant => grant.Permission);
            var response = new SmbHelperResponse(
                SmbHelperProtocol.Version, request.CommandId, request.DeviceId, request.Revision, request.Action,
                request.LocalUserName, Environment.MachineName,
                request.ExpectedLocalUserSid ?? "S-1-5-21-1-2-3-1001", true, 0,
                resources.Select(resource =>
                {
                    var permission = desired.GetValueOrDefault(resource.ResourceId, SmbHelperProtocol.None);
                    return new SmbHelperResourceObservation(
                        resource.ResourceId, resource.ShareName, resource.RootPath, permission, permission);
                }).ToArray());
            return ValueTask.FromResult(mutate(response));
        }
    }

    private sealed class CountingExecutor : ISmbAclExecutor
    {
        public int Calls { get; private set; }

        public ValueTask<SmbHelperResponse> ReconcileAsync(
            SmbHelperRequest request,
            IReadOnlyList<ValidatedHelperResource> resources,
            CancellationToken cancellationToken)
        {
            Calls++;
            var response = new SmbHelperResponse(
                SmbHelperProtocol.Version, request.CommandId, request.DeviceId, request.Revision, request.Action, request.LocalUserName,
                Environment.MachineName, request.ExpectedLocalUserSid, false, 0,
                resources.Select(resource => new SmbHelperResourceObservation(
                    resource.ResourceId, resource.ShareName, resource.RootPath, SmbHelperProtocol.None, SmbHelperProtocol.None)).ToArray());
            return ValueTask.FromResult(response);
        }
    }
}
