using System.Text.Json;
using Connector.Access;
using Connector.Access.NetBird;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Connector.Access.NetBird.AspNetCore;

public static class NetBirdPeerAttestationEndpoints
{
    public const string Route = "/api/platform/connector/private/v1/peer-attestation";
    private const int MaximumRequestBytes = 2 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // This private transport boundary is opt-in. It is deliberately separate
    // from the public MapConnectorAccess route set.
    public static IEndpointRouteBuilder MapNetBirdPeerAttestation(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost(Route, AttestAsync);
        return endpoints;
    }

    private static async Task<IResult> AttestAsync(
        HttpContext context,
        DeviceCertificateAuthenticator authenticator,
        INetBirdPeerAttestationService attestationService)
    {
        context.Response.Headers.CacheControl = "no-store";

        if (!context.Request.IsHttps)
            return Rejected(StatusCodes.Status400BadRequest);

        var certificate = await context.Connection
            .GetClientCertificateAsync(context.RequestAborted)
            .ConfigureAwait(false);
        if (certificate is null)
            return Rejected(StatusCodes.Status401Unauthorized);

        DeviceAccessResult<AuthenticatedDevice> authentication;
        try
        {
            authentication = await authenticator
                .AuthenticateAsync(certificate, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Rejected(StatusCodes.Status401Unauthorized);
        }

        if (!authentication.IsSuccess || authentication.Value is null)
            return Rejected(StatusCodes.Status401Unauthorized);

        var localIp = context.Connection.LocalIpAddress;
        var remoteIp = context.Connection.RemoteIpAddress;
        if (localIp is null || remoteIp is null)
            return Rejected(StatusCodes.Status403Forbidden);

        var body = await ReadBodyAsync(context).ConfigureAwait(false);
        if (body?.Nonce is null || body.PublicKey is null)
            return Rejected(StatusCodes.Status400BadRequest);

        NetBirdPeerAttestationResult result;
        try
        {
            result = await attestationService.AttestAsync(
                new NetBirdPeerAttestationRequest(
                    authentication.Value,
                    localIp.ToString(),
                    remoteIp.ToString(),
                    body.PublicKey,
                    body.Nonce),
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Rejected(StatusCodes.Status403Forbidden);
        }

        return result is NetBirdPeerAttestationAccepted accepted
            ? Results.Json(accepted.Attestation)
            : Rejected(StatusCodes.Status403Forbidden);
    }

    private static async Task<PeerAttestationBody?> ReadBodyAsync(HttpContext context)
    {
        if (!context.Request.HasJsonContentType() ||
            context.Request.ContentLength is > MaximumRequestBytes)
        {
            return null;
        }

        try
        {
            using var buffer = new MemoryStream(MaximumRequestBytes);
            var chunk = new byte[512];
            while (true)
            {
                var remainingWithSentinel = MaximumRequestBytes - (int)buffer.Length + 1;
                var read = await context.Request.Body.ReadAsync(
                    chunk.AsMemory(0, Math.Min(chunk.Length, remainingWithSentinel)),
                    context.RequestAborted).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (buffer.Length + read > MaximumRequestBytes)
                    return null;
                await buffer.WriteAsync(chunk.AsMemory(0, read), context.RequestAborted).ConfigureAwait(false);
            }

            return JsonSerializer.Deserialize<PeerAttestationBody>(
                buffer.GetBuffer().AsSpan(0, (int)buffer.Length),
                JsonOptions);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or IOException or BadHttpRequestException)
        {
            return null;
        }
    }

    private static IResult Rejected(int statusCode) => Results.Json(
        new
        {
            code = "peer_attestation_rejected",
            message = "Peer attestation was rejected."
        },
        statusCode: statusCode);

    private sealed class PeerAttestationBody
    {
        public PeerAttestationBody()
        {
        }

        public string? Nonce { get; init; }
        public string? PublicKey { get; init; }
    }
}
