using Connector.Upgrade.Core;

namespace Connector.Upgrade.WindowsHost;

/// <summary>Provides a user-side ownership-only view of machine-managed NetBird state.</summary>
public sealed class ReadOnlyWindowsHostNetBirdPort(
    Func<CancellationToken, ValueTask<NetBirdOwnership>> inspectOwnership) : IWindowsHostNetBirdPort
{
    private readonly Func<CancellationToken, ValueTask<NetBirdOwnership>> _inspectOwnership =
        inspectOwnership ?? throw new ArgumentNullException(nameof(inspectOwnership));

    public async ValueTask<NetBirdAssessment> InspectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ownership = await _inspectOwnership(cancellationToken).ConfigureAwait(false);
        if (!Enum.IsDefined(ownership))
            throw new InvalidDataException("The machine returned an unsupported NetBird ownership value.");
        return new NetBirdAssessment(ownership, InstallationId: null, OwnedState: null);
    }

    public ValueTask<NetBirdMutationPlan> PrepareAsync(
        NetBirdAssessment assessment,
        Guid operationId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("The user-side NetBird port is read-only.");

    public ValueTask<NetBirdMutationReceipt> ApplyAsync(
        NetBirdMutationPlan plan,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("The user-side NetBird port is read-only.");

    public ValueTask<NetBirdInterruptedRecoveryResult> ReconcileAsync(
        NetBirdMutationPlan plan,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("The user-side NetBird port is read-only.");

    public ValueTask RemoveInstalledThisRunAsync(
        NetBirdInstalledThisRunReceipt receipt,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("The user-side NetBird port is read-only.");

    public ValueTask RestoreUpdatedThisRunAsync(
        NetBirdUpdatedThisRunReceipt receipt,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("The user-side NetBird port is read-only.");
}
