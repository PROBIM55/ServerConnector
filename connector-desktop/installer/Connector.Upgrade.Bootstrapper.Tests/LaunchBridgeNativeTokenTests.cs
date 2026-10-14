using System.Reflection;
using System.Security;
using System.Security.Principal;
using Xunit;

namespace Connector.Upgrade.Bootstrapper.Tests;

public sealed class LaunchBridgeNativeTokenTests
{
    [Fact]
    public void NativeTokenPreflightChecksAuthenticationAndRejectsElevation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        var program = Assembly.Load("Connector.Upgrade.LaunchBridge")
            .GetType("Connector.Upgrade.LaunchBridge.Program", throwOnError: true)!;
        var check = program.GetMethod("AssertUnelevatedCurrentToken", BindingFlags.NonPublic | BindingFlags.Static)!;
        Exception? failure = null;
        try { check.Invoke(null, null); }
        catch (TargetInvocationException error) { failure = error.InnerException; }

        if (!identity.IsAuthenticated || identity.IsSystem)
        {
            Assert.IsType<SecurityException>(failure);
            Assert.Equal("The bridge requires an interactive authenticated user.", failure!.Message);
        }
        else if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Assert.IsType<SecurityException>(failure);
            Assert.Equal("The bridge and its caller must not be elevated.", failure!.Message);
        }
        else
        {
            // With TOKEN_QUERY alone IsAuthenticated throws before these real OS
            // checks can complete, even for an ordinary authenticated user.
            Assert.Null(failure);
        }
    }
}
