using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Connector.Access.Contracts;

namespace Connector.Access.NetBird.Tests;

public sealed class NetBirdPeerAttestationServiceCollectionTests
{
    [Fact]
    public void ExplicitOptIn_RegistersAttestationAndDaemonReaderAfterProviderAccess()
    {
        var services = new ServiceCollection();
        var attestationOptions = ValidAttestationOptions();
        var daemonOptions = ValidDaemonOptions();

        var returned = services
            .AddNetBirdConnectorAccess(ValidProviderOptions(), CreateTestProtectionProvider())
            .AddNetBirdPeerAttestation(attestationOptions, daemonOptions);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        Assert.Same(services, returned);
        Assert.Same(attestationOptions, provider.GetRequiredService<NetBirdPeerAttestationOptions>());
        Assert.Same(daemonOptions, provider.GetRequiredService<NetBirdDaemonPeerSnapshotOptions>());
        Assert.IsType<NetBirdDaemonPeerSnapshotReader>(
            provider.GetRequiredService<INetBirdDaemonPeerSnapshotReader>());
        Assert.IsType<NetBirdPeerAttestationService>(
            provider.GetRequiredService<INetBirdPeerAttestationService>());
    }

    [Fact]
    public void OptIn_RequiresExistingNetBirdConnectorAccess()
    {
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddNetBirdPeerAttestation(ValidAttestationOptions(), ValidDaemonOptions()));

        Assert.Contains("AddNetBirdConnectorAccess", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(INetBirdPeerAttestationService));
    }

    [Fact]
    public void ManualVpnReaderCannotBypassNetBirdConnectorAccessPrerequisite()
    {
        var services = new ServiceCollection()
            .AddSingleton<IConnectorVpnBootstrapReader>(new FakeVpnReader());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddNetBirdPeerAttestation(ValidAttestationOptions(), ValidDaemonOptions()));

        Assert.Contains("AddNetBirdConnectorAccess", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(INetBirdPeerAttestationService));
    }

    [Fact]
    public void ForgedProviderAndReaderCannotBypassNetBirdConnectorAccessPrerequisite()
    {
        var services = new ServiceCollection()
            .AddSingleton<NetBirdDeviceAccessGrantProvider>(_ => throw new InvalidOperationException("unused"))
            .AddSingleton<IConnectorVpnBootstrapReader>(new FakeVpnReader());

        Assert.Throws<InvalidOperationException>(() =>
            services.AddNetBirdPeerAttestation(ValidAttestationOptions(), ValidDaemonOptions()));

        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(INetBirdPeerAttestationService));
    }

    [Fact]
    public void OverriddenVpnReaderCannotJoinExistingNetBirdRegistration()
    {
        var services = new ServiceCollection()
            .AddNetBirdConnectorAccess(ValidProviderOptions(), CreateTestProtectionProvider())
            .AddSingleton<IConnectorVpnBootstrapReader>(new FakeVpnReader());

        Assert.Throws<InvalidOperationException>(() =>
            services.AddNetBirdPeerAttestation(ValidAttestationOptions(), ValidDaemonOptions()));

        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(INetBirdPeerAttestationService));
    }

    [Fact]
    public void InvalidOptions_FailBeforeAttestationServicesAreRegistered()
    {
        var services = new ServiceCollection()
            .AddNetBirdConnectorAccess(ValidProviderOptions(), CreateTestProtectionProvider());
        var invalidAttestation = new NetBirdPeerAttestationOptions
        {
            ExpectedServerOverlayListenerIp = "127.0.0.1"
        };

        Assert.Throws<ArgumentException>(() =>
            services.AddNetBirdPeerAttestation(invalidAttestation, ValidDaemonOptions()));

        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(INetBirdPeerAttestationService) ||
            descriptor.ServiceType == typeof(INetBirdDaemonPeerSnapshotReader));
    }

    [Fact]
    public void InvalidDaemonOptions_FailBeforeAttestationServicesAreRegistered()
    {
        var services = new ServiceCollection()
            .AddNetBirdConnectorAccess(ValidProviderOptions(), CreateTestProtectionProvider());
        var invalidDaemon = new NetBirdDaemonPeerSnapshotOptions
        {
            ExecutablePath = ValidDaemonOptions().ExecutablePath,
            CommandTimeout = TimeSpan.Zero
        };

        Assert.Throws<ArgumentException>(() =>
            services.AddNetBirdPeerAttestation(ValidAttestationOptions(), invalidDaemon));

        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(INetBirdPeerAttestationService) ||
            descriptor.ServiceType == typeof(INetBirdDaemonPeerSnapshotReader));
    }

    private static NetBirdOptions ValidProviderOptions() => new()
    {
        ManagementUri = new Uri("https://netbird.example.test/"),
        AccessToken = "test-access-token",
        StateDirectory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "connector-access-netbird-tests"))
    };

    private static NetBirdPeerAttestationOptions ValidAttestationOptions() => new()
    {
        ExpectedServerOverlayListenerIp = "100.90.0.1"
    };

    private static NetBirdDaemonPeerSnapshotOptions ValidDaemonOptions() => new()
    {
        ExecutablePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "trusted-netbird", "netbird.exe"))
    };

    private static IDataProtectionProvider CreateTestProtectionProvider() =>
        DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(
            Path.GetTempPath(),
            "connector-access-netbird-attestation-tests",
            Guid.NewGuid().ToString("N"))));

    private sealed class FakeVpnReader : IConnectorVpnBootstrapReader
    {
        public ValueTask<DeviceAccessResult<ConnectorVpnBootstrap>> GetAsync(
            AuthenticatedDevice device,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(DeviceAccessResult<ConnectorVpnBootstrap>.Fail("unused", "unused"));

        public ValueTask<DeviceAccessResult<ConnectorVpnTransportState>> GetStateAsync(
            AuthenticatedDevice device,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(DeviceAccessResult<ConnectorVpnTransportState>.Fail("unused", "unused"));
    }
}
