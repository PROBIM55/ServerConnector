using Connector.Platform;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class PlatformConnectionPolicyTests
{
    [Theory]
    [InlineData("https://bimplatforma.ru/api/platform", "https://bimplatforma.ru/api/platform")]
    [InlineData(" HTTPS://bimplatforma.ru/api/platform/ ", "https://bimplatforma.ru/api/platform")]
    [InlineData("http://localhost:8090/api/platform", "http://localhost:8090/api/platform")]
    public void TryNormalizeServerUrl_AllowsHttpsAndLocalhostHttp(string source, string expected)
    {
        var allowed = PlatformConnectionPolicy.TryNormalizeServerUrl(source, out var normalized, out _);

        Assert.True(allowed);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("http://bimplatforma.ru/api/platform")]
    [InlineData("ftp://bimplatforma.ru/api/platform")]
    [InlineData("https://token@example.test/api/platform")]
    [InlineData("https://bimplatforma.ru/api/platform?token=value")]
    [InlineData("https://bimplatforma.ru/api/platform#token")]
    public void TryNormalizeServerUrl_RejectsUnsafeOrCredentialBearingAddress(string source) =>
        Assert.False(PlatformConnectionPolicy.TryNormalizeServerUrl(source, out _, out _));

    [Fact]
    public void CanUseImportedToken_RequiresTheSameNormalizedApiAddress()
    {
        Assert.True(PlatformConnectionPolicy.CanUseImportedToken(
            "https://bimplatforma.ru/api/platform", "https://bimplatforma.ru/api/platform"));
        Assert.False(PlatformConnectionPolicy.CanUseImportedToken(
            "https://bimplatforma.ru/api/platform-next", "https://bimplatforma.ru/api/platform"));
    }

    [Theory]
    [InlineData("https://bimplatforma.ru/api/platform", true)]
    [InlineData("https://bimplatforma.ru/api/platform/", true)]
    [InlineData("https://platform.example.test/api/platform", false)]
    [InlineData("http://localhost:8090/api/platform", false)]
    public void IsAutomaticServerUrl_AllowsOnlyPolicyEndpoint(string source, bool expected) =>
        Assert.Equal(expected, PlatformConnectionPolicy.IsAutomaticServerUrl(source));
}
