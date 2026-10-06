using Connector.Desktop.Services;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class ManagedSmbMappingStoreTests
{
    [Fact]
    public void RestoredRecordRequiresSameUserAndActualAssignedTarget()
    {
        var path = Path.Combine(Path.GetTempPath(), "connector-smb-" + Guid.NewGuid().ToString("N"), "ownership.json");
        var actualTarget = @"\\vpn-server\assigned-share";
        var owner = new ManagedSmbMappingStore(path, "test-user", _ => actualTarget);
        owner.Record("Z:", actualTarget);
        var restarted = new ManagedSmbMappingStore(path, "test-user", _ => actualTarget);
        Assert.True(restarted.MatchesRecordedTarget("Z:", actualTarget));
        Assert.False(new ManagedSmbMappingStore(path, "other-user", _ => actualTarget).MatchesRecordedTarget("Z:", actualTarget));
        actualTarget = @"\\other-server\personal-share";
        Assert.False(restarted.MatchesRecordedTarget("Z:", @"\\vpn-server\assigned-share"));
        Assert.False(restarted.MatchesRecordedTarget("Y:", actualTarget));
    }

    [Fact]
    public void MissingChangedCorruptAndForgottenMappingsNeverAuthorizeRemoval()
    {
        var path = Path.Combine(Path.GetTempPath(), "connector-smb-" + Guid.NewGuid().ToString("N"), "ownership.json");
        string? target = @"\\vpn-server\assigned-share";
        var store = new ManagedSmbMappingStore(path, "test-user", _ => target);
        Assert.False(store.MatchesRecordedTarget("Z:", target));
        store.Record("Z:", target);
        target = null;
        Assert.False(store.MatchesRecordedTarget("Z:", @"\\vpn-server\assigned-share"));
        Assert.Throws<InvalidOperationException>(() => store.Record("Z:", @"\\vpn-server\assigned-share"));
        File.WriteAllText(path, "corrupt");
        Assert.False(store.MatchesRecordedTarget("Z:", @"\\vpn-server\assigned-share"));
        Assert.Throws<InvalidOperationException>(() => store.Record("Z:", @"\\vpn-server\assigned-share"));
        Assert.Equal("corrupt", File.ReadAllText(path));
    }

    [Fact]
    public void RecordingAnotherAssignedDriveDoesNotLosePriorRecord()
    {
        var path = Path.Combine(Path.GetTempPath(), "connector-smb-" + Guid.NewGuid().ToString("N"), "ownership.json");
        var target = @"\\vpn-server\assigned-share";
        var store = new ManagedSmbMappingStore(path, "test-user", _ => target);
        store.Record("Z:", target);
        store.Record("Y:", target);
        Assert.True(store.MatchesRecordedTarget("Z:", target));
        Assert.True(store.MatchesRecordedTarget("Y:", target));
        store.Forget("Z:");
        Assert.False(store.MatchesRecordedTarget("Z:", target));
        Assert.True(store.MatchesRecordedTarget("Y:", target));
    }
}
