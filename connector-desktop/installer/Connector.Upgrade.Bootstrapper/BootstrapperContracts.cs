using Connector.Upgrade.Core;

namespace Connector.Upgrade.Bootstrapper;

public enum BootstrapperAction
{
    ExecuteUpgrade,
    RecoverInterrupted,
}

public sealed record BootstrapperBlocker(string Code);

public sealed record BootstrapperPreparation(
    IOriginalUserUpgradeComposition? Composition,
    IReadOnlyList<BootstrapperBlocker> Blockers,
    IDisposable? RunGuard = null)
{
    public bool IsReady => Composition is not null && Blockers.Count == 0;
}

/// <summary>Typed boundary for the shared, machine-trusted Connector runtime composition.</summary>
public interface IWindowsUpgradeBootstrapperRuntimeFactory
{
    ValueTask<BootstrapperPreparation> PrepareAsync(BootstrapperAction action, CancellationToken cancellationToken);
}

/// <summary>The one-use token is accepted only by an original-user composition.</summary>
public interface IOriginalUserUpgradeComposition : IDisposable
{
    ValueTask<UpgradeExecutionResult> ExecuteAsync(OneTimePlatformToken token, CancellationToken cancellationToken);
    ValueTask<UpgradeExecutionResult> RecoverInterruptedAsync(CancellationToken cancellationToken);
}

public interface IPlatformTokenPrompt
{
    ValueTask<string?> RequestTokenAsync(CancellationToken cancellationToken);
}

public interface IBootstrapperStatusSink
{
    void Show(string message);
}
