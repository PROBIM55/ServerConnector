using Connector.Desktop.Services;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class LocalRuntimeIdentityTests
{
    [Fact]
    public void IdentitySurvivesRestartAndRejectsAnotherWindowsComputerBinding()
    {
        var root = Path.Combine(Path.GetTempPath(), "connector-identity-" + Guid.NewGuid().ToString("N"));
        var id = LocalRuntimeIdentity.LoadOrCreate(root, "current-user-and-computer");
        Assert.Equal(id, LocalRuntimeIdentity.LoadOrCreate(root, "current-user-and-computer"));
        Assert.Throws<InvalidOperationException>(() => LocalRuntimeIdentity.LoadOrCreate(root, "different-computer"));
        Assert.Equal(id, LocalRuntimeIdentity.LoadOrCreate(root, "current-user-and-computer"));
    }

    [Fact]
    public void CorruptIdentityIsPreservedForRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "connector-identity-" + Guid.NewGuid().ToString("N"));
        LocalRuntimeIdentity.LoadOrCreate(root, "current-user-and-computer");
        var path = Path.Combine(root, "local-identity.json");
        File.WriteAllText(path, "damaged");
        Assert.Throws<InvalidOperationException>(() => LocalRuntimeIdentity.LoadOrCreate(root, "current-user-and-computer"));
        Assert.Equal("damaged", File.ReadAllText(path));
    }
}
