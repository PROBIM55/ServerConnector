using System.Security.AccessControl;
using System.Security.Principal;
using Connector.Upgrade.MachineAcl;
using Connector.Upgrade.VelopackPayload;
using Connector.Upgrade.WindowsPayload;
using Connector.Upgrade.WindowsUserStage;
using Xunit;

namespace Connector.Upgrade.WindowsPayload.Tests;

public sealed class SharedMachineAclContractTests
{
    private static readonly SecurityIdentifier FirstSid = new("S-1-5-21-101-202-303-1001");
    private static readonly SecurityIdentifier SecondSid = new("S-1-5-21-101-202-303-1002");

    [Fact]
    public void CanonicalSharedRootIsAcceptedByAllThreeAdaptersForEitherInitiatingSid()
    {
        var shared = WindowsSharedMachineDirectoryAcl.Create();

        WindowsSharedMachineDirectoryAcl.Validate(shared);
        WindowsMachineStagingSecurity.ValidateSharedDirectoryAcl(shared);
        WindowsVelopackSetupStager.ValidateProtectedAcl(shared, FirstSid, sharedDirectory: true);
        WindowsVelopackSetupStager.ValidateProtectedAcl(shared, SecondSid, sharedDirectory: true);

        var stageModel = new WindowsUserStageDirectorySecurity(
            shared.GetOwner(typeof(SecurityIdentifier))!.Value,
            shared.AreAccessRulesProtected,
            shared.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Select(rule => new WindowsUserStageAccessRule(
                    rule.IdentityReference.Value, rule.FileSystemRights, rule.AccessControlType, rule.IsInherited,
                    rule.InheritanceFlags, rule.PropagationFlags)).ToArray());
        WindowsUserStageSecurity.ValidateSharedDirectory("cross-module shared root", stageModel);

        var groupReaders = shared.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(rule => ((SecurityIdentifier)rule.IdentityReference).Value == WindowsSharedMachineDirectoryAcl.UsersSid.Value)
            .ToArray();
        Assert.Single(groupReaders);
        Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, groupReaders[0].FileSystemRights);
    }

    [Fact]
    public void MachineOnlyWindowsPayloadRootAndLegacySidRootAreBothSafeMigrationInputs()
    {
        foreach (var legacy in new[] { MachineOnlyLegacyRoot(), LegacySidRoot(FirstSid) })
        {
            WindowsSharedMachineDirectoryAcl.ValidateMigratable(legacy);
            Assert.Throws<UnauthorizedAccessException>(() => WindowsSharedMachineDirectoryAcl.Validate(legacy));
        }

        // Both module creation orders converge on the same shared ACL contract.
        var windowsPayloadFirst = WindowsSharedMachineDirectoryAcl.Create();
        WindowsVelopackSetupStager.ValidateProtectedAcl(windowsPayloadFirst, FirstSid, sharedDirectory: true);
        WindowsMachineStagingSecurity.ValidateSharedDirectoryAcl(windowsPayloadFirst);

        var userStageFirst = WindowsSharedMachineDirectoryAcl.Create();
        WindowsMachineStagingSecurity.ValidateSharedDirectoryAcl(userStageFirst);
        WindowsVelopackSetupStager.ValidateProtectedAcl(userStageFirst, SecondSid, sharedDirectory: true);
    }

    [Fact]
    public void PerSidStageAndSetupFileRemainIsolatedFromOtherUsers()
    {
        var userSlot = WindowsUserStageSecurity.CreateUserAcl(FirstSid.Value);
        WindowsUserStageSecurity.ValidateUserDirectory("user stage", FirstSid.Value, userSlot);
        Assert.Throws<UnauthorizedAccessException>(() =>
            WindowsUserStageSecurity.ValidateUserDirectory("user stage", SecondSid.Value, userSlot));

        var setupFile = PrivateFile(FirstSid);
        WindowsVelopackSetupStager.ValidateProtectedAcl(setupFile, FirstSid);
        Assert.Throws<UnauthorizedAccessException>(() =>
            WindowsVelopackSetupStager.ValidateProtectedAcl(setupFile, SecondSid));
        Assert.Throws<UnauthorizedAccessException>(() =>
            WindowsVelopackSetupStager.ValidateProtectedAcl(setupFile, SecondSid, sharedDirectory: true));

        var machinePayload = MachineOnlyFile();
        WindowsMachineStagingSecurity.ValidateProtectedAcl("private payload", machinePayload);
        machinePayload.AddAccessRule(new FileSystemAccessRule(WindowsSharedMachineDirectoryAcl.UsersSid,
            FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        Assert.Throws<UnauthorizedAccessException>(() =>
            WindowsMachineStagingSecurity.ValidateProtectedAcl("private payload", machinePayload));
    }

    [Fact]
    public void RollbackBatchAndMsiAclGrantOnlyInitiatingSidReadAccess()
    {
        var batch = WindowsMachineStagingSecurity.CreateRollbackBatchDirectorySecurity(FirstSid.Value);
        var payload = WindowsMachineStagingSecurity.CreateRollbackPayloadFileSecurity(FirstSid.Value);

        WindowsMachineStagingSecurity.ValidateRollbackBatchDirectoryAcl("rollback batch", batch, FirstSid.Value);
        WindowsMachineStagingSecurity.ValidateRollbackPayloadFileAcl("rollback MSI", payload, FirstSid.Value);
        Assert.Throws<UnauthorizedAccessException>(() =>
            WindowsMachineStagingSecurity.ValidateRollbackBatchDirectoryAcl("rollback batch", batch, SecondSid.Value));
        Assert.Throws<UnauthorizedAccessException>(() =>
            WindowsMachineStagingSecurity.ValidateRollbackPayloadFileAcl("rollback MSI", payload, SecondSid.Value));

        var batchUserRule = Assert.Single(batch.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>(),
            rule => ((SecurityIdentifier)rule.IdentityReference).Value == FirstSid.Value);
        Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, batchUserRule.FileSystemRights);
        Assert.Equal(InheritanceFlags.None, batchUserRule.InheritanceFlags);
        var fileUserRule = Assert.Single(payload.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>(),
            rule => ((SecurityIdentifier)rule.IdentityReference).Value == FirstSid.Value);
        Assert.Equal(FileSystemRights.Read | FileSystemRights.Synchronize, fileUserRule.FileSystemRights);
        Assert.Equal(InheritanceFlags.None, fileUserRule.InheritanceFlags);
    }

    [Fact]
    public void RollbackAclRejectsAnAdditionalUserAce()
    {
        var payload = WindowsMachineStagingSecurity.CreateRollbackPayloadFileSecurity(FirstSid.Value);
        payload.AddAccessRule(new FileSystemAccessRule(
            SecondSid,
            FileSystemRights.WriteData | FileSystemRights.Delete,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));

        Assert.Throws<UnauthorizedAccessException>(() =>
            WindowsMachineStagingSecurity.ValidateRollbackPayloadFileAcl("rollback MSI", payload, FirstSid.Value));
    }

    [Theory]
    [InlineData("S-1-5-32-545")]
    [InlineData("S-1-1-0")]
    [InlineData("S-1-5-11")]
    public void RollbackAclRejectsGroupAsInitiatingUser(string groupSid)
    {
        var batch = WindowsMachineStagingSecurity.CreateRollbackBatchDirectorySecurity(FirstSid.Value);
        var payload = WindowsMachineStagingSecurity.CreateRollbackPayloadFileSecurity(FirstSid.Value);

        Assert.Throws<ArgumentException>(() =>
            WindowsMachineStagingSecurity.CreateRollbackBatchDirectorySecurity(groupSid));
        Assert.Throws<ArgumentException>(() =>
            WindowsMachineStagingSecurity.CreateRollbackPayloadFileSecurity(groupSid));
        Assert.Throws<ArgumentException>(() =>
            WindowsMachineStagingSecurity.ValidateRollbackBatchDirectoryAcl("rollback batch", batch, groupSid));
        Assert.Throws<ArgumentException>(() =>
            WindowsMachineStagingSecurity.ValidateRollbackPayloadFileAcl("rollback MSI", payload, groupSid));
    }

    private static DirectorySecurity MachineOnlyLegacyRoot()
    {
        var security = NewProtectedAcl();
        AddFullControl(security, WindowsSharedMachineDirectoryAcl.TrustedInstallerSid);
        return security;
    }

    private static DirectorySecurity LegacySidRoot(SecurityIdentifier legacySid)
    {
        var security = NewProtectedAcl();
        security.AddAccessRule(new FileSystemAccessRule(legacySid, FileSystemRights.ReadAndExecute,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static FileSecurity PrivateFile(SecurityIdentifier ownerSid)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(WindowsSharedMachineDirectoryAcl.AdministratorsSid);
        AddFullControl(security, WindowsSharedMachineDirectoryAcl.SystemSid);
        AddFullControl(security, WindowsSharedMachineDirectoryAcl.AdministratorsSid);
        security.AddAccessRule(new FileSystemAccessRule(ownerSid, FileSystemRights.ReadAndExecute,
            AccessControlType.Allow));
        return security;
    }

    private static FileSecurity MachineOnlyFile()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(WindowsSharedMachineDirectoryAcl.AdministratorsSid);
        AddFullControl(security, WindowsSharedMachineDirectoryAcl.SystemSid);
        AddFullControl(security, WindowsSharedMachineDirectoryAcl.AdministratorsSid);
        AddFullControl(security, WindowsSharedMachineDirectoryAcl.TrustedInstallerSid);
        return security;
    }

    private static DirectorySecurity NewProtectedAcl()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(WindowsSharedMachineDirectoryAcl.AdministratorsSid);
        AddFullControl(security, WindowsSharedMachineDirectoryAcl.SystemSid);
        AddFullControl(security, WindowsSharedMachineDirectoryAcl.AdministratorsSid);
        return security;
    }

    private static void AddFullControl(FileSystemSecurity security, SecurityIdentifier sid) =>
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
}
