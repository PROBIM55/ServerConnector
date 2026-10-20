using System.ComponentModel;
using System.Net;
using Connector.Network;
using Connector.SmbAccess;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class LegacyEncryptedSmbTests
{
    private const string Share = LegacyEncryptedSmbPolicy.ShareUnc;
    private static readonly IPAddress Public = IPAddress.Parse(LegacyEncryptedSmbPolicy.Destination);
    private static readonly IPAddress[] Overlay = [IPAddress.Parse("100.64.1.9")];

    [Theory]
    [InlineData(@"\\62.113.36.107\Other")]
    [InlineData(@"\\62.113.36.108\BIM_Models")]
    [InlineData(@"\\62.113.36.107\BIM_Models\")]
    [InlineData(@"\\62.113.36.107\BIM_Models\child")]
    [InlineData(@"\\server\BIM_Models")]
    public void Protected_exception_accepts_only_exact_legacy_share(string other) =>
        Assert.Throws<ArgumentException>(() => LegacyEncryptedSmbPolicy.Validate([other]));

    [Fact]
    public async Task Explicit_legacy_mapping_requires_crypto_without_plain_fallback()
    {
        var native = new Native { Encrypted = true };
        using var port = new WindowsSmbMappingPort(native, legacyEncryptedShares: [Share]);
        Assert.True(await port.IsShareRouteAllowedAsync(Share, Public, Overlay, default));
        await port.MountAsync("Z:", Share, "device-user", "secret", Public, Overlay, default);
        Assert.Equal(1, native.RequiredCryptoCalls);
        Assert.Equal(0, native.PlainCalls);
        Assert.True(await port.IsMappingSecurityAllowedAsync("Z:", Share, default));
        native.Encrypted = false;
        Assert.False(await port.IsMappingSecurityAllowedAsync("Z:", Share, default));
        Assert.Equal(SmbExactMappingRemoval.Removed, await port.TryUnmountExactAsync("Z:", Share, default));
    }

    [Fact]
    public async Task Crypto_failure_is_not_retried_plain_and_mapping_is_rolled_back()
    {
        var native = new Native { Encrypted = false };
        using var port = new WindowsSmbMappingPort(native, legacyEncryptedShares: [Share]);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await port.MountAsync("Z:", Share, "device-user", "secret", Public, Overlay, default));
        Assert.Null(native.Target);
        Assert.Equal(1, native.RequiredCryptoCalls);
        Assert.Equal(0, native.PlainCalls);
        Assert.Equal(1, native.Removals);
    }

    [Fact]
    public async Task Unsupported_required_crypto_fails_without_fallback_or_removal()
    {
        var native = new Native { Result = 50 };
        using var port = new WindowsSmbMappingPort(native, legacyEncryptedShares: [Share]);
        await Assert.ThrowsAsync<Win32Exception>(async () =>
            await port.MountAsync("Z:", Share, "device-user", "secret", Public, Overlay, default));
        Assert.Equal(1, native.RequiredCryptoCalls);
        Assert.Equal(0, native.PlainCalls);
        Assert.Equal(0, native.Removals);
    }

    [Fact]
    public async Task Matching_foreign_or_old_process_mapping_is_never_adopted()
    {
        var native = new Native { Target = Share, Encrypted = true };
        using var port = new WindowsSmbMappingPort(native, legacyEncryptedShares: [Share]);
        Assert.False(await port.IsMappingSecurityAllowedAsync("Z:", Share, default));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await port.MountAsync("Z:", Share, "device-user", "secret", Public, Overlay, default));
        Assert.Equal(0, native.RequiredCryptoCalls);
        Assert.Equal(0, native.Removals);
    }

    [Fact]
    public async Task Default_and_other_shares_keep_private_overlay_route_requirement()
    {
        using var normal = new WindowsSmbMappingPort(new Native());
        Assert.False(await normal.IsShareRouteAllowedAsync(Share, Public, Overlay, default));
        using var legacy = new WindowsSmbMappingPort(new Native(), legacyEncryptedShares: [Share]);
        Assert.False(await legacy.IsShareRouteAllowedAsync(@"\\62.113.36.107\Other", Public, Overlay, default));
        Assert.False(await legacy.IsShareRouteAllowedAsync(Share, IPAddress.Parse("8.8.8.8"), Overlay, default));
        Assert.False(await legacy.IsShareRouteAllowedAsync(Share, Public, [], default));
    }

    private sealed class Native : IWindowsSmbNativeApi
    {
        public string? Target { get; set; }
        public bool Encrypted { get; set; }
        public int Result { get; init; }
        public int RequiredCryptoCalls { get; private set; }
        public int PlainCalls { get; private set; }
        public int Removals { get; private set; }
        public string? GetTarget(string drive) => Target;
        public IPAddress GetBestRouteSource(IPAddress destination) => IPAddress.Parse("192.168.1.5");
        public bool HasEncryptedIntegrity(string drive, string expectedShareUnc) => Encrypted;
        public int AddConnection(string drive, string shareUnc, string userName, string password) { PlainCalls++; return 50; }
        public int AddEncryptedConnection(string drive, string shareUnc, string userName, string password)
        { RequiredCryptoCalls++; if (Result == 0) Target = shareUnc; return Result; }
        public int CancelConnection(string drive) { Removals++; Target = null; return 0; }
    }
}
