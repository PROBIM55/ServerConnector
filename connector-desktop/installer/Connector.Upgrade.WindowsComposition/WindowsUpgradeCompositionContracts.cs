using Connector.Access.Client;
using Connector.Network;
using Connector.Upgrade.Core;
using Connector.Upgrade.PlatformAccess;
using Connector.Upgrade.VelopackPayload;
using Connector.Upgrade.WindowsHost;
using Connector.Upgrade.OriginalUserSession;
using Platform.Connector.Core;

namespace Connector.Upgrade.WindowsComposition;

public sealed record WindowsUpgradeCompositionOptions(
    WindowsHostProductionOptions Host,
    Uri PlatformProfileUri,
    Uri ExpectedNetBirdManagementUri,
    string DeviceDisplayName,
    // Retained for source compatibility. Production composition uses the signed release pin and
    // the original-user existing-slot opener; these caller-provided values are never trust inputs.
    VelopackSetupSource VelopackSetupSource,
    VelopackSetupSignaturePolicy VelopackSignaturePolicy,
    string? NetBirdPackageStagingRoot = null,
    string? NetBirdRestoreRoot = null,
    string? VelopackSetupStagingRoot = null);

/// <summary>
/// Already-created common-access runtime objects. Configuration and secrets stay owned by the
/// common runtime factory; the installer neither reparses nor duplicates them.
/// </summary>
public sealed record WindowsUpgradeRuntimeBindings(
    IConnectorEnrollmentClient CommonAccessClient,
    INetworkOverlayClient Overlay,
    IProtectedServiceNetworkGate ProtectedNetworkGate,
    ICommonConnectorRequestTransport RequestTransport,
    ManagedOverlayDestinationPolicy SmbDestinations);

public enum WindowsUpgradeCompositionBlockerCode
{
    ExactVpnPeerBindingUnavailable = 1,
    ExactDeviceSelfRevocationUnavailable = 2,
    CommonAccessCredentialContractIncomplete = 3,
    PlatformAccessSchemaUnavailable = 4,
    WindowsHostRequired = 5,
    MachineBoundaryNotReady = 6,
    SignedReleasePinUnavailable = 7,
}

public sealed record WindowsUpgradeCompositionBlocker(
    WindowsUpgradeCompositionBlockerCode Code,
    string EvidenceId);

public sealed class WindowsUpgradeCompositionPreparation
{
    private WindowsUpgradeCompositionPreparation(
        WindowsUpgradeComposition? composition,
        IReadOnlyList<WindowsUpgradeCompositionBlocker> blockers)
    {
        Composition = composition;
        Blockers = blockers;
    }

    public WindowsUpgradeComposition? Composition { get; }
    public IReadOnlyList<WindowsUpgradeCompositionBlocker> Blockers { get; }
    public bool IsReady => Composition is not null && Blockers.Count == 0;

    internal static WindowsUpgradeCompositionPreparation Ready(WindowsUpgradeComposition composition) =>
        new(composition ?? throw new ArgumentNullException(nameof(composition)), []);

    internal static WindowsUpgradeCompositionPreparation Blocked(
        params WindowsUpgradeCompositionBlocker[] blockers)
    {
        ArgumentNullException.ThrowIfNull(blockers);
        if (blockers.Length == 0)
            throw new ArgumentException("At least one production blocker is required.", nameof(blockers));
        return new WindowsUpgradeCompositionPreparation(null, Array.AsReadOnly(blockers));
    }
}

/// <summary>
/// The only object that accepts the one-use token. A blocked preparation never exposes one.
/// </summary>
public sealed class WindowsUpgradeComposition : IDisposable
{
    private readonly WindowsOriginalUserUpgradeSession _session;
    private readonly IDisposable[] _ownedResources;
    private int _disposed;

    internal WindowsUpgradeComposition(
        WindowsOriginalUserUpgradeSession session,
        params IDisposable[] ownedResources)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentNullException.ThrowIfNull(ownedResources);
        _ownedResources = ownedResources.ToArray();
        if (_ownedResources.Any(resource => resource is null))
            throw new ArgumentException("Composition resources must not contain null values.", nameof(ownedResources));
    }

    public ValueTask<UpgradeExecutionResult> ExecuteAsync(
        OneTimePlatformToken token,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _session.ExecuteAsync(token, cancellationToken);
    }

    public ValueTask<UpgradeExecutionResult> RecoverInterruptedAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _session.RecoverInterruptedAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _session.Dispose(); }
        finally
        {
            for (var index = _ownedResources.Length - 1; index >= 0; index--)
                _ownedResources[index].Dispose();
        }
    }
}
