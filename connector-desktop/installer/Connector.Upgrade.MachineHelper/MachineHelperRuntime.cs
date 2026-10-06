using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Connector.Upgrade.MachineCommandChannel;
using Connector.Upgrade.MachineDispatcher;
using Connector.Upgrade.MachineJournal;
using Connector.Upgrade.MutualMachineChannel;
using Connector.Upgrade.NetBirdMachine;
using Connector.Upgrade.NetBirdPackageStage;
using Connector.Upgrade.NetBirdWindowsState;
using Connector.Upgrade.ProtectedCallerImage;
using Connector.Upgrade.WindowsHost;
using Connector.Upgrade.HelperReleaseTrust;
using Connector.Upgrade.Velopack;
using Connector.Upgrade.VelopackPayload;
using Connector.Upgrade.WindowsUserStage;
using Connector.Upgrade.WindowsPayload;
using Connector.Upgrade.Core;
using System.Security;
using MutualChannel = Connector.Upgrade.MutualMachineChannel.MutualMachineChannel;

namespace Connector.Upgrade.MachineHelper;

internal interface IHelperCallerProcessOpener
{
    SafeProcessHandle Open(int processId);
}

internal interface IHelperPreSessionTrust
{
    void AssertTrustedCaller(SafeProcessHandle retainedCaller, SecurityIdentifier expectedSid);
}

internal interface IHelperCommandSessionHost
{
    ValueTask RunAsync(HelperArguments arguments, SafeProcessHandle retainedCaller,
        Func<MachineUpgradeDispatcher> createDispatcher, CancellationToken cancellationToken);
}

internal interface IHelperMachineComposition
{
    MachineUpgradeDispatcher CreateDispatcher(Guid operationId, SecurityIdentifier callerSid, SafeProcessHandle retainedCaller);
}

internal sealed class MachineHelperRuntime(
    IHelperCallerProcessOpener processOpener,
    IHelperPreSessionTrust preSessionTrust,
    IHelperCommandSessionHost sessionHost,
    IHelperMachineComposition composition)
{
    internal async ValueTask RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        var parsed = HelperArguments.Parse(args);
        using var caller = processOpener.Open(parsed.CallerPid);
        if (caller.IsInvalid || caller.IsClosed)
            throw new InvalidOperationException("The original caller process handle could not be retained.");

        // The elevated-token gate and protected image pin run before authentication and before
        // the composition can create the machine journal or any NetBird port.
        preSessionTrust.AssertTrustedCaller(caller, parsed.CallerSid);
        await sessionHost.RunAsync(parsed, caller,
            () => composition.CreateDispatcher(parsed.OperationId, parsed.CallerSid, caller), cancellationToken).ConfigureAwait(false);
        GC.KeepAlive(caller);
    }
}

internal sealed class WindowsHelperCallerProcessOpener : IHelperCallerProcessOpener
{
    internal static readonly WindowsHelperCallerProcessOpener Instance = new();
    private const uint Synchronize = 0x00100000;
    private const uint QueryLimitedInformation = 0x00001000;
    private WindowsHelperCallerProcessOpener() { }

    public SafeProcessHandle Open(int processId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The machine helper requires Windows.");
        var handle = OpenProcess(Synchronize | QueryLimitedInformation, false, processId);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Could not open and retain the original caller process.");
        }
        return handle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);
}

internal sealed class WindowsHelperPreSessionTrust : IHelperPreSessionTrust
{
    private readonly WindowsTrustedElevatedCallerGate _elevation = new();
    private readonly ProtectedCallerImagePinSource _callerPins = new();

    public void AssertTrustedCaller(SafeProcessHandle retainedCaller, SecurityIdentifier expectedSid)
    {
        _elevation.AssertTrustedElevatedCaller();
        _callerPins.AssertTrustedCaller(retainedCaller, expectedSid);
    }
}

internal sealed class MutualMachineHelperCommandSessionHost : IHelperCommandSessionHost
{
    private readonly ITrustedCallerImagePinSource _callerPins;

    internal MutualMachineHelperCommandSessionHost(ITrustedCallerImagePinSource callerPins) =>
        _callerPins = callerPins ?? throw new ArgumentNullException(nameof(callerPins));

