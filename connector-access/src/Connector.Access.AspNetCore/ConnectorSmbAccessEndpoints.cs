using Connector.Access.Smb;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Connector.Access.AspNetCore;

public static class ConnectorSmbAccessEndpoints
{
    public static IEndpointRouteBuilder MapConnectorSmbAccess(
        this IEndpointRouteBuilder endpoints,
        ConnectorPrivateAccessOptions privateAccessOptions,
        bool smbEnabled)
    {
        endpoints.MapGet("/api/platform/connector/access/v1/smb/access", async (HttpContext context) =>
        {
            var authorization = await ConnectorPrivateAccessHttpGuard.AuthorizeAsync(context, privateAccessOptions);
            if (authorization.Context is null) return authorization.Error!;
            var reader = context.RequestServices.GetService<IConnectorSmbAccessReader>();
            if (!smbEnabled || reader is null)
                return Failure("smb_access_unavailable", StatusCodes.Status503ServiceUnavailable);
            var result = await reader.GetAsync(
                authorization.Context.Identity, authorization.Context.Profile, context.RequestAborted);
            if (!result.IsSuccess || result.Value is null)
            {
                var unavailable = result.Failure?.Code.EndsWith("_unavailable", StringComparison.Ordinal) == true;
                return Failure(result.Failure?.Code ?? "smb_access_denied",
                    unavailable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status403Forbidden);
            }
            return Results.Json(result.Value);
        });
        return endpoints;
    }

    private static IResult Failure(string code, int status) => Results.Json(new { error = code }, statusCode: status);
}
