using Connector.Upgrade.Core;
using Connector.Upgrade.NetBirdMachine;
using Connector.Upgrade.UserState;
using Connector.Upgrade.Velopack;
using Connector.Upgrade.WindowsHost;
using Connector.Upgrade.WindowsMsi;
using Connector.Upgrade.WindowsPayload;
using Microsoft.Win32.SafeHandles;

namespace Connector.Upgrade.WindowsHost.Tests;

internal sealed class HostFixture : IDisposable, IAsyncDisposable
{
    public const string Sid = "S-1-5-21-1000";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "windows-host-tests-" + Guid.NewGuid().ToString("N"));
    private WindowsHostUpgradePorts? _host;

    public HostFixture(bool createHost = true)
    {
        Directory.CreateDirectory(_root);
        Lock = LegacyUpgradeLock.LoadEmbedded();
        Inventory = new FakeInventory(Lock, Sid, Calls);
        Environment = new FakeEnvironment(Sid, Calls);
        if (createHost) _host = CreateHost();
    }

    public List<string> Calls { get; } = [];
    public LegacyUpgradeLock Lock { get; }
    public FakeInventory Inventory { get; }
    public FakeEnvironment Environment { get; }
    public WindowsHostSession Session { get; private set; } = null!;
    public bool UnifiedReady { get => Unified.Ready; set => Unified.Ready = value; }
    public bool AccessSucceeds { get => Access.Succeeds; set => Access.Succeeds = value; }
    public string JournalPath => Path.Combine(_root, "journal.json");
    public WindowsHostUpgradePorts Host => _host ?? throw new InvalidOperationException();
    private FakeUnifiedPort Unified { get; } = new();
    private FakeAccessPort Access { get; } = new();

    public WindowsHostUpgradePorts CreateHost()
    {
        Unified.Calls = Calls;
        Access.Calls = Calls;
        var runner = new FakeMsiRunner(Inventory, Lock, Calls);
        var msi = new WindowsMsiMutationAdapter(
            Inventory,
            runner,
            Sid,
            Path.Combine(_root, "msiexec.exe"),
            TimeSpan.FromSeconds(10),
            Lock,
            beforeRemoval: async (_, cancellationToken) =>
            {
                foreach (var kind in new[]
                         { LegacyApplicationKind.StructuraConnector, LegacyApplicationKind.PlatformConnector })
                    await Environment.DrainLegacyApplicationAsync(kind, cancellationToken);
            });
        Session = new WindowsHostSession(Guid.NewGuid().ToString("N"), Sid);
        _host = new WindowsHostUpgradePorts(
            Session,
            Environment,
            msi,
            new FakePayloadStore(Lock, _root, Calls),
            new FakeUserStatePort(Sid, Calls),
            new FakeNetBirdPort(Calls),
            Unified,
            new FakeEnrollmentPort(Calls),
            Access,
            Lock);
        return _host;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeEnvironment(string currentSid, List<string> calls) : IWindowsHostEnvironmentPort
{
    public string CurrentSid { get; set; } = currentSid;
    public int? RejectAtDrainCall { get; set; }
    private int _drainCalls;
    public string GetCurrentUserSid() => CurrentSid;

    public ValueTask<WindowsHostReadiness> InspectReadinessAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        calls.Add("readiness");
        return ValueTask.FromResult(new WindowsHostReadiness(true, true, "ready-1"));
    }

    public ValueTask<LegacyProcessDrainProof> DrainLegacyApplicationAsync(
        LegacyApplicationKind kind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        calls.Add("drain:" + kind);
        if (++_drainCalls == RejectAtDrainCall)
            throw new InvalidOperationException("A legacy application restarted before MSI mutation.");
        return ValueTask.FromResult(new LegacyProcessDrainProof(kind, true, "drain-" + kind));
    }
}

