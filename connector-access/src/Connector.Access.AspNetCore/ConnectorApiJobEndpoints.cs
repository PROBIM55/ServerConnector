using System.Text.Json;
using System.Text.Json.Serialization;
using System.Data.Common;
using Connector.Access;
using Connector.Access.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Connector.Access.AspNetCore;

public sealed record ConnectorApiJobAuthorization(ConnectorPrivateAccessContext? Context, IResult? Error);

public interface IConnectorApiJobAuthorizer
{
    ValueTask<ConnectorApiJobAuthorization> AuthorizeAsync(HttpContext context);
}

public interface IConnectorApiJobStore
{
    bool TryGetSession(string deviceId, out string sessionId);
    void UpsertSession(string deviceId, string sessionId, string hostname, string agentType, string moduleScope, string capabilitiesJson);
    void UpsertHeartbeat(string deviceId, string hostname, string? agentVersion, string? publicIp, string agentType, string moduleScope, string capabilitiesJson);
    ConnectorApiJobEnvelope? TryTakeNextJob(
        string deviceId,
        string? agentType,
        Func<ConnectorApiJobEnvelope, bool> canExecute,
        Action<DbConnection, DbTransaction, ConnectorApiJobEnvelope> recordDelivery);
    void AppendStatus(ConnectorApiJobStatusEnvelope status);
}

public sealed record ConnectorApiJobEnvelope(
    int SchemaVersion,
    string RequestId,
    string ModuleId,
    int Provider,
    int Operation,
    DateTime CreatedAtUtc,
    JsonElement Payload,
    string? CorrelationId,
    string? ExecutorId,
    ConnectorApiJobExecutionScope? Scope);

public sealed record ConnectorApiJobExecutionScope(string ProductId, string? TenantId, string ScopeKind, string? ProjectId);

public sealed record ConnectorApiJobStatusEnvelope(
    int SchemaVersion,
    string RequestId,
    string DeviceId,
    string ModuleId,
    int Provider,
    int Status,
    DateTime UpdatedAtUtc,
    string? Message,
    int? Progress,
    string? ErrorCode,
    JsonElement? Result,
    string? CorrelationId,
    string? ExecutorId);

public sealed record ConnectorApiJobDelivery(
    string RequestId,
    string DeviceId,
    string UserId,
    string CompanyId,
    long AccessRevision,
    string SessionId,
    int SchemaVersion,
    string ModuleId,
    int Provider,
    int Operation,
    string? ExecutorId,
    string? CorrelationId,
    ConnectorApiJobExecutionScope Scope,
    DateTimeOffset DeliveredAtUtc,
    int? LastStatus,
    DateTimeOffset? LastStatusAtUtc);

public interface IConnectorApiJobDeliveryStore
{
    ValueTask RecordAsync(ConnectorApiJobDelivery delivery, CancellationToken cancellationToken);
    void RecordInTransaction(DbConnection connection, DbTransaction transaction, ConnectorApiJobDelivery delivery);
    ValueTask<ConnectorApiJobDelivery?> FindAsync(string requestId, CancellationToken cancellationToken);
    ValueTask<bool> AdvanceStatusAndAppendAsync(
        ConnectorApiJobDelivery delivery,
        ConnectorApiJobStatusEnvelope status,
        CancellationToken cancellationToken);
}

public sealed record ConnectorApiJobSessionRequest(
    int SchemaVersion, string Hostname, string? AgentVersion, string AgentType,
    IReadOnlyList<string>? Capabilities);

public sealed record ConnectorApiJobSessionResponse(
    int SchemaVersion, string SessionId, string DeviceId, long AccessRevision, int HeartbeatSeconds);

public sealed record ConnectorApiJobHeartbeatRequest(
    int SchemaVersion, string Hostname, string? AgentVersion, string AgentType,
    IReadOnlyList<string>? Capabilities);

