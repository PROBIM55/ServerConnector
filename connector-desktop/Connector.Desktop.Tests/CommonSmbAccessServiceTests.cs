using System.Net;
using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Connector.Access.Contracts;
using Connector.Desktop.Services;
using Connector.Network;
using Connector.SmbAccess;
using Platform.Connector.Core;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class CommonSmbAccessServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Listing_ten_company_folders_never_mounts_drives_and_opening_one_reuses_only_that_mapping()
    {
        using var fixture = new Fixture();
        fixture.Receipt = fixture.Receipt with
        {
            Resources = Enumerable.Range(0, 10).Select(index => fixture.Receipt.Resources[0] with
            {
                ResourceId = $"company-{index}", ShareUnc = $@"\\100.64.1.20\Company{index}"
            }).ToArray()
        };
        fixture.Profile = fixture.Profile with
        {
            Resources = fixture.Receipt.Resources.Select(resource => new ResourceGrant(
                resource.ResourceId, resource.ResourceKind, resource.ProjectId, resource.Permissions)).ToArray()
        };

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var folders = await fixture.Service.RefreshAvailableAsync();
            Assert.Equal(10, folders.Count);
            Assert.All(folders, folder => Assert.Null(folder.Drive));
            Assert.Empty(fixture.Port.Mounts);
        }

        Assert.Equal("Z:\\", await fixture.Service.GetFolderToOpenAsync("company-7"));
        Assert.Equal(@"\\100.64.1.20\Company7", Assert.Single(fixture.Port.Mounts).ShareUnc);
        Assert.Equal("Z:\\", await fixture.Service.GetFolderToOpenAsync("company-7"));
        Assert.Single(fixture.Port.Mounts);
        var refreshed = await fixture.Service.RefreshAvailableAsync();
        Assert.Single(refreshed.Where(folder => folder.Drive is not null));
        Assert.Equal("company-7", refreshed.Single(folder => folder.Drive is not null).ResourceId);
    }

    [Fact]
    public async Task On_demand_open_never_duplicates_or_adopts_an_existing_foreign_target()
    {
        using var fixture = new Fixture();
        fixture.Port.Targets["Q:"] = fixture.Receipt.Resources[0].ShareUnc;

        var error = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.GetFolderToOpenAsync("docs"));

        Assert.Equal("smb_mapping_not_owned", error.Code);
        Assert.Empty(fixture.Port.Mounts);
        Assert.Empty(fixture.Port.Unmounts);
        Assert.Equal(fixture.Receipt.Resources[0].ShareUnc, fixture.Port.Targets["Q:"]);
    }

    [Fact]
    public async Task On_demand_open_cannot_mount_a_folder_outside_the_current_grant()
    {
        using var fixture = new Fixture();
        var error = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.GetFolderToOpenAsync("unassigned-company"));
        Assert.Equal("smb_resource_not_granted", error.Code);
        Assert.Empty(fixture.Port.Mounts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Routed_compatibility_unc_still_requires_the_authorized_native_overlay_route(bool routeAllowed)
    {
        using var fixture = new Fixture(destinationAllowed: address => address.Equals(IPAddress.Parse("62.113.36.107")));
        fixture.Receipt = fixture.Receipt with
        {
            Resources = [fixture.Receipt.Resources[0] with { ShareUnc = @"\\62.113.36.107\BIM_Models" }]
        };
        fixture.Port.RouteAllowed = routeAllowed;
        if (routeAllowed)
        {
            await fixture.Service.RefreshAndMountAsync();
            Assert.Equal(@"\\62.113.36.107\BIM_Models", Assert.Single(fixture.Port.Mounts).ShareUnc);
        }
        else
        {
            await Assert.ThrowsAsync<CommonSmbAccessException>(() => fixture.Service.RefreshAndMountAsync());
            Assert.Empty(fixture.Port.Mounts);
        }
    }

    [Fact]
    public async Task Public_unc_outside_the_fixed_administrator_policy_is_rejected()
    {
        using var fixture = new Fixture();
        fixture.Receipt = fixture.Receipt with
        {
            Resources = [fixture.Receipt.Resources[0] with { ShareUnc = @"\\8.8.8.8\Projects" }]
        };
        var error = await Assert.ThrowsAsync<CommonSmbAccessException>(() => fixture.Service.RefreshAndMountAsync());
        Assert.Equal("smb_destination_denied", error.Code);
        Assert.Empty(fixture.Port.Mounts);
    }

    [Fact]
    public async Task Refresh_uses_local_overlay_evidence_from_the_same_smb_request()
    {
        using var fixture = new Fixture(overlayAwareTransport: true);
        fixture.Port.LiveOverlay = () => throw new InvalidOperationException("No separate health route is permitted.");

        var folders = await fixture.Service.RefreshAndMountAsync();

        Assert.Single(folders);
        Assert.Equal(1, fixture.Transport.Calls);
        Assert.Equal(0, fixture.Port.LiveOverlayReads);
        Assert.Single(fixture.Port.Mounts);
    }

    [Fact]
    public async Task Refresh_rejects_nonready_local_evidence_without_mounting()
    {
        using var fixture = new Fixture(overlayAwareTransport: true);
        fixture.Overlay = fixture.Overlay! with { SignalConnected = false };

        var error = await Assert.ThrowsAsync<CommonSmbAccessException>(() => fixture.Service.RefreshAndMountAsync());

        Assert.Equal("smb_overlay_not_ready", error.Code);
        Assert.Empty(fixture.Port.Mounts);
        Assert.False(fixture.Service.IsAuthorizationAdmissionOpen);
    }

    [Fact]
    public async Task Refresh_rejects_unapplied_profile_before_http_or_mapping()
    {
        var fixture = new Fixture();
        fixture.Profile = fixture.Profile with { AppliedRevision = fixture.Profile.DesiredRevision - 1 };

        var error = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.RefreshAndMountAsync());

        Assert.Equal("smb_profile_invalid", error.Code);
        Assert.Equal(0, fixture.Transport.Calls);
        Assert.Empty(fixture.Port.Mounts);
    }

    [Fact]
    public async Task Refresh_rejects_resource_from_other_project()
    {
        var fixture = new Fixture();
        fixture.Receipt = fixture.Receipt with
        {
            Resources = [fixture.Receipt.Resources[0] with { ProjectId = "project-other" }]
        };

        var error = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.RefreshAndMountAsync());

        Assert.Equal("smb_resource_not_granted", error.Code);
        Assert.Empty(fixture.Port.Mounts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Refresh_rejects_duplicate_resources_and_unknown_permissions(bool duplicate)
    {
        var fixture = new Fixture();
        var resource = fixture.Receipt.Resources[0];
        fixture.Receipt = fixture.Receipt with
        {
            Resources = duplicate
                ? [resource, resource]
                : [resource with { Permissions = [ConnectorPermission.Read, (ConnectorPermission)999] }]
        };

        var error = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.RefreshAndMountAsync());

        Assert.Equal("smb_receipt_invalid", error.Code);
        Assert.Empty(fixture.Port.Mounts);
    }

    [Theory]
    [InlineData(-1, 30)]
    [InlineData(6, 30)]
    [InlineData(2, 1)]
    public async Task Refresh_rejects_expired_unbounded_or_profile_exceeding_receipt(
        int receiptMinutes,
        int profileMinutes)
    {
        var fixture = new Fixture();
        fixture.Receipt = fixture.Receipt with { ExpiresAtUtc = Now.AddMinutes(receiptMinutes) };
        fixture.Profile = fixture.Profile with { ExpiresAtUtc = Now.AddMinutes(profileMinutes) };

        var error = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.RefreshAndMountAsync());

        Assert.Equal("smb_receipt_invalid", error.Code);
        Assert.Empty(fixture.Port.Mounts);
    }

    [Theory]
    [InlineData("\\\\files.internal\\Projects")]
    [InlineData("\\\\100.64.1.20\\Projects\\Child")]
    [InlineData("\\\\100.64.1.20\\admin$")]
    [InlineData("\\\\127.0.0.1\\Projects")]
    public async Task Refresh_rejects_nonliteral_nested_or_reserved_unc(string unc)
    {
        var fixture = new Fixture();
        fixture.Receipt = fixture.Receipt with
        {
            Resources = [fixture.Receipt.Resources[0] with { ShareUnc = unc }]
        };

        var error = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.RefreshAndMountAsync());

        Assert.Equal("smb_unc_invalid", error.Code);
        Assert.Empty(fixture.Port.Mounts);
    }

    [Fact]
    public async Task Refresh_rejects_destination_outside_admin_allowlist()
    {
        var fixture = new Fixture(destinationAllowed: _ => false);

        var error = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.RefreshAndMountAsync());

        Assert.Equal("smb_destination_denied", error.Code);
        Assert.Empty(fixture.Port.Mounts);
    }

    [Fact]
    public async Task Refresh_rejects_missing_overlay_before_http()
    {
        var fixture = new Fixture();
        fixture.Overlay = null;

        var error = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.RefreshAndMountAsync());

        Assert.Equal("smb_overlay_unavailable", error.Code);
        Assert.Equal(0, fixture.Transport.Calls);
        Assert.Empty(fixture.Port.Mounts);
    }

    [Fact]
    public async Task Auto_mount_skips_foreign_occupied_drive_and_never_overwrites_it()
    {
        var fixture = new Fixture();
        fixture.Port.Targets["Z:"] = @"\\10.20.30.40\Foreign";

        var folders = await fixture.Service.RefreshAndMountAsync();

        var mount = Assert.Single(fixture.Port.Mounts);
        Assert.Equal("Y:", mount.Drive);
        Assert.Equal(@"\\10.20.30.40\Foreign", fixture.Port.Targets["Z:"]);
        Assert.Equal("Y:", Assert.Single(folders).Drive);
    }

    [Fact]
    public async Task Explicit_mount_never_adopts_foreign_mapping_even_when_target_matches()
    {
        var fixture = new Fixture();
        fixture.Port.Targets["Z:"] = fixture.Receipt.Resources[0].ShareUnc;

        var error = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.MountAsync("docs", "Z:"));

        Assert.Equal("smb_drive_occupied", error.Code);
        Assert.Empty(fixture.Port.Mounts);
        Assert.NotNull(fixture.Port.Targets["Z:"]);
    }

    [Fact]
    public async Task Credential_conflict_with_foreign_session_is_typed_and_leaves_foreign_mapping_untouched()
    {
        using var fixture = new Fixture();
        fixture.Port.Targets["P:"] = @"\\100.64.1.20\BIM_Models";
        fixture.Port.RejectedMount = new SmbCredentialConflictException();

        var open = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.GetFolderToOpenAsync("docs"));
        var mount = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.MountAsync("docs", "Y:"));

        foreach (var error in new[] { open, mount })
        {
            Assert.Equal("smb_credential_conflict", error.Code);
            Assert.Equal(1219, Assert.IsAssignableFrom<Win32Exception>(error.InnerException).NativeErrorCode);
            Assert.DoesNotContain("top-secret-password", error.ToString(), StringComparison.Ordinal);
        }
        Assert.Equal(2, fixture.Port.Mounts.Count);
        Assert.Empty(fixture.Port.Unmounts);
        Assert.Equal(@"\\100.64.1.20\BIM_Models", fixture.Port.Targets["P:"]);
        Assert.Null(fixture.Port.Targets.GetValueOrDefault("Z:"));
        Assert.Null(fixture.Port.Targets.GetValueOrDefault("Y:"));
    }

    [Fact]
    public async Task Other_native_mount_failure_keeps_untyped_win32_failure()
    {
        using var fixture = new Fixture();
        fixture.Port.RejectedMount = new Win32Exception(1326);

        var error = await Assert.ThrowsAsync<Win32Exception>(
            () => fixture.Service.GetFolderToOpenAsync("docs"));

        Assert.Equal(1326, error.NativeErrorCode);
        Assert.Empty(fixture.Port.Unmounts);
    }

    [Fact]
    public async Task Neighbour_mode_refuses_only_the_legacy_share_without_any_native_call()
    {
        var detector = new FakeLegacyConnector { NeighbourMode = true };
        using var fixture = LegacyFixture(detector);

        var open = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.GetFolderToOpenAsync("bim"));
        var mount = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.MountAsync("bim", "Y:"));

        Assert.All(new[] { open, mount }, error => Assert.Equal("smb_legacy_client_active", error.Code));
        Assert.Empty(fixture.Port.Mounts);
        Assert.Empty(fixture.Port.Unmounts);
        // The folder list stays available and marks the legacy share as served by the old client.
        Assert.Contains(fixture.Service.Folders, folder => folder is { ResourceId: "bim", ViaLegacyConnector: true });
        Assert.Contains(fixture.Service.Folders, folder => folder is { ResourceId: "docs", ViaLegacyConnector: false });
        Assert.True(fixture.Service.LegacyConnectorSnapshot!.NeighbourModeActive);

        // Company overlay folders keep working in neighbour mode.
        Assert.Equal("Z:\\", await fixture.Service.GetFolderToOpenAsync("docs"));
        Assert.Equal(@"\\100.64.1.20\Projects", Assert.Single(fixture.Port.Mounts).ShareUnc);
    }

    [Fact]
    public async Task Neighbour_mode_bulk_mount_skips_legacy_share_and_mounts_overlay_shares()
    {
        using var fixture = LegacyFixture(new FakeLegacyConnector { NeighbourMode = true });

        var folders = await fixture.Service.RefreshAndMountAsync();

        Assert.Equal(@"\\100.64.1.20\Projects", Assert.Single(fixture.Port.Mounts).ShareUnc);
        Assert.Null(folders.Single(folder => folder.ResourceId == "bim").Drive);
        Assert.True(folders.Single(folder => folder.ResourceId == "bim").ViaLegacyConnector);
    }

    [Fact]
    public async Task Neighbour_mode_is_reevaluated_on_each_action_and_transfer_allows_legacy_mount()
    {
        var detector = new FakeLegacyConnector { NeighbourMode = true };
        using var fixture = LegacyFixture(detector);
        await Assert.ThrowsAsync<CommonSmbAccessException>(() => fixture.Service.GetFolderToOpenAsync("bim"));
        // One detection per locked operation, although listing and mounting both consult it.
        Assert.Equal(1, detector.Calls);
        Assert.Equal("Z:\\", await fixture.Service.GetFolderToOpenAsync("docs"));
        Assert.Equal(2, detector.Calls);

        detector.Transferred = true; // K5 transfer marker: neighbour mode is off even if the old process runs.
        Assert.Equal("Y:\\", await fixture.Service.GetFolderToOpenAsync("bim"));

        Assert.Equal(3, detector.Calls);
        Assert.Equal(LegacyEncryptedSmbPolicy.ShareUnc, fixture.Port.Mounts.Single(call => call.Drive == "Y:").ShareUnc);
        Assert.False(fixture.Service.Folders.Single(folder => folder.ResourceId == "bim").ViaLegacyConnector);
    }

    [Fact]
    public async Task Leftover_drive_mapping_outside_neighbour_mode_is_refused_as_not_owned()
    {
        using var fixture = LegacyFixture(new FakeLegacyConnector { NeighbourMode = false });
        fixture.Port.Targets["Q:"] = LegacyEncryptedSmbPolicy.ShareUnc; // e.g. left by a killed earlier process

        var error = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.GetFolderToOpenAsync("bim"));

        Assert.Equal("smb_mapping_not_owned", error.Code);
        Assert.Empty(fixture.Port.Mounts);
        Assert.Empty(fixture.Port.Unmounts);
        Assert.Equal(LegacyEncryptedSmbPolicy.ShareUnc, fixture.Port.Targets["Q:"]);
    }

    [Fact]
    public async Task Failing_detector_fails_closed_for_legacy_share()
    {
        using var fixture = LegacyFixture(new FakeLegacyConnector { Failure = new InvalidOperationException("boom") });

        var error = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.GetFolderToOpenAsync("bim"));

        Assert.Equal("smb_legacy_client_active", error.Code);
        Assert.Empty(fixture.Port.Mounts);
        Assert.Contains("detector:InvalidOperationException",
            fixture.Service.LegacyConnectorSnapshot!.ToSafeDiagnostic(), StringComparison.Ordinal);
        Assert.DoesNotContain("boom", fixture.Service.LegacyConnectorSnapshot.ToSafeDiagnostic(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_rollback_releases_only_this_process_mapping_of_the_legacy_share()
    {
        using var fixture = LegacyFixture(new FakeLegacyConnector { Transferred = true });
        Assert.Equal("Z:\\", await fixture.Service.GetFolderToOpenAsync("docs"));
        Assert.Equal("Y:\\", await fixture.Service.GetFolderToOpenAsync("bim"));
        fixture.Port.Targets["Q:"] = LegacyEncryptedSmbPolicy.ShareUnc; // earlier-process or foreign mapping
        Assert.True(fixture.Service.Folders.Single(folder => folder.ResourceId == "bim").LegacyShare);
        Assert.False(fixture.Service.Folders.Single(folder => folder.ResourceId == "docs").LegacyShare);

        Assert.Equal(1, await fixture.Service.ReleaseOwnedLegacyShareAsync());

        Assert.Equal("Y:", Assert.Single(fixture.Port.Unmounts));
        Assert.Equal(@"\\100.64.1.20\Projects", fixture.Port.Targets["Z:"]);
        Assert.Equal(LegacyEncryptedSmbPolicy.ShareUnc, fixture.Port.Targets["Q:"]);
        Assert.Null(fixture.Service.Folders.Single(folder => folder.ResourceId == "bim").Drive);
        Assert.Equal(0, await fixture.Service.ReleaseOwnedLegacyShareAsync());
        Assert.Single(fixture.Port.Unmounts);
    }

    [Fact]
    public async Task Legacy_rollback_keeps_a_busy_legacy_mapping_owned_for_a_retry()
    {
        using var fixture = LegacyFixture(new FakeLegacyConnector { Transferred = true });
        Assert.Equal("Z:\\", await fixture.Service.GetFolderToOpenAsync("bim"));
        fixture.Port.ExactUnmountFailure = new Win32Exception(2401); // ERROR_OPEN_FILES with force=false

        var busy = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.ReleaseOwnedLegacyShareAsync());

        Assert.Equal("smb_mapping_busy", busy.Code);
        Assert.Equal(LegacyEncryptedSmbPolicy.ShareUnc, fixture.Port.Targets["Z:"]);
        fixture.Port.ExactUnmountFailure = null;
        Assert.Equal(1, await fixture.Service.ReleaseOwnedLegacyShareAsync());
        Assert.Equal("Z:", Assert.Single(fixture.Port.Unmounts));
    }

    private static Fixture LegacyFixture(FakeLegacyConnector detector)
    {
        var fixture = new Fixture(
            address => address.Equals(IPAddress.Parse("100.64.1.20")) ||
                address.Equals(IPAddress.Parse(LegacyEncryptedSmbPolicy.Destination)),
            legacyConnector: detector);
        fixture.Receipt = fixture.Receipt with
        {
            Resources = [.. fixture.Receipt.Resources, fixture.Receipt.Resources[0] with
            {
                ResourceId = "bim", ShareUnc = LegacyEncryptedSmbPolicy.ShareUnc
            }]
        };
        fixture.Profile = fixture.Profile with
        {
            Resources = [.. fixture.Profile.Resources, fixture.Profile.Resources[0] with { ResourceId = "bim" }]
        };
        return fixture;
    }

    private sealed class FakeLegacyConnector : ILegacyConnectorDetector
    {
        public bool NeighbourMode { get; set; }
        public bool Transferred { get; set; }
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }

        public LegacyConnectorSnapshot Detect()
        {
            Calls++;
            if (Failure is not null) throw Failure;
            var process = NeighbourMode ? LegacySignal.Present : LegacySignal.Absent;
            return new LegacyConnectorSnapshot(process, LegacySignal.Absent, LegacySignal.Absent, LegacySignal.Absent,
                Transferred ? LegacySignal.Present : LegacySignal.Absent, []);
        }
    }

    [Fact]
    public async Task Explicit_mount_reuse_rechecks_live_route_when_network_notification_was_missed()
    {
        using var fixture = new Fixture();
        await fixture.Service.RefreshAndMountAsync();
        fixture.Port.RouteAllowed = false;
        CommonSmbRouteInvalidation? invalidation = null;
        fixture.Service.RouteInvalidated += value => invalidation = value;
        var routeReads = fixture.Port.RouteReads;

        var denied = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.MountAsync("docs", "Z:"));

        Assert.Equal("smb_route_unavailable", denied.Code);
        Assert.True(fixture.Port.RouteReads > routeReads);
        Assert.NotNull(invalidation);
        Assert.False(fixture.Service.IsAuthorizationAdmissionOpen);
        Assert.Single(fixture.Port.Mounts);
        Assert.Equal(fixture.Receipt.Resources[0].ShareUnc, fixture.Port.Targets["Z:"]);
        Assert.Empty(fixture.Port.Unmounts);
    }

    [Fact]
    public async Task Empty_grant_needs_no_credentials_and_creates_no_mapping()
    {
        var fixture = new Fixture();
        fixture.Receipt = fixture.Receipt with { UserName = null, Password = null, Resources = [] };

        var folders = await fixture.Service.RefreshAndMountAsync();

        Assert.Empty(folders);
        Assert.Empty(fixture.Port.Mounts);
    }

    [Fact]
    public async Task Mounted_folder_snapshot_contains_only_safe_metadata()
    {
        var fixture = new Fixture();

        var folders = await fixture.Service.RefreshAndMountAsync();

        var mount = Assert.Single(fixture.Port.Mounts);
        Assert.Equal(@"SMBHOST\cnb_device", mount.UserName);
        Assert.Equal("top-secret-password", mount.Password);
        Assert.Equal(IPAddress.Parse("100.64.1.20"), mount.Destination);
        Assert.Equal([IPAddress.Parse("100.64.1.5")], mount.ExpectedOverlayAddresses);
        var json = JsonSerializer.Serialize(folders);
        Assert.DoesNotContain("top-secret-password", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SMBHOST", json, StringComparison.Ordinal);
        Assert.DoesNotContain("100.64.1.20", json, StringComparison.Ordinal);
        var folder = Assert.Single(folders);
        Assert.Equal(new CommonSmbFolder("docs", "Projects", "Z:"), folder);
        Assert.Equal("Z:" + Path.DirectorySeparatorChar, await fixture.Service.GetFolderToOpenAsync("docs"));
        Assert.All(fixture.Transport.Requests, request =>
        {
            Assert.Equal("smb/access", request.RelativeUri);
            Assert.Null(request.Body);
            Assert.Empty(request.Headers);
        });
    }

    [Fact]
    public async Task Unmount_requires_confirmation_and_rechecks_exact_owned_target()
    {
        var fixture = new Fixture();
        await fixture.Service.RefreshAndMountAsync();
        var readsBefore = fixture.Port.Reads;

        var denied = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.UnmountAsync("docs", "Z:", () => Task.FromResult(false)));

        Assert.Equal("smb_unmount_not_confirmed", denied.Code);
        Assert.Empty(fixture.Port.Unmounts);
        Assert.Equal(fixture.Receipt.Resources[0].ShareUnc, fixture.Port.Targets["Z:"]);

        await fixture.Service.UnmountAsync("docs", "Z:", () => Task.FromResult(true));

        Assert.Equal("Z:", Assert.Single(fixture.Port.Unmounts));
        Assert.Null(fixture.Port.Targets["Z:"]);
        Assert.True(fixture.Port.Reads >= readsBefore + 4);
        Assert.Null(Assert.Single(fixture.Service.Folders).Drive);
    }

    [Fact]
    public async Task Clear_authorization_hides_folders_keeps_mapping_and_still_allows_confirmed_unmount()
    {
        var fixture = new Fixture();
        await fixture.Service.RefreshAndMountAsync();

        await fixture.Service.ClearAuthorizationAsync();

        Assert.Empty(fixture.Service.Folders);
        Assert.Equal(fixture.Receipt.Resources[0].ShareUnc, fixture.Port.Targets["Z:"]);
        Assert.Empty(fixture.Port.Unmounts);

        await fixture.Service.UnmountAsync("docs", "Z:", () => Task.FromResult(true));

        Assert.Null(fixture.Port.Targets["Z:"]);
        Assert.Empty(fixture.Service.Folders);
    }

    [Fact]
    public async Task Invalidation_DrainsAcceptedMount_ClearsSnapshotAndRejectsNextCredentialRequest()
    {
        var fixture = new Fixture();
        fixture.Port.BlockMount = true;

        var accepted = fixture.Service.RefreshAndMountAsync();
        await fixture.Port.MountStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        fixture.Service.InvalidateAuthorization();
        var clearing = fixture.Service.ClearAuthorizationAsync();

        Assert.Empty(fixture.Service.Folders);
        Assert.False(clearing.IsCompleted);

        fixture.Port.ReleaseMount.TrySetResult();
        Assert.Empty(await accepted);
        await clearing;

        var callsBeforeRejectedRequest = fixture.Transport.Calls;
        var error = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.RefreshAndMountAsync());
        Assert.Equal("smb_authorization_invalidated", error.Code);
        Assert.Equal(callsBeforeRejectedRequest, fixture.Transport.Calls);
        Assert.Empty(fixture.Service.Folders);
        Assert.Equal(fixture.Receipt.Resources[0].ShareUnc, fixture.Port.Targets["Z:"]);
        Assert.Empty(fixture.Port.Unmounts);
    }

    [Fact]
    public async Task CancelledClear_WaitsForAcceptedUnmountAndDropsCredential()
    {
        var fixture = new Fixture();
        await fixture.Service.RefreshAndMountAsync();
        var confirmationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConfirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unmount = fixture.Service.UnmountAsync("docs", "Z:", async () =>
        {
            confirmationStarted.TrySetResult();
            await releaseConfirmation.Task;
            return false;
        });
        await confirmationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cancellation = new CancellationTokenSource();
        var clearing = fixture.Service.ClearAuthorizationAsync(cancellation.Token);
        cancellation.Cancel();
        Assert.False(clearing.IsCompleted);
        Assert.Empty(fixture.Service.Folders);

        releaseConfirmation.TrySetResult();
        await Assert.ThrowsAsync<CommonSmbAccessException>(() => unmount);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clearing);

        var credential = typeof(CommonSmbAccessService).GetField("_authorization",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(credential);
        Assert.Null(credential!.GetValue(fixture.Service));
        Assert.Equal(fixture.Receipt.Resources[0].ShareUnc, fixture.Port.Targets["Z:"]);
    }

    [Fact]
    public async Task Uncertain_native_cleanup_is_pending_not_published_and_can_be_explicitly_removed()
    {
        var fixture = new Fixture();
        fixture.Port.MountFailure = new SmbMappingCleanupException(
            new InvalidOperationException("postcondition"), new Win32Exception(5));

        await Assert.ThrowsAsync<SmbMappingCleanupException>(
            () => fixture.Service.RefreshAndMountAsync());

        Assert.Empty(fixture.Service.Folders);
        Assert.Equal(fixture.Receipt.Resources[0].ShareUnc, fixture.Port.Targets["Z:"]);
        await fixture.Service.UnmountAsync("docs", "Z:", () => Task.FromResult(true));
        Assert.Null(fixture.Port.Targets["Z:"]);
    }

    [Fact]
    public async Task Network_change_closes_admission_then_removes_only_unsafe_exact_owned_mapping()
    {
        var fixture = new Fixture();
        await fixture.Service.RefreshAndMountAsync();
        fixture.Port.Targets["Y:"] = @"\\100.64.1.99\Foreign";
        fixture.Port.RouteAllowed = false;
        CommonSmbRouteInvalidation? invalidation = null;
        fixture.Service.RouteInvalidated += value => invalidation = value;

        fixture.Port.RaiseNetworkChanged();

        Assert.NotNull(invalidation);
        var denied = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.GetFolderToOpenAsync("docs"));
        Assert.Equal("smb_authorization_invalidated", denied.Code);
        await fixture.Service.CleanupUnsafeMappingsAfterDrainAsync(invalidation!.RouteEpoch);

        Assert.Null(fixture.Port.Targets["Z:"]);
        Assert.Equal(@"\\100.64.1.99\Foreign", fixture.Port.Targets["Y:"]);
        Assert.Equal("Z:", Assert.Single(fixture.Port.Unmounts));
    }

    [Fact]
    public async Task Confirmed_route_invalidation_removes_old_exact_mapping_even_if_route_recovers()
    {
        var fixture = new Fixture();
        await fixture.Service.RefreshAndMountAsync();
        CommonSmbRouteInvalidation? invalidation = null;
        fixture.Service.RouteInvalidated += value => invalidation = value;

        fixture.Port.RaiseNetworkChanged();
        await fixture.Service.CleanupUnsafeMappingsAfterDrainAsync(invalidation!.RouteEpoch);

        Assert.Null(fixture.Port.Targets["Z:"]);
        Assert.Equal("Z:", Assert.Single(fixture.Port.Unmounts));
        var denied = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.RefreshAndMountAsync());
        Assert.Equal("smb_authorization_invalidated", denied.Code);
    }

    [Fact]
    public async Task Busy_mapping_after_route_invalidation_stays_pending_and_cannot_be_reused()
    {
        var fixture = new Fixture();
        await fixture.Service.RefreshAndMountAsync();
        fixture.Port.ExactUnmountFailure = new Win32Exception(2404);
        CommonSmbRouteInvalidation? invalidation = null;
        fixture.Service.RouteInvalidated += value => invalidation = value;

        fixture.Port.RaiseNetworkChanged();
        var cleanup = await Assert.ThrowsAsync<CommonSmbAccessException>(() =>
            fixture.Service.CleanupUnsafeMappingsAfterDrainAsync(invalidation!.RouteEpoch));
        Assert.Equal("smb_route_cleanup_incomplete", cleanup.Code);
        Assert.Equal(fixture.Receipt.Resources[0].ShareUnc, fixture.Port.Targets["Z:"]);

        fixture.Service.EnableAuthorization(fixture.Service.RouteEpoch);
        var calls = fixture.Transport.Calls;
        var denied = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.RefreshAndMountAsync());
        Assert.Equal("smb_cleanup_pending", denied.Code);
        Assert.Equal(calls, fixture.Transport.Calls);
        Assert.Single(fixture.Port.Mounts);
    }

    [Fact]
    public async Task Route_cleanup_forgets_foreign_replacement_without_unmounting_it()
    {
        var fixture = new Fixture();
        await fixture.Service.RefreshAndMountAsync();
        fixture.Port.Targets["Z:"] = @"\\100.64.1.99\Foreign";
        fixture.Port.RouteAllowed = false;
        CommonSmbRouteInvalidation? invalidation = null;
        fixture.Service.RouteInvalidated += value => invalidation = value;

        fixture.Port.RaiseNetworkChanged();
        await fixture.Service.CleanupUnsafeMappingsAfterDrainAsync(invalidation!.RouteEpoch);

        Assert.Equal(@"\\100.64.1.99\Foreign", fixture.Port.Targets["Z:"]);
        Assert.Empty(fixture.Port.Unmounts);
        var denied = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.UnmountAsync("docs", "Z:", () => Task.FromResult(true)));
        Assert.Equal("smb_mapping_not_owned", denied.Code);
    }

    [Fact]
    public async Task Cancelled_route_cleanup_finishes_nonforce_exact_removal_before_reporting_cancellation()
    {
        var fixture = new Fixture();
        await fixture.Service.RefreshAndMountAsync();
        fixture.Port.RouteAllowed = false;
        fixture.Port.BlockExactUnmount = true;
        CommonSmbRouteInvalidation? invalidation = null;
        fixture.Service.RouteInvalidated += value => invalidation = value;
        fixture.Port.RaiseNetworkChanged();
        using var cancellation = new CancellationTokenSource();

        var cleanup = fixture.Service.CleanupUnsafeMappingsAfterDrainAsync(
            invalidation!.RouteEpoch, cancellation.Token);
        await fixture.Port.ExactUnmountStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.False(cleanup.IsCompleted);
        fixture.Port.ReleaseExactUnmount.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cleanup);
        Assert.Null(fixture.Port.Targets["Z:"]);
        Assert.Equal("Z:", Assert.Single(fixture.Port.Unmounts));
    }

    [Fact]
    public async Task Route_change_during_mount_drains_the_accepted_mount_then_cleans_it()
    {
        var fixture = new Fixture();
        fixture.Port.BlockMount = true;
        var accepted = fixture.Service.RefreshAndMountAsync();
        await fixture.Port.MountStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Port.RouteAllowed = false;
        CommonSmbRouteInvalidation? invalidation = null;
        fixture.Service.RouteInvalidated += value => invalidation = value;

        fixture.Port.RaiseNetworkChanged();
        var cleanup = fixture.Service.CleanupUnsafeMappingsAfterDrainAsync(invalidation!.RouteEpoch);
        Assert.False(cleanup.IsCompleted);
        fixture.Port.ReleaseMount.TrySetResult();

        Assert.Empty(await accepted);
        await cleanup;
        Assert.Null(fixture.Port.Targets["Z:"]);
        Assert.Equal("Z:", Assert.Single(fixture.Port.Unmounts));
    }

    [Fact]
    public async Task Repeated_open_rechecks_live_route_even_if_network_notification_was_missed()
    {
        var fixture = new Fixture();
        await fixture.Service.RefreshAndMountAsync();
        fixture.Port.RouteAllowed = false;
        CommonSmbRouteInvalidation? invalidation = null;
        fixture.Service.RouteInvalidated += value => invalidation = value;

        var denied = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.GetFolderToOpenAsync("docs"));

        Assert.Equal("smb_route_unavailable", denied.Code);
        Assert.NotNull(invalidation);
        Assert.Empty(fixture.Service.Folders);
        await fixture.Service.CleanupUnsafeMappingsAfterDrainAsync(invalidation!.RouteEpoch);
        Assert.Null(fixture.Port.Targets["Z:"]);
    }

    [Fact]
    public async Task Each_new_action_actively_checks_overlay_and_fails_closed_on_peer_loss()
    {
        var fixture = new Fixture();
        await fixture.Service.RefreshAndMountAsync();
        var liveReads = fixture.Port.LiveOverlayReads;
        await fixture.Service.GetFolderToOpenAsync("docs");
        Assert.True(fixture.Port.LiveOverlayReads > liveReads);

        fixture.Overlay = fixture.Overlay! with
        {
            Status = NetworkServiceStatus.Degraded,
            ManagementConnected = false,
            SignalConnected = false,
            DiagnosticCode = "peer_offline"
        };
        CommonSmbRouteInvalidation? invalidation = null;
        fixture.Service.RouteInvalidated += value => invalidation = value;
        var calls = fixture.Transport.Calls;

        var denied = await Assert.ThrowsAsync<CommonSmbAccessException>(
            () => fixture.Service.GetFolderToOpenAsync("docs"));

        Assert.Equal("smb_overlay_not_ready", denied.Code);
        Assert.Equal(calls, fixture.Transport.Calls);
        Assert.NotNull(invalidation);
        Assert.Empty(fixture.Service.Folders);
        await fixture.Service.CleanupUnsafeMappingsAfterDrainAsync(invalidation!.RouteEpoch);
        Assert.Null(fixture.Port.Targets["Z:"]);
    }

    [Fact]
    public async Task Periodic_liveness_detects_peer_loss_without_network_change_notification()
    {
        var delay = new OneShotDelay();
        using var fixture = new Fixture(delay: delay.WaitAsync);
        await fixture.Service.RefreshAndMountAsync();
        fixture.Overlay = fixture.Overlay! with
        {
            Status = NetworkServiceStatus.Degraded,
            SignalConnected = false,
            DiagnosticCode = "peer_offline"
        };
        var invalidated = new TaskCompletionSource<CommonSmbRouteInvalidation>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.RouteInvalidated += value => invalidated.TrySetResult(value);

        delay.Release();
        var invalidation = await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(fixture.Service.IsAuthorizationAdmissionOpen);
        await fixture.Service.CleanupUnsafeMappingsAfterDrainAsync(invalidation.RouteEpoch);
        Assert.Null(fixture.Port.Targets["Z:"]);
    }

    [Fact]
    public async Task Cancelled_active_liveness_probe_does_not_publish_false_invalidation()
    {
        using var fixture = new Fixture();
        await fixture.Service.RefreshAndMountAsync();
        fixture.Port.BlockLiveOverlay = true;
        var invalidations = 0;
        fixture.Service.RouteInvalidated += _ => Interlocked.Increment(ref invalidations);
        using var cancellation = new CancellationTokenSource();

        var opening = fixture.Service.GetFolderToOpenAsync("docs", cancellation.Token);
        await fixture.Port.LiveOverlayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        Assert.Equal(0, Volatile.Read(ref invalidations));
        Assert.NotNull(fixture.Port.Targets["Z:"]);
    }

    [Fact]
    public void Stale_route_epoch_cannot_reopen_authorization()
    {
        var fixture = new Fixture();
        var staleEpoch = fixture.Service.RouteEpoch;
        fixture.Port.RaiseNetworkChanged();

        var denied = Assert.Throws<CommonSmbAccessException>(
            () => fixture.Service.EnableAuthorization(staleEpoch));

        Assert.Equal("smb_route_changed", denied.Code);
        Assert.False(fixture.Service.IsAuthorizationAdmissionOpen);
    }

    [Fact]
    public async Task Cold_vpn_changes_are_revalidated_and_do_not_stop_the_new_connection()
    {
        using var fixture = new Fixture();
        var session = new CommonConnectionActivation();
        var invalidations = new List<long>();
        fixture.Service.RouteInvalidated += value => session.InvalidateRoute(value.RouteEpoch, invalidations.Add);
        session.BeginSession(_ => { });
        fixture.Service.InvalidateAuthorization();

        // VPN establishment changes Windows addresses while no host is admitted.
        fixture.Port.RaiseNetworkChanged();
        Assert.False(fixture.Service.IsAuthorizationAdmissionOpen);
        var proofCalls = 0;
        var ready = await session.ActivateAsync(fixture.Service, _ =>
        {
            proofCalls++;
            return Task.CompletedTask;
        }, () => true, default);

        Assert.True(ready);
        Assert.Equal(1, proofCalls);
        Assert.True(fixture.Service.IsAuthorizationAdmissionOpen);
        Assert.Empty(invalidations);
        // A delayed callback for the establishment epoch cannot stop the host.
        session.InvalidateRoute(fixture.Service.RouteEpoch, invalidations.Add);
        Assert.Empty(invalidations);

        await fixture.Service.RefreshAndMountAsync();
        fixture.Port.RaiseNetworkChanged();
        Assert.False(fixture.Service.IsAuthorizationAdmissionOpen);
        Assert.Single(invalidations);
        await fixture.Service.CleanupUnsafeMappingsAfterDrainAsync(fixture.Service.RouteEpoch);
        Assert.Null(fixture.Port.Targets["Z:"]);
    }

    [Fact]
    public async Task Recovery_profile_identity_changed_by_final_probe_cannot_open_admission()
    {
        using var fixture = new Fixture();
        var session = new CommonConnectionActivation();
        session.BeginSession(_ => { });
        fixture.Service.InvalidateAuthorization();
        var original = new CommonRecoveryIdentity("device-a", "user-a", "company-a");
        var attempt = new CommonRecoveryAttempt(1, original, default);
        foreach (var replacement in new[] { original with { UserId = "user-b" }, original with { CompanyId = "company-b" } })
        {
            var current = original;
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ActivateAsync(fixture.Service,
                _ => { current = replacement; return Task.CompletedTask; }, () => true, default,
                () => CommonConnectionRecovery.RequireIdentity(attempt, current)));
            Assert.False(fixture.Service.IsAuthorizationAdmissionOpen);
        }
    }

    [Fact]
    public async Task Route_change_during_final_proof_requires_a_second_fresh_proof()
    {
        using var fixture = new Fixture();
        var session = new CommonConnectionActivation();
        session.BeginSession(_ => { });
        fixture.Service.InvalidateAuthorization();
        var calls = 0;

        Assert.True(await session.ActivateAsync(fixture.Service, _ =>
        {
            if (++calls == 1) fixture.Port.RaiseNetworkChanged();
            return Task.CompletedTask;
        }, () => true, default));

        Assert.Equal(2, calls);
        Assert.True(fixture.Service.IsAuthorizationAdmissionOpen);
    }

    [Fact]
    public async Task Unstable_route_cannot_be_admitted_by_repeated_retries()
    {
        using var fixture = new Fixture();
        var session = new CommonConnectionActivation();
        session.BeginSession(_ => { });
        var calls = 0;
        var error = await Assert.ThrowsAsync<CommonSmbAccessException>(() =>
            session.ActivateAsync(fixture.Service, _ =>
            {
                calls++;
                fixture.Port.RaiseNetworkChanged();
                return Task.CompletedTask;
            }, () => true, default));

        Assert.Equal("smb_route_changed", error.Code);
        Assert.Equal(2, calls);
        Assert.False(fixture.Service.IsAuthorizationAdmissionOpen);
    }

    [Fact]
    public async Task Replaced_or_cancelled_session_cannot_reopen_admission()
    {
        using var fixture = new Fixture();
        var session = new CommonConnectionActivation();
        session.BeginSession(_ => { });
        fixture.Service.InvalidateAuthorization();
        Assert.False(await session.ActivateAsync(fixture.Service, _ =>
        {
            session.BeginSession(value => { });
            return Task.CompletedTask;
        }, () => true, default));
        Assert.False(fixture.Service.IsAuthorizationAdmissionOpen);

        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.ActivateAsync(fixture.Service, _ =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            }, () => true, cancellation.Token));
        Assert.False(fixture.Service.IsAuthorizationAdmissionOpen);
    }

    [Fact]
    public async Task Revocation_before_admission_and_old_session_events_cannot_activate_a_new_host()
    {
        using var fixture = new Fixture();
        var session = new CommonConnectionActivation();
        long first = 0;
        long second = 0;
        var invalidations = new List<long>();
        session.BeginSession(value => first = value);
        fixture.Service.InvalidateAuthorization();
        Assert.False(await session.ActivateAsync(fixture.Service, _ => Task.CompletedTask, () => false, default));
        Assert.False(fixture.Service.IsAuthorizationAdmissionOpen);
        session.BeginSession(value => second = value);
        Assert.True(await session.ActivateAsync(fixture.Service, _ => Task.CompletedTask, () => true, default));
        session.InvalidateAccess(first, invalidations.Add);
        Assert.Empty(invalidations);
        session.InvalidateAccess(second, invalidations.Add);
        Assert.Equal(second, Assert.Single(invalidations));
    }

    [Fact]
    public async Task Account_change_cleanup_removes_only_owned_targets_without_a_network_event()
    {
        using var fixture = new Fixture();
        await fixture.Service.RefreshAndMountAsync();
        fixture.Port.Targets["Y:"] = @"\\100.64.1.99\Foreign";

        await fixture.Service.CleanupOwnedMappingsAfterDrainAsync();

        Assert.False(fixture.Service.IsAuthorizationAdmissionOpen);
        Assert.Null(fixture.Port.Targets["Z:"]);
        Assert.Equal(@"\\100.64.1.99\Foreign", fixture.Port.Targets["Y:"]);
        Assert.Equal("Z:", Assert.Single(fixture.Port.Unmounts));
    }

    [Fact]
    public async Task Busy_account_cleanup_stays_closed_until_an_exact_nonforce_retry_finishes()
    {
        using var fixture = new Fixture();
        await fixture.Service.RefreshAndMountAsync();
        fixture.Port.ExactUnmountFailure = new Win32Exception(2404);

        await Assert.ThrowsAsync<CommonSmbAccessException>(() => fixture.Service.CleanupOwnedMappingsAfterDrainAsync());
        Assert.False(fixture.Service.IsAuthorizationAdmissionOpen);
        fixture.Service.EnableAuthorization(fixture.Service.RouteEpoch);
        var calls = fixture.Transport.Calls;
        var denied = await Assert.ThrowsAsync<CommonSmbAccessException>(() => fixture.Service.RefreshAndMountAsync());
        Assert.Equal("smb_cleanup_pending", denied.Code);
        Assert.Equal(calls, fixture.Transport.Calls);

        fixture.Port.ExactUnmountFailure = null;
        await fixture.Service.CleanupOwnedMappingsAfterDrainAsync();
        fixture.Service.EnableAuthorization(fixture.Service.RouteEpoch);
        Assert.Single(await fixture.Service.RefreshAndMountAsync());
        Assert.Equal(2, fixture.Port.Mounts.Count);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly FixedTimeProvider _time = new(Now);
        private readonly Func<IPAddress, bool> _destinationAllowed;

        public Fixture(
            Func<IPAddress, bool>? destinationAllowed = null,
            Func<TimeSpan, CancellationToken, Task>? delay = null,
            bool overlayAwareTransport = false,
            ILegacyConnectorDetector? legacyConnector = null)
        {
            _destinationAllowed = destinationAllowed ?? (address => address.Equals(IPAddress.Parse("100.64.1.20")));
            Profile = CreateProfile();
            Overlay = CreateOverlay();
            Receipt = CreateReceipt();
            Transport = overlayAwareTransport
                ? new OverlayTransport(() => Receipt, () => Overlay!)
                : new FakeTransport(() => Receipt);
            Port = new FakePort();
            Port.LiveOverlay = () => Overlay;
            Service = new CommonSmbAccessService(
                Transport,
                () => Profile,
                _destinationAllowed,
                Port,
                _time,
                mappingLivenessInterval: TimeSpan.FromSeconds(15),
                delay,
                legacyConnector: legacyConnector);
        }

        public DeviceAccessProfile Profile { get; set; }
        public NetworkOverlaySnapshot? Overlay { get; set; }
        public ConnectorSmbAccess Receipt { get; set; }
        public FakeTransport Transport { get; }
        public FakePort Port { get; }
        public CommonSmbAccessService Service { get; }

        public void Dispose() => Service.Dispose();

        private static DeviceAccessProfile CreateProfile() => new(
            DeviceAccessProtocol.Version,
            "dev_0123456789abcdef0123456789abcdef",
            "user-1",
            "company-1",
            7,
            7,
            Now.AddMinutes(30),
            [],
            [new ResourceGrant("docs", "smb-folder", "project-1", [ConnectorPermission.Read, ConnectorPermission.Publish])]);

        private static ConnectorSmbAccess CreateReceipt() => new(
            DeviceAccessProtocol.Version,
            "dev_0123456789abcdef0123456789abcdef",
            7,
            Now.AddMinutes(2),
            @"SMBHOST\cnb_device",
            "top-secret-password",
            [new ConnectorSmbResourceAccess(
                "docs", "smb-folder", "project-1", [ConnectorPermission.Read], @"\\100.64.1.20\Projects")]);

        private static NetworkOverlaySnapshot CreateOverlay() => new(
            NetworkServiceStatus.Ready,
            StartupCheckPassed: true,
            ManagementConnected: true,
            SignalConnected: true,
            [IPAddress.Parse("100.64.1.5")],
            Now,
            "ready",
            new Uri("https://netbird.internal"));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private class FakeTransport(Func<ConnectorSmbAccess> receipt) : ICommonConnectorRequestTransport
    {
        public int Calls => Requests.Count;
        public List<Request> Requests { get; } = [];

        public Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string relativeUri,
            object? body,
            IReadOnlyDictionary<string, string> managedHeaders,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new Request(method, relativeUri, body,
                new Dictionary<string, string>(managedHeaders, StringComparer.Ordinal)));
            var json = JsonSerializer.Serialize(receipt(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        public sealed record Request(
            HttpMethod Method,
            string RelativeUri,
            object? Body,
            IReadOnlyDictionary<string, string> Headers);
    }

    private sealed class OverlayTransport(Func<ConnectorSmbAccess> receipt, Func<NetworkOverlaySnapshot> overlay)
        : FakeTransport(receipt), IOverlayAwareConnectorRequestTransport
    {
        public async Task<ProtectedConnectorResponse> SendWithOverlayAsync(HttpMethod method, string relativeUri,
            object? body, IReadOnlyDictionary<string, string> managedHeaders, CancellationToken cancellationToken)
            => new(await SendAsync(method, relativeUri, body, managedHeaders, cancellationToken), overlay());
    }

    private sealed class FakePort : IWindowsSmbMappingPort
    {
        public event Action? NetworkChanged;
        public Dictionary<string, string?> Targets { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<MountCall> Mounts { get; } = [];
        public List<string> Unmounts { get; } = [];
        public int Reads { get; private set; }
        public int RouteReads { get; private set; }
        public int LiveOverlayReads { get; private set; }
        public bool RouteAllowed { get; set; } = true;
        public Func<NetworkOverlaySnapshot?> LiveOverlay { get; set; } = () => null;
        public Exception? MountFailure { get; set; }
        public Exception? RejectedMount { get; set; } // Native refusal before any mapping exists.
        public Exception? ExactUnmountFailure { get; set; }
        public bool BlockMount { get; set; }
        public bool BlockExactUnmount { get; set; }
        public bool BlockLiveOverlay { get; set; }
        public TaskCompletionSource MountStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseMount { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ExactUnmountStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseExactUnmount { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LiveOverlayStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void RaiseNetworkChanged() => NetworkChanged?.Invoke();

        public async ValueTask<NetworkOverlaySnapshot> GetLiveOverlayAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LiveOverlayReads++;
            LiveOverlayStarted.TrySetResult();
            if (BlockLiveOverlay) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return LiveOverlay() ?? throw new InvalidOperationException("overlay unavailable");
        }

        public ValueTask<string?> GetTargetAsync(string drive, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return ValueTask.FromResult(Targets.GetValueOrDefault(drive));
        }

        public ValueTask<bool> IsRouteAllowedAsync(
            IPAddress destination,
            IReadOnlyList<IPAddress> expectedOverlayAddresses,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RouteReads++;
            return ValueTask.FromResult(RouteAllowed);
        }

        public async ValueTask MountAsync(
            string drive,
            string shareUnc,
            string userName,
            string password,
            IPAddress destination,
            IReadOnlyList<IPAddress> expectedOverlayAddresses,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Mounts.Add(new MountCall(drive, shareUnc, userName, password, destination,
                expectedOverlayAddresses.ToArray()));
            if (RejectedMount is not null) throw RejectedMount;
            Targets[drive] = shareUnc;
            MountStarted.TrySetResult();
            if (BlockMount) await ReleaseMount.Task.WaitAsync(cancellationToken);
            if (MountFailure is not null) throw MountFailure;
        }

        public ValueTask UnmountAsync(string drive, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Unmounts.Add(drive);
            Targets[drive] = null;
            return ValueTask.CompletedTask;
        }

        public async ValueTask<SmbExactMappingRemoval> TryUnmountExactAsync(
            string drive,
            string expectedShareUnc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            var target = Targets.GetValueOrDefault(drive);
            if (target is null) return SmbExactMappingRemoval.Missing;
            if (!string.Equals(target.TrimEnd('\\'), expectedShareUnc.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return SmbExactMappingRemoval.TargetChanged;
            ExactUnmountStarted.TrySetResult();
            if (BlockExactUnmount) await ReleaseExactUnmount.Task.WaitAsync(cancellationToken);
            if (ExactUnmountFailure is not null) throw ExactUnmountFailure;
            Unmounts.Add(drive);
            Targets[drive] = null;
            Reads++;
            return SmbExactMappingRemoval.Removed;
        }

        public sealed record MountCall(
            string Drive,
            string ShareUnc,
            string UserName,
            string Password,
            IPAddress Destination,
            IReadOnlyList<IPAddress> ExpectedOverlayAddresses);
    }

    private sealed class OneShotDelay
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task WaitAsync(TimeSpan interval, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _calls) == 1
                ? _release.Task.WaitAsync(cancellationToken)
                : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        public void Release() => _release.TrySetResult();
    }
}

public sealed class WindowsSmbMappingPortTests
{
    private static readonly IPAddress Destination = IPAddress.Parse("100.64.1.20");
    private static readonly IPAddress OverlaySource = IPAddress.Parse("100.64.1.5");
    private const string Share = @"\\100.64.1.20\Projects";

    [Fact]
    public async Task Post_route_failure_rolls_back_exact_mapping_and_preserves_primary_failure()
    {
        var native = new FakeNativeApi(OverlaySource, IPAddress.Parse("192.168.1.10"));
        var port = new WindowsSmbMappingPort(native);

        var error = await Assert.ThrowsAsync<SmbRouteUnavailableException>(
            async () => await port.MountAsync("Z:", Share, @"SMBHOST\cnb", "secret",
                Destination, [OverlaySource], CancellationToken.None));

        Assert.Contains("OS-selected", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, native.CancelCalls);
        Assert.Null(native.Target);
    }

    [Fact]
    public async Task Rollback_preserves_foreign_replacement_of_just_created_mapping()
    {
        var native = new FakeNativeApi(OverlaySource, IPAddress.Parse("192.168.1.10"))
        {
            ReplaceWithForeignOnTargetRead = 3
        };
        var port = new WindowsSmbMappingPort(native);

        await Assert.ThrowsAsync<SmbRouteUnavailableException>(
            async () => await port.MountAsync("Z:", Share, @"SMBHOST\cnb", "secret",
                Destination, [OverlaySource], CancellationToken.None));

        Assert.Equal(0, native.CancelCalls);
        Assert.Equal(@"\\100.64.1.99\Foreign", native.Target);
    }

    [Fact]
    public async Task Rollback_cancel_failure_reports_uncertain_cleanup_with_primary_failure()
    {
        var native = new FakeNativeApi(OverlaySource, IPAddress.Parse("192.168.1.10"))
        {
            CancelResult = 5
        };
        var port = new WindowsSmbMappingPort(native);

        var error = await Assert.ThrowsAsync<SmbMappingCleanupException>(
            async () => await port.MountAsync("Z:", Share, @"SMBHOST\cnb", "secret",
                Destination, [OverlaySource], CancellationToken.None));

        Assert.IsType<SmbRouteUnavailableException>(error.InnerException);
        Assert.IsType<Win32Exception>(error.CleanupFailure);
        Assert.Equal(1, native.CancelCalls);
        Assert.Equal(Share, native.Target);
    }

    [Theory]
    [InlineData(1219, true, false)]
    [InlineData(1219, true, true)]
    [InlineData(1326, false, false)]
    [InlineData(1326, false, true)]
    [InlineData(53, false, false)]
    public async Task Native_refusal_is_typed_only_for_credential_conflict_and_never_cancels_anything(
        int nativeError, bool credentialConflict, bool legacyEncrypted)
    {
        var native = new FakeNativeApi(OverlaySource) { AddResult = nativeError };
        using var port = legacyEncrypted
            ? new WindowsSmbMappingPort(native, legacyEncryptedShares: [LegacyEncryptedSmbPolicy.ShareUnc])
            : new WindowsSmbMappingPort(native);
        var share = legacyEncrypted ? LegacyEncryptedSmbPolicy.ShareUnc : Share;
        var destination = legacyEncrypted ? IPAddress.Parse(LegacyEncryptedSmbPolicy.Destination) : Destination;

        var error = await Assert.ThrowsAnyAsync<Win32Exception>(
            async () => await port.MountAsync("Z:", share, @"SMBHOST\cnb", "secret",
                destination, [OverlaySource], CancellationToken.None));

        Assert.Equal(nativeError, error.NativeErrorCode);
        Assert.Equal(credentialConflict, error is SmbCredentialConflictException);
        if (!credentialConflict) Assert.IsType<Win32Exception>(error);
        Assert.Equal(legacyEncrypted ? 1 : 0, native.EncryptedAdds);
        Assert.Equal(0, native.CancelCalls);
        Assert.Null(native.Target);
    }

    [Fact]
    public async Task Route_recheck_reads_current_os_route_each_time()
    {
        var native = new FakeNativeApi(OverlaySource, IPAddress.Parse("192.168.1.10"));
        using var port = new WindowsSmbMappingPort(native);

        Assert.True(await port.IsRouteAllowedAsync(Destination, [OverlaySource], CancellationToken.None));
        Assert.False(await port.IsRouteAllowedAsync(Destination, [OverlaySource], CancellationToken.None));
    }

    [Fact]
    public void Network_monitor_forwards_changes_and_is_unsubscribed_on_dispose()
    {
        var monitor = new FakeNetworkChangeMonitor();
        var port = new WindowsSmbMappingPort(new FakeNativeApi(), monitor);
        var changes = 0;
        port.NetworkChanged += () => changes++;

        monitor.Raise();
        port.Dispose();
        monitor.Raise();

        Assert.Equal(1, changes);
        Assert.True(monitor.IsDisposed);
    }

    [Fact]
    public async Task Exact_unmount_preserves_mapping_that_changed_before_native_cancel()
    {
        var native = new FakeNativeApi { ReplaceWithForeignOnTargetRead = 1 };
        native.AddConnection("Z:", Share, @"SMBHOST\cnb", "secret");
        using var port = new WindowsSmbMappingPort(native);

        var removal = await port.TryUnmountExactAsync("Z:", Share, CancellationToken.None);

        Assert.Equal(SmbExactMappingRemoval.TargetChanged, removal);
        Assert.Equal(0, native.CancelCalls);
        Assert.Equal(@"\\100.64.1.99\Foreign", native.Target);
    }

    [Fact]
    public async Task Exact_unmount_preserves_busy_mapping_when_nonforce_cancel_is_refused()
    {
        var native = new FakeNativeApi { CancelResult = 2401 };
        native.AddConnection("Z:", Share, @"SMBHOST\cnb", "secret");
        using var port = new WindowsSmbMappingPort(native);

        await Assert.ThrowsAsync<Win32Exception>(
            async () => await port.TryUnmountExactAsync("Z:", Share, CancellationToken.None));

        Assert.Equal(1, native.CancelCalls);
        Assert.Equal(Share, native.Target);
    }

    private sealed class FakeNativeApi(params IPAddress[] routeSources) : IWindowsSmbNativeApi
    {
        private readonly Queue<IPAddress> _routeSources = new(routeSources);
        private int _targetReads;

        public string? Target { get; private set; }
        public int CancelCalls { get; private set; }
        public int CancelResult { get; init; }
        public int AddResult { get; init; }
        public int EncryptedAdds { get; private set; }
        public int? ReplaceWithForeignOnTargetRead { get; init; }

        public string? GetTarget(string drive)
        {
            _targetReads++;
            if (_targetReads == ReplaceWithForeignOnTargetRead)
                Target = @"\\100.64.1.99\Foreign";
            return Target;
        }

        public IPAddress GetBestRouteSource(IPAddress destination) => _routeSources.Dequeue();

        public int AddConnection(string drive, string shareUnc, string userName, string password)
        {
            if (AddResult != 0) return AddResult;
            Target = shareUnc;
            return 0;
        }

        public int AddEncryptedConnection(string drive, string shareUnc, string userName, string password)
        {
            EncryptedAdds++;
            return AddConnection(drive, shareUnc, userName, password);
        }

        public int CancelConnection(string drive)
        {
            CancelCalls++;
            if (CancelResult == 0) Target = null;
            return CancelResult;
        }
    }

    private sealed class FakeNetworkChangeMonitor : IWindowsNetworkChangeMonitor
    {
        public event Action? Changed;
        public bool IsDisposed { get; private set; }
        public void Raise() => Changed?.Invoke();
        public void Dispose() => IsDisposed = true;
    }
}
