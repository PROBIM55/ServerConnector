using Connector.Upgrade.Core;
using Connector.Upgrade.WindowsHost;

namespace Connector.Upgrade.WindowsHost.Tests;

public sealed class WindowsHostUpgradePortsTests
{
    [Fact]
    public async Task RollbackLease_AfterDisposalCanBeReopenedForSameSessionRecovery()
    {
        await using var fixture = new HostFixture();
        var pin = fixture.Lock.Get(LegacyApplicationKind.StructuraConnector);
        var payload = new LegacyRollbackPayload(pin.Identity, pin.PackageId, pin.InstallerName,
            pin.Version, pin.SizeBytes, pin.Sha256, Verified: true);

        var first = await fixture.Host.AcquireVerifiedRollbackPayloadAsync(payload, CancellationToken.None);
        await first.DisposeAsync();
        var reopened = await fixture.Host.AcquireVerifiedRollbackPayloadAsync(payload, CancellationToken.None);

        Assert.Equal(pin.Identity, reopened.Inspection.Identity);
        await reopened.DisposeAsync();
    }

    [Fact]
    public void AssertSessionMatches_RejectsDifferentOperationIdOrSidWithoutCreatingJournal()
    {
        using var fixture = new HostFixture();

        Assert.Throws<UnauthorizedAccessException>(() => fixture.Host.AssertSessionMatches(
            fixture.Session with { OperationId = Guid.NewGuid().ToString("N") }));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Host.AssertSessionMatches(
            fixture.Session with { InitiatingUserSid = "S-1-5-21-foreign" }));

