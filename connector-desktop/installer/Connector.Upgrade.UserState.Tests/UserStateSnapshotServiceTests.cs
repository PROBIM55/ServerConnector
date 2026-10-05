using System.Security.Cryptography;
using System.Diagnostics;
using Connector.Upgrade.UserState;
using Xunit;

namespace Connector.Upgrade.UserState.Tests;

public sealed class UserStateSnapshotServiceTests
{
    private const string CurrentSid = "S-1-5-21-111-222-333-1001";

    [Fact]
    public async Task Snapshot_hydrate_and_rollback_preserve_every_file_byte()
    {
        using var fixture = new Fixture();
        var sourceRoots = fixture.CreateLegacyState();
        var expected = CaptureTrees(sourceRoots);
        var service = fixture.CreateService(sourceRoots);
        var handle = await service.CreateSnapshotAsync(
            new UserStateSnapshotRequest(CurrentSid), fixture.Stage);

        Assert.True(handle.PayloadBytes > 0);
        Assert.Equal(expected.Values.Sum(files => files.Values.Sum(bytes => (long)bytes.Length)), handle.PayloadBytes);

        var hydrateRoots = fixture.CreateTargetRoots("hydrate");
        var hydrateService = fixture.CreateService(hydrateRoots);
        var empty = await hydrateService.InspectTargetsAsync(CurrentSid);
        var hydrated = await hydrateService.ApplySnapshotAsync(
            new UserStateApplyRequest(CurrentSid, handle, empty, UserStateApplyPurpose.HydrateFreshTarget),
            fixture.Stage);
        Assert.Equal(handle.PayloadBytes, hydrated.RestoredBytes);
        AssertTreesEqual(expected, CaptureTrees(hydrateRoots));

        File.WriteAllBytes(Path.Combine(sourceRoots[0].Path, "settings.json"), [9, 8, 7]);
        File.Delete(Path.Combine(sourceRoots[0].Path, "managed-sync", "Tekla", "state.bin"));
        Directory.CreateDirectory(Path.Combine(sourceRoots[1].Path, "unexpected"));
        File.WriteAllText(Path.Combine(sourceRoots[1].Path, "unexpected", "new.json"), "mutated");
        Directory.CreateDirectory(sourceRoots[2].Path);
        File.WriteAllText(Path.Combine(sourceRoots[2].Path, "settings.json"), "new unified Platform state");
        var approvedMutation = await service.InspectTargetsAsync(CurrentSid);

        await service.ApplySnapshotAsync(
            new UserStateApplyRequest(CurrentSid, handle, approvedMutation, UserStateApplyPurpose.ExactRollback),
            fixture.Stage);

        AssertTreesEqual(expected, CaptureTrees(sourceRoots));
        Assert.False(Directory.Exists(Path.Combine(sourceRoots[1].Path, "unexpected")));
        Assert.False(Directory.Exists(sourceRoots[2].Path));
    }

    [Fact]
    public async Task RollbackRestoresPreexistingUnifiedPlatformStateByteForByte()
    {
        using var fixture = new Fixture();
        var roots = fixture.CreateLegacyState();
        Directory.CreateDirectory(roots[2].Path);
        var settings = Path.Combine(roots[2].Path, "settings.json");
        var original = RandomNumberGenerator.GetBytes(113);
        await File.WriteAllBytesAsync(settings, original);
        var service = fixture.CreateService(roots);
        var handle = await service.CreateSnapshotAsync(
            new UserStateSnapshotRequest(CurrentSid), fixture.Stage);

        await File.WriteAllBytesAsync(settings, [1, 2, 3]);
        var changed = await service.InspectTargetsAsync(CurrentSid);
        await service.ApplySnapshotAsync(
            new UserStateApplyRequest(CurrentSid, handle, changed, UserStateApplyPurpose.ExactRollback),
            fixture.Stage);

        Assert.Equal(original, await File.ReadAllBytesAsync(settings));
    }

    [Fact]
    public async Task Different_current_sid_is_rejected_before_stage_or_source_access()
    {
        using var fixture = new Fixture(identitySid: "S-1-5-21-other");
        var service = fixture.CreateService(fixture.CreateTargetRoots("sid"));

        await Assert.ThrowsAsync<UserStateSnapshotException>(async () =>
            await service.CreateSnapshotAsync(
                new UserStateSnapshotRequest(CurrentSid),
                fixture.Stage));

        Assert.False(fixture.Stage.WasAsserted);
    }

