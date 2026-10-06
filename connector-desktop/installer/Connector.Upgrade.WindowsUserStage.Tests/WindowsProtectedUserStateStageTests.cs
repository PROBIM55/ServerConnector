using System.Security.AccessControl;
using Connector.Upgrade.WindowsUserStage;
using Xunit;

namespace Connector.Upgrade.WindowsUserStage.Tests;

public sealed class WindowsProtectedUserStateStageTests
{
    private const string CurrentSid = "S-1-5-21-1000000001-1000000002-1000000003-1001";

    [Fact]
    public async Task CreatesCanonicalStageWithExactUserSystemAndAdministratorsAcl()
    {
        var platform = new FakeWindowsUserStagePlatform();
        var operationId = Guid.NewGuid().ToString("N");

        var stage = new WindowsProtectedUserStateStageFactory(platform)
            .CreateForCurrentUser(CurrentSid, operationId);

        Assert.Equal(
            Path.Combine(platform.ProgramData, "StructuraConnectorInstaller", "UserState", CurrentSid, operationId),
            stage.RootPath);
        var acl = platform.Security[stage.RootPath];
        Assert.True(acl.IsDaclProtected);
        Assert.Equal(WindowsUserStageSecurity.AdministratorsSid, acl.OwnerSid);
        Assert.Equal(
            new[] { CurrentSid, WindowsUserStageSecurity.SystemSid, WindowsUserStageSecurity.AdministratorsSid }.Order(),
            acl.Rules.Select(rule => rule.Sid).Order());
        Assert.All(acl.Rules, rule =>
        {
            Assert.Equal(AccessControlType.Allow, rule.AccessType);
            Assert.Equal(FileSystemRights.FullControl, rule.Rights);
            Assert.False(rule.IsInherited);
        });
        var installerRoot = Path.Combine(platform.ProgramData, "StructuraConnectorInstaller");
        var moduleRoot = Path.Combine(installerRoot, "UserState");
        WindowsUserStageSecurity.ValidateSharedDirectory("installer-root", platform.Security[installerRoot]);
        WindowsUserStageSecurity.ValidateSharedDirectory("module-root", platform.Security[moduleRoot]);
        Assert.Equal(FileSystemRights.ReadAndExecute,
            platform.Security[installerRoot].Rules.Single(rule => rule.Sid == WindowsUserStageSecurity.BuiltinUsersSid).Rights);
        await stage.AssertProtectedForUserAsync(CurrentSid, CancellationToken.None);
    }

    [Fact]
    public void RefusesUnelevatedCreationBeforeAnyFilesystemMutation()
    {
        var platform = new FakeWindowsUserStagePlatform { Elevated = false };

        Assert.Throws<UnauthorizedAccessException>(() =>
            new WindowsProtectedUserStateStageFactory(platform).CreateForCurrentUser(CurrentSid, Guid.NewGuid().ToString("N")));

        Assert.Empty(platform.CreatedPaths);
    }

    [Fact]
    public async Task CreatesStageForAuthenticatedInitiatingSidAcrossOverTheShoulderElevation()
    {
        const string initiatingSid = "S-1-5-21-1000000001-1000000002-1000000003-1002";
        const string helperSid = "S-1-5-21-2000000001-2000000002-2000000003-1001";
        var platform = new FakeWindowsUserStagePlatform { CurrentSid = helperSid };
        var operationId = Guid.NewGuid().ToString("N");

        var stage = new WindowsProtectedUserStateStageFactory(platform)
            .CreateForInitiatingUser(initiatingSid, operationId);

        Assert.Equal(Path.Combine(platform.ProgramData, "StructuraConnectorInstaller", "UserState", initiatingSid, operationId),
            stage.RootPath);
        Assert.Equal(0, platform.LocalApplicationDataReadCount);
        Assert.Equal(new[] { initiatingSid, WindowsUserStageSecurity.SystemSid, WindowsUserStageSecurity.AdministratorsSid }.Order(),
            platform.Security[stage.RootPath].Rules.Select(rule => rule.Sid).Order());

        platform.CurrentSid = initiatingSid;
        platform.LocalApplicationData = @"C:\Users\initiating\AppData\Local";
        var createdCount = platform.CreatedPaths.Count;
        var protectCount = platform.ProtectCount;
        var reopened = new WindowsProtectedUserStateStageFactory(platform)
            .OpenExistingForCurrentUser(initiatingSid, operationId);
        await reopened.AssertProtectedForUserAsync(initiatingSid, CancellationToken.None);
        Assert.Equal(stage.RootPath, reopened.RootPath);
        Assert.Equal(createdCount, platform.CreatedPaths.Count);
        Assert.Equal(protectCount, platform.ProtectCount);
    }

