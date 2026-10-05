using System.Buffers.Binary;
using System.Text.Json;
using Connector.Upgrade.Core;
using Connector.Upgrade.MachineIpc;
using Connector.Upgrade.MachineJournal;

namespace Connector.Upgrade.MachineDispatcher;

/// <summary>Strict bounded framing for machine-dispatcher responses. No journal identity or paths cross this boundary.</summary>
public static class MachineDispatcherResultFrameCodec
{
    public const int MaximumFrameLength = 64 * 1024;
    public const int HeaderLength = sizeof(int);
    public const int CurrentVersion = 1;

    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "version", "correlationId", "operation", "status", "code", "state", "phase", "revision", "netBirdOwnership", "netBirdChange",
        "reconciliationAction", "reconciliationEvidenceId", "rebootRequired"
    };
    private static readonly HashSet<string> OptionalFields = new(StringComparer.Ordinal)
    {
        "reconciliationAction", "reconciliationEvidenceId", "rebootRequired"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static byte[] Encode(MachineDispatcherResult result)
    {
        Validate(result);
        var payload = JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions);
        if (payload.Length is 0 or > MaximumFrameLength)
            throw new InvalidDataException("The dispatcher result frame length is outside the allowed range.");
        var frame = new byte[HeaderLength + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, HeaderLength), payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    public static async ValueTask WriteAsync(Stream stream, MachineDispatcherResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite) throw new ArgumentException("Stream must be writable.", nameof(stream));
        await stream.WriteAsync(Encode(result), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns null only for EOF before any byte of a new frame.</summary>
    public static async ValueTask<MachineDispatcherResult?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
        var header = new byte[HeaderLength];
        if (!await ReadExactlyAsync(stream, header, true, cancellationToken).ConfigureAwait(false)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumFrameLength) throw new InvalidDataException("The dispatcher result frame length is outside the allowed range.");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, false, cancellationToken).ConfigureAwait(false);
        return DecodePayload(payload);
    }

    private static async ValueTask<bool> ReadExactlyAsync(Stream stream, Memory<byte> destination, bool allowCleanEof, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (allowCleanEof && offset == 0) return false;
                throw new EndOfStreamException("The dispatcher result frame ended before its declared length was read.");
            }
            offset += read;
        }
        return true;
    }

    private static MachineDispatcherResult DecodePayload(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The dispatcher result must be a JSON object.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new InvalidDataException("The dispatcher result contains a duplicate field.");
                if (!Fields.Contains(property.Name)) throw new InvalidDataException("The dispatcher result contains an unsupported field.");
                if (property.Name is "operation" or "status" or "code" or "state" or "phase" or "netBirdOwnership" or "netBirdChange" or "reconciliationAction")
                    if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) ||
                        property.Value.ValueKind == JsonValueKind.String && !EnumNameExists(property.Name, property.Value.GetString()!))
                        throw new InvalidDataException("The dispatcher result contains an invalid enum value.");
                if (property.Name == "rebootRequired" && property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("The dispatcher result contains an invalid reboot flag.");
            }
            var requiredFields = Fields.Except(OptionalFields).ToHashSet(StringComparer.Ordinal);
            if (!requiredFields.IsSubsetOf(seen) || !seen.IsSubsetOf(Fields))
                throw new InvalidDataException("The dispatcher result is missing a required field.");
            var result = JsonSerializer.Deserialize<MachineDispatcherResult>(payload.Span, JsonOptions)
                ?? throw new InvalidDataException("The dispatcher result is empty.");
            Validate(result);
            return result;
        }
        catch (JsonException exception) { throw new InvalidDataException("The dispatcher result is malformed.", exception); }
    }

    private static bool EnumNameExists(string field, string value) => field switch
    {
        "operation" => Enum.GetNames<MachineIpcOperation>().Any(n => string.Equals(JsonNamingPolicy.CamelCase.ConvertName(n), value, StringComparison.Ordinal)),
        "status" => Enum.GetNames<MachineDispatcherStatus>().Any(n => string.Equals(JsonNamingPolicy.CamelCase.ConvertName(n), value, StringComparison.Ordinal)),
        "code" => Enum.GetNames<MachineDispatcherCode>().Any(n => string.Equals(JsonNamingPolicy.CamelCase.ConvertName(n), value, StringComparison.Ordinal)),
        "state" => Enum.GetNames<MachineUpgradeState>().Any(n => string.Equals(JsonNamingPolicy.CamelCase.ConvertName(n), value, StringComparison.Ordinal)),
        "phase" => Enum.GetNames<MachineUpgradePhase>().Any(n => string.Equals(JsonNamingPolicy.CamelCase.ConvertName(n), value, StringComparison.Ordinal)),
        "netBirdOwnership" => Enum.GetNames<NetBirdOwnership>().Any(n => string.Equals(JsonNamingPolicy.CamelCase.ConvertName(n), value, StringComparison.Ordinal)),
        "netBirdChange" => Enum.GetNames<NetBirdChangeKind>().Any(n => string.Equals(JsonNamingPolicy.CamelCase.ConvertName(n), value, StringComparison.Ordinal)),
        "reconciliationAction" => Enum.GetNames<NetBirdInterruptedRecoveryAction>().Any(n => string.Equals(JsonNamingPolicy.CamelCase.ConvertName(n), value, StringComparison.Ordinal)),
        _ => false
    };

    private static void Validate(MachineDispatcherResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Version != CurrentVersion) throw new InvalidDataException("Unsupported dispatcher result version.");
        if (result.CorrelationId == Guid.Empty) throw new InvalidDataException("The dispatcher result correlation id must not be empty.");
        if (!Enum.IsDefined(result.Operation) || !Enum.IsDefined(result.Status) || !Enum.IsDefined(result.Code) ||
            result.State is { } state && !Enum.IsDefined(state) || result.Phase is { } phase && !Enum.IsDefined(phase) ||
            result.NetBirdOwnership is { } ownership && !Enum.IsDefined(ownership) || result.NetBirdChange is { } change && !Enum.IsDefined(change) ||
            result.ReconciliationAction is { } action && !Enum.IsDefined(action))
            throw new InvalidDataException("The dispatcher result contains an unsupported enum value.");
        var snapshotCount = (result.State.HasValue ? 1 : 0) + (result.Phase.HasValue ? 1 : 0) + (result.Revision.HasValue ? 1 : 0);
        if (snapshotCount is not (0 or 3) || result.Revision < 0) throw new InvalidDataException("The dispatcher result snapshot fields are inconsistent.");
        if (result.NetBirdChange.HasValue && !result.NetBirdOwnership.HasValue) throw new InvalidDataException("A NetBird change requires its ownership assessment.");
        if (result.Status == MachineDispatcherStatus.Completed && (result.Code != MachineDispatcherCode.None || snapshotCount != 3))
            throw new InvalidDataException("A completed result requires a snapshot and the none code.");
        if (result.RebootRequired && (result.Operation != MachineIpcOperation.Apply ||
            result.Status != MachineDispatcherStatus.Completed ||
            result.NetBirdChange is not (NetBirdChangeKind.InstalledThisRun or NetBirdChangeKind.UpdatedThisRun)))
            throw new InvalidDataException("The reboot-required receipt is not bound to a completed NetBird mutation.");
        if (result.Status == MachineDispatcherStatus.Cancelled && result.Code != MachineDispatcherCode.Cancelled ||
            result.Status == MachineDispatcherStatus.Blocked && result.Code is MachineDispatcherCode.None or MachineDispatcherCode.Cancelled)
            throw new InvalidDataException("The dispatcher result status and code are inconsistent.");
        var hasReconciliationAction = result.ReconciliationAction.HasValue;
        var hasReconciliationEvidence = result.ReconciliationEvidenceId is not null;
        if (hasReconciliationAction != hasReconciliationEvidence ||
            hasReconciliationAction && (result.Operation != MachineIpcOperation.Reconcile ||
                result.Status != MachineDispatcherStatus.Completed || !IsSafeEvidenceId(result.ReconciliationEvidenceId)))
            throw new InvalidDataException("The dispatcher reconciliation evidence is inconsistent or unsafe.");
        if (result.Operation == MachineIpcOperation.Reconcile && result.Status == MachineDispatcherStatus.Completed && !hasReconciliationAction)
            throw new InvalidDataException("A completed reconciliation result requires its typed action and evidence.");
        if (hasReconciliationAction &&
            (result.State is not (MachineUpgradeState.RollingBack or MachineUpgradeState.RolledBack) ||
             result.Phase is not (MachineUpgradePhase.Rollback or MachineUpgradePhase.RecoveryRollback)))
            throw new InvalidDataException("A completed reconciliation result must report durable rollback state.");
        if (result.Operation is not (MachineIpcOperation.Inspect or MachineIpcOperation.Prepare or
            MachineIpcOperation.Apply or MachineIpcOperation.Reconcile or MachineIpcOperation.Remove or
            MachineIpcOperation.Restore or MachineIpcOperation.StageVelopack or MachineIpcOperation.PrepareUserStateStage or
            MachineIpcOperation.StageStructuraRollback or MachineIpcOperation.StagePlatformRollback or
            MachineIpcOperation.Rollover) &&
            (result.Status != MachineDispatcherStatus.Blocked || result.Code is not (MachineDispatcherCode.UnsupportedOperation or MachineDispatcherCode.InvalidRequest)))
            throw new InvalidDataException("The dispatcher result operation and outcome are inconsistent.");
        if (result.Operation is MachineIpcOperation.Inspect or MachineIpcOperation.Prepare &&
            result.Status == MachineDispatcherStatus.Blocked && result.Code == MachineDispatcherCode.UnsupportedOperation)
            throw new InvalidDataException("The dispatcher result operation and outcome are inconsistent.");
    }

    private static bool IsSafeEvidenceId(string? value)
    {
        const string prefix = "netbird-windows-v1:";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256) return false;
        var parts = value.Split('|');
        return parts.Length is 1 or 2 && parts.All(part =>
            part.StartsWith(prefix, StringComparison.Ordinal) && part.Length == prefix.Length + 64 &&
            part.AsSpan(prefix.Length).ToString().All(Uri.IsHexDigit));
    }
}
