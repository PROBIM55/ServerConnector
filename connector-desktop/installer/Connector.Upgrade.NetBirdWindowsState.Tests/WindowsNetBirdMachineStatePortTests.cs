using Connector.Upgrade.Core;
using Connector.Upgrade.NetBirdMachine;

namespace Connector.Upgrade.NetBirdWindowsState.Tests;

public sealed class WindowsNetBirdMachineStatePortTests
{
    [Fact]
    public async Task Production_inventory_smoke_is_read_only_and_returns_fail_closed_evidence()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var port = new WindowsNetBirdMachineStatePort();

        var actual = await port.InspectAsync(CancellationToken.None);

        Assert.StartsWith("netbird-windows-v1:", actual.EvidenceId, StringComparison.Ordinal);
        if (!actual.InspectionComplete)
            Assert.Equal(NetBirdOwnership.Unattributed, actual.Assessment.Ownership);
    }

    [Fact]
    public async Task Complete_empty_inventory_is_absent()
    {
        var environment = new FakeWindowsNetBirdStateEnvironment(WindowsNetBirdStateTestSupport.Absent());
        var port = CreatePort(environment);

        var actual = await port.InspectAsync(CancellationToken.None);

        Assert.True(actual.InspectionComplete);
        Assert.Equal(NetBirdOwnership.Absent, actual.Assessment.Ownership);
        Assert.Null(actual.InstalledPackage);
    }

    [Fact]
    public async Task Exact_official_install_without_owner_marker_stays_foreign()
    {
        var environment = new FakeWindowsNetBirdStateEnvironment(
            WindowsNetBirdStateTestSupport.Exact(marker: null, markerState: NetBirdProtectedFileState.Missing));
        var port = CreatePort(environment);

        var actual = await port.InspectAsync(CancellationToken.None);

        Assert.True(actual.InspectionComplete);
        Assert.Equal(NetBirdOwnership.Foreign, actual.Assessment.Ownership);
        Assert.Null(actual.InstallerSha256);
    }

    [Fact]
    public async Task Exact_marker_package_service_cli_and_configuration_are_one_owned_state()
    {
        var marker = WindowsNetBirdStateTestSupport.Marker();
        var environment = new FakeWindowsNetBirdStateEnvironment(WindowsNetBirdStateTestSupport.Exact(marker));
        var port = CreatePort(environment);

        var actual = await port.InspectAsync(CancellationToken.None);

        Assert.True(actual.InspectionComplete);
        Assert.Equal(NetBirdOwnership.OwnedByConnector, actual.Assessment.Ownership);
        Assert.Equal(marker.OwnedState, actual.Assessment.OwnedState);
        Assert.Equal(WindowsNetBirdStateTestSupport.PriorOperationId, actual.OwnerOperationId);
        Assert.True(actual.ServiceIdentityVerified);
    }

    [Fact]
    public async Task Changed_service_makes_stale_marker_unattributed()
    {
        var marker = WindowsNetBirdStateTestSupport.Marker();
        var environment = new FakeWindowsNetBirdStateEnvironment(
            WindowsNetBirdStateTestSupport.Exact(marker, serviceExact: false));
        var port = CreatePort(environment);

        var actual = await port.InspectAsync(CancellationToken.None);

        Assert.True(actual.InspectionComplete);
        Assert.Equal(NetBirdOwnership.Unattributed, actual.Assessment.Ownership);
        Assert.False(actual.ServiceIdentityVerified);
    }

    [Fact]
    public async Task Changed_package_makes_stale_marker_unattributed()
    {
        var marker = WindowsNetBirdStateTestSupport.Marker();
        var priorPackage = WindowsNetBirdStateTestSupport.Package with
        {
            ProductCode = Guid.Parse("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA"),
            ProductVersion = "0.78.0",
        };
        var changed = WindowsNetBirdStateTestSupport.Exact(marker);
        changed = changed with
        {
            Registrations = [new WindowsNetBirdRegistration(priorPackage, "Machine", null, "prior.msi")],
            Cli = changed.Cli with { Version = priorPackage.ProductVersion },
        };
        var port = CreatePort(new FakeWindowsNetBirdStateEnvironment(changed));

        var actual = await port.InspectAsync(CancellationToken.None);

        Assert.True(actual.InspectionComplete);
        Assert.Equal(NetBirdOwnership.Unattributed, actual.Assessment.Ownership);
    }

    [Fact]
    public async Task Prior_owned_version_uses_its_marker_bound_trusted_netbird_signer()
    {
        var priorPackage = WindowsNetBirdStateTestSupport.Package with
        {
            ProductCode = Guid.Parse("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA"),
            ProductVersion = "0.78.0",
        };
        var priorSigner = new string('D', 40);
        var marker = WindowsNetBirdStateTestSupport.Marker(
            package: priorPackage,
            installerHash: new string('E', 64),
            cliSignerThumbprint: priorSigner);
        var prior = WindowsNetBirdStateTestSupport.Exact(marker);
        prior = prior with
        {
            Registrations = [new WindowsNetBirdRegistration(priorPackage, "Machine", null, "prior.msi")],
            Cli = prior.Cli with
            {
                Version = priorPackage.ProductVersion,
                SignerThumbprint = priorSigner,
            },
        };
        var port = CreatePort(new FakeWindowsNetBirdStateEnvironment(prior));

        var actual = await port.InspectAsync(CancellationToken.None);

        Assert.Equal(NetBirdOwnership.OwnedByConnector, actual.Assessment.Ownership);
        Assert.Equal(priorPackage, actual.InstalledPackage);
        Assert.Equal(new string('E', 64), actual.InstallerSha256);
    }

    [Theory]
    [InlineData((int)NetBirdProtectedFileState.Unsafe)]
    [InlineData((int)NetBirdProtectedFileState.Unreadable)]
    public async Task Unsafe_or_unreadable_marker_fails_inventory_closed(int rawState)
    {
        var state = (NetBirdProtectedFileState)rawState;
        var environment = new FakeWindowsNetBirdStateEnvironment(
            WindowsNetBirdStateTestSupport.Exact(null, markerState: state));
        var port = CreatePort(environment);

        var actual = await port.InspectAsync(CancellationToken.None);

        Assert.False(actual.InspectionComplete);
        Assert.Equal(NetBirdOwnership.Unattributed, actual.Assessment.Ownership);
    }

    [Fact]
    public async Task Ownership_publication_requires_absent_pre_mutation_fence_and_exact_target()
    {
        var environment = new FakeWindowsNetBirdStateEnvironment(WindowsNetBirdStateTestSupport.Absent());
        var port = CreatePort(environment);
        var prior = await port.InspectAsync(CancellationToken.None);
        Assert.Equal(NetBirdOwnership.Absent, prior.Assessment.Ownership);
        environment.Current = WindowsNetBirdStateTestSupport.Exact(
            marker: null,
            evidence: "installed",
            markerState: NetBirdProtectedFileState.Missing);

        await port.PublishOwnershipAsync(
            WindowsNetBirdStateTestSupport.NewOperationId,
            new FakeVerifiedPackageLease(),
            WindowsNetBirdStateTestSupport.ServiceVerification(),
            CancellationToken.None);

        var marker = WindowsNetBirdOwnerMarker.Parse(environment.WrittenMarker!);
        Assert.Equal(WindowsNetBirdStateTestSupport.NewOperationId, marker.OperationId);
        Assert.Equal(WindowsNetBirdStateTestSupport.Package, marker.Package);
        Assert.Equal(WindowsNetBirdStateTestSupport.NewOperationId, marker.InstallationId);
        var final = await port.InspectAsync(CancellationToken.None);
        Assert.Equal(NetBirdOwnership.OwnedByConnector, final.Assessment.Ownership);
    }

    [Fact]
    public async Task Ownership_publication_never_adopts_existing_unmarked_netbird()
    {
        var environment = new FakeWindowsNetBirdStateEnvironment(
            WindowsNetBirdStateTestSupport.Exact(null, markerState: NetBirdProtectedFileState.Missing));
        var port = CreatePort(environment);
        var prior = await port.InspectAsync(CancellationToken.None);
        Assert.Equal(NetBirdOwnership.Foreign, prior.Assessment.Ownership);

        await Assert.ThrowsAsync<NetBirdMachineInvariantException>(async () =>
            await port.PublishOwnershipAsync(
                WindowsNetBirdStateTestSupport.NewOperationId,
                new FakeVerifiedPackageLease(),
                WindowsNetBirdStateTestSupport.ServiceVerification(),
                CancellationToken.None));

        Assert.Null(environment.WrittenMarker);
    }

    [Fact]
    public async Task Owned_update_replaces_durable_installation_and_operation_fence_together()
    {
        var priorMarker = WindowsNetBirdStateTestSupport.Marker();
        var before = WindowsNetBirdStateTestSupport.Exact(priorMarker, "prior");
        var environment = new FakeWindowsNetBirdStateEnvironment(before);
        var port = CreatePort(environment);
        var prior = await port.InspectAsync(CancellationToken.None);
        Assert.Equal(NetBirdOwnership.OwnedByConnector, prior.Assessment.Ownership);
        environment.Current = WindowsNetBirdStateTestSupport.Exact(priorMarker, "updated");

        await port.PublishOwnershipAsync(
            WindowsNetBirdStateTestSupport.NewOperationId,
            new FakeVerifiedPackageLease(),
            WindowsNetBirdStateTestSupport.ServiceVerification(),
            CancellationToken.None);

        var marker = WindowsNetBirdOwnerMarker.Parse(environment.WrittenMarker!);
        Assert.Equal(WindowsNetBirdStateTestSupport.NewOperationId, marker.InstallationId);
        Assert.Equal(WindowsNetBirdStateTestSupport.NewOperationId, marker.OperationId);
    }

    [Fact]
    public async Task Stale_owned_state_cannot_remove_a_later_operation_marker()
    {
        var priorMarker = WindowsNetBirdStateTestSupport.Marker();
        var currentMarker = WindowsNetBirdStateTestSupport.Marker(
            operationId: WindowsNetBirdStateTestSupport.NewOperationId,
            installationId: WindowsNetBirdStateTestSupport.NewOperationId);
        var afterUninstall = WindowsNetBirdStateTestSupport.Exact(currentMarker) with
        {
            Registrations = [],
            Service = WindowsNetBirdStateTestSupport.MissingService(),
            Cli = WindowsNetBirdStateTestSupport.MissingCli(),
        };
        var environment = new FakeWindowsNetBirdStateEnvironment(afterUninstall);
        var port = CreatePort(environment);

        await Assert.ThrowsAsync<NetBirdMachineInvariantException>(async () =>
            await port.RemoveOwnershipAsync(priorMarker.OwnedState, CancellationToken.None));

        Assert.Equal(0, environment.DeleteCalls);
        Assert.Equal(currentMarker, WindowsNetBirdOwnerMarker.Parse(environment.Current.OwnerMarker.Content!));
    }

    [Fact]
    public async Task Exact_verification_ignores_stale_owner_marker_but_not_service_or_cli()
    {
        var stalePackage = WindowsNetBirdStateTestSupport.Package with { ProductVersion = "0.78.0" };
        var staleMarker = WindowsNetBirdStateTestSupport.Marker(package: stalePackage);
        var environment = new FakeWindowsNetBirdStateEnvironment(
            WindowsNetBirdStateTestSupport.Exact(staleMarker));
        var port = CreatePort(environment);

        var actual = await port.VerifyInstalledPackageAndServiceAsync(
            WindowsNetBirdStateTestSupport.Package,
            CancellationToken.None);

        Assert.True(actual.IsExact);
        Assert.Equal(WindowsNetBirdStateTestSupport.ServiceIdentity, actual.ServiceIdentity);
    }

    [Fact]
    public async Task Restore_reinstates_protected_configuration_and_prior_operation_fence()
    {
        var updatedHash = new string('D', 64);
        var updatedMarker = WindowsNetBirdStateTestSupport.Marker(
            operationId: WindowsNetBirdStateTestSupport.NewOperationId,
            installationId: WindowsNetBirdStateTestSupport.NewOperationId,
            configurationHash: updatedHash);
        var updated = WindowsNetBirdStateTestSupport.Exact(updatedMarker) with
        {
            Configuration = new WindowsNetBirdProtectedFileSnapshot(
                NetBirdProtectedFileState.Present,
                null,
                updatedHash),
        };
        var environment = new FakeWindowsNetBirdStateEnvironment(updated);
        var port = CreatePort(environment);
        var lease = new FakeWindowsRestoreLease();

        await port.RestoreOwnershipAsync(
            lease,
            WindowsNetBirdStateTestSupport.ServiceVerification(),
            CancellationToken.None);

        Assert.Equal(1, environment.RestoreCalls);
        var marker = WindowsNetBirdOwnerMarker.Parse(environment.WrittenMarker!);
        Assert.Equal(WindowsNetBirdStateTestSupport.PriorOperationId, marker.OperationId);
        Assert.Equal(WindowsNetBirdStateTestSupport.ConfigurationHash, marker.ConfigurationSha256);
        var final = await port.InspectAsync(CancellationToken.None);
        Assert.Equal(NetBirdOwnership.OwnedByConnector, final.Assessment.Ownership);
        Assert.Equal(lease.OwnedState, final.Assessment.OwnedState);
    }

    private static WindowsNetBirdMachineStatePort CreatePort(FakeWindowsNetBirdStateEnvironment environment) =>
        new(environment, WindowsNetBirdStateTestSupport.Pin);
}