    public async ValueTask RunAsync(HelperArguments arguments, SafeProcessHandle retainedCaller,
        Func<MachineUpgradeDispatcher> createDispatcher, CancellationToken cancellationToken)
    {
        var channel = new MutualChannel(_callerPins);
        using var session = await channel.RunHelperSessionAsync(arguments.OperationId, arguments.CallerSid,
            retainedCaller, MutualChannel.MaximumTimeout, cancellationToken).ConfigureAwait(false);
        using var dispatcher = createDispatcher();
        await MachineCommandServer.ServeAsync(session, dispatcher, MachineCommandClient.MaximumTimeout,
            cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class WindowsMachineHelperComposition : IHelperMachineComposition
{
    private readonly string _fixedMsiPath;
    private readonly string _helperDirectory;

    internal WindowsMachineHelperComposition(string? helperDirectory = null)
    {
        var directory = Path.GetFullPath(helperDirectory ?? AppContext.BaseDirectory);
        _helperDirectory = directory;
        var pin = OfficialNetBirdPackagePin.LoadEmbedded();
        _fixedMsiPath = Path.GetFullPath(Path.Combine(directory, pin.InstallerName));
        var prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!_fixedMsiPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The fixed NetBird MSI path escaped the helper directory.");
    }

    public MachineUpgradeDispatcher CreateDispatcher(Guid operationId, SecurityIdentifier callerSid, SafeProcessHandle retainedCaller)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The machine helper requires Windows.");
        if (!File.Exists(_fixedMsiPath))
            throw new FileNotFoundException("The fixed bundled NetBird MSI is missing; machine operations are unavailable.", _fixedMsiPath);

        var elevation = new WindowsTrustedElevatedCallerGate();
        var stager = WindowsVerifiedNetBirdPackageStagerFactory.Create();
        var state = new WindowsNetBirdMachineStatePort();
        var restorePoints = new WindowsNetBirdOwnedRestorePointStore();
        var msi = new WindowsNetBirdMsiMutationRunner();
        var service = new NetBirdMachineInstallService(elevation, stager, state, restorePoints, msi);
        var port = new WindowsHostNetBirdPort(service, state, _fixedMsiPath);
        // CreateDispatcher is called only after the mutual machine session authenticates. The
        // signed release pin is loaded lazily by StageVelopack so an unprovisioned key blocks
        // that operation without disabling unrelated machine commands.
        var setupStager = new TrustedHelperVelopackSetupStager(new HelperReleasePinSource(), callerSid.Value);
        var userStateStageFactory = new WindowsProtectedUserStateStageFactory();
        // This wrapper is inert until a Stage* request. In particular, dispatcher construction
        // for Inspect/Prepare never creates or prepares the rollback payload root.
        var rollbackPayloadStager = new TrustedHelperRollbackPayloadStager(
            operationId, callerSid, retainedCaller, _helperDirectory);
        var journal = new MachineUpgradeJournalStore();
        var adapter = new MachineUpgradeJournalAdapter(journal);
        try
        {
            return new MachineUpgradeDispatcher(operationId, callerSid.Value, adapter, port, setupStager,
                (sid, trustedOperationId) => _ = userStateStageFactory.CreateForInitiatingUser(sid, trustedOperationId),
                rollbackPayloadStager);
        }
        catch { adapter.Dispose(); throw; }
    }
}

/// <summary>
/// Fixed-input bridge from the authenticated original process token to operation-scoped rollback
/// staging. Construction is inert; token access and staging-root preparation happen only for Stage*.
/// </summary>
internal sealed class TrustedHelperRollbackPayloadStager : IWindowsOperationRollbackPayloadStager
{
    private const uint TokenQuery = 0x0008;
    private readonly Guid _operationId;
    private readonly SecurityIdentifier _callerSid;
    private readonly SafeProcessHandle _retainedCaller;
    private readonly string _helperDirectory;
    private readonly Func<Guid, WindowsIdentity, SecurityIdentifier, WindowsRollbackPayloadSources,
        IWindowsOperationRollbackPayloadStager> _createStager;

    internal TrustedHelperRollbackPayloadStager(
        Guid operationId,
        SecurityIdentifier callerSid,
        SafeProcessHandle retainedCaller,
        string helperDirectory,
        Func<Guid, WindowsIdentity, SecurityIdentifier, WindowsRollbackPayloadSources,
            IWindowsOperationRollbackPayloadStager>? createStager = null)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A trusted operation ID is required.", nameof(operationId));
        _operationId = operationId;
        _callerSid = callerSid ?? throw new ArgumentNullException(nameof(callerSid));
        _retainedCaller = retainedCaller ?? throw new ArgumentNullException(nameof(retainedCaller));
        _helperDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(helperDirectory ?? throw new ArgumentNullException(nameof(helperDirectory))));
        _createStager = createStager ?? ((id, identity, expectedSid, sources) =>
            new WindowsOperationRollbackPayloadStager(id, identity, expectedSid, sources));
    }