public sealed class ConnectorApiJobStatusRequest
{
    public int SchemaVersion { get; init; }
    public int Status { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public string? Message { get; init; }
    public int? Progress { get; init; }
    public string? ErrorCode { get; init; }
    public JsonElement? Result { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}

/// <summary>Maps the Connector API jobs protocol while delegating Platform persistence and authorization.</summary>
public static class ConnectorApiJobEndpoints
{
    private const int ProtocolVersion = 1;
    private const int MaximumStatusBytes = 1024 * 1024;

    public static IEndpointRouteBuilder MapConnectorApiJobs(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/platform/connector/access/v1/jobs");
        group.MapMethods("/health", [HttpMethods.Head], (Delegate)HealthAsync);
        group.MapPost("/session", StartSessionAsync);
        group.MapPost("/heartbeat", HeartbeatAsync);
        group.MapGet("/next", NextAsync);
        group.MapPost("/{requestId}/status", StatusAsync);
        return endpoints;
    }

    private static async Task<IResult> HealthAsync(HttpContext context, IConnectorApiJobAuthorizer authorizer)
    {
        var authorization = await authorizer.AuthorizeAsync(context);
        return authorization.Context is null ? authorization.Error! : Results.NoContent();
    }

    private static async Task<IResult> StartSessionAsync(
        ConnectorApiJobSessionRequest request, HttpContext context,
        IConnectorApiJobAuthorizer authorizer, IConnectorApiJobStore store)
    {
        var authorization = await authorizer.AuthorizeAsync(context);
        if (authorization.Context is null) return authorization.Error!;
        if (!ValidAgent(request.SchemaVersion, request.Hostname, request.AgentType, request.Capabilities))
            return Failure("invalid_request", StatusCodes.Status400BadRequest);

        var sessionId = Guid.NewGuid().ToString("N");
        var moduleScope = string.Join(',', authorization.Context.Profile.Modules
            .Where(module => module.Permissions.Contains(ConnectorPermission.Execute))
            .Select(module => module.ModuleId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(module => module, StringComparer.Ordinal));
        var capabilities = NormalizeCapabilities(request.Capabilities);
        store.UpsertSession(authorization.Context.Identity.DeviceId, sessionId, request.Hostname.Trim(), request.AgentType.Trim(),
            moduleScope, JsonSerializer.Serialize(capabilities));
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(new ConnectorApiJobSessionResponse(ProtocolVersion, sessionId,
            authorization.Context.Identity.DeviceId, authorization.Context.Profile.AppliedRevision, 60));
    }

    private static async Task<IResult> HeartbeatAsync(
        ConnectorApiJobHeartbeatRequest request, HttpContext context,
        IConnectorApiJobAuthorizer authorizer, IConnectorApiJobStore store)
    {
        var authorization = await authorizer.AuthorizeAsync(context);
        if (authorization.Context is null) return authorization.Error!;
        if (!ValidAgent(request.SchemaVersion, request.Hostname, request.AgentType, request.Capabilities))
            return Failure("invalid_request", StatusCodes.Status400BadRequest);
        if (!TryValidateSession(context, store, authorization.Context.Identity.DeviceId, out var sessionId, out var error))
            return error!;

        var modules = string.Join(',', authorization.Context.Profile.Modules
            .Where(module => module.Permissions.Contains(ConnectorPermission.Execute))
            .Select(module => module.ModuleId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(module => module, StringComparer.Ordinal));
        var capabilities = JsonSerializer.Serialize(NormalizeCapabilities(request.Capabilities));
        store.UpsertSession(authorization.Context.Identity.DeviceId, sessionId, request.Hostname.Trim(),
            request.AgentType.Trim(), modules, capabilities);
        store.UpsertHeartbeat(authorization.Context.Identity.DeviceId, request.Hostname.Trim(),
            request.AgentVersion?.Trim(), context.Connection.RemoteIpAddress?.ToString(), request.AgentType.Trim(), modules, capabilities);
        return Results.NoContent();
    }

    private static async Task<IResult> NextAsync(
        string? agentType, HttpContext context,
        IConnectorApiJobAuthorizer authorizer, IConnectorApiJobStore store,
        IConnectorApiJobDeliveryStore deliveries)
    {
        var authorization = await authorizer.AuthorizeAsync(context);
        if (authorization.Context is null) return authorization.Error!;
        if (!TryValidateSession(context, store, authorization.Context.Identity.DeviceId, out var sessionId, out var error))
            return error!;
        var job = store.TryTakeNextJob(authorization.Context.Identity.DeviceId, agentType?.Trim(),
            candidate => CanExecute(authorization.Context.Profile, candidate.ModuleId, candidate.Scope),
            (connection, transaction, claimed) => deliveries.RecordInTransaction(
                connection, transaction, new ConnectorApiJobDelivery(claimed.RequestId,
                    authorization.Context.Identity.DeviceId, authorization.Context.Identity.UserId,
                    authorization.Context.Identity.CompanyId, authorization.Context.Profile.AppliedRevision,
                    sessionId, claimed.SchemaVersion, claimed.ModuleId, claimed.Provider, claimed.Operation,
                    claimed.ExecutorId, claimed.CorrelationId, claimed.Scope!, DateTimeOffset.UtcNow, null, null)));
        if (job is null) return Results.NoContent();
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(job);
    }

    private static async Task<IResult> StatusAsync(
        string requestId, ConnectorApiJobStatusRequest request, HttpContext context,
        IConnectorApiJobAuthorizer authorizer, IConnectorApiJobStore store,
        IConnectorApiJobDeliveryStore deliveries)
    {
        if (context.Request.ContentLength is > MaximumStatusBytes || request.ExtensionData is { Count: > 0 })
            return Failure("invalid_request", StatusCodes.Status400BadRequest);
        var authorization = await authorizer.AuthorizeAsync(context);
        if (authorization.Context is null) return authorization.Error!;
        if (!TryValidateSession(context, store, authorization.Context.Identity.DeviceId, out var sessionId, out var error))
            return error!;
        if (string.IsNullOrWhiteSpace(requestId) || request.Status is < 1 or > 7 ||
            request.Progress is < 0 or > 100 || request.Message?.Length > 4096 || request.ErrorCode?.Length > 256)
            return Failure("invalid_request", StatusCodes.Status400BadRequest);

        var delivery = await deliveries.FindAsync(requestId, context.RequestAborted);
        var now = DateTimeOffset.UtcNow;
        if (delivery is null || delivery.DeviceId != authorization.Context.Identity.DeviceId ||
            delivery.UserId != authorization.Context.Identity.UserId || delivery.CompanyId != authorization.Context.Identity.CompanyId ||
            delivery.AccessRevision != authorization.Context.Profile.AppliedRevision || delivery.SessionId != sessionId ||
            request.SchemaVersion != delivery.SchemaVersion || request.UpdatedAtUtc < delivery.DeliveredAtUtc ||
            request.UpdatedAtUtc > now.AddMinutes(2) || delivery.LastStatus is not null and not (1 or 2) ||
            delivery.LastStatusAtUtc is not null && request.UpdatedAtUtc <= delivery.LastStatusAtUtc ||
            !CanExecute(authorization.Context.Profile, delivery.ModuleId, delivery.Scope))
            return Failure("job_status_denied", StatusCodes.Status403Forbidden);

        var status = new ConnectorApiJobStatusEnvelope(delivery.SchemaVersion, delivery.RequestId, delivery.DeviceId,
            delivery.ModuleId, delivery.Provider, request.Status, request.UpdatedAtUtc.UtcDateTime, request.Message,
            request.Progress, request.ErrorCode, request.Result, delivery.CorrelationId, delivery.ExecutorId);
        if (!await deliveries.AdvanceStatusAndAppendAsync(delivery, status, context.RequestAborted))
            return Failure("stale_job_status", StatusCodes.Status409Conflict);
        return Results.NoContent();
    }

    private static bool CanExecute(DeviceAccessProfile profile, string moduleId, ConnectorApiJobExecutionScope? scope)
    {
        if (scope is null || scope.ScopeKind != "project" || scope.TenantId != profile.CompanyId ||
            string.IsNullOrWhiteSpace(scope.ProjectId)) return false;
        if (!profile.Modules.Any(module => string.Equals(module.Product.ToString(), scope.ProductId, StringComparison.OrdinalIgnoreCase) && module.ModuleId == moduleId &&
                                            module.Permissions.Contains(ConnectorPermission.Execute))) return false;
        return profile.Resources.Any(resource => resource.ResourceKind == "project" &&
            resource.ResourceId == scope.ProjectId && resource.ProjectId == scope.ProjectId &&
            resource.Permissions.Contains(ConnectorPermission.Execute));
    }

    private static bool TryValidateSession(HttpContext context, IConnectorApiJobStore store, string deviceId,
        out string sessionId, out IResult? error)
    {
        sessionId = context.Request.Headers["X-Device-Session"].ToString().Trim();
        if (sessionId.Length == 0 || !store.TryGetSession(deviceId, out var current) || current != sessionId)
        {
            error = Failure("session_required_or_superseded", StatusCodes.Status409Conflict);
            return false;
        }
        error = null;
        return true;
    }

    private static bool ValidAgent(int schemaVersion, string hostname, string agentType, IReadOnlyList<string>? capabilities) =>
        schemaVersion == ProtocolVersion && !string.IsNullOrWhiteSpace(hostname) && hostname.Length <= 255 &&
        !string.IsNullOrWhiteSpace(agentType) && agentType.Length <= 128 && capabilities is { Count: <= 64 };

    private static string[] NormalizeCapabilities(IReadOnlyList<string>? capabilities) =>
        (capabilities ?? []).Where(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 128)
            .Select(value => value.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static IResult Failure(string code, int status) => Results.Json(new { error = code }, statusCode: status);
}
