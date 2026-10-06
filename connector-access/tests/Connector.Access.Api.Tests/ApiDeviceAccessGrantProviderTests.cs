using Connector.Access.Contracts;

namespace Connector.Access.Api.Tests;

public sealed class ApiDeviceAccessGrantProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "connector-access-api-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Apply_persists_exact_policy_and_reader_verifies_identity_revision()
    {
        var provider = Create();
        var command = Command(7, DeviceAccessProviderCommandKind.Apply);

        var receipt = await provider.ApplyAsync(command, CancellationToken.None);
        var policy = await provider.GetCurrentAsync(new AuthenticatedDevice("device", "user", "company", "cert", 7), CancellationToken.None);

        Assert.Equal(7, receipt.AppliedRevision);
        Assert.True(policy.IsSuccess);
        Assert.Single(policy.Value!.Modules);
        Assert.Equal("bridge", policy.Value.Modules[0].ModuleId);
        Assert.Equal([ConnectorPermission.Read, ConnectorPermission.Execute], policy.Value.Modules[0].Permissions);
        Assert.Single(policy.Value.Resources);
        Assert.Equal("project-1", policy.Value.Resources[0].ProjectId);
        Assert.False((await provider.GetCurrentAsync(
            new AuthenticatedDevice("device", "other", "company", "cert", 7), CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public async Task Same_revision_with_different_policy_is_rejected()
    {
        var provider = Create();
        await provider.ApplyAsync(Command(7, DeviceAccessProviderCommandKind.Apply), CancellationToken.None);
        var changed = Command(7, DeviceAccessProviderCommandKind.Apply) with { Modules = [] };

        await Assert.ThrowsAsync<ApiAccessRevisionRejectedException>(async () =>
            await provider.ApplyAsync(changed, CancellationToken.None));
    }

    [Fact]
    public async Task Lower_revision_is_rejected()
    {
        var provider = Create();
        await provider.ApplyAsync(Command(7, DeviceAccessProviderCommandKind.Apply), CancellationToken.None);

        await Assert.ThrowsAsync<ApiAccessRevisionRejectedException>(async () =>
            await provider.ApplyAsync(Command(6, DeviceAccessProviderCommandKind.Apply), CancellationToken.None));
    }

    [Fact]
    public async Task Revoke_is_durable_and_blocks_policy_readback()
    {
        var provider = Create();
        await provider.ApplyAsync(Command(7, DeviceAccessProviderCommandKind.Apply), CancellationToken.None);
        await provider.RevokeAsync(Command(8, DeviceAccessProviderCommandKind.Revoke), CancellationToken.None);

        var result = await provider.GetCurrentAsync(
            new AuthenticatedDevice("device", "user", "company", "cert", 8), CancellationToken.None);

        Assert.False(result.IsSuccess);
        await Assert.ThrowsAsync<ApiAccessRevisionRejectedException>(async () =>
            await provider.ApplyAsync(Command(7, DeviceAccessProviderCommandKind.Apply), CancellationToken.None));
    }

    private ApiDeviceAccessGrantProvider Create() => new(new ApiAccessOptions { StateDirectory = _directory });

    private static DeviceAccessProviderCommand Command(long revision, DeviceAccessProviderCommandKind kind) => new(
        $"command-{revision}-{kind}", ApiDeviceAccessGrantProvider.Name, kind,
        "device", "user", "company", revision,
        [new ModuleGrant(ConnectorProduct.Platform, "bridge", [ConnectorPermission.Read, ConnectorPermission.Execute])],
        [new ResourceGrant("project-1", "project", "project-1", [ConnectorPermission.Read, ConnectorPermission.Execute])]);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
