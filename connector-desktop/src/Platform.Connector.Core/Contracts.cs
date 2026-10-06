using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Connector.Core;

public enum CadProvider
{
    AutoCad,
    Tekla
}

public enum JobOperation
{
    Build,
    Read,
    Apply,
    Rebuild,
    Export,
    Healthcheck
}

public enum JobStatus
{
    Queued,
    Picked,
    Running,
    Success,
    Error,
    Cancelled,
    Timeout,
    Interrupted
}

[JsonConverter(typeof(CamelCaseEnumJsonConverter<ConnectorProductId>))]
public enum ConnectorProductId
{
    Structura,
    Platform
}

[JsonConverter(typeof(CamelCaseEnumJsonConverter<ConnectorScopeKind>))]
public enum ConnectorScopeKind
{
    DeviceLocal,
    Project
}

public sealed class CamelCaseEnumJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String
            || !Enum.TryParse<TEnum>(reader.GetString(), ignoreCase: true, out var value)
            || !Enum.IsDefined(value))
        {
            throw new JsonException($"Invalid {typeof(TEnum).Name} value.");
        }
        return value;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value))
        {
            throw new JsonException($"Invalid {typeof(TEnum).Name} value.");
        }
        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
    }
}

/// <summary>
/// Immutable execution boundary captured when a job is admitted.
/// Device-local jobs never carry tenant/project identity; project jobs require both.
/// Server authorization remains a server responsibility and is not inferred here.
/// </summary>
public sealed record ExecutionScope
{
    [JsonConstructor]
    public ExecutionScope(
        ConnectorProductId productId,
        string? tenantId,
        ConnectorScopeKind scopeKind,
        string? projectId)
    {
        if (!Enum.IsDefined(productId))
        {
            throw new ArgumentOutOfRangeException(nameof(productId));
        }
        if (!Enum.IsDefined(scopeKind))
        {
            throw new ArgumentOutOfRangeException(nameof(scopeKind));
        }

        var normalizedTenantId = Normalize(tenantId);
        var normalizedProjectId = Normalize(projectId);
        if (scopeKind == ConnectorScopeKind.DeviceLocal)
        {
            if (normalizedTenantId is not null || normalizedProjectId is not null)
            {
                throw new ArgumentException("DeviceLocal scope requires explicit null tenantId and projectId.");
            }
        }
        else if (normalizedTenantId is null || normalizedProjectId is null)
        {
            throw new ArgumentException("Project scope requires non-empty tenantId and projectId.");
        }

        ProductId = productId;
        TenantId = normalizedTenantId;
        ScopeKind = scopeKind;
        ProjectId = normalizedProjectId;
    }

    public ConnectorProductId ProductId { get; }
    public string? TenantId { get; }
    public ConnectorScopeKind ScopeKind { get; }
    public string? ProjectId { get; }

    public static ExecutionScope DeviceLocal(ConnectorProductId productId)
        => new(productId, tenantId: null, ConnectorScopeKind.DeviceLocal, projectId: null);

    public static ExecutionScope Project(ConnectorProductId productId, string tenantId, string projectId)
        => new(productId, tenantId, ConnectorScopeKind.Project, projectId);

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public static class ConnectorExecutorIds
{
    public const string AutoCad = "cad.autocad";
    public const string Tekla = "cad.tekla";

    public static string FromProvider(CadProvider provider) => provider switch
    {
        CadProvider.AutoCad => AutoCad,
        CadProvider.Tekla => Tekla,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown CAD provider."),
    };
}

public sealed record ConnectorJobEnvelope(
    int SchemaVersion,
    string RequestId,
    string ModuleId,
    CadProvider Provider,
    JobOperation Operation,
    DateTime CreatedAtUtc,
    JsonElement Payload,
    string? CorrelationId = null,
    string? ExecutorId = null,
    ExecutionScope? Scope = null)
{
    public string EffectiveExecutorId => string.IsNullOrWhiteSpace(ExecutorId)
        ? ConnectorExecutorIds.FromProvider(Provider)
        : ExecutorId.Trim().ToLowerInvariant();
}

public sealed record ConnectorJobStatusEnvelope(
    int SchemaVersion,
    string RequestId,
    string DeviceId,
    string ModuleId,
    CadProvider Provider,
    JobStatus Status,
    DateTime UpdatedAtUtc,
    string? Message = null,
    int? Progress = null,
    string? ErrorCode = null,
    JsonElement? Result = null,
    string? CorrelationId = null,
    string? ExecutorId = null,
    ExecutionScope? Scope = null);

public sealed record ConnectorExecutionProgress(
    int Progress,
    string Message,
    JsonElement? Details = null);

public sealed record ConnectorExecutionResult(
    bool IsSuccess,
    string? Message = null,
    string? ErrorCode = null,
    JsonElement? Result = null);

public sealed record ProviderExecutionResult(
    bool IsSuccess,
    string? Message = null,
    string? ErrorCode = null,
    JsonElement? Result = null);