    [Fact]
    public void SharedAncestorsUseBuiltinUsersReadExecuteAndKeepSlotsSidPrivate()
    {
        const string firstSid = "S-1-5-21-1000000001-1000000002-1000000003-1001";
        const string secondSid = "S-1-5-21-1000000001-1000000002-1000000003-1002";
        var platform = new FakeWindowsUserStagePlatform
        {
            CurrentSid = "S-1-5-21-2000000001-2000000002-2000000003-1001",
        };
        var factory = new WindowsProtectedUserStateStageFactory(platform);
        var first = factory.CreateForInitiatingUser(firstSid, Guid.NewGuid().ToString("N"));
        var second = factory.CreateForInitiatingUser(secondSid, Guid.NewGuid().ToString("N"));
        var installerRoot = Path.Combine(platform.ProgramData, "StructuraConnectorInstaller");
        var moduleRoot = Path.Combine(installerRoot, "UserState");

        foreach (var path in new[] { installerRoot, moduleRoot })
        {
            var acl = platform.Security[path];
            WindowsUserStageSecurity.ValidateSharedDirectory(path, acl);
            Assert.Equal(FileSystemRights.ReadAndExecute,
                acl.Rules.Single(rule => rule.Sid == WindowsUserStageSecurity.BuiltinUsersSid).Rights);
            Assert.DoesNotContain(firstSid, acl.Rules.Select(rule => rule.Sid));
            Assert.DoesNotContain(secondSid, acl.Rules.Select(rule => rule.Sid));
        }
        Assert.DoesNotContain(secondSid, platform.Security[first.RootPath].Rules.Select(rule => rule.Sid));
        Assert.DoesNotContain(firstSid, platform.Security[second.RootPath].Rules.Select(rule => rule.Sid));
    }

    [Fact]
    public void RetainsFailedPostCreateAttestationForManualRecovery()
    {
        var platform = new FakeWindowsUserStagePlatform { CorruptStageAclAfterCreate = true };
        var operationId = Guid.NewGuid().ToString("N");

        var error = Assert.Throws<UserStateStageNeedsManualRecoveryException>(() =>
            new WindowsProtectedUserStateStageFactory(platform)
                .CreateForCurrentUser(CurrentSid, operationId));

        Assert.EndsWith(operationId, error.StageRoot, StringComparison.Ordinal);
        Assert.Contains(error.StageRoot, platform.CreatedPaths);
        Assert.True(platform.DirectoryExists(error.StageRoot));
        Assert.IsType<UnauthorizedAccessException>(error.InnerException);
    }

