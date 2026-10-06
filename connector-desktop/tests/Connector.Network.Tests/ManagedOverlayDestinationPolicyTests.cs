using System.Net;
using Xunit;

namespace Connector.Network.Tests;

public sealed class ManagedOverlayDestinationPolicyTests
{
    [Fact]
    public void ExactManagedPeerDoesNotAllowOtherPrivateOrPublicAddresses()
    {
        var policy = new ManagedOverlayDestinationPolicy(["100.90.1.8"]);
        Assert.True(policy.Contains(IPAddress.Parse("100.90.1.8")));
        Assert.False(policy.Contains(IPAddress.Parse("100.90.1.9")));
        Assert.False(policy.Contains(IPAddress.Parse("192.168.1.8")));
        Assert.False(policy.Contains(IPAddress.Parse("8.8.8.8")));
        Assert.False(new ManagedOverlayDestinationPolicy([]).Contains(IPAddress.Parse("100.90.1.8")));
    }

    [Theory]
    [InlineData("100.64.0.1/0")]
    [InlineData("100.64.0.0/10")]
    [InlineData("100.90.1.8/32")]
    [InlineData("fd00::1/128")]
    [InlineData("10.1.1.1/7")]
    [InlineData("192.168.1.1/15")]
    [InlineData("172.16.1.1/11")]
    [InlineData("fd00::/6")]
    [InlineData("8.8.8.8")]
    [InlineData("127.0.0.1")]
    public void InvalidOrOverbroadAdministrativeRangeIsRejected(string range)
        => Assert.Throws<ArgumentException>(() => new ManagedOverlayDestinationPolicy([range]));
}
