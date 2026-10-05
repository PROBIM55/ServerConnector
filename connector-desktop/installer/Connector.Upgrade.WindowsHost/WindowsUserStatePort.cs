using System.Text.Json;
using Connector.Upgrade.Core;
using Connector.Upgrade.UserState;
using Connector.Upgrade.WindowsUserStage;

namespace Connector.Upgrade.WindowsHost;

/// <summary>
/// Bridges Core's compact snapshot identity to the complete typed user-state recovery evidence kept
/// inside the ACL-protected stage. The snapshot covers both legacy roots and the unified Platform
/// target, so compensation also removes a newly imported Platform credential. The operation id is
/// encoded into Core's snapshot id so recovery can reopen the exact stage after process restart.
/// </summary>
public sealed class WindowsUserStatePort : IWindowsHostUserStatePort
{
    private const string MetadataFileName = "windows-host-recovery.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly UserStateSnapshotService _service;
    private readonly IWindowsHostUserStateStageAccess _stageAccess;
    private readonly bool _captureFromPrecreatedStage;

    public WindowsUserStatePort(
        UserStateSnapshotService? service = null,
        WindowsProtectedUserStateStageFactory? stageFactory = null,
        bool captureFromPrecreatedStage = false)
    {
        _service = service ?? new UserStateSnapshotService();
        _stageAccess = new WindowsHostUserStateStageAccess(stageFactory ?? new WindowsProtectedUserStateStageFactory());
        _captureFromPrecreatedStage = captureFromPrecreatedStage;
    }

    public WindowsUserStatePort(
        UserStateSnapshotService? service,
        IWindowsHostUserStateStageAccess stageAccess,
        bool captureFromPrecreatedStage = false)
    {
        _service = service ?? new UserStateSnapshotService();
        _stageAccess = stageAccess ?? throw new ArgumentNullException(nameof(stageAccess));
        _captureFromPrecreatedStage = captureFromPrecreatedStage;
    }

