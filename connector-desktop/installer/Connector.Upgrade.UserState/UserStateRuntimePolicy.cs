namespace Connector.Upgrade.UserState;

internal interface IUserStatePathPolicy
{
    IReadOnlyList<UserStateRoot> GetCanonicalRoots();
}

internal sealed class CurrentUserLegacyStatePathPolicy : IUserStatePathPolicy
{
    public IReadOnlyList<UserStateRoot> GetCanonicalRoots() => LegacyUserStateRoots.ForCurrentUser();
}

internal enum UserStateApplyFaultPoint
{
    AfterJournalTemporaryFlushedBeforePublication,
    BeforeRootMutation,
    AfterTargetMovedToBackup,
    AfterSnapshotMovedToTarget
}

internal sealed record UserStateApplyFaultContext(
    string OperationId,
    string RootId,
    string TargetPath,
    string IncomingPath,
    string BackupPath);

internal interface IUserStateApplyFaultInjector
{
    void OnFaultPoint(UserStateApplyFaultPoint point, UserStateApplyFaultContext context);
}

internal sealed class NoUserStateApplyFaultInjector : IUserStateApplyFaultInjector
{
    public static NoUserStateApplyFaultInjector Instance { get; } = new();

    public void OnFaultPoint(UserStateApplyFaultPoint point, UserStateApplyFaultContext context) { }
}

internal sealed class UserStateInjectedProcessCrashException : Exception
{
    public UserStateInjectedProcessCrashException(string operationId)
        : base("Injected process interruption for recovery verification.")
    {
        OperationId = operationId;
    }

    public string OperationId { get; }
}
