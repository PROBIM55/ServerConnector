using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Connector.Core;

public sealed class HttpConnectorControlPlaneClient(HttpClient httpClient) : IConnectorControlPlaneClient
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task CheckHealthAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
    {
        EnsureBaseAddress(options);
        using var response = await httpClient.GetAsync("health", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<BootstrapResponseDto> BootstrapAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
    {
        EnsureBaseAddress(options);

        using var request = new HttpRequestMessage(HttpMethod.Post, "connect/bootstrap");
        request.Headers.Add("X-Device-Token", options.DeviceToken);
        request.Content = JsonContent.Create(new
        {
            Hostname = Environment.MachineName,
            agentVersion = "platform-connector/0.1.0",
            agent_version = "platform-connector/0.1.0",
            agentType = options.AgentType,
            agent_type = options.AgentType,
            moduleScope = options.ModuleScope,
            module_scope = options.ModuleScope,
            capabilities = options.Capabilities
        });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        ThrowIfTokenRejected(response, body);
        response.EnsureSuccessStatusCode();

        var bootstrap = JsonSerializer.Deserialize<BootstrapResponseDto>(body, JsonOptions)
            ?? throw new InvalidOperationException("Invalid bootstrap response payload.");

        if (!bootstrap.Ok)
        {
            throw new InvalidOperationException("Bootstrap response indicates not ok.");
        }

        if (!string.IsNullOrWhiteSpace(bootstrap.AgentType))
        {
            options.AgentType = bootstrap.AgentType;
        }

        if (!string.IsNullOrWhiteSpace(bootstrap.ModuleScope))
        {
            options.ModuleScope = bootstrap.ModuleScope;
        }

        if (bootstrap.Capabilities is { Count: > 0 })
        {
            options.Capabilities = bootstrap.Capabilities;
        }

        if (!string.IsNullOrWhiteSpace(bootstrap.UpdateManifestUrl))
        {
            options.UpdateManifestUrl = bootstrap.UpdateManifestUrl;
        }

        if (bootstrap.SmbAccess is not null)
        {
            options.SmbAccess = bootstrap.SmbAccess;
        }

        return bootstrap;
    }

    public async Task SendHeartbeatAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
    {
        EnsureBaseAddress(options);
        using var request = new HttpRequestMessage(HttpMethod.Post, "heartbeat");
        request.Headers.Add("X-Device-Token", options.DeviceToken);
        if (!string.IsNullOrWhiteSpace(options.SessionId))
        {
            request.Headers.Add("X-Device-Session", options.SessionId);
        }

        request.Content = JsonContent.Create(new
        {
            deviceId = options.DeviceId,
            device_id = options.DeviceId,
            hostname = Environment.MachineName,
            agentVersion = "platform-connector/0.1.0",
            agent_version = "platform-connector/0.1.0",
            agentType = options.AgentType,
            agent_type = options.AgentType,
            moduleScope = options.ModuleScope,
            module_scope = options.ModuleScope,
            capabilities = options.Capabilities
        });

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw await BuildSessionConflictAsync(response, cancellationToken);
        }

        await ThrowIfTokenRejectedAsync(response, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<ConnectorJobEnvelope?> TryPollJobAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
    {
        EnsureBaseAddress(options);
        var path = $"connector/jobs/next?deviceId={Uri.EscapeDataString(options.DeviceId)}";
        if (!string.IsNullOrWhiteSpace(options.AgentType))
        {
            path += $"&agentType={Uri.EscapeDataString(options.AgentType)}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Device-Token", options.DeviceToken);
        if (!string.IsNullOrWhiteSpace(options.SessionId))
        {
            request.Headers.Add("X-Device-Session", options.SessionId);
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw await BuildSessionConflictAsync(response, cancellationToken);
        }

        await ThrowIfTokenRejectedAsync(response, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<ConnectorJobEnvelope>(body, JsonOptions);
    }

    public async Task SendJobStatusAsync(ConnectorRuntimeOptions options, ConnectorJobStatusEnvelope status, CancellationToken cancellationToken)
    {
        EnsureBaseAddress(options);
        var path = $"connector/jobs/{Uri.EscapeDataString(status.RequestId)}/status";
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("X-Device-Token", options.DeviceToken);
        if (!string.IsNullOrWhiteSpace(options.SessionId))
        {
            request.Headers.Add("X-Device-Session", options.SessionId);
        }

        request.Content = status.SchemaVersion == 1
            ? JsonContent.Create(ConnectorJobStatusV1Dto.From(status), options: JsonOptions)
            : JsonContent.Create(status, options: JsonOptions);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw await BuildSessionConflictAsync(response, cancellationToken);
        }

        await ThrowIfTokenRejectedAsync(response, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<SessionConflictException> BuildSessionConflictAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = TryExtractErrorMessage(body) ?? "Session superseded by newer login.";
        return new SessionConflictException(message, response.StatusCode, body);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new CadProviderWireJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed class CadProviderWireJsonConverter : JsonConverter<CadProvider>
    {
        public override CadProvider Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            return value?.ToLowerInvariant() switch
            {
                "autocad" => CadProvider.AutoCad,
                "tekla" => CadProvider.Tekla,
                _ => throw new JsonException("Invalid CAD provider value.")
            };
        }

        public override void Write(Utf8JsonWriter writer, CadProvider value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value switch
            {
                CadProvider.AutoCad => "autocad",
                CadProvider.Tekla => "tekla",
                _ => throw new JsonException("Invalid CAD provider value.")
            });
        }
    }

    private sealed record ConnectorJobStatusV1Dto(
        int SchemaVersion,
        string RequestId,
        string DeviceId,
        string ModuleId,
        CadProvider Provider,
        JobStatus Status,
        DateTime UpdatedAtUtc,
        string? Message,
        int? Progress,
        string? ErrorCode,
        JsonElement? Result,
        string? CorrelationId)
    {
        public static ConnectorJobStatusV1Dto From(ConnectorJobStatusEnvelope status)
            => new(
                status.SchemaVersion,
                status.RequestId,
                status.DeviceId,
                status.ModuleId,
                status.Provider,
                status.Status,
                status.UpdatedAtUtc,
                status.Message,
                status.Progress,
                status.ErrorCode,
                status.Result,
                status.CorrelationId);
    }

    private static async Task ThrowIfTokenRejectedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        ThrowIfTokenRejected(response, body);
    }

    private static void ThrowIfTokenRejected(HttpResponseMessage response, string body)
    {
        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
        {
            return;
        }

        var message = TryExtractErrorMessage(body) ?? "Device token rejected by server.";
        throw new TokenRejectedException(message, response.StatusCode, body);
    }

    private static string? TryExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                return error.GetString();
            }

            if (doc.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }
        }
        catch
        {
            // Ignore malformed payloads and fallback to default text.
        }

        return null;
    }

    private void EnsureBaseAddress(ConnectorRuntimeOptions options)
    {
        if (httpClient.BaseAddress is null)
        {
            httpClient.BaseAddress = new Uri(options.ServerUrl.TrimEnd('/') + "/");
        }
    }
}
