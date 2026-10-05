// Phase 5 (plan §9.1): тонкий HTTP-форвардер от connector-job к Bridge.Desktop.
//
// Connector НЕ знает компонентов. Один универсальный handler для
// provider=tekla. Payload-контракт:
//
//   {
//     "endpoint": "/component/upsert" | "/component/modify" | "/component/delete"
//                 | "/component/read" | "/health" | "/capabilities" | "/pick/...",
//     "body":     <raw JSON object — шлётся as-is в Bridge.Desktop>,
//     "interactive": true|false,        // optional, default false; ↑ timeout cascade
//     "bridgeBaseUrl": "http://...",    // optional override (default 127.0.0.1:39421)
//     "tokenPath": "...",               // optional override (default DPAPI store)
//     "operationTimeoutSeconds": 120,   // optional override
//     "interactiveTimeoutSeconds": 210
//   }
//
// Старый file-based / probe-exe / commandText / pendingInsertPayload удалён
// полностью — никаких fallback'ов на legacy (deliberate per plan §9.1).

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Platform.Connector.Core;

namespace Platform.Cad.Tekla.Adapter;

[SupportedOSPlatform("windows")]
public sealed class TeklaProviderAdapter : IProviderAdapter
{
    public const string DefaultBridgeBaseUrl = "http://127.0.0.1:39421";

    /// <summary>Plan §9.2.1 — regular ops. Connector slot = 120s (Bridge=90, Server=150, Web=180).</summary>
    public const int DefaultOperationTimeoutSeconds = 120;

    /// <summary>Plan §9.2.1 — interactive (pick). Connector slot = 210s (Bridge=180, Server=240, Web=270).</summary>
    public const int DefaultInteractiveTimeoutSeconds = 210;

    private const string ProbeHealthPath = "/health";

    /// <summary>Total timeout для hard-restart Bridge.Desktop (Stop-Process + Start-ScheduledTask + wait /health).</summary>
    public static readonly TimeSpan RestartTimeout = TimeSpan.FromSeconds(30);

    private const string StaleErrorCode = "BRIDGE_STALE_TEKLA";

    private readonly Func<string?, string?> _tokenLoader;
    private readonly Func<string, HttpClient> _httpClientFactory;
    private readonly IBridgeDesktopRestarter _restarter;

    public TeklaProviderAdapter()
        : this(BridgeTokenReader.TryLoad, _ => new HttpClient(), new WindowsBridgeDesktopRestarter())
    {
    }

    /// <summary>Constructor для тестов: можно подменить token source, HttpClient factory и restarter.</summary>
    public TeklaProviderAdapter(
        Func<string?, string?> tokenLoader,
        Func<string, HttpClient> httpClientFactory,
        IBridgeDesktopRestarter? restarter = null)
    {
        _tokenLoader = tokenLoader;
        _httpClientFactory = httpClientFactory;
        _restarter = restarter ?? new NoopBridgeDesktopRestarter();
    }

    public CadProvider Provider => CadProvider.Tekla;