    [Fact]
    public async Task HelperDoesNotUseItsLocalApplicationDataVolumeForInitiatingUserStageCreation()
    {
        var platform = new FakeWindowsUserStagePlatform();
        platform.VolumeOverrides[Path.Combine(platform.LocalApplicationData, "Platform", "Connector")] = "volume-d";
        var operationId = Guid.NewGuid().ToString("N");

        _ = new WindowsProtectedUserStateStageFactory(platform).CreateForInitiatingUser(CurrentSid, operationId);

        Assert.Equal(0, platform.LocalApplicationDataReadCount);
        platform.LocalApplicationData = @"C:\Users\initiating\AppData\Local";
        platform.VolumeOverrides[Path.Combine(platform.LocalApplicationData, "Platform", "Connector")] = "volume-d";
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new WindowsProtectedUserStateStageFactory(platform)
                .OpenExistingForCurrentUser(CurrentSid, operationId)
                .AssertProtectedForUserAsync(CurrentSid, CancellationToken.None));
    }

    [Fact]
    public void UnsafeSharedAncestorIsRejectedBeforeAnyFilesystemMutation()
    {
        var platform = new FakeWindowsUserStagePlatform();
        var installerRoot = Path.Combine(platform.ProgramData, "StructuraConnectorInstaller");
        var moduleRoot = Path.Combine(installerRoot, "UserState");
        platform.Directories.Add(installerRoot);
        platform.Directories.Add(moduleRoot);
        platform.Security[installerRoot] = WindowsUserStageSecurity.CreateSharedAcl();
        var safeAcl = WindowsUserStageSecurity.CreateSharedAcl();
        platform.Security[moduleRoot] = safeAcl with
        {
            Rules = safeAcl.Rules.Append(new WindowsUserStageAccessRule("S-1-1-0",
                FileSystemRights.ReadAndExecute, AccessControlType.Allow, IsInherited: false,
                InheritanceFlags.None, PropagationFlags.None)).ToArray()
        };

        Assert.Throws<UnauthorizedAccessException>(() =>
            new WindowsProtectedUserStateStageFactory(platform)
                .CreateForInitiatingUser(CurrentSid, Guid.NewGuid().ToString("N")));

        Assert.Empty(platform.CreatedPaths);
        Assert.Equal(0, platform.ProtectCount);
        Assert.DoesNotContain("S-1-1-0", platform.Security[installerRoot].Rules.Select(rule => rule.Sid));
    }

    [Fact]
    public void SafelyMigratesLegacySidReadAclOnlyAfterPreflightAndReplacesItWithBuiltinUsers()
    {
        var platform = new FakeWindowsUserStagePlatform();
        var installerRoot = Path.Combine(platform.ProgramData, "StructuraConnectorInstaller");
        platform.Directories.Add(installerRoot);
        var current = WindowsUserStageSecurity.CreateSharedAcl();
        platform.Security[installerRoot] = current with
        {
            Rules = current.Rules.Where(rule => rule.Sid != WindowsUserStageSecurity.BuiltinUsersSid)
                .Append(new WindowsUserStageAccessRule(CurrentSid, FileSystemRights.ReadAndExecute,
                    AccessControlType.Allow, IsInherited: false, InheritanceFlags.None, PropagationFlags.None))
                .ToArray()
        };

        _ = new WindowsProtectedUserStateStageFactory(platform)
            .CreateForInitiatingUser(CurrentSid, Guid.NewGuid().ToString("N"));

        Assert.Equal(1, platform.ProtectCount);
        Assert.DoesNotContain(CurrentSid, platform.Security[installerRoot].Rules.Select(rule => rule.Sid));
        Assert.Equal(FileSystemRights.ReadAndExecute,
            platform.Security[installerRoot].Rules.Single(rule => rule.Sid == WindowsUserStageSecurity.BuiltinUsersSid).Rights);
        WindowsUserStageSecurity.ValidateSharedDirectory(installerRoot, platform.Security[installerRoot]);
    }

    [Fact]
    public void RepeatedStageCreationReopensTheSameEmptySlotWithoutMutation()
    {
        var platform = new FakeWindowsUserStagePlatform();
        var operationId = Guid.NewGuid().ToString("N");
        var first = new WindowsProtectedUserStateStageFactory(platform).CreateForInitiatingUser(CurrentSid, operationId);
        var createdPaths = platform.CreatedPaths.ToArray();
        var protectCount = platform.ProtectCount;

        var repeated = new WindowsProtectedUserStateStageFactory(platform).CreateForInitiatingUser(CurrentSid, operationId);

        Assert.Equal(first.RootPath, repeated.RootPath);
        Assert.Equal(createdPaths, platform.CreatedPaths);
        Assert.Equal(protectCount, platform.ProtectCount);
    }

    [Fact]
    public void ExistingNonemptyStageFailsClosedWithoutReplacement()
    {
        var platform = new FakeWindowsUserStagePlatform();
        var operationId = Guid.NewGuid().ToString("N");
        var stage = new WindowsProtectedUserStateStageFactory(platform).CreateForInitiatingUser(CurrentSid, operationId);
        var payload = Path.Combine(stage.RootPath, "unexpected.bin");
        platform.Files.Add(payload);
        var createdPaths = platform.CreatedPaths.ToArray();
        var protectCount = platform.ProtectCount;

        var error = Assert.Throws<UserStateStageNeedsManualRecoveryException>(() =>
            new WindowsProtectedUserStateStageFactory(platform).CreateForInitiatingUser(CurrentSid, operationId));

        Assert.Equal(stage.RootPath, error.StageRoot);
        Assert.True(platform.FileExists(payload));
        Assert.Equal(createdPaths, platform.CreatedPaths);
        Assert.Equal(protectCount, platform.ProtectCount);
    }

    [Fact]
    public async Task AssertRechecksAclOnEveryCall()
    {
        var platform = new FakeWindowsUserStagePlatform();
        var stage = new WindowsProtectedUserStateStageFactory(platform)
            .CreateForCurrentUser(CurrentSid, Guid.NewGuid().ToString("N"));
        platform.Security[stage.RootPath] = platform.Security[stage.RootPath] with
        {
            Rules = platform.Security[stage.RootPath].Rules.Append(new WindowsUserStageAccessRule(
                "S-1-1-0",
                FileSystemRights.WriteData | FileSystemRights.Delete | FileSystemRights.ChangePermissions,
                AccessControlType.Allow,
                IsInherited: false,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None)).ToArray()
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await stage.AssertProtectedForUserAsync(CurrentSid, CancellationToken.None));
    }

    [Fact]
    public async Task AssertRejectsAnotherUserAndAChangedCurrentToken()
    {
        var platform = new FakeWindowsUserStagePlatform();
        var stage = new WindowsProtectedUserStateStageFactory(platform)
            .CreateForCurrentUser(CurrentSid, Guid.NewGuid().ToString("N"));
        const string anotherSid = "S-1-5-21-1000000001-1000000002-1000000003-1002";

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await stage.AssertProtectedForUserAsync(anotherSid, CancellationToken.None));
        platform.CurrentSid = anotherSid;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await stage.AssertProtectedForUserAsync(CurrentSid, CancellationToken.None));
    }

    [Fact]
    public async Task AssertRejectsReparseAncestorIntroducedAfterCreation()
    {
        var platform = new FakeWindowsUserStagePlatform();
        var stage = new WindowsProtectedUserStateStageFactory(platform)
            .CreateForCurrentUser(CurrentSid, Guid.NewGuid().ToString("N"));
        platform.ReparsePaths.Add(Path.GetDirectoryName(stage.RootPath)!);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await stage.AssertProtectedForUserAsync(CurrentSid, CancellationToken.None));
    }

    [Fact]
    public async Task AssertRejectsVolumeOrProgramDataChangesAfterCreation()
    {
        var platform = new FakeWindowsUserStagePlatform();
        var stage = new WindowsProtectedUserStateStageFactory(platform)
            .CreateForCurrentUser(CurrentSid, Guid.NewGuid().ToString("N"));
        platform.VolumeOverrides[stage.RootPath] = "volume-z";
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await stage.AssertProtectedForUserAsync(CurrentSid, CancellationToken.None));

        platform.VolumeOverrides.Clear();
        platform.ProgramData = @"C:\OtherProgramData";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await stage.AssertProtectedForUserAsync(CurrentSid, CancellationToken.None));
    }

    [Fact]
    public void ProductionAclReaderRejectsOrdinaryTempDirectoriesWithoutChangingThem()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var first = Directory.CreateTempSubdirectory("user-stage-acl-");
        var second = Directory.CreateTempSubdirectory("user-stage-acl-");
        try
        {
            var platform = new WindowsUserStagePlatform();
            var firstAcl = platform.ReadDirectorySecurity(first.FullName);
            var secondAcl = platform.ReadDirectorySecurity(second.FullName);

            Assert.Throws<UnauthorizedAccessException>(() =>
                WindowsUserStageSecurity.ValidateUserDirectory(first.FullName, CurrentSid, firstAcl));
            Assert.Throws<UnauthorizedAccessException>(() =>
                WindowsUserStageSecurity.ValidateMachineDirectory(second.FullName, secondAcl));
        }
        finally
        {
            first.Delete(recursive: true);
            second.Delete(recursive: true);
        }
    }

    private sealed class FakeWindowsUserStagePlatform : IWindowsUserStagePlatform
    {
        public FakeWindowsUserStagePlatform()
        {
            Directories.Add(ProgramData);
            Directories.Add(LocalApplicationData);
        }

        public string CurrentSid { get; set; } = WindowsProtectedUserStateStageTests.CurrentSid;
        public bool Elevated { get; set; } = true;
        public bool CorruptStageAclAfterCreate { get; set; }
        public string ProgramData { get; set; } = @"C:\ProgramData";
        public string LocalApplicationData { get; set; } = @"C:\Users\current\AppData\Local";
        public int LocalApplicationDataReadCount { get; private set; }
        public int ProtectCount { get; private set; }
        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ReparsePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, WindowsUserStageDirectorySecurity> Security { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> VolumeOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> CreatedPaths { get; } = [];

        public string GetCurrentUserSid() => CurrentSid;

        public void AssertElevatedAdministrator()
        {
            if (!Elevated)
                throw new UnauthorizedAccessException("Test token is not elevated.");
        }

        public string GetProgramDataPath() => ProgramData;

        public string GetLocalApplicationDataPath()
        {
            LocalApplicationDataReadCount++;
            return LocalApplicationData;
        }

        public string GetVolumeId(string path)
        {
            var fullPath = Path.GetFullPath(path);
            return VolumeOverrides
                .Where(pair => IsSameOrDescendant(fullPath, pair.Key))
                .OrderByDescending(pair => pair.Key.Length)
                .Select(pair => pair.Value)
                .FirstOrDefault() ?? "volume-c";
        }

        public bool DirectoryExists(string path) => Directories.Contains(Path.GetFullPath(path));

        public bool FileExists(string path) => Files.Contains(Path.GetFullPath(path));

        public void CreateDirectory(string path, WindowsUserStageDirectorySecurity security)
        {
            var fullPath = Path.GetFullPath(path);
            if (!Directories.Contains(Path.GetDirectoryName(fullPath)!))
                throw new DirectoryNotFoundException("Fake parent directory is missing.");
            if (!Directories.Add(fullPath) || Files.Contains(fullPath))
                throw new IOException("Fake path already exists.");
            Security[fullPath] = security;
            CreatedPaths.Add(fullPath);
            if (CorruptStageAclAfterCreate && CreatedPaths.Count == 4)
                Security[fullPath] = security with { IsDaclProtected = false };
        }

        public void ProtectSharedDirectory(string path)
        {
            ProtectCount++;
            var fullPath = Path.GetFullPath(path);
            Security[fullPath] = WindowsUserStageSecurity.CreateSharedAcl();
        }

        public WindowsUserStageDirectorySecurity ReadDirectorySecurity(string path) =>
            Security.TryGetValue(Path.GetFullPath(path), out var security)
                ? security
                : throw new UnauthorizedAccessException("Fake ACL is unavailable.");

        public bool IsDirectoryEmpty(string path)
        {
            var fullPath = Path.GetFullPath(path);
            return !Files.Any(file => IsSameOrDescendant(file, fullPath) && !string.Equals(file, fullPath, StringComparison.OrdinalIgnoreCase)) &&
                   !Directories.Any(directory => IsSameOrDescendant(directory, fullPath) && !string.Equals(directory, fullPath, StringComparison.OrdinalIgnoreCase));
        }

        public void AssertNoReparseAncestors(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (ReparsePaths.Any(item => IsSameOrDescendant(fullPath, item)))
                throw new InvalidDataException("Fake path contains a reparse-point ancestor.");
        }

        private static bool IsSameOrDescendant(string candidate, string root)
        {
            var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                   normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }
}