internal sealed class FakeInventory(
    LegacyUpgradeLock upgradeLock,
    string sid,
    List<string> calls) : IWindowsMsiInventory
{
    private readonly Dictionary<LegacyApplicationKind, bool> _present = upgradeLock.Pins
        .ToDictionary(pin => pin.Kind, _ => true);

    public bool IsPresent(LegacyApplicationKind kind) => _present[kind];
    public void SetPresent(LegacyApplicationKind kind, bool present) => _present[kind] = present;

    public WindowsMsiInventorySnapshot Inspect(LegacyUpgradePin pin)
    {
        var present = _present[pin.Kind];
        calls.Add($"msi:inspect:{pin.Kind}:{(present ? "present" : "absent")}");
        return new WindowsMsiInventorySnapshot(
            pin.Identity.UpgradeCode,
            present ? [pin.Identity.ProductCode] : [],
            present
                ? [new WindowsMsiRegistration(
                    pin.Identity.ProductCode,
                    pin.Identity.UpgradeCode,
                    pin.Version,
                    WindowsMsiRegistrationContext.UserUnmanaged,
                    sid)]
                : []);
    }
}

internal sealed class FakeMsiRunner(
    FakeInventory inventory,
    LegacyUpgradeLock upgradeLock,
    List<string> calls) : IWindowsMsiProcessRunner
{
    public ValueTask<WindowsMsiProcessResult> RunAsync(
        WindowsMsiProcessRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uninstall = request.Arguments[0] == "/x";
        var pin = uninstall
            ? upgradeLock.Pins.Single(value => request.Arguments[1].Contains(value.Identity.ProductCode.ToString("D"), StringComparison.OrdinalIgnoreCase))
            : upgradeLock.Pins.Single(value => string.Equals(
                Path.GetFileName(request.Arguments[1]),
                value.InstallerName,
                StringComparison.Ordinal));
        inventory.SetPresent(pin.Kind, !uninstall);
        calls.Add($"msi:{(uninstall ? "remove" : "restore")}:{pin.Kind}");
        return ValueTask.FromResult(new WindowsMsiProcessResult(WindowsMsiProcessCompletion.Exited, 0));
    }
}

internal sealed class FakePayloadStore : IWindowsRollbackPayloadStore
{
    private readonly LegacyUpgradeLock _lock;
    private readonly string _root;
    private readonly List<string> _calls;

    public FakePayloadStore(LegacyUpgradeLock upgradeLock, string root, List<string> calls)
    {
        _lock = upgradeLock;
        _root = root;
        _calls = calls;
    }

    public ValueTask<IVerifiedRollbackPayloadLease> AcquireVerifiedRollbackPayloadAsync(
        LegacyRollbackPayload payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _calls.Add("payload:acquire:" + payload.Identity.Kind);
        return ValueTask.FromResult<IVerifiedRollbackPayloadLease>(Create(payload.Identity.Kind));
    }

    public ValueTask<IVerifiedRollbackPayloadLease> ReacquireVerifiedRollbackPayloadAsync(
        ProtectedRollbackPayloadReceipt durableReceipt,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IVerifiedRollbackPayloadLease>(Create(durableReceipt.Kind));

    private FakeRollbackLease Create(LegacyApplicationKind kind)
    {
        var pin = _lock.Get(kind);
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, pin.InstallerName);
        File.WriteAllBytes(path, [1]);
        return new FakeRollbackLease(
            "lease-" + kind,
            path,
            new RollbackPayloadInspection(
                pin.Identity,
                pin.PackageId,
                pin.InstallerName,
                pin.Version,
                pin.SizeBytes,
                pin.Sha256,
                TrustedSource: true));
    }
}

internal sealed class FakeRollbackLease : IWindowsVerifiedRollbackPayloadLease
{
    private FileStream? _stream;

    public FakeRollbackLease(string handleId, string stagedPath, RollbackPayloadInspection inspection)
    {
        HandleId = handleId;
        StagedPath = stagedPath;
        Inspection = inspection;
        _stream = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public string HandleId { get; }
    public RollbackPayloadProtection Protection => RollbackPayloadProtection.ProtectedMachineStaging;
    public RollbackPayloadInspection Inspection { get; }
    public string StagedPath { get; }
    public SafeFileHandle ContentHandle => _stream?.SafeFileHandle ?? throw new ObjectDisposedException(nameof(FakeRollbackLease));
    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _stream = null;
        return ValueTask.CompletedTask;
    }
}
