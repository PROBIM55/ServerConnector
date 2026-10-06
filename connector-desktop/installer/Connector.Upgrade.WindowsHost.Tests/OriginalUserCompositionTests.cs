using System.Reflection;
using System.Security.Principal;
using Connector.Upgrade.Core;
using Connector.Upgrade.UserState;
using Connector.Upgrade.Velopack;
using Connector.Upgrade.VelopackPayload;
using Connector.Upgrade.WindowsHost;
using Connector.Upgrade.WindowsPayload;

namespace Connector.Upgrade.WindowsHost.Tests;

public sealed class OriginalUserCompositionTests
{
    [Fact]
    public void CreateForCurrentUser_FailsClosedAsPrototype()
    {
        Assert.Throws<NotSupportedException>(() => WindowsHostUpgradePorts.CreateForCurrentUser(
            null!, null!, null!, null!, null!, null!, null!, null!));
    }

    [Fact]
    public void CreateForOriginalUser_RejectsDifferentCurrentSidBeforeConstructingPorts()
    {
        if (!OperatingSystem.IsWindows()) return;
        var currentSid = WindowsIdentity.GetCurrent().User!.Value;
        var options = CreateOptions("S-1-5-21-not-current");

        Assert.NotEqual(currentSid, options.Session.InitiatingUserSid);
        Assert.Throws<UnauthorizedAccessException>(() => WindowsHostUpgradePorts.CreateForOriginalUser(
            options,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!));
    }

    [Fact]
    public void CreateForOriginalUser_UsesInjectedMachineAndUserPortsAndPerUserRunner()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var calls = new List<string>();
        var environment = new FakeEnvironment(sid, calls);
        var netBird = new FakeNetBirdPort(calls);
        var userState = new FakeUserStatePort(sid, calls);
        var stager = new FakeSetupStager();
        var root = Path.Combine(Path.GetTempPath(), "original-user-compose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var host = WindowsHostUpgradePorts.CreateForOriginalUser(
                CreateOptions(sid),
                environment,
                new FakeEnrollmentPort(calls),
                new FakeAccessPort(),
                netBird,
                stager,
                userState,
                new FakePayloadStore(LegacyUpgradeLock.LoadEmbedded(), root, calls));

            Assert.Same(netBird, GetField(host, "_netBird"));
            Assert.Same(userState, GetField(host, "_userState"));
            var unified = GetField(host, "_unified");
            Assert.Same(stager, GetField(unified, "_stager"));
            var service = GetField(unified, "_service");
            Assert.IsType<SystemVelopackProcessRunner>(GetField(service, "_runner"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CreateForOriginalUser_DefaultsToOperationScopedReadOnlyRollbackPort()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        var calls = new List<string>();
        var host = WindowsHostUpgradePorts.CreateForOriginalUser(
            CreateOptions(sid),
            new FakeEnvironment(sid, calls),
            new FakeEnrollmentPort(calls),
            new FakeAccessPort(),
            new FakeNetBirdPort(calls),
            new FakeSetupStager(),
            new FakeUserStatePort(sid, calls));

        Assert.IsAssignableFrom<IOriginalUserOperationRollbackPayloadOpener>(host);
        Assert.IsType<WindowsOriginalUserRollbackPayloadStore>(GetField(host, "_rollbackPayloads"));
    }

    [Fact]
    public async Task WindowsUserStatePort_PrecreatedCaptureOpensExistingStageAndFailsClosedWhenMissing()
    {
        var stages = new RecordingStageAccess { OpenError = new DirectoryNotFoundException("stage absent") };
        var port = new WindowsUserStatePort(null, stages, captureFromPrecreatedStage: true);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            port.CaptureAsync("S-1-5-21-1000", Guid.NewGuid().ToString("N"), CancellationToken.None).AsTask());

        Assert.Equal(1, stages.OpenCalls);
        Assert.Equal(0, stages.CreateCalls);
    }

    [Fact]
    public async Task WindowsUserStatePort_DefaultCaptureStillUsesCreateMode()
    {
        var stages = new RecordingStageAccess { CreateError = new IOException("test stop before capture") };
        var port = new WindowsUserStatePort(null, stages);

        await Assert.ThrowsAsync<IOException>(() =>
            port.CaptureAsync("S-1-5-21-1000", Guid.NewGuid().ToString("N"), CancellationToken.None).AsTask());

        Assert.Equal(1, stages.CreateCalls);
        Assert.Equal(0, stages.OpenCalls);
    }

    private static WindowsHostProductionOptions CreateOptions(string sid) => new(
        new WindowsHostSession(Guid.NewGuid().ToString("N"), sid),
        new WindowsRollbackPayloadSources("C:\\safe\\structura.msi", "C:\\safe\\platform.msi"),
        "C:\\safe\\netbird.msi",
        new VelopackSetupPin(UnifiedVelopackApplication.PackId, "1.0.0", 1, new string('A', 64)),
        TimeSpan.FromSeconds(15));

    private static object GetField(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private sealed class FakeSetupStager : IWindowsVelopackSetupStager
    {
        public ValueTask<IVerifiedVelopackSetupLease> StageAndVerifyAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The setup stager must not run during composition.");

        public ValueTask<IVerifiedVelopackSetupLease> ReacquireAsync(
            ProtectedVelopackSetupReceipt receipt,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The setup stager must not run during composition.");
    }

    private sealed class RecordingStageAccess : IWindowsHostUserStateStageAccess
    {
        public int CreateCalls { get; private set; }
        public int OpenCalls { get; private set; }
        public Exception? CreateError { get; init; }
        public Exception? OpenError { get; init; }

        public IProtectedUserStateStage CreateForCurrentUser(string initiatingUserSid, string operationId)
        {
            CreateCalls++;
            throw CreateError ?? new InvalidOperationException("Unexpected create call.");
        }

        public IProtectedUserStateStage OpenExistingForCurrentUser(string initiatingUserSid, string operationId)
        {
            OpenCalls++;
            throw OpenError ?? new InvalidOperationException("Unexpected open call.");
        }
    }
}
