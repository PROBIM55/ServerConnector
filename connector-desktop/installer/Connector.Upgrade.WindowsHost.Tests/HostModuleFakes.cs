using Connector.Upgrade.Core;
using Connector.Upgrade.NetBirdMachine;
using Connector.Upgrade.UserState;
using Connector.Upgrade.Velopack;
using Connector.Upgrade.WindowsHost;

namespace Connector.Upgrade.WindowsHost.Tests;

internal sealed class FakeUserStatePort(string sid, List<string> calls) : IWindowsHostUserStatePort
{
    public ValueTask<UserStateSnapshot> CaptureAsync(
        string initiatingUserSid,
        string operationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal(sid, initiatingUserSid);
        calls.Add("user-state:capture");
        return ValueTask.FromResult(new UserStateSnapshot(sid, operationId + ":snapshot", new string('A', 64), true));
    }

    public ValueTask<WindowsHostUserStateRecoveryStatus> InspectRecoveryAsync(
        UserStateSnapshot snapshot,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new WindowsHostUserStateRecoveryStatus(true, true, "user-state-proof"));

    public ValueTask RestoreAsync(UserStateSnapshot snapshot, CancellationToken cancellationToken)
    {
        calls.Add("user-state:restore");
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeNetBirdPort(List<string> calls) : IWindowsHostNetBirdPort
{
    private static readonly NetBirdOwnedState Owned = new(
        "netbird-install",
        "0.79.0",
        new string('A', 64),
        "NetBird");
    private static readonly NetBirdAssessment Assessment = new(NetBirdOwnership.OwnedByConnector, Owned.InstallationId, Owned);
    private static readonly ProtectedNetBirdPackageReceipt Package = new(
        "netbird-package",
        RollbackPayloadProtection.ProtectedMachineStaging,
        "netbird.msi",
        new NetBirdMsiPackageIdentity(Guid.NewGuid(), Guid.NewGuid(), "0.79.0", "NetBird", "NetBird"),
        1,
        new string('B', 64),
        "NetBird Inc.",
        "thumbprint");

    public ValueTask<NetBirdAssessment> InspectAsync(CancellationToken cancellationToken)
    {
        calls.Add("netbird:inspect");
        return ValueTask.FromResult(Assessment);
    }

    public ValueTask<NetBirdMutationPlan> PrepareAsync(NetBirdAssessment assessment, Guid operationId, CancellationToken cancellationToken)
    {
        calls.Add("netbird:prepare");
        return ValueTask.FromResult(new NetBirdMutationPlan(NetBirdChangeKind.NoChange, null, Assessment, Package, null));
    }

    public ValueTask<NetBirdMutationReceipt> ApplyAsync(NetBirdMutationPlan plan, CancellationToken cancellationToken)
    {
        calls.Add("netbird:apply");
        return ValueTask.FromResult<NetBirdMutationReceipt>(new NetBirdNoChangeReceipt(Owned));
    }

    public ValueTask<NetBirdInterruptedRecoveryResult> ReconcileAsync(NetBirdMutationPlan plan, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new NetBirdInterruptedRecoveryResult(NetBirdInterruptedRecoveryAction.NoMutationObserved, "netbird-proof"));

    public ValueTask RemoveInstalledThisRunAsync(NetBirdInstalledThisRunReceipt receipt, CancellationToken cancellationToken)
    {
        calls.Add("netbird:remove");
        return ValueTask.CompletedTask;
    }

    public ValueTask RestoreUpdatedThisRunAsync(NetBirdUpdatedThisRunReceipt receipt, CancellationToken cancellationToken)
    {
        calls.Add("netbird:restore");
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeUnifiedPort : IWindowsHostUnifiedApplicationPort
{
    private readonly UnifiedApplicationReceipt _receipt = new("unified-op");
    public List<string> Calls { get; set; } = [];
    public bool Ready { get; set; } = true;

    public ValueTask<UnifiedApplicationReceipt> InstallAsync(CancellationToken cancellationToken)
    {
        Calls.Add("velopack:install");
        return ValueTask.FromResult(_receipt);
    }

    public ValueTask RemoveAsync(UnifiedApplicationReceipt receipt, CancellationToken cancellationToken)
    {
        Assert.Equal(_receipt, receipt);
        Calls.Add("velopack:remove");
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> IsExactInstallReadyAsync(CancellationToken cancellationToken)
    {
        Calls.Add("velopack:verify");
        return ValueTask.FromResult(Ready);
    }

    public WindowsHostVelopackRecoveryMetadata? GetRecoveryMetadata(UnifiedApplicationReceipt receipt) => new(
        receipt.OperationId,
        new VelopackSetupPin(UnifiedVelopackApplication.PackId, "1.0.0", 1, new string('C', 64)),
        new VelopackInstalledIdentity(
            HostFixture.Sid,
            UnifiedVelopackApplication.PackId,
            "1.0.0",
            "C:\\Users\\test\\AppData\\Local\\Structura.Connector.Desktop",
            new string('D', 64),
            new string('E', 64),
            new string('F', 64),
            new string('A', 64),
            new VelopackRegistration(HostFixture.Sid, "key", "root", "publisher", "1.0.0", "u", "q")));
}

internal sealed class FakeEnrollmentPort(List<string> calls) : IPlatformEnrollmentPort
{
    public ValueTask<PlatformEnrollmentReceipt> EnrollAsync(OneTimePlatformToken token, CancellationToken cancellationToken)
    {
        calls.Add("enrollment:create");
        return ValueTask.FromResult(new PlatformEnrollmentReceipt("enrollment-1"));
    }

    public ValueTask RemoveAsync(PlatformEnrollmentReceipt receipt, CancellationToken cancellationToken)
    {
        calls.Add("enrollment:remove");
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeAccessPort : ILiveNewAccessVerificationPort
{
    public List<string> Calls { get; set; } = [];
    public bool Succeeds { get; set; } = true;

    public ValueTask<NewAccessVerification> VerifyAsync(PlatformEnrollmentReceipt receipt, CancellationToken cancellationToken)
    {
        Calls.Add("access:verify");
        return ValueTask.FromResult(new NewAccessVerification(Succeeds, Succeeds, Succeeds));
    }
}
