using System.Text.Json;
using Connector.Access.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Connector.Access.AspNetCore;

// Implemented by Platform's existing web authentication. Device identities and
// HTTP headers must never be substituted for the authenticated web administrator.
public interface IPlatformConnectorAdminIdentity
{
    ValueTask<string?> GetAuthenticatedWebUserIdAsync(HttpContext context, CancellationToken cancellationToken);
}

public interface IPlatformConnectorGrantAdministration
{
    ValueTask<DeviceAccessResult<ConnectorAccessAdminCatalog>> GetCatalogAsync(
        string actorUserId, string? userId, string companyId, CancellationToken cancellationToken);
    ValueTask<DeviceAccessResult<ConnectorAccessAdminProfile>> GetProfileAsync(
        string actorUserId, string userId, string companyId, CancellationToken cancellationToken);
    ValueTask<DeviceAccessResult<ConnectorAccessAdminProfile>> UpdateGrantsAsync(
        string actorUserId, string userId, string companyId, long expectedRevision,
        IReadOnlyList<ModuleGrant> modules, IReadOnlyList<ResourceGrant> resources,
        CancellationToken cancellationToken);
}

public sealed record IssueTokenRequest(string UserId, string CompanyId, int? LifetimeSeconds = null);
public sealed record RevokeDeviceRequest(string CompanyId);
public sealed record UpdateGrantsRequest(
    string CompanyId, long ExpectedRevision,
    IReadOnlyList<ModuleGrant> Modules, IReadOnlyList<ResourceGrant> Resources);

public static class ConnectorAccessEndpoints
{
    public const string EnrollmentRateLimitPolicy = "connector-device-enrollment";
    private const long MaximumRequestBytes = 32768;

    public static IEndpointRouteBuilder MapConnectorAccess(this IEndpointRouteBuilder endpoints)
    {
        var device = endpoints.MapGroup("/api/platform/connector/access/v1");
        device.MapPost("/enroll", EnrollAsync).RequireRateLimiting(EnrollmentRateLimitPolicy);
        device.MapGet("/profile", ProfileAsync);
        device.MapGet("/vpn/bootstrap", VpnBootstrapAsync);
        device.MapGet("/vpn/state", VpnStateAsync);
        device.MapGet("/enrollments/{requestId:guid}", RecoverAsync);
        device.MapPost("/enrollments/{requestId:guid}/resume", ResumeAsync)
            .RequireRateLimiting(EnrollmentRateLimitPolicy);
        device.MapPost("/devices/{deviceId}/enrollments/{requestId:guid}/self-revoke", SelfRevokeAsync);
        device.MapGet("/devices/{deviceId}/enrollments/{requestId:guid}/self-revoke", SelfRevokeStatusAsync);
        var admin = endpoints.MapGroup("/api/platform/admin/connector/access/v1");
        admin.MapPost("/enrollment-tokens", IssueAsync);
        admin.MapGet("/devices", ListDevicesAsync);
        admin.MapGet("/catalog", CatalogAsync);
        admin.MapGet("/users/{userId}/profile", AdminProfileAsync);
        admin.MapPut("/users/{userId}/grants", UpdateGrantsAsync);
        admin.MapPost("/devices/{deviceId}/revoke", RevokeAsync);
        return endpoints;
    }