    public async Task<ProviderExecutionResult> ExecuteAsync(ConnectorJobEnvelope job, CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTime.UtcNow;
        var diagnosticsEnabled = TryGetBoolean(job.Payload, "diagnostics", out var diag) && diag;

        if (!TryGetString(job.Payload, "endpoint", out var endpoint))
        {
            return BuildFailure(job, startedAtUtc, "BRIDGE_PAYLOAD_INVALID",
                "payload.endpoint is required (e.g. \"/component/upsert\").", diagnosticsEnabled);
        }
        if (!endpoint.StartsWith("/", StringComparison.Ordinal))
        {
            return BuildFailure(job, startedAtUtc, "BRIDGE_PAYLOAD_INVALID",
                $"payload.endpoint must start with '/'; got '{endpoint}'.", diagnosticsEnabled);
        }

        var baseUrl = TryGetString(job.Payload, "bridgeBaseUrl", out var customBase)
            ? customBase.TrimEnd('/')
            : DefaultBridgeBaseUrl;

        var interactive = TryGetBoolean(job.Payload, "interactive", out var i) && i;
        var timeoutSec = ResolveTimeoutSeconds(job.Payload, interactive);

        var tokenPathOverride = TryGetString(job.Payload, "tokenPath", out var tp) ? tp : null;
        var token = _tokenLoader(tokenPathOverride);
        if (string.IsNullOrEmpty(token))
        {
            return BuildFailure(job, startedAtUtc, "BRIDGE_TOKEN_UNAVAILABLE",
                "%LOCALAPPDATA%\\Platform\\Bridge\\token.dat not found or unreadable. " +
                "Bridge.Desktop must run at least once on this Windows account to provision the token.",
                diagnosticsEnabled);
        }

        // Reachability probe — короткий timeout до /health. Рабочие операции
        // всегда восстанавливают Bridge. Именованный профиль Tekla 2020 также
        // поднимается по status/capabilities: он не обязан постоянно работать,
        // но проверка KMD обязана подключить именно его. Default-профиль остаётся
        // чистым read-only probe, чтобы общая фоновая проверка не запускала CAD.
        if (!await IsBridgeReachableAsync(baseUrl, cancellationToken))
        {
            var recovered = ShouldEnsureBridge(endpoint, baseUrl) &&
                            _restarter.IsAvailable &&
                            await _restarter.RestartAndWaitAsync(
                                baseUrl, _httpClientFactory, RestartTimeout, cancellationToken).ConfigureAwait(false);
            if (!recovered)
            {
                return BuildFailure(job, startedAtUtc, "BRIDGE_DESKTOP_UNREACHABLE",
                    $"Bridge.Desktop did not respond at {baseUrl}{ProbeHealthPath} within 5 seconds. " +
                    "Make sure Platform.Bridge.Desktop.Tekla.exe is running and Tekla is open.",
                    diagnosticsEnabled);
            }
        }

        var bodyText = TryGetRawObjectOrString(job.Payload, "body");

        // Attempt 1.
        var firstAttempt = await SendOnceAsync(job, startedAtUtc, baseUrl, endpoint, token,
            bodyText, timeoutSec, interactive, diagnosticsEnabled, cancellationToken).ConfigureAwait(false);

        // BRIDGE_STALE_TEKLA → Bridge.Desktop сам уже сделал in-process reconnect
        // и не помогло. Fallback: hard-restart Bridge.Desktop процесса и повтор.
        // Без участия пользователя / Claude / PowerShell.
        if (firstAttempt.ErrorCode != StaleErrorCode ||
            !_restarter.IsAvailable ||
            IsReadOnlyEndpoint(endpoint))
        {
            return firstAttempt;
        }
        var restarted = await _restarter.RestartAndWaitAsync(
            baseUrl, _httpClientFactory, RestartTimeout, cancellationToken).ConfigureAwait(false);
        if (!restarted)
        {
            return firstAttempt;
        }
        var secondAttempt = await SendOnceAsync(job, startedAtUtc, baseUrl, endpoint, token,
            bodyText, timeoutSec, interactive, diagnosticsEnabled, cancellationToken).ConfigureAwait(false);
        return secondAttempt;
    }

