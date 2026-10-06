using Connector.Upgrade.Core;

namespace Connector.Upgrade.WindowsMsi;

[Flags]
public enum WindowsMsiRegistrationContext
{
    UserManaged = 1,
    UserUnmanaged = 2,
    Machine = 4,
}

public sealed record WindowsMsiRegistration(
    Guid ProductCode,
    Guid UpgradeCode,
    string Version,
    WindowsMsiRegistrationContext Context,
    string? UserSid);

public sealed record WindowsMsiInventorySnapshot(
    Guid UpgradeCode,
    IReadOnlyList<Guid> RelatedProductCodes,
    IReadOnlyList<WindowsMsiRegistration> Registrations);

/// <summary>
/// Reads Windows Installer registration through MsiEnumProductsEx,
/// MsiEnumRelatedProducts and MsiGetProductInfoEx. It must not mutate MSI state.
/// </summary>
public interface IWindowsMsiInventory
{
    WindowsMsiInventorySnapshot Inspect(LegacyUpgradePin pin);
}

public enum ExactWindowsMsiPresence
{
    Absent = 0,
    ExactInstalled = 1,
}

public sealed record ExactWindowsMsiInspection(
    LegacyApplicationIdentity Identity,
    string Version,
    ExactWindowsMsiPresence Presence,
    WindowsMsiRegistration? Registration);

public sealed record LegacyProcessDrainProof(
    LegacyApplicationKind Kind,
    bool Drained,
    string EvidenceId);

public enum WindowsMsiProcessCompletion
{
    Exited = 0,
    TimedOut = 1,
    Cancelled = 2,
    StartFailed = 3,
}

public sealed record WindowsMsiProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout);

public sealed record WindowsMsiProcessResult(
    WindowsMsiProcessCompletion Completion,
    int? ExitCode = null,
    string? Failure = null);

public interface IWindowsMsiProcessRunner
{
    ValueTask<WindowsMsiProcessResult> RunAsync(
        WindowsMsiProcessRequest request,
        CancellationToken cancellationToken);
}

public enum WindowsMsiMutationDisposition
{
    Succeeded = 0,
    SucceededRebootRequired = 1,
    AlreadyInDesiredState = 2,
    RetryRequired = 3,
    ManualRecoveryRequired = 4,
}

public sealed record WindowsMsiMutationResult(
    LegacyApplicationIdentity Identity,
    WindowsMsiMutationDisposition Disposition,
    ExactWindowsMsiInspection? StatusProbe,
    WindowsMsiProcessResult Process,
    string Detail);

public sealed class WindowsMsiInvariantException : InvalidOperationException
{
    public WindowsMsiInvariantException(string message) : base(message)
    {
    }

    public WindowsMsiInvariantException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