    public async ValueTask<IWindowsVerifiedRollbackPayloadLease> StageVerifiedRollbackPayloadAsync(
        LegacyApplicationKind kind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var upgradeLock = LegacyUpgradeLock.LoadEmbedded();
        var structura = ResolveLockedAsset(upgradeLock.Get(LegacyApplicationKind.StructuraConnector));
        var platform = ResolveLockedAsset(upgradeLock.Get(LegacyApplicationKind.PlatformConnector));
        var selected = kind switch
        {
            LegacyApplicationKind.StructuraConnector => structura,
            LegacyApplicationKind.PlatformConnector => platform,
            _ => throw new InvalidDataException("The requested legacy MSI kind is not supported."),
        };
        if (!File.Exists(selected))
            throw new WindowsRollbackPayloadNeedsManualRecoveryException(kind);
        var attributes = File.GetAttributes(selected);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new WindowsRollbackPayloadNeedsManualRecoveryException(kind);

        using var token = OpenAuthenticatedCallerToken(_retainedCaller);
        using var identity = new WindowsIdentity(token.DangerousGetHandle());
        if (token.IsInvalid || token.IsClosed || identity.User is null || !identity.User.Equals(_callerSid))
            throw new UnauthorizedAccessException("The retained caller token does not match the authenticated session SID.");

        var sources = new WindowsRollbackPayloadSources(structura, platform);
        var stager = _createStager(_operationId, identity, _callerSid, sources);
        return await stager.StageVerifiedRollbackPayloadAsync(kind, cancellationToken).ConfigureAwait(false);
    }

    private string ResolveLockedAsset(LegacyUpgradePin pin)
    {
        var name = pin.InstallerName;
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) ||
            name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, .. Path.GetInvalidFileNameChars()]) >= 0)
            throw new InvalidDataException("An embedded legacy MSI lock entry has an invalid fixed filename.");
        var path = Path.GetFullPath(Path.Combine(_helperDirectory, name));
        if (!string.Equals(Path.GetDirectoryName(path), _helperDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An embedded legacy MSI path escaped the pinned helper directory.");
        return path;
    }

    private static SafeAccessTokenHandle OpenAuthenticatedCallerToken(SafeProcessHandle process)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Rollback payload staging requires a Windows caller token.");
        if (process.IsInvalid || process.IsClosed)
            throw new UnauthorizedAccessException("The retained caller process handle is not live.");
        if (!OpenProcessToken(process, TokenQuery, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not query the retained authenticated caller token.");
        return token;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);
}

internal sealed class TrustedHelperVelopackSetupStager(
    HelperReleasePinSource pinSource,
    string initiatingSid) : IWindowsVelopackSetupStager
{
    private readonly HelperReleasePinSource _pinSource = pinSource ?? throw new ArgumentNullException(nameof(pinSource));
    private readonly string _initiatingSid = initiatingSid ?? throw new ArgumentNullException(nameof(initiatingSid));

    public ValueTask<IVerifiedVelopackSetupLease> StageAndVerifyAsync(CancellationToken cancellationToken = default) =>
        CreateTrustedStager().StageAndVerifyAsync(cancellationToken);

    public ValueTask<IVerifiedVelopackSetupLease> ReacquireByHandleAsync(string handleId,
        CancellationToken cancellationToken = default) =>
        CreateTrustedStager().ReacquireByHandleAsync(handleId, cancellationToken);

    public ValueTask<IVerifiedVelopackSetupLease> ReacquireAsync(ProtectedVelopackSetupReceipt receipt,
        CancellationToken cancellationToken = default) =>
        CreateTrustedStager().ReacquireAsync(receipt, cancellationToken);

    private IWindowsVelopackSetupStager CreateTrustedStager()
    {
        var pin = _pinSource.GetVelopackSetupPin();
        if (!string.Equals(pin.PackageId, UnifiedVelopackApplication.PackId, StringComparison.Ordinal))
            throw new SecurityException("Signed Velopack Setup package id does not match the Connector application.");
        var trustMode = pin.TrustMode switch
        {
            Connector.Upgrade.HelperLauncher.ReleaseArtifactTrustMode.Authenticode =>
                Connector.Upgrade.Velopack.VelopackSetupTrustMode.Authenticode,
            Connector.Upgrade.HelperLauncher.ReleaseArtifactTrustMode.SignedManifestHash =>
                Connector.Upgrade.Velopack.VelopackSetupTrustMode.SignedManifestHash,
            _ => throw new SecurityException("Unsupported signed release Setup trust mode."),
        };
        var localPin = new Connector.Upgrade.Velopack.VelopackSetupPin(
            pin.PackageId, pin.Version, pin.Size, pin.Sha256, trustMode, pin.SignerThumbprint);
        VelopackSetupSignaturePolicy? policy = trustMode == Connector.Upgrade.Velopack.VelopackSetupTrustMode.Authenticode
            ? new VelopackSetupSignaturePolicy(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pin.SignerThumbprint! })
            : null;
        return WindowsVelopackSetupStagerFactory.Create(new VelopackSetupSource(pin.AbsolutePath), localPin,
            policy, _initiatingSid);
    }
}