    [Fact]
    public async Task Reparse_point_in_source_is_rejected_without_following_it()
    {
        using var fixture = new Fixture();
        var roots = fixture.CreateTargetRoots("source-with-link");
        Directory.CreateDirectory(roots[0].Path);
        var outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "must-not-copy.txt"), "outside");
        var link = Path.Combine(roots[0].Path, "escape");
        Assert.True(TryCreateDirectoryLink(link, outside), "A temporary reparse-point fixture could not be created.");

        var error = await Assert.ThrowsAsync<UserStateSnapshotException>(async () =>
            await fixture.CreateService(roots).CreateSnapshotAsync(
                new UserStateSnapshotRequest(CurrentSid), fixture.Stage));

        Assert.Contains("Reparse", error.Message, StringComparison.OrdinalIgnoreCase);
        var snapshots = Path.Combine(fixture.Stage.RootPath, "snapshots");
        Assert.True(!Directory.Exists(snapshots) || !Directory.EnumerateFileSystemEntries(snapshots).Any());
    }

    [Fact]
    public async Task Corrupted_payload_is_rejected_before_target_swap()
    {
        using var fixture = new Fixture();
        var roots = fixture.CreateLegacyState();
        var service = fixture.CreateService(roots);
        var handle = await service.CreateSnapshotAsync(
            new UserStateSnapshotRequest(CurrentSid), fixture.Stage);
        var payload = Path.Combine(
            fixture.Stage.RootPath,
            "snapshots",
            handle.SnapshotId,
            "payload",
            LegacyUserStateRootIds.Structura,
            "settings.json");
        File.SetAttributes(payload, FileAttributes.Normal);
        var bytes = File.ReadAllBytes(payload);
        bytes[0] ^= 0x5a;
        File.WriteAllBytes(payload, bytes);

        var targets = fixture.CreateTargetRoots("corrupt-target");
        var applyService = fixture.CreateService(targets);
        var approved = await applyService.InspectTargetsAsync(CurrentSid);
        await Assert.ThrowsAsync<UserStateIntegrityException>(async () =>
            await applyService.ApplySnapshotAsync(
                new UserStateApplyRequest(CurrentSid, handle, approved, UserStateApplyPurpose.HydrateFreshTarget),
                fixture.Stage));

        Assert.All(targets, target => Assert.False(Directory.Exists(target.Path)));
    }

    [Fact]
    public async Task Incomplete_snapshot_without_commit_is_rejected()
    {
        using var fixture = new Fixture();
        var snapshotId = Guid.NewGuid().ToString("N");
        var incomplete = Path.Combine(fixture.Stage.RootPath, "snapshots", snapshotId);
        Directory.CreateDirectory(incomplete);
        await File.WriteAllTextAsync(Path.Combine(incomplete, "manifest.json"), "{}");
        var handle = new UserStateSnapshotHandle(snapshotId, CurrentSid, 2, new string('0', 64), 0, 0);
        var targets = fixture.CreateTargetRoots("incomplete-target");
        var service = fixture.CreateService(targets);
        var approved = await service.InspectTargetsAsync(CurrentSid);

        var error = await Assert.ThrowsAsync<UserStateIntegrityException>(async () =>
            await service.ApplySnapshotAsync(
                new UserStateApplyRequest(CurrentSid, handle, approved, UserStateApplyPurpose.ExactRollback),
                fixture.Stage));

        Assert.Contains("incomplete", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Target_change_after_approval_is_rejected_without_overwrite()
    {
        using var fixture = new Fixture();
        var sourceRoots = fixture.CreateLegacyState();
        var service = fixture.CreateService(sourceRoots);
        var handle = await service.CreateSnapshotAsync(
            new UserStateSnapshotRequest(CurrentSid), fixture.Stage);
        var targets = fixture.CreateTargetRoots("changed-target");
        var applyService = fixture.CreateService(targets);
        var approved = await applyService.InspectTargetsAsync(CurrentSid);
        Directory.CreateDirectory(targets[0].Path);
        var unexpected = Path.Combine(targets[0].Path, "unexpected.txt");
        await File.WriteAllTextAsync(unexpected, "belongs to another actor");

        await Assert.ThrowsAsync<UserStateChangedException>(async () =>
            await applyService.ApplySnapshotAsync(
                new UserStateApplyRequest(CurrentSid, handle, approved, UserStateApplyPurpose.ExactRollback),
                fixture.Stage));

        Assert.Equal("belongs to another actor", await File.ReadAllTextAsync(unexpected));
    }

    [Fact]
    public async Task Source_and_stage_overlap_is_rejected()
    {
        using var fixture = new Fixture();
        var nestedSource = Path.Combine(fixture.Stage.RootPath, "user-state");
        Directory.CreateDirectory(nestedSource);
        await File.WriteAllTextAsync(Path.Combine(nestedSource, "settings.json"), "{}");
        var roots = new UserStateRoot[]
        {
            new(LegacyUserStateRootIds.Structura, nestedSource),
            new(LegacyUserStateRootIds.Platform, Path.Combine(fixture.Root, "platform-other"))
        };

        await Assert.ThrowsAsync<UserStateSnapshotException>(async () =>
            await fixture.CreateService(roots).CreateSnapshotAsync(
                new UserStateSnapshotRequest(CurrentSid),
                fixture.Stage));
    }

    [Fact]
    public async Task Input_open_for_writes_is_rejected_instead_of_producing_a_partial_snapshot()
    {
        using var fixture = new Fixture();
        var roots = fixture.CreateLegacyState();
        var changingPath = Path.Combine(roots[0].Path, "managed-sync", "Tekla", "state.bin");
        await using var writer = new FileStream(changingPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        await Assert.ThrowsAnyAsync<IOException>(async () =>
            await fixture.CreateService(roots).CreateSnapshotAsync(
                new UserStateSnapshotRequest(CurrentSid), fixture.Stage));

        var snapshots = Path.Combine(fixture.Stage.RootPath, "snapshots");
        Assert.True(!Directory.Exists(snapshots) || !Directory.EnumerateFileSystemEntries(snapshots).Any());
    }

    [Fact]
    public async Task Recovery_finishes_both_roots_after_crash_following_target_backup_move()
    {
        using var fixture = new Fixture();
        var roots = fixture.CreateLegacyState();
        var expected = CaptureTrees(roots);
        var snapshotService = fixture.CreateService(roots);
        var handle = await snapshotService.CreateSnapshotAsync(new UserStateSnapshotRequest(CurrentSid), fixture.Stage);
        MutateForRollback(roots);
        var approved = await snapshotService.InspectTargetsAsync(CurrentSid);
        var crash = new CallbackFaultInjector((point, context) =>
        {
            if (point == UserStateApplyFaultPoint.AfterTargetMovedToBackup &&
                context.RootId == LegacyUserStateRootIds.Structura)
            {
                throw new UserStateInjectedProcessCrashException(context.OperationId);
            }
        });
        var crashingService = fixture.CreateService(roots, crash);

        var interruption = await Assert.ThrowsAsync<UserStateInjectedProcessCrashException>(async () =>
            await crashingService.ApplySnapshotAsync(
                new UserStateApplyRequest(CurrentSid, handle, approved, UserStateApplyPurpose.ExactRollback),
                fixture.Stage));
        var recoveryService = fixture.CreateService(roots);
        Assert.Contains(
            await recoveryService.ListPendingRecoveryOperationsAsync(CurrentSid, fixture.Stage),
            item => item.OperationId == interruption.OperationId &&
                    item.Status == UserStatePendingRecoveryStatus.Recoverable);

        await recoveryService.RecoverInterruptedApplyAsync(CurrentSid, interruption.OperationId, fixture.Stage);

        AssertTreesEqual(expected, CaptureTrees(roots));
        Assert.Empty(await recoveryService.ListPendingRecoveryOperationsAsync(CurrentSid, fixture.Stage));
    }

    [Fact]
    public async Task Flushed_temporary_journal_is_discovered_and_recovered_at_publication_boundary()
    {
        using var fixture = new Fixture();
        var roots = fixture.CreateLegacyState();
        var expected = CaptureTrees(roots);
        var service = fixture.CreateService(roots);
        var handle = await service.CreateSnapshotAsync(new UserStateSnapshotRequest(CurrentSid), fixture.Stage);
        MutateForRollback(roots);
        var approved = await service.InspectTargetsAsync(CurrentSid);
        var interrupted = false;
        var boundary = new CallbackFaultInjector((point, context) =>
        {
            if (interrupted || point != UserStateApplyFaultPoint.AfterJournalTemporaryFlushedBeforePublication) return;
            interrupted = true;
            throw new UserStateInjectedProcessCrashException(context.OperationId);
        });

        var crash = await Assert.ThrowsAsync<UserStateInjectedProcessCrashException>(async () =>
            await fixture.CreateService(roots, boundary).ApplySnapshotAsync(
                new UserStateApplyRequest(CurrentSid, handle, approved, UserStateApplyPurpose.ExactRollback),
                fixture.Stage));
        var recovery = fixture.CreateService(roots);
        var pending = Assert.Single(await recovery.ListPendingRecoveryOperationsAsync(CurrentSid, fixture.Stage));
        Assert.Equal(crash.OperationId, pending.OperationId);
        Assert.Equal(UserStatePendingRecoveryStatus.RecoverableTemporaryJournal, pending.Status);

        await recovery.RecoverInterruptedApplyAsync(CurrentSid, crash.OperationId, fixture.Stage);

        AssertTreesEqual(expected, CaptureTrees(roots));
        Assert.Empty(await recovery.ListPendingRecoveryOperationsAsync(CurrentSid, fixture.Stage));
    }

    [Fact]
    public async Task Operation_workspace_without_any_safe_journal_is_reported_for_manual_recovery()
    {
        using var fixture = new Fixture();
        var roots = fixture.CreateTargetRoots("manual-recovery");
        var operationId = Guid.NewGuid().ToString("N");
        var orphan = Path.Combine(fixture.Stage.RootPath, "operations", operationId, "incoming");
        Directory.CreateDirectory(orphan);
        await File.WriteAllTextAsync(Path.Combine(orphan, "partial.bin"), "incomplete");
        var service = fixture.CreateService(roots);

        var pending = Assert.Single(await service.ListPendingRecoveryOperationsAsync(CurrentSid, fixture.Stage));
        Assert.Equal(operationId, pending.OperationId);
        Assert.Equal(UserStatePendingRecoveryStatus.NeedsManualRecovery, pending.Status);
        await Assert.ThrowsAsync<UserStateIntegrityException>(async () =>
            await service.RecoverInterruptedApplyAsync(CurrentSid, operationId, fixture.Stage));
    }

    [Fact]
    public async Task Recovery_finishes_second_root_after_crash_following_first_root_swap()
    {
        using var fixture = new Fixture();
        var roots = fixture.CreateLegacyState();
        var expected = CaptureTrees(roots);
        var snapshotService = fixture.CreateService(roots);
        var handle = await snapshotService.CreateSnapshotAsync(new UserStateSnapshotRequest(CurrentSid), fixture.Stage);
        MutateForRollback(roots);
        var approved = await snapshotService.InspectTargetsAsync(CurrentSid);
        var crash = new CallbackFaultInjector((point, context) =>
        {
            if (point == UserStateApplyFaultPoint.AfterSnapshotMovedToTarget &&
                context.RootId == LegacyUserStateRootIds.Structura)
            {
                throw new UserStateInjectedProcessCrashException(context.OperationId);
            }
        });

        var interruption = await Assert.ThrowsAsync<UserStateInjectedProcessCrashException>(async () =>
            await fixture.CreateService(roots, crash).ApplySnapshotAsync(
                new UserStateApplyRequest(CurrentSid, handle, approved, UserStateApplyPurpose.ExactRollback),
                fixture.Stage));

        await fixture.CreateService(roots).RecoverInterruptedApplyAsync(
            CurrentSid,
            interruption.OperationId,
            fixture.Stage);

        AssertTreesEqual(expected, CaptureTrees(roots));
    }

    [Fact]
    public async Task Recovery_refuses_unexpected_target_state_before_any_further_swap()
    {
        using var fixture = new Fixture();
        var roots = fixture.CreateLegacyState();
        var service = fixture.CreateService(roots);
        var handle = await service.CreateSnapshotAsync(new UserStateSnapshotRequest(CurrentSid), fixture.Stage);
        MutateForRollback(roots);
        var approved = await service.InspectTargetsAsync(CurrentSid);
        var crash = new CallbackFaultInjector((point, context) =>
        {
            if (point == UserStateApplyFaultPoint.AfterSnapshotMovedToTarget &&
                context.RootId == LegacyUserStateRootIds.Structura)
            {
                throw new UserStateInjectedProcessCrashException(context.OperationId);
            }
        });
        var interruption = await Assert.ThrowsAsync<UserStateInjectedProcessCrashException>(async () =>
            await fixture.CreateService(roots, crash).ApplySnapshotAsync(
                new UserStateApplyRequest(CurrentSid, handle, approved, UserStateApplyPurpose.ExactRollback),
                fixture.Stage));
        var unexpected = Path.Combine(roots[1].Path, "unexpected-after-crash.txt");
        await File.WriteAllTextAsync(unexpected, "must not be overwritten");
        var beforeRecovery = CaptureTrees(roots);

        await Assert.ThrowsAsync<UserStateChangedException>(async () =>
            await fixture.CreateService(roots).RecoverInterruptedApplyAsync(
                CurrentSid,
                interruption.OperationId,
                fixture.Stage));

        AssertTreesEqual(beforeRecovery, CaptureTrees(roots));
    }

    [Fact]
    public async Task Protected_incoming_reparse_swap_is_detected_immediately()
    {
        using var fixture = new Fixture();
        var roots = fixture.CreateLegacyState();
        var service = fixture.CreateService(roots);
        var handle = await service.CreateSnapshotAsync(new UserStateSnapshotRequest(CurrentSid), fixture.Stage);
        MutateForRollback(roots);
        var approved = await service.InspectTargetsAsync(CurrentSid);
        var beforeApply = CaptureTrees(roots);
        var injected = false;
        var outside = Path.Combine(fixture.Root, "junction-target");
        Directory.CreateDirectory(outside);
        var reparse = new CallbackFaultInjector((point, context) =>
        {
            if (injected || point != UserStateApplyFaultPoint.BeforeRootMutation) return;
            injected = true;
            DeleteFixtureTree(context.IncomingPath);
            Assert.True(TryCreateDirectoryLink(context.IncomingPath, outside));
        });

        await Assert.ThrowsAsync<UserStateSnapshotException>(async () =>
            await fixture.CreateService(roots, reparse).ApplySnapshotAsync(
                new UserStateApplyRequest(CurrentSid, handle, approved, UserStateApplyPurpose.ExactRollback),
                fixture.Stage));

        Assert.True(injected);
        AssertTreesEqual(beforeApply, CaptureTrees(roots));
    }

    [Fact]
    public void Public_service_api_does_not_accept_caller_selected_user_state_paths()
    {
        var constructors = typeof(UserStateSnapshotService).GetConstructors();
        Assert.Single(constructors);
        Assert.Empty(constructors[0].GetParameters());
        Assert.DoesNotContain(typeof(UserStateSnapshotRequest).GetProperties(), property => property.Name == "Roots");
        Assert.DoesNotContain(typeof(UserStateApplyRequest).GetProperties(), property => property.Name == "Targets");
    }

    private static void MutateForRollback(IReadOnlyList<UserStateRoot> roots)
    {
        File.WriteAllText(Path.Combine(roots[0].Path, "settings.json"), "changed structura");
        File.WriteAllText(Path.Combine(roots[1].Path, "desktop", "settings.json"), "changed platform");
        File.WriteAllText(Path.Combine(roots[1].Path, "logs", "new.log"), "new state");
    }

    private static void DeleteFixtureTree(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (IOException)
        {
        }

        if (!OperatingSystem.IsWindows()) return false;
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        process?.WaitForExit();
        return process?.ExitCode == 0 && Directory.Exists(link);
    }

    private static Dictionary<string, Dictionary<string, byte[]>> CaptureTrees(IReadOnlyList<UserStateRoot> roots)
    {
        return roots.ToDictionary(
            root => root.Id,
            root => Directory.Exists(root.Path)
                ? Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories)
                    .ToDictionary(
                        path => Path.GetRelativePath(root.Path, path).Replace('\\', '/'),
                        File.ReadAllBytes,
                        StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void AssertTreesEqual(
        IReadOnlyDictionary<string, Dictionary<string, byte[]>> expected,
        IReadOnlyDictionary<string, Dictionary<string, byte[]>> actual)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var root in expected.Keys)
        {
            Assert.Equal(expected[root].Keys.Order(), actual[root].Keys.Order());
            foreach (var path in expected[root].Keys)
            {
                Assert.Equal(expected[root][path], actual[root][path]);
            }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _identitySid;

        public Fixture(string identitySid = CurrentSid)
        {
            _identitySid = identitySid;
            Root = Path.Combine(Path.GetTempPath(), "Connector.Upgrade.UserState.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            var stageRoot = Path.Combine(Root, "protected-stage");
            Directory.CreateDirectory(stageRoot);
            Stage = new TestProtectedStage(stageRoot);
        }

        public string Root { get; }
        public TestProtectedStage Stage { get; }

        public UserStateSnapshotService CreateService(
            IReadOnlyList<UserStateRoot> roots,
            IUserStateApplyFaultInjector? faultInjector = null) => new(
                new TestIdentity(_identitySid),
                new TestPathPolicy(roots),
                faultInjector);

        public IReadOnlyList<UserStateRoot> CreateLegacyState()
        {
            var roots = CreateTargetRoots("legacy");
            Directory.CreateDirectory(Path.Combine(roots[0].Path, "managed-sync", "Tekla"));
            Directory.CreateDirectory(Path.Combine(roots[0].Path, "bundled-tools", "git"));
            Directory.CreateDirectory(Path.Combine(roots[0].Path, "backups", "empty"));
            File.WriteAllBytes(Path.Combine(roots[0].Path, "settings.json"),
                [0xef, 0xbb, 0xbf, .. System.Text.Encoding.UTF8.GetBytes("{\r\n  \"TokenEncrypted\":\"opaque-dpapi-base64\",\r\n  \"UnknownFuture\": {\"keep\": true}\r\n}")]);
            File.WriteAllBytes(Path.Combine(roots[0].Path, "managed-sync", "Tekla", "state.bin"), RandomNumberGenerator.GetBytes(257 * 1024 + 17));
            File.WriteAllBytes(Path.Combine(roots[0].Path, "bundled-tools", "git", "git.exe"), RandomNumberGenerator.GetBytes(73 * 1024 + 3));
            File.WriteAllText(Path.Combine(roots[0].Path, "backups", "settings.previous.json"), "{\"old\":1}");

            Directory.CreateDirectory(Path.Combine(roots[1].Path, "desktop"));
            Directory.CreateDirectory(Path.Combine(roots[1].Path, "secrets"));
            Directory.CreateDirectory(Path.Combine(roots[1].Path, "logs"));
            File.WriteAllBytes(Path.Combine(roots[1].Path, "desktop", "settings.json"),
                System.Text.Encoding.UTF8.GetBytes("{ \"ServerUrl\": \"https://example.invalid\", \"Unknown\": [1,2,3] }"));
            File.WriteAllBytes(Path.Combine(roots[1].Path, "secrets", "device-token.dat"), RandomNumberGenerator.GetBytes(211));
            File.WriteAllText(Path.Combine(roots[1].Path, "logs", "connector.log"), "prior log bytes\r\n");
            return roots;
        }

        public IReadOnlyList<UserStateRoot> CreateTargetRoots(string name)
        {
            var local = Path.Combine(Root, name);
            return
            [
                new UserStateRoot(LegacyUserStateRootIds.Structura, Path.Combine(local, "ConnectorAgentDesktop")),
                new UserStateRoot(LegacyUserStateRootIds.Platform, Path.Combine(local, "Platform", "Connector")),
                new UserStateRoot(LegacyUserStateRootIds.UnifiedPlatform, Path.Combine(local, "Structura Connector", "Platform"))
            ];
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            RemoveReparseDirectories(Root);
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }

        private static void RemoveReparseDirectories(string root)
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var info = new DirectoryInfo(directory);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
                {
                    Directory.Delete(directory);
                    continue;
                }

                RemoveReparseDirectories(directory);
            }
        }
    }

    private sealed class TestIdentity(string sid) : IUserIdentityProvider
    {
        public string GetCurrentUserSid() => sid;
    }

    private sealed class TestPathPolicy(IReadOnlyList<UserStateRoot> roots) : IUserStatePathPolicy
    {
        public IReadOnlyList<UserStateRoot> GetCanonicalRoots() => roots;
    }

    private sealed class CallbackFaultInjector(Action<UserStateApplyFaultPoint, UserStateApplyFaultContext> callback)
        : IUserStateApplyFaultInjector
    {
        public void OnFaultPoint(UserStateApplyFaultPoint point, UserStateApplyFaultContext context) => callback(point, context);
    }

    private sealed class TestProtectedStage(string rootPath) : IProtectedUserStateStage
    {
        public string RootPath { get; } = rootPath;
        public bool WasAsserted { get; private set; }

        public ValueTask AssertProtectedForUserAsync(string expectedUserSid, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(CurrentSid, expectedUserSid);
            WasAsserted = true;
            return ValueTask.CompletedTask;
        }
    }
}