    private static async Task<IResult> EnrollAsync(HttpContext context, DeviceEnrollmentService service)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.IsHttps) return Failure("https_required", StatusCodes.Status400BadRequest);
        var request = await ReadBoundedJsonAsync<DeviceEnrollmentRequest>(context);
        if (request is null) return Failure("invalid_request", StatusCodes.Status400BadRequest);
        var result = await service.EnrollAsync(request, context.RequestAborted);
        // Token validity/reuse/owner errors intentionally have the same public response.
        return result.IsSuccess ? Results.Json(result.Value) : Failure("enrollment_rejected", StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> ProfileAsync(HttpContext context,
        DeviceCertificateAuthenticator authenticator, DeviceAccessProfileService service)
    {
        var identity = await AuthenticateAsync(context, authenticator);
        if (identity is null) return Failure("device_unauthorized", StatusCodes.Status401Unauthorized);
        var result = await service.GetProfileAsync(identity, context.RequestAborted);
        return result.IsSuccess ? Results.Json(result.Value) : Failure("access_pending_or_denied", StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> VpnBootstrapAsync(HttpContext context,
        DeviceCertificateAuthenticator authenticator)
    {
        context.Response.Headers.CacheControl = "no-store";
        var identity = await AuthenticateAsync(context, authenticator);
        if (identity is null) return Failure("device_unauthorized", StatusCodes.Status401Unauthorized);
        var reader = context.RequestServices.GetService<IConnectorVpnBootstrapReader>();
        if (reader is null) return Failure("vpn_not_configured", StatusCodes.Status503ServiceUnavailable);
        var result = await reader.GetAsync(identity, context.RequestAborted);
        return result.IsSuccess
            ? Results.Json(result.Value)
            : Failure("vpn_bootstrap_unavailable", StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> VpnStateAsync(HttpContext context,
        DeviceCertificateAuthenticator authenticator)
    {
        context.Response.Headers.CacheControl = "no-store";
        var identity = await AuthenticateAsync(context, authenticator);
        if (identity is null) return Failure("device_unauthorized", StatusCodes.Status401Unauthorized);
        var reader = context.RequestServices.GetService<IConnectorVpnBootstrapReader>();
        if (reader is null) return Failure("vpn_not_configured", StatusCodes.Status503ServiceUnavailable);
        var result = await reader.GetStateAsync(identity, context.RequestAborted);
        return result.IsSuccess
            ? Results.Json(result.Value)
            : Failure("vpn_state_unavailable", StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> RecoverAsync(Guid requestId, HttpContext context,
        DeviceCertificateAuthenticator authenticator, DeviceEnrollmentService service)
    {
        var identity = await AuthenticateAsync(context, authenticator);
        if (identity is null) return Failure("device_unauthorized", StatusCodes.Status401Unauthorized);
        var result = await service.RecoverResponseAsync(requestId, identity, context.RequestAborted);
        return result.IsSuccess ? Results.Json(result.Value) : Failure("enrollment_not_available", StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ResumeAsync(Guid requestId, HttpContext context,
        DeviceEnrollmentService service)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.IsHttps) return Failure("device_unauthorized", StatusCodes.Status401Unauthorized);
        var certificate = await context.Connection.GetClientCertificateAsync(context.RequestAborted);
        if (certificate is null) return Failure("device_unauthorized", StatusCodes.Status401Unauthorized);
        var bootstrap = await service.AuthenticateEnrollmentKeyAsync(certificate, requestId, context.RequestAborted);
        if (!bootstrap.IsSuccess || bootstrap.Value is null) return Failure("device_unauthorized", StatusCodes.Status401Unauthorized);
        var result = await service.RecoverOrResumeAsync(bootstrap.Value, context.RequestAborted);
        return result.IsSuccess ? Results.Json(result.Value) : Failure("enrollment_not_available", StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> SelfRevokeAsync(
        string deviceId,
        Guid requestId,
        HttpContext context,
        [Microsoft.AspNetCore.Mvc.FromServices] DeviceSelfRevocationService service)
    {
        context.Response.Headers.CacheControl = "no-store";
        var certificate = await GetPresentedCertificateAsync(context);
        if (certificate is null) return Failure("device_unauthorized", StatusCodes.Status401Unauthorized);
        var result = await service.RevokeAsync(certificate, deviceId, requestId, context.RequestAborted);
        return SelfRevocationResult(result);
    }

    private static async Task<IResult> SelfRevokeStatusAsync(
        string deviceId,
        Guid requestId,
        HttpContext context,
        [Microsoft.AspNetCore.Mvc.FromServices] DeviceSelfRevocationService service)
    {
        context.Response.Headers.CacheControl = "no-store";
        var certificate = await GetPresentedCertificateAsync(context);
        if (certificate is null) return Failure("device_unauthorized", StatusCodes.Status401Unauthorized);
        var result = await service.GetStatusAsync(certificate, deviceId, requestId, context.RequestAborted);
        return SelfRevocationResult(result);
    }

    private static IResult SelfRevocationResult(DeviceAccessResult<DeviceSelfRevocationStatus> result)
    {
        if (!result.IsSuccess || result.Value is null)
        {
            return result.Failure?.Code == "provider_configuration"
                ? Failure("self_revoke_unavailable", StatusCodes.Status503ServiceUnavailable)
                : Failure("device_unauthorized", StatusCodes.Status401Unauthorized);
        }

        return result.Value.Completed
            ? Results.NoContent()
            : Results.Json(result.Value, statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> IssueAsync(HttpContext context, DeviceEnrollmentService service)
    {
        var actor = await GetAdministratorAsync(context);
        if (actor is null) return Failure("access_denied", StatusCodes.Status403Forbidden);
        var request = await ReadBoundedJsonAsync<IssueTokenRequest>(context);
        if (request is null || request.LifetimeSeconds is <= 0 or > 3600)
            return Failure("invalid_request", StatusCodes.Status400BadRequest);
        var result = await service.IssueTokenAsync(new IssueEnrollmentTokenCommand(actor, request.UserId, request.CompanyId,
            request.LifetimeSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null), context.RequestAborted);
        context.Response.Headers.CacheControl = "no-store";
        return result.IsSuccess ? Results.Json(result.Value) : Failure("access_denied", StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> RevokeAsync(string deviceId, HttpContext context, DeviceAccessAdministrationService service)
    {
        var actor = await GetAdministratorAsync(context);
        if (actor is null) return Failure("access_denied", StatusCodes.Status403Forbidden);
        var request = await ReadBoundedJsonAsync<RevokeDeviceRequest>(context);
        if (request is null) return Failure("invalid_request", StatusCodes.Status400BadRequest);
        var result = await service.RevokeAsync(new RevokeDeviceCommand(actor, request.CompanyId, deviceId), context.RequestAborted);
        return result.IsSuccess ? Results.NoContent() : Failure("access_denied", StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> ListDevicesAsync(
        string? companyId, HttpContext context, DeviceAccessAdministrationService service)
    {
        var actor = await GetAdministratorAsync(context);
        if (actor is null || string.IsNullOrWhiteSpace(companyId))
            return Failure("access_denied", StatusCodes.Status403Forbidden);
        var result = await service.ListAsync(actor, companyId.Trim(), context.RequestAborted);
        return result.IsSuccess
            ? Results.Json(new { items = result.Value })
            : Failure("access_denied", StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> CatalogAsync(string? companyId, string? userId, HttpContext context)
    {
        var actor = await GetAdministratorAsync(context);
        if (actor is null || string.IsNullOrWhiteSpace(companyId))
            return Failure("access_denied", StatusCodes.Status403Forbidden);
        var administration = context.RequestServices.GetRequiredService<IPlatformConnectorGrantAdministration>();
        var result = await administration.GetCatalogAsync(actor, userId, companyId.Trim(), context.RequestAborted);
        return AdminResult(result);
    }

    private static async Task<IResult> AdminProfileAsync(string userId, string? companyId, HttpContext context)
    {
        var actor = await GetAdministratorAsync(context);
        if (actor is null || string.IsNullOrWhiteSpace(companyId))
            return Failure("access_denied", StatusCodes.Status403Forbidden);
        var administration = context.RequestServices.GetRequiredService<IPlatformConnectorGrantAdministration>();
        var result = await administration.GetProfileAsync(actor, userId, companyId.Trim(), context.RequestAborted);
        return AdminResult(result);
    }

    private static async Task<IResult> UpdateGrantsAsync(string userId, HttpContext context)
    {
        var actor = await GetAdministratorAsync(context);
        if (actor is null) return Failure("access_denied", StatusCodes.Status403Forbidden);
        var request = await ReadBoundedJsonAsync<UpdateGrantsRequest>(context);
        if (request is null || string.IsNullOrWhiteSpace(request.CompanyId) || request.ExpectedRevision < 0 ||
            request.Modules is null || request.Resources is null)
            return Failure("invalid_request", StatusCodes.Status400BadRequest);
        var administration = context.RequestServices.GetRequiredService<IPlatformConnectorGrantAdministration>();
        var result = await administration.UpdateGrantsAsync(
            actor, userId, request.CompanyId.Trim(), request.ExpectedRevision,
            request.Modules, request.Resources, context.RequestAborted);
        return AdminResult(result);
    }

    private static IResult AdminResult<T>(DeviceAccessResult<T> result)
    {
        if (result.IsSuccess) return Results.Json(result.Value);
        var status = result.Failure?.Code switch
        {
            "revision_conflict" => StatusCodes.Status409Conflict,
            "invalid_grants" or "invalid_request" => StatusCodes.Status400BadRequest,
            "subject_not_found" => StatusCodes.Status404NotFound,
            _ => StatusCodes.Status403Forbidden,
        };
        return Failure(result.Failure?.Code ?? "access_denied", status);
    }

    private static async ValueTask<AuthenticatedDevice?> AuthenticateAsync(HttpContext context, DeviceCertificateAuthenticator authenticator)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.IsHttps) return null;
        // TLS middleware supplies this feature. No X-Client-Cert or arbitrary
        // forwarded certificate header is parsed by the connector API.
        var certificate = await context.Connection.GetClientCertificateAsync(context.RequestAborted);
        if (certificate is null) return null;
        var result = await authenticator.AuthenticateAsync(certificate, context.RequestAborted);
        return result.IsSuccess ? result.Value : null;
    }

    private static async ValueTask<System.Security.Cryptography.X509Certificates.X509Certificate2?> GetPresentedCertificateAsync(
        HttpContext context)
    {
        if (!context.Request.IsHttps) return null;
        return await context.Connection.GetClientCertificateAsync(context.RequestAborted);
    }

    private static async ValueTask<string?> GetAdministratorAsync(HttpContext context)
    {
        if (!context.Request.IsHttps) return null;
        // JSON-only + same-origin requests protect cookie-authenticated mutations.
        if (context.Request.Headers.Origin is { Count: > 0 } origin &&
            !string.Equals(origin.ToString(), $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase))
            return null;
        var identity = context.RequestServices.GetRequiredService<IPlatformConnectorAdminIdentity>();
        return await identity.GetAuthenticatedWebUserIdAsync(context, context.RequestAborted);
    }

    private static async Task<T?> ReadBoundedJsonAsync<T>(HttpContext context)
    {
        if (!context.Request.HasJsonContentType() || context.Request.ContentLength > MaximumRequestBytes) return default;
        var size = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (size is { IsReadOnly: false }) size.MaxRequestBodySize = MaximumRequestBytes;
        // Bounded buffering also covers chunked requests and custom HTTP hosts.
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted);
            if (read == 0) break;
            if (body.Length + read > MaximumRequestBytes) return default;
            body.Write(buffer, 0, read);
        }
        try { return JsonSerializer.Deserialize<T>(body.GetBuffer().AsSpan(0, (int)body.Length), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return default; }
    }

    private static IResult Failure(string code, int status) => Results.Json(
        new DeviceAccessFailure(code, "Доступ не подтверждён. Проверьте подключение или обратитесь к администратору."), statusCode: status);
}
