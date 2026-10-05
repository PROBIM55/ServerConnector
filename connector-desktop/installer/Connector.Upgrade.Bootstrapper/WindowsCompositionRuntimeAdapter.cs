using Connector.Upgrade.WindowsComposition;

namespace Connector.Upgrade.Bootstrapper;

/// <summary>
/// Adapter seam for the shared CommonAccess runtime factory. It accepts only the
/// production composition factory's typed preparation result; it does not load settings,
/// endpoints, keys, or credentials itself.
/// </summary>
public sealed class WindowsCompositionRuntimeAdapter(
    Func<CancellationToken, ValueTask<WindowsUpgradeCompositionPreparation>> prepare)
    : IWindowsUpgradeBootstrapperRuntimeFactory
{
    private readonly Func<CancellationToken, ValueTask<WindowsUpgradeCompositionPreparation>> _prepare =
        prepare ?? throw new ArgumentNullException(nameof(prepare));

    public async ValueTask<BootstrapperPreparation> PrepareAsync(BootstrapperAction action, CancellationToken cancellationToken)
    {
        var preparation = await _prepare(cancellationToken).ConfigureAwait(false);
        if (preparation.IsReady && preparation.Composition is { } composition)
            return new BootstrapperPreparation(new CompositionAdapter(composition), []);

        return new BootstrapperPreparation(null, preparation.Blockers
            .Select(blocker => new BootstrapperBlocker(blocker.Code.ToString()))
            .ToArray());
    }

    private sealed class CompositionAdapter(WindowsUpgradeComposition composition)
        : IOriginalUserUpgradeComposition
    {
        public ValueTask<Connector.Upgrade.Core.UpgradeExecutionResult> ExecuteAsync(
            Connector.Upgrade.Core.OneTimePlatformToken token, CancellationToken cancellationToken) =>
            composition.ExecuteAsync(token, cancellationToken);

        public ValueTask<Connector.Upgrade.Core.UpgradeExecutionResult> RecoverInterruptedAsync(
            CancellationToken cancellationToken) => composition.RecoverInterruptedAsync(cancellationToken);

        public void Dispose() => composition.Dispose();
    }
}