    private async Task<ProviderExecutionResult> SendOnceAsync(
        ConnectorJobEnvelope job, DateTime startedAtUtc,
        string baseUrl, string endpoint, string token, string bodyText,
        int timeoutSec, bool interactive, bool diagnosticsEnabled,
        CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        var requestUri = baseUrl + endpoint;

        try
        {
            using var http = _httpClientFactory(baseUrl);
            http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            var method = IsReadOnlyEndpoint(endpoint)
                ? HttpMethod.Get
                : HttpMethod.Post;
            using var request = new HttpRequestMessage(method, requestUri);
            request.Headers.Add("X-Bridge-Token", token);
            request.Headers.Add(
                "X-Bridge-Operation-Timeout-Seconds",
                timeoutSec.ToString(CultureInfo.InvariantCulture));
            if (method != HttpMethod.Get)
            {
                request.Content = new StringContent(bodyText, Encoding.UTF8, "application/json");
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, linkedCts.Token);
            var responseText = await response.Content.ReadAsStringAsync(linkedCts.Token);

            if (response.IsSuccessStatusCode)
            {
                return BuildSuccess(job, startedAtUtc, $"Bridge {endpoint} → {(int)response.StatusCode}.",
                    diagnosticsEnabled, response.StatusCode, responseText, baseUrl, endpoint, interactive);
            }

            var (errorCode, message) = ExtractErrorCode(responseText, response.StatusCode);
            return BuildFailure(job, startedAtUtc, errorCode, message, diagnosticsEnabled,
                response.StatusCode, responseText, baseUrl, endpoint, interactive);
        }
        catch (TaskCanceledException) when (linkedCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            var code = interactive ? "BRIDGE_HTTP_INTERACTIVE_TIMEOUT" : "BRIDGE_HTTP_TIMEOUT";
            return BuildFailure(job, startedAtUtc, code,
                $"Bridge call to {requestUri} timed out after {timeoutSec}s.",
                diagnosticsEnabled, statusCode: null, baseUrl: baseUrl, endpoint: endpoint, interactive: interactive);
        }
        catch (HttpRequestException ex)
        {
            return BuildFailure(job, startedAtUtc, "BRIDGE_HTTP_ERROR",
                $"HTTP error calling {requestUri}: {ex.Message}",
                diagnosticsEnabled, statusCode: null, baseUrl: baseUrl, endpoint: endpoint, interactive: interactive);
        }
        catch (Exception ex)
        {
            return BuildFailure(job, startedAtUtc, "BRIDGE_UNEXPECTED_ERROR",
                $"Unexpected error: {ex.GetType().Name}: {ex.Message}",
                diagnosticsEnabled, statusCode: null, baseUrl: baseUrl, endpoint: endpoint, interactive: interactive);
        }
    }

    private static bool IsReadOnlyEndpoint(string endpoint)
    {
        return string.Equals(endpoint, ProbeHealthPath, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(endpoint, "/capabilities", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldEnsureBridge(string endpoint, string baseUrl)
    {
        if (!IsReadOnlyEndpoint(endpoint)) return true;
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
               uri.IsLoopback &&
               !IsDefaultBridgeBaseUrl(baseUrl);
    }

    private static bool IsDefaultBridgeBaseUrl(string baseUrl)
        => string.Equals(
            baseUrl.TrimEnd('/'),
            DefaultBridgeBaseUrl,
            StringComparison.OrdinalIgnoreCase);

    private async Task<bool> IsBridgeReachableAsync(string baseUrl, CancellationToken outerCt)
    {
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            probeCts.CancelAfter(TimeSpan.FromSeconds(5));
            using var http = _httpClientFactory(baseUrl);
            using var resp = await http.GetAsync(baseUrl + ProbeHealthPath, probeCts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static int ResolveTimeoutSeconds(JsonElement payload, bool interactive)
    {
        var key = interactive ? "interactiveTimeoutSeconds" : "operationTimeoutSeconds";
        var fallback = interactive ? DefaultInteractiveTimeoutSeconds : DefaultOperationTimeoutSeconds;

        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty(key, out var p) &&
            p.ValueKind == JsonValueKind.Number &&
            p.TryGetInt32(out var v) && v >= 1)
        {
            return Math.Min(v, 86_400);
        }
        return fallback;
    }

    private static bool TryGetString(JsonElement payload, string key, out string value)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty(key, out var p) &&
            p.ValueKind == JsonValueKind.String)
        {
            value = p.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }
        value = string.Empty;
        return false;
    }

    private static bool TryGetBoolean(JsonElement payload, string key, out bool value)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty(key, out var p) &&
            (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False))
        {
            value = p.GetBoolean();
            return true;
        }
        value = false;
        return false;
    }

    /// <summary>
    /// Возвращает body как plain JSON-text. Поддерживает:
    /// (1) "body": { ... } — JSON object → GetRawText()
    /// (2) "body": "...escaped json..." — string-encoded JSON
    /// (3) отсутствует / null — "{}" (поведение для /health-style требований).
    /// </summary>
    private static string TryGetRawObjectOrString(JsonElement payload, string key)
    {
        if (payload.ValueKind != JsonValueKind.Object) return "{}";
        if (!payload.TryGetProperty(key, out var p)) return "{}";
        return p.ValueKind switch
        {
            JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.True or JsonValueKind.False
                or JsonValueKind.Number => p.GetRawText(),
            JsonValueKind.String => p.GetString() ?? "{}",
            _ => "{}",
        };
    }

