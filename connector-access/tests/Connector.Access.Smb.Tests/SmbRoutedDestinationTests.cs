using Connector.Access.Smb;
using Xunit;

namespace Connector.Access.Smb.Tests;

public sealed class SmbRoutedDestinationTests
{
    [Fact]
    public void Public_compatibility_unc_requires_an_explicit_exact_destination()
    {
        Assert.Throws<ArgumentException>(() => Options([]).Validate(requireCertificate: false));
        Assert.Throws<ArgumentException>(() => Options(["62.113.36.108"]).Validate(requireCertificate: false));
        Options(["62.113.36.107"]).Validate(requireCertificate: false);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.1.2")]
    [InlineData("224.0.0.1")]
    [InlineData("62.113.36.0/24")]
    [InlineData("server.example")]
    public void Routed_destination_is_a_single_unicast_literal(string value)
        => Assert.Throws<ArgumentException>(() => Options([value]).Validate(requireCertificate: false));

    private static SmbProviderOptions Options(string[] routed) => new()
    {
        HelperBaseUri = new("https://127.0.0.1:24445/"),
        StateDirectory = Path.Combine(Path.GetTempPath(), "smb-routing-test-state"),
        DataProtectionKeyDirectory = Path.Combine(Path.GetTempPath(), "smb-routing-test-keys"),
        DataProtectionCertificatePfxPath = "", DataProtectionCertificatePasswordEnvironmentVariable = "",
        ClientCertificatePfxPath = "", ClientCertificatePasswordEnvironmentVariable = "",
        RoutedDestinationAddresses = routed,
        ResourceBindings = [new SmbResourceBinding
        {
            ResourceId = "brigada-models", HelperResourceId = "brigada-models",
            ClientShareUnc = @"\\62.113.36.107\BIM_Models",
        }],
    };
}
