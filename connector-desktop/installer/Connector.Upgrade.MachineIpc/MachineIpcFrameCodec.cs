using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Connector.Upgrade.MachineIpc;

public enum MachineIpcOperation
{
    Inspect,
    Prepare,
    Apply,
    Reconcile,
    Remove,
    Restore,
    StageVelopack,
    PrepareUserStateStage,
    StageStructuraRollback,
    StagePlatformRollback,
    Rollover
}

public sealed record MachineIpcRequest(
    int Version, Guid CorrelationId, MachineIpcOperation Operation,
    Guid? PreviousOperationId = null, Guid? NextOperationId = null);

/// <summary>Bounded framing and strict v1 request-envelope codec. It provides no transport or privilege boundary.</summary>
public static class MachineIpcFrameCodec
{
    public const int MaximumFrameLength = 64 * 1024;
    public const int HeaderLength = sizeof(int);
    public const int CurrentVersion = 1;

    private static readonly HashSet<string> OperationNames = new(StringComparer.Ordinal)
    {
        "inspect", "prepare", "apply", "reconcile", "remove", "restore", "stageVelopack", "prepareUserStateStage",
        "stageStructuraRollback", "stagePlatformRollback", "rollover"
    };

    private static readonly HashSet<string> EnvelopeFields = new(StringComparer.Ordinal)
    {
        "version", "correlationId", "operation", "previousOperationId", "nextOperationId"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static byte[] Encode(MachineIpcRequest request)
    {
        Validate(request);
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        if (payload.Length is 0 or > MaximumFrameLength)
            throw new InvalidDataException("The encoded IPC frame length is outside the allowed range.");

        var frame = new byte[HeaderLength + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, HeaderLength), payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    public static async ValueTask WriteAsync(Stream stream, MachineIpcRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite) throw new ArgumentException("Stream must be writable.", nameof(stream));
        var frame = Encode(request);
        await stream.WriteAsync(frame.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns null only when EOF occurs before any byte of a new frame.</summary>
    public static async ValueTask<MachineIpcRequest?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));

        var header = new byte[HeaderLength];
        var headerRead = await ReadExactlyAsync(stream, header, allowCleanEof: true, cancellationToken).ConfigureAwait(false);
        if (!headerRead) return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumFrameLength)
            throw new InvalidDataException("The IPC frame length is outside the allowed range.");

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, allowCleanEof: false, cancellationToken).ConfigureAwait(false);
        return DecodePayload(payload);
    }

    private static async ValueTask<bool> ReadExactlyAsync(Stream stream, Memory<byte> destination, bool allowCleanEof, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var count = await stream.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (allowCleanEof && offset == 0) return false;
                throw new EndOfStreamException("The IPC frame ended before its declared length was read.");
            }
            offset += count;
        }
        return true;
    }

    private static MachineIpcRequest DecodePayload(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The IPC envelope must be a JSON object.");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw new InvalidDataException("The IPC envelope contains a duplicate field.");
                if (!EnvelopeFields.Contains(property.Name))
                    throw new InvalidDataException("The IPC envelope contains an unsupported field.");
                if (property.Name == "operation" &&
                    (property.Value.ValueKind != JsonValueKind.String || !OperationNames.Contains(property.Value.GetString()!)))
                    throw new InvalidDataException("The IPC operation is not supported.");
            }

            var isRollover = document.RootElement.TryGetProperty("operation", out var operation) &&
                operation.ValueKind == JsonValueKind.String && operation.GetString() == "rollover";
            var expectedFields = isRollover ? EnvelopeFields : EnvelopeFields.Take(3).ToHashSet(StringComparer.Ordinal);
            if (!seen.SetEquals(expectedFields))
                throw new InvalidDataException("The IPC envelope is missing a required field.");

            var request = JsonSerializer.Deserialize<MachineIpcRequest>(payload.Span, JsonOptions)
                ?? throw new InvalidDataException("The IPC envelope is empty.");
            Validate(request);
            return request;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The IPC envelope is malformed or contains unsupported fields.", exception);
        }
    }

    private static void Validate(MachineIpcRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Version != CurrentVersion)
            throw new InvalidDataException($"Unsupported IPC envelope version {request.Version}.");
        if (request.CorrelationId == Guid.Empty)
            throw new InvalidDataException("The IPC correlation id must not be empty.");
        if (!Enum.IsDefined(request.Operation))
            throw new InvalidDataException("The IPC operation is not supported.");
        if (request.Operation == MachineIpcOperation.Rollover)
        {
            if (request.PreviousOperationId is not { } previous || previous == Guid.Empty ||
                request.NextOperationId is not { } next || next == Guid.Empty || previous == next)
                throw new InvalidDataException("Rollover requires distinct non-empty previous and next operation ids.");
        }
        else if (request.PreviousOperationId is not null || request.NextOperationId is not null)
            throw new InvalidDataException("Operation ids are supported only by the rollover command.");
    }
}
