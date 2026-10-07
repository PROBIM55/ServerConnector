using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Connector.Access.NetBird.AspNetCore;

public static class NetBirdPeerAttestationBoundaryExtensions
{
    private static readonly PathString PrivateApiPrefix = new(
        NetBirdPeerAttestationEndpoints.Route[
            ..NetBirdPeerAttestationEndpoints.Route.LastIndexOf('/')]);

    /// <summary>
    /// Hides the private peer-attestation route tree from every listener except the configured local socket.
    /// </summary>
    public static IApplicationBuilder UseNetBirdPeerAttestationPrivateBoundary(
        this IApplicationBuilder app,
        NetBirdPeerAttestationListenerBinding binding)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(binding);

        app.Use(async (context, next) =>
        {
            if (IsPrivateApiPath(context.Request.Path) && !binding.Matches(context))
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context);
        });
        return app;
    }

    private static bool IsPrivateApiPath(PathString requestPath)
    {
        var candidate = requestPath.Value;
        if (string.IsNullOrEmpty(candidate)) return false;

        for (var decodePass = 0; decodePass <= 3; decodePass++)
        {
            var normalizedSeparators = candidate.Replace('\\', '/');
            try
            {
                if (new PathString(normalizedSeparators).StartsWithSegments(
                        PrivateApiPrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (decodePass == 3) break;
            if (!candidate.Contains('%')) return false;
            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(candidate);
            }
            catch (UriFormatException)
            {
                return false;
            }
            if (string.Equals(decoded, candidate, StringComparison.Ordinal)) return false;
            candidate = decoded;
        }

        return false;
    }
}