    public async ValueTask<UserStateSnapshot> CaptureAsync(
        string initiatingUserSid,
        string operationId,
        CancellationToken cancellationToken)
    {
        var stage = _captureFromPrecreatedStage
            ? _stageAccess.OpenExistingForCurrentUser(initiatingUserSid, operationId)
            : _stageAccess.CreateForCurrentUser(initiatingUserSid, operationId);
        var originalTargets = await _service
            .InspectTargetsAsync(initiatingUserSid, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var handle = await _service
            .CreateSnapshotAsync(new UserStateSnapshotRequest(initiatingUserSid), stage, cancellationToken)
            .ConfigureAwait(false);
        var metadata = new WindowsHostUserStateRecoveryMetadata(
            WindowsHostUserStateRecoveryMetadata.CurrentSchemaVersion,
            operationId,
            initiatingUserSid,
            handle,
            originalTargets);
        await WriteMetadataAsync(stage.RootPath, metadata, cancellationToken).ConfigureAwait(false);
        return new UserStateSnapshot(
            initiatingUserSid,
            FormatCoreSnapshotId(operationId, handle.SnapshotId),
            handle.ManifestSha256,
            Verified: true);
    }

    public async ValueTask<WindowsHostUserStateRecoveryStatus> InspectRecoveryAsync(
        UserStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            var (metadata, stage) = await LoadAsync(snapshot, cancellationToken).ConfigureAwait(false);
            var current = await _service
                .InspectTargetsAsync(metadata.OwnerSid, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new WindowsHostUserStateRecoveryStatus(
                CanRestore: true,
                MatchesSnapshot: FingerprintsEqual(current, metadata.OriginalTargets),
                EvidenceId: current.FingerprintSha256);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                      InvalidDataException or JsonException or UserStateSnapshotException)
        {
            return new WindowsHostUserStateRecoveryStatus(false, false, error.GetType().Name);
        }
    }

    public async ValueTask RestoreAsync(UserStateSnapshot snapshot, CancellationToken cancellationToken)
    {
        var (metadata, stage) = await LoadAsync(snapshot, cancellationToken).ConfigureAwait(false);
        var before = await _service
            .InspectTargetsAsync(metadata.OwnerSid, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await _service.ApplySnapshotAsync(
                new UserStateApplyRequest(
                    metadata.OwnerSid,
                    metadata.Snapshot,
                    before,
                    UserStateApplyPurpose.ExactRollback),
                stage,
                cancellationToken)
            .ConfigureAwait(false);
        var after = await _service
            .InspectTargetsAsync(metadata.OwnerSid, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!FingerprintsEqual(after, metadata.OriginalTargets))
            throw new WindowsHostManualRecoveryRequiredException(
                "User-state rollback completed without reproducing the original typed fingerprint.");
    }

    private async ValueTask<(WindowsHostUserStateRecoveryMetadata Metadata, IProtectedUserStateStage Stage)> LoadAsync(
        UserStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var (operationId, snapshotId) = ParseCoreSnapshotId(snapshot.SnapshotId);
        var stage = _stageAccess.OpenExistingForCurrentUser(snapshot.WindowsUserId, operationId);
        var path = Path.Combine(stage.RootPath, MetadataFileName);
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var metadata = await JsonSerializer.DeserializeAsync<WindowsHostUserStateRecoveryMetadata>(
                stream,
                JsonOptions,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("Windows host recovery metadata is empty.");
        if (metadata.Snapshot is null || metadata.OriginalTargets is null ||
            metadata.OriginalTargets.Roots is null ||
            metadata.OriginalTargets.Roots.Any(root => root is null || string.IsNullOrWhiteSpace(root.RootId)) ||
            string.IsNullOrWhiteSpace(metadata.OriginalTargets.FingerprintSha256) ||
            !string.Equals(metadata.OriginalTargets.OwnerSid, metadata.OwnerSid, StringComparison.OrdinalIgnoreCase) ||
            metadata.SchemaVersion != WindowsHostUserStateRecoveryMetadata.CurrentSchemaVersion ||
            !string.Equals(metadata.OperationId, operationId, StringComparison.Ordinal) ||
            !string.Equals(metadata.OwnerSid, snapshot.WindowsUserId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(metadata.Snapshot.SnapshotId, snapshotId, StringComparison.Ordinal) ||
            !string.Equals(metadata.Snapshot.ManifestSha256, snapshot.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !metadata.Snapshot.OwnerSid.Equals(metadata.OwnerSid, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Windows host recovery metadata does not match the Core snapshot identity.");
        }
        return (metadata, stage);
    }

    private static async ValueTask WriteMetadataAsync(
        string stageRoot,
        WindowsHostUserStateRecoveryMetadata metadata,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(stageRoot, MetadataFileName);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, metadata, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string FormatCoreSnapshotId(string operationId, string snapshotId) =>
        operationId + ":" + snapshotId;

    private static bool FingerprintsEqual(
        UserStateTargetFingerprint left,
        UserStateTargetFingerprint right) =>
        string.Equals(left.OwnerSid, right.OwnerSid, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.FingerprintSha256, right.FingerprintSha256, StringComparison.OrdinalIgnoreCase) &&
        left.Roots.Count == right.Roots.Count &&
        left.Roots.OrderBy(root => root.RootId, StringComparer.Ordinal)
            .SequenceEqual(right.Roots.OrderBy(root => root.RootId, StringComparer.Ordinal));

    private static (string OperationId, string SnapshotId) ParseCoreSnapshotId(string value)
    {
        var parts = (value ?? string.Empty).Split(':');
        if (parts.Length != 2 ||
            !Guid.TryParseExact(parts[0], "N", out var operationId) ||
            !Guid.TryParseExact(parts[1], "N", out var snapshotId))
        {
            throw new InvalidDataException("The Core user-state snapshot id has no exact Windows stage identity.");
        }
        return (operationId.ToString("N"), snapshotId.ToString("N"));
    }
}

/// <summary>Access to user-owned snapshot stages; pre-created mode only opens existing stages.</summary>
public interface IWindowsHostUserStateStageAccess
{
    IProtectedUserStateStage CreateForCurrentUser(string initiatingUserSid, string operationId);

    IProtectedUserStateStage OpenExistingForCurrentUser(string initiatingUserSid, string operationId);
}

internal sealed class WindowsHostUserStateStageAccess(WindowsProtectedUserStateStageFactory factory)
    : IWindowsHostUserStateStageAccess
{
    private readonly WindowsProtectedUserStateStageFactory _factory = factory;

    public IProtectedUserStateStage CreateForCurrentUser(string initiatingUserSid, string operationId) =>
        _factory.CreateForCurrentUser(initiatingUserSid, operationId);

    public IProtectedUserStateStage OpenExistingForCurrentUser(string initiatingUserSid, string operationId) =>
        _factory.OpenExistingForCurrentUser(initiatingUserSid, operationId);
}
