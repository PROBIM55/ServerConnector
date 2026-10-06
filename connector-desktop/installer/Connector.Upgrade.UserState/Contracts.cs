using System.Security.Principal;

namespace Connector.Upgrade.UserState;

public static class LegacyUserStateRootIds
{
    public const string Structura = "structura-connector";
    public const string Platform = "platform-connector";
    public const string UnifiedPlatform = "unified-platform";
}

public sealed record UserStateRoot(string Id, string Path);

public sealed record UserStateSnapshotLimits(
    int MaxEntries,
    long MaxFileBytes,
    long MaxTotalBytes,
    int MaxDepth,
    int BufferBytes,
    long MaxManifestBytes)
{
    public static UserStateSnapshotLimits Default { get; } = new(
        MaxEntries: 200_000,
        MaxFileBytes: 8L * 1024 * 1024 * 1024,
        MaxTotalBytes: 16L * 1024 * 1024 * 1024,
        MaxDepth: 128,
        BufferBytes: 1024 * 1024,
        MaxManifestBytes: 128L * 1024 * 1024);

    internal void Validate()
    {
        if (MaxEntries is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(MaxEntries));
        if (MaxFileBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxFileBytes));
        if (MaxTotalBytes < MaxFileBytes) throw new ArgumentOutOfRangeException(nameof(MaxTotalBytes));
        if (MaxDepth is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(MaxDepth));
        if (BufferBytes is < 4096 or > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(BufferBytes));
        if (MaxManifestBytes is < 4096 or > 512L * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(MaxManifestBytes));
    }
}

public sealed record UserStateSnapshotRequest(
    string ExpectedUserSid,
    UserStateSnapshotLimits? Limits = null);

public sealed record UserStateSnapshotHandle(
    string SnapshotId,
    string OwnerSid,
    long ManifestBytes,
    string ManifestSha256,
    int FileCount,
    long PayloadBytes);

public sealed record UserStateTargetFingerprint(
    string OwnerSid,
    string FingerprintSha256,
    IReadOnlyList<UserStateRootFingerprint> Roots);

public sealed record UserStateRootFingerprint(
    string RootId,
    bool Exists,
    int EntryCount,
    long PayloadBytes,
    string FingerprintSha256);

public enum UserStateApplyPurpose
{
    HydrateFreshTarget,
    ExactRollback
}

public sealed record UserStateApplyRequest(
    string ExpectedUserSid,
    UserStateSnapshotHandle Snapshot,
    UserStateTargetFingerprint ExpectedTargets,
    UserStateApplyPurpose Purpose);

public sealed record UserStateApplyResult(
    string OperationId,
    UserStateApplyPurpose Purpose,
    int RestoredFiles,
    long RestoredBytes);

public sealed record UserStateRecoveryResult(
    string OperationId,
    int RestoredFiles,
    long RestoredBytes);

public enum UserStatePendingRecoveryStatus
{
    Recoverable = 1,
    RecoverableTemporaryJournal = 2,
    NeedsManualRecovery = 3
}

public sealed record UserStatePendingRecovery(
    string OperationId,
    UserStatePendingRecoveryStatus Status);

/// <summary>
/// The installer supplies a stage whose ACL and ownership it has already validated.
/// Implementations must fail unless the root is protected for <paramref name="expectedUserSid"/>,
/// Administrators and SYSTEM, and must prevent unrelated users from changing committed bytes.
/// </summary>
public interface IProtectedUserStateStage
{
    string RootPath { get; }

    ValueTask AssertProtectedForUserAsync(string expectedUserSid, CancellationToken cancellationToken);
}

internal interface IUserIdentityProvider
{
    string GetCurrentUserSid();
}

internal sealed class WindowsUserIdentityProvider : IUserIdentityProvider
{
    public string GetCurrentUserSid()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows user SID is required for legacy DPAPI state migration.");
        }

        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
            ?? throw new UserStateSnapshotException("The current Windows identity does not have a SID.");
    }
}

public class UserStateSnapshotException : Exception
{
    public UserStateSnapshotException(string message) : base(message) { }
    public UserStateSnapshotException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class UserStateChangedException : UserStateSnapshotException
{
    public UserStateChangedException(string message) : base(message) { }
}

public sealed class UserStateIntegrityException : UserStateSnapshotException
{
    public UserStateIntegrityException(string message) : base(message) { }
}
