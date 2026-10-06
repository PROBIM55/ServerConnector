using Connector.Access.Client;
using Connector.Upgrade.Core;
using Xunit;

namespace Connector.Upgrade.PlatformAccess.Tests;

public sealed class HttpExactDeviceRevocationPortTests
{
    private const string DeviceId = "dev_0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(true, "device-self-revoke-v1:https-401-device_unauthorized")]
    [InlineData(false, "device-self-revoke-unavailable")]
    public async Task Inspect_MapsOnlyExactClientProbe(bool available, string evidence)
    {
        var client = new FakeExactRevocationClient { Available = available };
        var port = new HttpExactDeviceRevocationPort(client);

        var result = await port.InspectAsync(default);

        Assert.Equal(available, result.Available);
        Assert.Equal(evidence, result.EvidenceId);
        Assert.Equal(1, client.InspectCalls);
    }

    [Fact]
    public async Task Revoke_ValidatesAndDelegatesExactDevice()
    {
        var client = new FakeExactRevocationClient();
        var port = new HttpExactDeviceRevocationPort(client);

        await port.RevokeAndConfirmExactAsync(DeviceId, default);

        Assert.Equal([DeviceId], client.RevokedDeviceIds);
    }

    [Fact]
    public async Task Revoke_NonCanonicalDevice_FailsBeforeClientCall()
    {
        var client = new FakeExactRevocationClient();
        var port = new HttpExactDeviceRevocationPort(client);

        await Assert.ThrowsAsync<UpgradeInvariantException>(async () =>
            await port.RevokeAndConfirmExactAsync("dev_other", default));

        Assert.Empty(client.RevokedDeviceIds);
    }

    [Fact]
    public async Task Operations_PropagateCancellation()
    {
        var client = new FakeExactRevocationClient();
        var port = new HttpExactDeviceRevocationPort(client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await port.InspectAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await port.RevokeAndConfirmExactAsync(DeviceId, cancellation.Token));
    }

    private sealed class FakeExactRevocationClient : IConnectorExactDeviceRevocationClient
    {
        public bool Available { get; init; }
        public int InspectCalls { get; private set; }
        public List<string> RevokedDeviceIds { get; } = [];

        public ValueTask<bool> InspectExactDeviceRevocationAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectCalls++;
            return ValueTask.FromResult(Available);
        }

        public ValueTask RevokeAndConfirmExactAsync(
            string deviceId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RevokedDeviceIds.Add(deviceId);
            return ValueTask.CompletedTask;
        }
    }
}
