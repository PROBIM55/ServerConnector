using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using Connector.Upgrade.Core;
using Connector.Upgrade.MachineJournal;
using Connector.Upgrade.WindowsJournal;
using Xunit;

namespace Connector.Upgrade.MachineJournal.Tests;

public sealed class MachineUpgradeJournalStoreTests
{
    [Fact]
    public async Task FreshProtectedRootInitializesAndReacquiresRecoveryAfterStoreRestart()
    {
        using var fixture = new Fixture();
        var operationId = Guid.NewGuid();
        var initial = fixture.Document(operationId);
        var store = fixture.CreateStore();
        await using (var lease = await store.AcquireLeaseAsync())
            await store.InitializeAsync(initial);
        store.Dispose();

        using var restarted = fixture.CreateStore();
        await using var restartedLease = await restarted.AcquireLeaseAsync();
        var recovered = await restarted.LoadAsync(operationId, fixture.Sid);
        Assert.Equal(initial, recovered);
        var next = initial with
        {
            Revision = 1,
            Phase = MachineUpgradePhase.AssessNetBird,
            NetBirdAssessment = new NetBirdAssessment(NetBirdOwnership.Absent, null),
        };
        await restarted.SaveAsync(operationId, fixture.Sid, expectedRevision: 0, next);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await restarted.SaveAsync(
            operationId, fixture.Sid, expectedRevision: 0, next with { Revision = 1 }));
        Assert.Equal(1, (await restarted.LoadAsync(operationId, fixture.Sid))!.Revision);
    }

    [Fact]
    public async Task RolloverArchivesOnlyFullyRolledBackOwnerAndResumesIdempotently()
    {
        using var fixture = new Fixture();
        var previous = fixture.Document(Guid.NewGuid());
        var next = fixture.Document(Guid.NewGuid());
        var rollingBack = previous with { Revision = 1, State = MachineUpgradeState.RollingBack,
            Phase = MachineUpgradePhase.Rollback };
        var rolledBack = rollingBack with { Revision = 2, State = MachineUpgradeState.RolledBack };
        using var store = fixture.CreateStore();
        await using var lease = await store.AcquireLeaseAsync();
        await store.InitializeAsync(previous);
        await store.SaveAsync(previous.OperationId, fixture.Sid, 0, rollingBack);
        await store.SaveAsync(previous.OperationId, fixture.Sid, 1, rolledBack);

        var activated = await store.RolloverAsync(previous.OperationId, next, fixture.Sid);
        Assert.Equal(next, activated);
        var archivePath = Path.Combine(fixture.Root, $"machine-journal-{previous.OperationId:N}.json");
        Assert.True(File.Exists(archivePath));
        var archivedJson = await File.ReadAllTextAsync(archivePath);
        Assert.Contains(previous.OperationId.ToString(), archivedJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RolledBack", archivedJson, StringComparison.Ordinal);
        Assert.Equal(next, await store.RolloverAsync(previous.OperationId, next, fixture.Sid));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await store.LoadAsync(previous.OperationId, fixture.Sid));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.RolloverAsync(previous.OperationId, fixture.Document(Guid.NewGuid()), fixture.Sid));

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } };
        await File.WriteAllTextAsync(archivePath, JsonSerializer.Serialize(rolledBack with { Revision = 99 }, options));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.RolloverAsync(previous.OperationId, next, fixture.Sid));
    }

    [Fact]
    public async Task RolloverRejectsManualRecoveryAndDifferentAuthenticatedSid()
    {
        using var fixture = new Fixture();
        var previous = fixture.Document(Guid.NewGuid()) with
        { Revision = 1, State = MachineUpgradeState.NeedsManualRecovery, Phase = MachineUpgradePhase.Rollback };
        using var store = fixture.CreateStore();
        await using var lease = await store.AcquireLeaseAsync();
        await store.InitializeAsync(fixture.Document(previous.OperationId));
        await store.SaveAsync(previous.OperationId, fixture.Sid, 0, previous);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await store.RolloverAsync(previous.OperationId, fixture.Document(Guid.NewGuid()), fixture.Sid));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.RolloverAsync(previous.OperationId, fixture.Document(Guid.NewGuid()), "S-1-5-18"));
        Assert.Equal(previous, await store.LoadAsync(previous.OperationId, fixture.Sid));
    }

    [Fact]
    public async Task RolloverRejectsCommittedOperation()
    {
        using var fixture = new Fixture();
        var committed = fixture.Document(Guid.NewGuid()) with
        { Revision = 1, State = MachineUpgradeState.Committed, Phase = MachineUpgradePhase.Complete };
        using var store = fixture.CreateStore();
        await using var lease = await store.AcquireLeaseAsync();
        await store.InitializeAsync(fixture.Document(committed.OperationId));
        await store.SaveAsync(committed.OperationId, fixture.Sid, 0, committed);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await store.RolloverAsync(committed.OperationId, fixture.Document(Guid.NewGuid()), fixture.Sid));
        Assert.Equal(committed, await store.LoadAsync(committed.OperationId, fixture.Sid));
    }

    [Fact]
    public async Task RolloverResumesAfterEachDurablePublicationBoundary()
    {
        foreach (var stage in new[] { 0, 1, 2 })
        {
            using var fixture = new Fixture();
            var previous = fixture.Document(Guid.NewGuid());
            var rollingBack = previous with { Revision = 1, State = MachineUpgradeState.RollingBack,
                Phase = MachineUpgradePhase.Rollback };
            var rolledBack = rollingBack with { Revision = 2, State = MachineUpgradeState.RolledBack };
            var next = fixture.Document(Guid.NewGuid());
            var archivePath = Path.Combine(fixture.Root, $"machine-journal-{previous.OperationId:N}.json");
            var journalPath = Path.Combine(fixture.Root, "machine-journal.json");
            var rolloverPath = Path.Combine(fixture.Root, "machine-journal.rollover.json");
            using (var store = fixture.CreateStore())
            {
                await using var lease = await store.AcquireLeaseAsync();
                await store.InitializeAsync(previous);
                await store.SaveAsync(previous.OperationId, fixture.Sid, 0, rollingBack);
                await store.SaveAsync(previous.OperationId, fixture.Sid, 1, rolledBack);
            }

            var oldBytes = await File.ReadAllBytesAsync(journalPath);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { Converters = { new JsonStringEnumConverter() } };
            var prepared = new MachineUpgradeRolloverRecord(1, previous.OperationId, next.OperationId,
                fixture.Sid, MachineUpgradeRolloverPhase.Prepared,
                Convert.ToHexString(SHA256.HashData(oldBytes)));
            await using (var record = WindowsJournalSecurity.CreateProtectedFile(rolloverPath, FileAccess.Write,
                FileShare.None, FileOptions.WriteThrough, fixture.Profile))
            {
                await JsonSerializer.SerializeAsync(record, prepared, options);
                record.Flush(flushToDisk: true);
            }
            if (stage >= 1)
                await WriteProtectedAsync(archivePath, oldBytes, fixture.Profile);
            if (stage >= 2)
                await File.WriteAllTextAsync(journalPath, JsonSerializer.Serialize(next, options));

            using var resumed = fixture.CreateStore();
            await using var resumedLease = await resumed.AcquireLeaseAsync();
            Assert.Equal(next, await resumed.RolloverAsync(previous.OperationId, next, fixture.Sid));
            Assert.Equal(oldBytes, await File.ReadAllBytesAsync(archivePath));
            Assert.Contains("Committed", await File.ReadAllTextAsync(rolloverPath), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PreparedRolloverRejectsArchivedJournalTamperedAfterPreparation()
    {
        using var fixture = new Fixture();
        var previous = fixture.Document(Guid.NewGuid());
        var rollingBack = previous with { Revision = 1, State = MachineUpgradeState.RollingBack,
            Phase = MachineUpgradePhase.Rollback };
        var rolledBack = rollingBack with { Revision = 2, State = MachineUpgradeState.RolledBack };
        var next = fixture.Document(Guid.NewGuid());
        var journalPath = Path.Combine(fixture.Root, "machine-journal.json");
        var archivePath = Path.Combine(fixture.Root, $"machine-journal-{previous.OperationId:N}.json");
        var rolloverPath = Path.Combine(fixture.Root, "machine-journal.rollover.json");
        using (var store = fixture.CreateStore())
        {
            await using var lease = await store.AcquireLeaseAsync();
            await store.InitializeAsync(previous);
            await store.SaveAsync(previous.OperationId, fixture.Sid, 0, rollingBack);
            await store.SaveAsync(previous.OperationId, fixture.Sid, 1, rolledBack);
        }

        var originalBytes = await File.ReadAllBytesAsync(journalPath);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } };
        var prepared = new MachineUpgradeRolloverRecord(1, previous.OperationId, next.OperationId,
            fixture.Sid, MachineUpgradeRolloverPhase.Prepared,
            Convert.ToHexString(SHA256.HashData(originalBytes)));
        await using (var record = WindowsJournalSecurity.CreateProtectedFile(rolloverPath, FileAccess.Write,
            FileShare.None, FileOptions.WriteThrough, fixture.Profile))
        {
            await JsonSerializer.SerializeAsync(record, prepared, options);
            record.Flush(flushToDisk: true);
        }
        var tamperedBytes = JsonSerializer.SerializeToUtf8Bytes(rolledBack with { Revision = 99 }, options);
        await WriteProtectedAsync(archivePath, tamperedBytes, fixture.Profile);
        await File.WriteAllTextAsync(journalPath, JsonSerializer.Serialize(next, options));

        using var resumed = fixture.CreateStore();
        await using var resumedLease = await resumed.AcquireLeaseAsync();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await resumed.RolloverAsync(previous.OperationId, next, fixture.Sid));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await resumed.LoadAsync(next.OperationId, fixture.Sid));
        Assert.Contains(next.OperationId.ToString(), await File.ReadAllTextAsync(journalPath), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Prepared", await File.ReadAllTextAsync(rolloverPath), StringComparison.Ordinal);
    }

    private static async Task WriteProtectedAsync(string path, byte[] content, WindowsJournalSecurityProfile profile)
    {
        await using var stream = WindowsJournalSecurity.CreateProtectedFile(path, FileAccess.Write,
            FileShare.None, FileOptions.WriteThrough, profile);
        await stream.WriteAsync(content);
        stream.Flush(flushToDisk: true);
    }

    [Fact]
    public async Task RejectsForeignOperationSidAndBackwardPhase()
    {
        using var fixture = new Fixture();
        var doc = fixture.Document(Guid.NewGuid());
        using var store = fixture.CreateStore();
        await using var lease = await store.AcquireLeaseAsync();
        await store.InitializeAsync(doc);
        var assessed = doc with
        {
            Revision = 1,
            Phase = MachineUpgradePhase.AssessNetBird,
            NetBirdAssessment = new NetBirdAssessment(NetBirdOwnership.Absent, null),
        };
        await store.SaveAsync(doc.OperationId, fixture.Sid, 0, assessed);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await store.LoadAsync(Guid.NewGuid(), fixture.Sid));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.LoadAsync(doc.OperationId, "S-1-5-18"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.SaveAsync(
            doc.OperationId, fixture.Sid, 1, assessed with { Revision = 2, Phase = MachineUpgradePhase.Preflight }));
    }

    [Fact]
    public async Task RollbackPayloadHandlesPersistAtAssessPhaseAndRemainOpaque()
    {
        using var fixture = new Fixture();
        var initial = fixture.Document(Guid.NewGuid());
        var assessment = initial with
        {
            Revision = 1,
            Phase = MachineUpgradePhase.AssessNetBird,
            NetBirdAssessment = new NetBirdAssessment(NetBirdOwnership.Absent, null),
        };
        var structuraHandle = $"windows-msi-op-v1:{initial.OperationId:N}:1";
        var platformHandle = $"windows-msi-op-v1:{initial.OperationId:N}:2";
        var structura = assessment with
        {
            Revision = 2,
            RollbackPayloadReceipts = new ProtectedRollbackPayloadReceiptIdentifiers(
                StructuraConnectorHandleId: structuraHandle),
        };
        var both = structura with
        {
            Revision = 3,
            RollbackPayloadReceipts = structura.RollbackPayloadReceipts! with
            {
                PlatformConnectorHandleId = platformHandle,
            },
        };

        using var store = fixture.CreateStore();
        await using var lease = await store.AcquireLeaseAsync();
        await store.InitializeAsync(initial);
        await store.SaveAsync(initial.OperationId, fixture.Sid, 0, assessment);
        await store.SaveAsync(initial.OperationId, fixture.Sid, 1, structura);
        await store.SaveAsync(initial.OperationId, fixture.Sid, 2, both);

        var later = both with { Revision = 4, Phase = MachineUpgradePhase.PrepareNetBirdMutation };
        await store.SaveAsync(initial.OperationId, fixture.Sid, 3, later);
        var loaded = await store.LoadAsync(initial.OperationId, fixture.Sid);
        Assert.Equal(later, loaded);

        var serialized = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "machine-journal.json"));
        Assert.Contains(structuraHandle, serialized, StringComparison.Ordinal);
        Assert.Contains(platformHandle, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Connector.Desktop.Setup.msi", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Platform.Connector.Desktop.Setup.msi", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sha256", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RollbackPayloadReceiptsRejectMalformedDuplicateAndWrongOperationHandles()
    {
        using var fixture = new Fixture();
        var initial = fixture.Document(Guid.NewGuid());
        var assessment = initial with
        {
            Revision = 1,
            Phase = MachineUpgradePhase.AssessNetBird,
            NetBirdAssessment = new NetBirdAssessment(NetBirdOwnership.Absent, null),
        };
        var structuraHandle = $"windows-msi-op-v1:{initial.OperationId:N}:1";
        var invalidReceipts = new[]
        {
            new ProtectedRollbackPayloadReceiptIdentifiers(StructuraConnectorHandleId: "windows-msi-op-v1:bad:1"),
            new ProtectedRollbackPayloadReceiptIdentifiers(
                StructuraConnectorHandleId: structuraHandle, PlatformConnectorHandleId: structuraHandle),
            new ProtectedRollbackPayloadReceiptIdentifiers(
                StructuraConnectorHandleId: $"windows-msi-op-v1:{Guid.NewGuid():N}:1"),
            new ProtectedRollbackPayloadReceiptIdentifiers(
                PlatformConnectorHandleId: $"windows-msi-op-v1:{initial.OperationId:N}:1"),
        };

        using var store = fixture.CreateStore();
        await using var lease = await store.AcquireLeaseAsync();
        await store.InitializeAsync(initial);
        await store.SaveAsync(initial.OperationId, fixture.Sid, 0, assessment);
        foreach (var receipts in invalidReceipts)
            await Assert.ThrowsAsync<InvalidDataException>(async () => await store.SaveAsync(
                initial.OperationId, fixture.Sid, 1, assessment with
                {
                    Revision = 2,
                    RollbackPayloadReceipts = receipts,
                }));
        Assert.Equal(assessment, await store.LoadAsync(initial.OperationId, fixture.Sid));
    }

    [Fact]
    public async Task RollbackPayloadReceiptsAreMonotonicAndCanOnlyBeAddedInAssessNetBird()
    {
        using var fixture = new Fixture();
        var initial = fixture.Document(Guid.NewGuid());
        var assessment = initial with
        {
            Revision = 1,
            Phase = MachineUpgradePhase.AssessNetBird,
            NetBirdAssessment = new NetBirdAssessment(NetBirdOwnership.Absent, null),
        };
        var one = assessment with
        {
            Revision = 2,
            RollbackPayloadReceipts = new ProtectedRollbackPayloadReceiptIdentifiers(
                StructuraConnectorHandleId: $"windows-msi-op-v1:{initial.OperationId:N}:1"),
        };
        using var store = fixture.CreateStore();
        await using var lease = await store.AcquireLeaseAsync();
        await store.InitializeAsync(initial);
        await store.SaveAsync(initial.OperationId, fixture.Sid, 0, assessment);
        await store.SaveAsync(initial.OperationId, fixture.Sid, 1, one);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.SaveAsync(
            initial.OperationId, fixture.Sid, 2, one with { Revision = 3, RollbackPayloadReceipts = null }));

        var later = one with { Revision = 3, Phase = MachineUpgradePhase.PrepareNetBirdMutation };
        await store.SaveAsync(initial.OperationId, fixture.Sid, 2, later);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.SaveAsync(
            initial.OperationId, fixture.Sid, 3, later with
            {
                Revision = 4,
                RollbackPayloadReceipts = later.RollbackPayloadReceipts! with
                {
                    PlatformConnectorHandleId = $"windows-msi-op-v1:{initial.OperationId:N}:2",
                },
            }));
        Assert.Equal(later, await store.LoadAsync(initial.OperationId, fixture.Sid));
    }

    [Fact]
    public async Task SchemaOneWithoutRollbackHandlesRemainsReadableAndUnknownVersionIsPreserved()
    {
        using var fixture = new Fixture();
        var initial = fixture.Document(Guid.NewGuid());
        using var store = fixture.CreateStore();
        await using var lease = await store.AcquireLeaseAsync();
        await store.InitializeAsync(initial);
        var raw = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "machine-journal.json"));
        Assert.DoesNotContain("rollbackPayloadReceipts", raw, StringComparison.Ordinal);
        Assert.Equal(initial, await store.LoadAsync(initial.OperationId, fixture.Sid));

        var futureSchema = raw.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal);
        Assert.NotEqual(raw, futureSchema);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "machine-journal.json"), futureSchema);
        var bytesBeforeLoad = await File.ReadAllBytesAsync(Path.Combine(fixture.Root, "machine-journal.json"));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await store.LoadAsync(initial.OperationId, fixture.Sid));
        Assert.Equal(bytesBeforeLoad, await File.ReadAllBytesAsync(Path.Combine(fixture.Root, "machine-journal.json")));
    }

    [Fact]
    public async Task RoundTripsRealNetBirdAndVelopackReceiptIdentifiers()
    {
        using var fixture = new Fixture();
        var operationId = Guid.NewGuid();
        var document = fixture.RealisticDocument(operationId);
        using var store = fixture.CreateStore();
        await using var lease = await store.AcquireLeaseAsync();
        await store.InitializeAsync(document);
        Assert.Equal(document, await store.LoadAsync(operationId, fixture.Sid));
    }

    [Fact]
    public async Task RoundTripsBoundedNetBirdReconciliationActionAndEvidence()
    {
        using var fixture = new Fixture();
        var operationId = Guid.NewGuid();
        var realistic = fixture.RealisticDocument(operationId);
        var document = fixture.Document(operationId);
        var assessed = document with
        {
            Revision = 1,
            Phase = MachineUpgradePhase.AssessNetBird,
            NetBirdAssessment = realistic.NetBirdAssessment,
        };
        var prepared = assessed with
        {
            Revision = 2,
            Phase = MachineUpgradePhase.PrepareNetBirdMutation,
            NetBirdMutationPlan = realistic.NetBirdMutationPlan,
        };
        var intent = prepared with { Revision = 3, Phase = MachineUpgradePhase.MutateNetBird };
        var reconciled = document with
        {
            Revision = 4,
            State = MachineUpgradeState.RollingBack,
            Phase = MachineUpgradePhase.Rollback,
            NetBirdAssessment = assessed.NetBirdAssessment,
            NetBirdMutationPlan = prepared.NetBirdMutationPlan,
            NetBirdReconciliation = new NetBirdReconciliationReceipt(
                NetBirdInterruptedRecoveryAction.RestoredPriorOwnedState,
                "netbird-windows-v1:" + new string('a', 64) + "|netbird-windows-v1:" + new string('b', 64)),
        };
        using var store = fixture.CreateStore();
        await using var lease = await store.AcquireLeaseAsync();
        await store.InitializeAsync(document);
        await store.SaveAsync(operationId, fixture.Sid, expectedRevision: 0, assessed);
        await store.SaveAsync(operationId, fixture.Sid, expectedRevision: 1, prepared);
        await store.SaveAsync(operationId, fixture.Sid, expectedRevision: 2, intent);
        await store.SaveAsync(operationId, fixture.Sid, expectedRevision: 3, reconciled);

        Assert.Equal(reconciled, await store.LoadAsync(operationId, fixture.Sid));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await store.SaveAsync(
            operationId, fixture.Sid, 4, reconciled with
            {
                Revision = 5,
                NetBirdReconciliation = reconciled.NetBirdReconciliation! with { EvidenceId = @"C:\\secret\\token" },
            }));
    }

    [Fact]
    public async Task CompensationIntentSurvivesRestartAndCompletedEvidenceIsDurable()
    {
        using var fixture = new Fixture();
        var operationId = Guid.NewGuid();
        var applied = fixture.RealisticDocument(operationId);
        var intent = applied with
        {
            Revision = 1,
            State = MachineUpgradeState.RollingBack,
            Phase = MachineUpgradePhase.Rollback,
            NetBirdCompensation = new MachineNetBirdCompensation(
                MachineNetBirdCompensationAction.RestoreUpdatedThisRun, Completed: false),
        };
        var evidence = "netbird-windows-v1:" + new string('a', 64);
        var complete = intent with
        {
            Revision = 2,
            State = MachineUpgradeState.RolledBack,
            NetBirdCompensation = intent.NetBirdCompensation! with { Completed = true, EvidenceId = evidence },
        };
        var store = fixture.CreateStore();
        await using (var lease = await store.AcquireLeaseAsync())
        {
            await store.InitializeAsync(applied);
            await store.SaveAsync(operationId, fixture.Sid, 0, intent);
        }
        store.Dispose();

        using var restarted = fixture.CreateStore();
        await using var restartedLease = await restarted.AcquireLeaseAsync();
        Assert.Equal(intent, await restarted.LoadAsync(operationId, fixture.Sid));
        await restarted.SaveAsync(operationId, fixture.Sid, 1, complete);
        Assert.Equal(complete, await restarted.LoadAsync(operationId, fixture.Sid));
    }

    [Fact]
    public async Task ManualRecoveryCanEnterMonotonicRecoveryRollbackAfterCompletePhase()
    {
        using var fixture = new Fixture();
        var document = fixture.Document(Guid.NewGuid());
        using var store = fixture.CreateStore();
        await using var lease = await store.AcquireLeaseAsync();
        await store.InitializeAsync(document);
        var recoveryRequired = document with
        {
            Revision = 1,
            State = MachineUpgradeState.NeedsManualRecovery,
            Phase = MachineUpgradePhase.Complete,
        };
        await store.SaveAsync(document.OperationId, fixture.Sid, 0, recoveryRequired);
        var rollback = recoveryRequired with
        {
            Revision = 2,
            State = MachineUpgradeState.RollingBack,
            Phase = MachineUpgradePhase.RecoveryRollback,
        };
        await store.SaveAsync(document.OperationId, fixture.Sid, 1, rollback);
        var rolledBack = rollback with { Revision = 3, State = MachineUpgradeState.RolledBack };
        await store.SaveAsync(document.OperationId, fixture.Sid, 2, rolledBack);
        Assert.Equal(rolledBack, await store.LoadAsync(document.OperationId, fixture.Sid));
    }

    [Fact]
    public void RejectsUntrustedRootOwnerAndInheritedAcl()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Root);
        var foreignProfile = new WindowsJournalSecurityProfile(
            new SecurityIdentifier("S-1-5-18"), ["S-1-5-18"], ["S-1-5-18"], ["S-1-5-18"],
            RequireElevatedCaller: false);
        Assert.Throws<UnauthorizedAccessException>(() =>
            new MachineUpgradeJournalStore(fixture.Root, foreignProfile));

        var inheritedRoot = Path.Combine(fixture.Parent, "inherited");
        Directory.CreateDirectory(inheritedRoot);
        Assert.Throws<UnauthorizedAccessException>(() =>
            new MachineUpgradeJournalStore(inheritedRoot, fixture.Profile));
    }

    [Fact]
    public async Task RejectsMalformedAndOversizedJournalDocuments()
    {
        using var fixture = new Fixture();
        using var store = fixture.CreateStore();
        await using var lease = await store.AcquireLeaseAsync();
        var doc = fixture.Document(Guid.NewGuid());
        await store.InitializeAsync(doc);
        File.WriteAllText(Path.Combine(fixture.Root, "machine-journal.json"), "{");
        await Assert.ThrowsAnyAsync<Exception>(async () => await store.LoadAsync(doc.OperationId, fixture.Sid));
        File.WriteAllText(Path.Combine(fixture.Root, "machine-journal.json"), new string('x', 300 * 1024));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await store.LoadAsync(doc.OperationId, fixture.Sid));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _name = "machine-journal-test-" + Guid.NewGuid().ToString("N");
        public string Parent { get; } = Path.Combine(Path.GetTempPath(), "ConnectorUpgradeTests", Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Parent, _name);
        public string Sid { get; }
        public WindowsJournalSecurityProfile Profile { get; }

        public Fixture()
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            Sid = identity.User?.Value ?? throw new InvalidOperationException("The test user SID is unavailable.");
            var owner = new SecurityIdentifier(Sid);
            Profile = new WindowsJournalSecurityProfile(owner, [Sid], [Sid], [Sid], RequireElevatedCaller: false);
            Directory.CreateDirectory(Parent);
        }

        public MachineUpgradeJournalStore CreateStore() => new(Root, Profile);

        public MachineUpgradeJournalDocument Document(Guid operationId) => new(
            MachineUpgradeJournalDocument.CurrentSchemaVersion,
            operationId,
            Sid,
            Revision: 0,
            MachineUpgradeState.InProgress,
            MachineUpgradePhase.Preflight,
            NetBirdAssessment: null,
            NetBirdMutationPlan: null,
            RecoveryReceipt: null,
            new ProtectedVelopackReceiptIdentifiers(null, null));

        public MachineUpgradeJournalDocument RealisticDocument(Guid operationId)
        {
            const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            var netBirdId = Guid.NewGuid().ToString("N");
            var service = new NetBirdOwnedState(netBirdId, "0.39.1", hash, "netbird-service-v1:" + hash);
            var assessment = new NetBirdAssessment(NetBirdOwnership.OwnedByConnector, netBirdId, service);
            var packageIdentity = new NetBirdMsiPackageIdentity(
                Guid.NewGuid(), Guid.NewGuid(), "0.39.1", "NetBird", "NetBird");
            var target = new ProtectedNetBirdPackageReceipt(
                "netbird-msi-v1:sha256:" + hash + ":stage:" + Guid.NewGuid().ToString("N"),
                RollbackPayloadProtection.ProtectedMachineStaging,
                "NetBirdSetup.exe",
                packageIdentity,
                12_345,
                hash,
                "CN=NetBird, O=NetBird",
                "0123456789ABCDEF0123456789ABCDEF01234567");
            var restore = new NetBirdOwnedRestorePoint(
                "netbird-restore-v1:" + Guid.NewGuid().ToString("N"),
                service,
                RollbackPayloadProtection.ProtectedMachineStaging,
                packageIdentity,
                hash);
            var plan = new NetBirdMutationPlan(
                NetBirdChangeKind.UpdatedThisRun,
                operationId.ToString("D"),
                assessment,
                target,
                restore);
            var receipt = new NetBirdRecoveryReceipt(
                NetBirdChangeKind.UpdatedThisRun,
                operationId.ToString("D"),
                restore.HandleId,
                RollbackPayloadProtection.ProtectedMachineStaging,
                service,
                service,
                packageIdentity,
                hash);
            return Document(operationId) with
            {
                NetBirdAssessment = assessment,
                NetBirdMutationPlan = plan,
                RecoveryReceipt = receipt,
                VelopackReceipts = new ProtectedVelopackReceiptIdentifiers(
                    "windows-velopack-setup-v1:" + hash + ":stage:" + Guid.NewGuid().ToString("N"),
                    operationId.ToString("N")),
            };
        }

        public void Dispose()
        {
            // Test-owned data only; cleanup is non-recursive by design.
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            var inherited = Path.Combine(Parent, "inherited");
            if (Directory.Exists(inherited)) Directory.Delete(inherited, recursive: true);
            if (Directory.Exists(Parent)) Directory.Delete(Parent, recursive: true);
        }
    }
}