    private static (string Code, string Message) ExtractErrorCode(string responseText, HttpStatusCode statusCode)
    {
        if (!string.IsNullOrWhiteSpace(responseText))
        {
            try
            {
                using var doc = JsonDocument.Parse(responseText);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var code = doc.RootElement.TryGetProperty("errorCode", out var ec) && ec.ValueKind == JsonValueKind.String
                        ? ec.GetString()
                        : null;
                    var msg = doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                        ? m.GetString()
                        : null;
                    if (!string.IsNullOrEmpty(code))
                        return (code!, msg ?? $"Bridge HTTP {(int)statusCode}");
                }
            }
            catch
            {
                // not JSON — fall through to status-code based code
            }
        }
        return ((int)statusCode) switch
        {
            401 => ("BRIDGE_UNAUTHORIZED", "Bridge.Desktop rejected X-Bridge-Token."),
            404 => ("BRIDGE_ROUTE_NOT_FOUND", $"Bridge.Desktop has no route for the requested endpoint."),
            408 => ("BRIDGE_HTTP_TIMEOUT", "Bridge.Desktop reported a timeout."),
            >= 500 => ("BRIDGE_INTERNAL_ERROR", $"Bridge.Desktop returned HTTP {(int)statusCode}."),
            _ => ("BRIDGE_HTTP_ERROR", $"Bridge.Desktop returned HTTP {(int)statusCode}."),
        };
    }

    private static ProviderExecutionResult BuildSuccess(
        ConnectorJobEnvelope job, DateTime startedAtUtc, string message, bool diagnosticsEnabled,
        HttpStatusCode statusCode, string responseText, string baseUrl, string endpoint, bool interactive)
    {
        var resultElement = ParseResponseAsJson(responseText);
        using var runtimeResult = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            provider = "tekla",
            requestId = job.RequestId,
            operation = job.Operation.ToString(),
            mode = "bridge-http",
            interactive,
            bridge = new { baseUrl, endpoint, statusCode = (int)statusCode },
            response = resultElement,
            diagnostics = diagnosticsEnabled ? BuildDiag(startedAtUtc) : null,
        }));
        return new ProviderExecutionResult(
            IsSuccess: true,
            Message: message,
            Result: runtimeResult.RootElement.Clone());
    }

    private static ProviderExecutionResult BuildFailure(
        ConnectorJobEnvelope job, DateTime startedAtUtc, string errorCode, string message,
        bool diagnosticsEnabled, HttpStatusCode? statusCode = null, string? responseText = null,
        string? baseUrl = null, string? endpoint = null, bool interactive = false)
    {
        using var runtimeResult = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            provider = "tekla",
            requestId = job.RequestId,
            operation = job.Operation.ToString(),
            mode = "bridge-http",
            interactive,
            bridge = new { baseUrl, endpoint, statusCode = statusCode.HasValue ? (int?)(int)statusCode.Value : null },
            response = responseText is null ? null : ParseResponseAsJson(responseText),
            diagnostics = diagnosticsEnabled ? BuildDiag(startedAtUtc) : null,
        }));
        return new ProviderExecutionResult(
            IsSuccess: false,
            Message: message,
            ErrorCode: errorCode,
            Result: runtimeResult.RootElement.Clone());
    }

    private static object? ParseResponseAsJson(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText)) return null;
        try
        {
            using var doc = JsonDocument.Parse(responseText);
            return doc.RootElement.Clone();
        }
        catch
        {
            return responseText; // non-JSON body — return as raw string
        }
    }

    private static object BuildDiag(DateTime startedAtUtc)
    {
        var now = DateTime.UtcNow;
        return new
        {
            startedAtUtc,
            finishedAtUtc = now,
            durationMs = (now - startedAtUtc).TotalMilliseconds,
        };
    }
}
