using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Platform.Connector.Core;

/// <summary>
/// Sends one request through the already-authorized common connector transport.
/// The transport owns private-overlay routing and the issued client certificate.
/// </summary>
public interface ICommonConnectorRequestTransport
{
    Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativeUri,
        object? body,
        IReadOnlyDictionary<string, string> managedHeaders,
        CancellationToken cancellationToken);
}

public sealed class HttpMtlsConnectorControlPlaneClient : IConnectorControlPlaneClient
{
    private const string AgentVersion = "platform-connector/0.1.0";
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private const int MaxOpaqueIdLength = 256;
    private const int MinHeartbeatSeconds = 10;
    private const int MaxHeartbeatSeconds = 3600;
    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly ICommonConnectorRequestTransport _transport;
    private readonly string _expectedCompanyId;

    public HttpMtlsConnectorControlPlaneClient(
        ICommonConnectorRequestTransport transport,
        string expectedCompanyId)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _expectedCompanyId = NormalizeRequired(expectedCompanyId, nameof(expectedCompanyId), MaxOpaqueIdLength);
    }

    public async Task CheckHealthAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
    {
        EnsureIssuedCertificate(options);
        using var response = await _transport.SendAsync(
            HttpMethod.Head, "jobs/health", body: null, EmptyHeaders, cancellationToken);
        EnsureStatus(response, HttpStatusCode.NoContent);
    }

    public async Task<BootstrapResponseDto> BootstrapAsync(
        ConnectorRuntimeOptions options,
        CancellationToken cancellationToken)
    {
        EnsureIssuedCertificate(options);
        var request = CreateSessionRequest(options);
        using var response = await _transport.SendAsync(
            HttpMethod.Post, "jobs/session", request, EmptyHeaders, cancellationToken);
        EnsureStatus(response, HttpStatusCode.OK);

        var receipt = await DeserializeBoundedAsync<SessionReceiptDto>(response, cancellationToken)
            ?? throw new InvalidDataException("The connector session response is empty.");
        ValidateReceipt(receipt, options.DeviceId);

        options.DeviceId = receipt.DeviceId;
        options.SessionId = receipt.SessionId;
        options.HeartbeatSeconds = receipt.HeartbeatSeconds;
        return new BootstrapResponseDto
        {
            Ok = true,
            DeviceId = receipt.DeviceId,
            SessionId = receipt.SessionId,
            HeartbeatSeconds = receipt.HeartbeatSeconds,
        };
    }

    public async Task SendHeartbeatAsync(ConnectorRuntimeOptions options, CancellationToken cancellationToken)
    {
        EnsureIssuedCertificate(options);
        var headers = SessionHeaders(options);
        using var response = await _transport.SendAsync(
            HttpMethod.Post, "jobs/heartbeat", CreateSessionRequest(options), headers, cancellationToken);
        EnsureStatus(response, HttpStatusCode.NoContent);
    }

    public async Task<ConnectorJobEnvelope?> TryPollJobAsync(
        ConnectorRuntimeOptions options,
        CancellationToken cancellationToken)
    {
        EnsureIssuedCertificate(options);
        var agentType = NormalizeRequired(options.AgentType, nameof(options.AgentType), 128);
        var relativeUri = "jobs/next?agentType=" + Uri.EscapeDataString(agentType);
        using var response = await _transport.SendAsync(
            HttpMethod.Get, relativeUri, body: null, SessionHeaders(options), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }
        EnsureStatus(response, HttpStatusCode.OK);

        var job = await DeserializeBoundedAsync<ConnectorJobEnvelope>(response, cancellationToken)
            ?? throw new InvalidDataException("The connector job response is empty.");
        ValidateRemoteJob(job);
        return job;
    }

    public async Task SendJobStatusAsync(
        ConnectorRuntimeOptions options,
        ConnectorJobStatusEnvelope status,
        CancellationToken cancellationToken)
    {
        EnsureIssuedCertificate(options);
        ArgumentNullException.ThrowIfNull(status);
        if (status.SchemaVersion is not (1 or 2))
        {
            throw new ArgumentException("The connector job status envelope schema version is unsupported.", nameof(status));
        }
        var requestId = EncodeOpaquePathSegment(status.RequestId, nameof(status.RequestId));
        var body = new JobStatusDto(
            1,
            JobStatusValue(status.Status),
            status.UpdatedAtUtc,
            status.Message,
            status.Progress,
            status.ErrorCode,
            status.Result);
        using var response = await _transport.SendAsync(
            HttpMethod.Post,
            "jobs/" + requestId + "/status",
            body,
            SessionHeaders(options),
            cancellationToken);
        EnsureStatus(response, HttpStatusCode.NoContent);
    }

    private static SessionRequestDto CreateSessionRequest(ConnectorRuntimeOptions options)
    {
        var agentType = NormalizeRequired(options.AgentType, nameof(options.AgentType), 128);
        if (options.Capabilities is null || options.Capabilities.Count > 128 ||
            options.Capabilities.Any(value => !IsValidText(value, 128)))
        {
            throw new ArgumentException("Connector capabilities are invalid.", nameof(options));
        }
        return new SessionRequestDto(
            1,
            NormalizeRequired(Environment.MachineName, "hostname", 255),
            AgentVersion,
            agentType,
            options.Capabilities.ToArray());
    }

    private static IReadOnlyDictionary<string, string> SessionHeaders(ConnectorRuntimeOptions options)
    {
        var sessionId = NormalizeRequired(options.SessionId, nameof(options.SessionId), MaxOpaqueIdLength);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Device-Session"] = sessionId,
        };
    }

    private void ValidateRemoteJob(ConnectorJobEnvelope job)
    {
        if (job.SchemaVersion is not (1 or 2) || !IsValidText(job.RequestId, MaxOpaqueIdLength) ||
            !IsValidText(job.ModuleId, 128) || !Enum.IsDefined(job.Provider) || !Enum.IsDefined(job.Operation) ||
            job.Scope is null || job.Scope.ScopeKind != ConnectorScopeKind.Project ||
            !Enum.IsDefined(job.Scope.ProductId) ||
            !string.Equals(job.Scope.TenantId, _expectedCompanyId, StringComparison.Ordinal) ||
            !IsValidText(job.Scope.ProjectId, MaxOpaqueIdLength))
        {
            throw new InvalidDataException("The remote job is outside the authorized company project scope.");
        }
    }

    private static void ValidateReceipt(SessionReceiptDto receipt, string expectedDeviceId)
    {
        if (receipt.SchemaVersion != 1 || !IsValidText(receipt.SessionId, MaxOpaqueIdLength) ||
            !IsValidText(receipt.DeviceId, MaxOpaqueIdLength) ||
            !IsValidText(expectedDeviceId, MaxOpaqueIdLength) ||
            !string.Equals(receipt.DeviceId, expectedDeviceId, StringComparison.Ordinal) ||
            receipt.AccessRevision < 1 ||
            receipt.HeartbeatSeconds is < MinHeartbeatSeconds or > MaxHeartbeatSeconds)
        {
            throw new InvalidDataException("The connector session receipt is invalid.");
        }
    }

    private static void EnsureIssuedCertificate(ConnectorRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.AuthenticationMode != ConnectorAuthenticationMode.IssuedCertificate)
        {
            throw new InvalidOperationException("The common connector control plane requires issued-certificate authentication.");
        }
    }

    private static void EnsureStatus(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new TokenRejectedException(
                "The issued connector certificate was rejected by the control plane.", response.StatusCode);
        }
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new SessionConflictException("The connector session was superseded.", response.StatusCode);
        }
        if (response.StatusCode != expected)
        {
            throw new HttpRequestException(
                $"Connector control-plane request failed with HTTP {(int)response.StatusCode}.",
                inner: null,
                response.StatusCode);
        }
    }

    private static async Task<T?> DeserializeBoundedAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new InvalidDataException("The connector control-plane response exceeds 2 MiB.");
        }
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new InvalidDataException("The connector control-plane response exceeds 2 MiB.");
            }
            buffer.Write(chunk, 0, read);
        }
        if (buffer.Length == 0) return default;
        return JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), JsonOptions);
    }

    private static string EncodeOpaquePathSegment(string value, string parameterName)
        => Uri.EscapeDataString(NormalizeRequired(value, parameterName, MaxOpaqueIdLength));

    private static string NormalizeRequired(string? value, string parameterName, int maxLength)
    {
        if (!IsValidText(value, maxLength))
        {
            throw new ArgumentException("A required connector identifier is invalid.", parameterName);
        }
        return value!.Trim();
    }

    private static bool IsValidText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim();
        return normalized.Length <= maxLength && !normalized.Any(char.IsControl);
    }

    private static int JobStatusValue(JobStatus status) =>
        Enum.IsDefined(status) && status != JobStatus.Queued ? (int)status :
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported remote connector job status.");

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record SessionRequestDto(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("hostname")] string Hostname,
        [property: JsonPropertyName("agentVersion")] string AgentVersion,
        [property: JsonPropertyName("agentType")] string AgentType,
        [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities);

    private sealed record SessionReceiptDto(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("sessionId")] string SessionId,
        [property: JsonPropertyName("deviceId")] string DeviceId,
        [property: JsonPropertyName("accessRevision")] long AccessRevision,
        [property: JsonPropertyName("heartbeatSeconds")] int HeartbeatSeconds);

    private sealed record JobStatusDto(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("status")] int Status,
        [property: JsonPropertyName("updatedAtUtc")] DateTime UpdatedAtUtc,
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("progress")] int? Progress,
        [property: JsonPropertyName("errorCode")] string? ErrorCode,
        [property: JsonPropertyName("result")] JsonElement? Result);
}