        Assert.False(File.Exists(fixture.JournalPath));
    }

    [Fact]
    public async Task Execute_WiresAcceptedModulesInDestructiveSafetyOrder()
    {
        await using var fixture = new HostFixture();
        using var journal = new FileUpgradeJournalStore(fixture.JournalPath);
        var result = await new UpgradeOrchestrator(fixture.Host, journal)
            .ExecuteAsync(new OneTimePlatformToken("one-time"));

        Assert.Equal(UpgradeOutcome.Succeeded, result.Outcome);
        AssertSubsequence(fixture.Calls,
            "readiness",
            "msi:inspect:StructuraConnector:present",
            "msi:inspect:PlatformConnector:present",
            "user-state:capture",
            "netbird:inspect",
            "payload:acquire:StructuraConnector",
            "payload:acquire:PlatformConnector",
            "netbird:prepare",
            "netbird:apply",
            "velopack:install",
            "enrollment:create",
            "access:verify",
            "drain:StructuraConnector",
            "msi:remove:StructuraConnector",
            "drain:PlatformConnector",
            "msi:remove:PlatformConnector",
            "velopack:verify",
            "access:verify",
            "msi:inspect:StructuraConnector:absent",
            "msi:inspect:PlatformConnector:absent");
        Assert.DoesNotContain("user-state:restore", fixture.Calls);
    }

    [Fact]
    public async Task InspectAsync_records_explicit_installed_and_absent_baseline_for_both_known_msis()
    {
        var cases = new LegacyApplicationKind[][]
        {
            [],
            [LegacyApplicationKind.StructuraConnector],
            [LegacyApplicationKind.StructuraConnector, LegacyApplicationKind.PlatformConnector],
        };

        foreach (var present in cases)
        {
            await using var fixture = new HostFixture();
            foreach (var kind in Enum.GetValues<LegacyApplicationKind>())
                fixture.Inventory.SetPresent(kind, present.Contains(kind));

            var preflight = await fixture.Host.InspectAsync(CancellationToken.None);

            Assert.NotNull(preflight.LegacyPresenceBaseline);
            Assert.Equal(2, preflight.LegacyPresenceBaseline!.Count);
            Assert.Equal(present.Length, preflight.LegacyApplications.Count);
            Assert.Equal(present.Length, preflight.RollbackPayloads.Count);
            foreach (var observation in preflight.LegacyPresenceBaseline)
            {
                Assert.False(string.IsNullOrWhiteSpace(observation.ObservationId));
                Assert.Equal(present.Contains(observation.Identity.Kind)
                        ? ExactLegacyPresence.ExactInstalled
                        : ExactLegacyPresence.Absent,
                    observation.Presence);
                Assert.Equal(present.Contains(observation.Identity.Kind) ? fixture.Lock.Get(observation.Identity.Kind).Version : null,
                    observation.InstalledVersion);
            }
        }
    }

    [Fact]
    public async Task Execute_FinalVerificationFailure_RestoresLegacyInReverseAndCompensatesUserState()
    {
        await using var fixture = new HostFixture { UnifiedReady = false };
        using var journal = new FileUpgradeJournalStore(fixture.JournalPath);
        var result = await new UpgradeOrchestrator(fixture.Host, journal)
            .ExecuteAsync(new OneTimePlatformToken("one-time"));

        Assert.Equal(UpgradeOutcome.FailedAndRolledBack, result.Outcome);
        AssertSubsequence(fixture.Calls,
            "msi:remove:StructuraConnector",
            "msi:remove:PlatformConnector",
            "msi:restore:PlatformConnector",
            "msi:restore:StructuraConnector",
            "enrollment:remove",
            "velopack:remove",
            "user-state:restore");
        Assert.True(fixture.Inventory.IsPresent(LegacyApplicationKind.StructuraConnector));
        Assert.True(fixture.Inventory.IsPresent(LegacyApplicationKind.PlatformConnector));
    }

    [Fact]
    public async Task Execute_AccessFailure_CompensatesBeforeAnyLegacyRemoval()
    {
        await using var fixture = new HostFixture { AccessSucceeds = false };
        using var journal = new FileUpgradeJournalStore(fixture.JournalPath);
        var result = await new UpgradeOrchestrator(fixture.Host, journal)
            .ExecuteAsync(new OneTimePlatformToken("one-time"));

        Assert.Equal(UpgradeOutcome.FailedAndRolledBack, result.Outcome);
        Assert.DoesNotContain(fixture.Calls, call => call.StartsWith("msi:remove:", StringComparison.Ordinal));
        AssertSubsequence(fixture.Calls,
            "enrollment:create",
            "access:verify",
            "enrollment:remove",
            "velopack:remove",
            "user-state:restore");
    }

    [Fact]
    public async Task Execute_LegacyProcessRestartAtMsiBoundary_LeavesBothOldApplicationsInstalled()
    {
        await using var fixture = new HostFixture();
        fixture.Environment.RejectAtDrainCall = 2;
        using var journal = new FileUpgradeJournalStore(fixture.JournalPath);

        var result = await new UpgradeOrchestrator(fixture.Host, journal)
            .ExecuteAsync(new OneTimePlatformToken("one-time"));

        Assert.Equal(UpgradeOutcome.FailedAndRolledBack, result.Outcome);
        Assert.DoesNotContain(fixture.Calls, call => call.StartsWith("msi:remove:", StringComparison.Ordinal));
        Assert.True(fixture.Inventory.IsPresent(LegacyApplicationKind.StructuraConnector));
        Assert.True(fixture.Inventory.IsPresent(LegacyApplicationKind.PlatformConnector));
    }

    [Fact]
    public void Constructor_RejectsDifferentCurrentWindowsSidBeforeInspection()
    {
        using var fixture = new HostFixture(createHost: false);
        fixture.Environment.CurrentSid = "S-1-5-21-other";

        Assert.Throws<UnauthorizedAccessException>(() => fixture.CreateHost());
        Assert.Empty(fixture.Calls);
    }

    [Fact]
    public async Task NetBirdRollback_RejectsChangedWindowsSidBeforeMachineMutation()
    {
        await using var fixture = new HostFixture();
        fixture.Environment.CurrentSid = "S-1-5-21-other";

        var operation = new NetBirdOperationIntent(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            NetBirdOwnership.Absent,
            NetBirdChangeKind.InstalledThisRun);
        var receipt = new NetBirdOperationReceipt(
            operation.OperationId,
            operation.PriorOwnership,
            operation.Change,
            NetBirdOperationStatus.Applied,
            NetBirdOwnership.OwnedByConnector);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Host.CompensateNetBirdMutationAsync(operation, receipt, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Host.ReconcileInterruptedNetBirdAsync(operation, CancellationToken.None).AsTask());
        Assert.DoesNotContain(fixture.Calls, call => call.StartsWith("netbird:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NetBirdRecoveryWithoutMachineOwnedPlan_FailsClosedInsteadOfRebuildingRichReceipt()
    {
        await using var fixture = new HostFixture();
        var operation = new NetBirdOperationIntent(
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            NetBirdOwnership.OwnedByConnector,
            NetBirdChangeKind.UpdatedThisRun);
        var receipt = new NetBirdOperationReceipt(
            operation.OperationId,
            operation.PriorOwnership,
            operation.Change,
            NetBirdOperationStatus.Applied,
            NetBirdOwnership.OwnedByConnector);

        await Assert.ThrowsAsync<WindowsHostManualRecoveryRequiredException>(() =>
            fixture.Host.CompensateNetBirdMutationAsync(operation, receipt, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<WindowsHostManualRecoveryRequiredException>(() =>
            fixture.Host.ReconcileInterruptedNetBirdAsync(operation, CancellationToken.None).AsTask());
        Assert.DoesNotContain(fixture.Calls, call => call.StartsWith("netbird:", StringComparison.Ordinal));
    }

    private static void AssertSubsequence(IReadOnlyList<string> actual, params string[] expected)
    {
        var position = -1;
        foreach (var item in expected)
        {
            position = actual
                .Select((value, index) => (value, index))
                .Where(pair => pair.index > position && pair.value == item)
                .Select(pair => pair.index)
                .DefaultIfEmpty(-1)
                .First();
            Assert.True(position >= 0, $"Missing ordered call '{item}'. Actual: {string.Join(", ", actual)}");
        }
    }
}
