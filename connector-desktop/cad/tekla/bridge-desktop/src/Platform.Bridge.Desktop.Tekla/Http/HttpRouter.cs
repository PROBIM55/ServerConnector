// Простой роутинг для встроенного HttpListener. Маршруты регистрируются
// на старте; каждый handler — async с context'ом. Auth-проверка X-Bridge-Token
// делается per-route (Health = public, Capabilities + component = authed).

#nullable enable

using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Platform.Bridge.Desktop.Tekla.Auth;
using Platform.Bridge.Desktop.Tekla.Logging;

namespace Platform.Bridge.Desktop.Tekla.Http
{
    public sealed class HttpRouter
    {
        private const string OperationTimeoutHeader = "X-Bridge-Operation-Timeout-Seconds";
        private readonly Dictionary<string, RouteHandler> _routes = new(StringComparer.Ordinal);
        private readonly JsonLineLogger _log;
        private readonly string _expectedToken;

        public HttpRouter(JsonLineLogger log, string expectedToken)
        {
            _log = log;
            _expectedToken = expectedToken;
        }

        public void Register(string method, string path, bool requiresAuth,
            Func<RequestContext, CancellationToken, Task<HttpResult>> handler)
        {
            _routes[Key(method, path)] = new RouteHandler(requiresAuth, handler);
        }

        public async Task DispatchAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            var method = ctx.Request.HttpMethod;
            var routeKey = Key(method, path);
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var operationTimeoutSeconds = ReadOperationTimeoutSeconds(ctx.Request);
            if (operationTimeoutSeconds.HasValue)
            {
                var safetyMargin = Math.Min(10, Math.Max(1, operationTimeoutSeconds.Value / 10));
                requestCts.CancelAfter(TimeSpan.FromSeconds(
                    Math.Max(1, operationTimeoutSeconds.Value - safetyMargin)));
            }
            try
            {
                if (!_routes.TryGetValue(routeKey, out var route))
                {
                    await WriteJson(ctx, 404, new { ok = false, errorCode = "NOT_FOUND", message = $"No route for {method} {path}" });
                    return;
                }

                if (route.RequiresAuth)
                {
                    var presented = ctx.Request.Headers["X-Bridge-Token"] ?? "";
                    if (!BridgeTokenStore.Verify(presented, _expectedToken))
                    {
                        await WriteJson(ctx, 401, new { ok = false, errorCode = "UNAUTHORIZED", message = "Invalid or missing X-Bridge-Token." });
                        return;
                    }
                }

                var requestCtx = new RequestContext(ctx);
                var result = await route.Handler(requestCtx, requestCts.Token);
                await WriteJson(ctx, result.StatusCode, result.Body);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && requestCts.IsCancellationRequested)
            {
                try
                {
                    await WriteJson(ctx, 408, new
                    {
                        ok = false,
                        errorCode = "BRIDGE_OPERATION_TIMEOUT",
                        message = operationTimeoutSeconds.HasValue
                            ? $"Bridge operation exceeded its {operationTimeoutSeconds.Value}s budget."
                            : "Bridge operation was cancelled."
                    });
                }
                catch { /* client may already have disconnected */ }
            }
            catch (Exception ex)
            {
                _log.Error("http.unhandled", new { route = routeKey, err = ex.Message, type = ex.GetType().Name });
                try
                {
                    await WriteJson(ctx, 500, new { ok = false, errorCode = "INTERNAL_ERROR", message = ex.Message });
                }
                catch { /* response already disposed */ }
            }
            finally
            {
                _log.Info("http.request", new { method, path, ms = sw.ElapsedMilliseconds, status = (int)ctx.Response.StatusCode });
            }
        }

        private static int? ReadOperationTimeoutSeconds(HttpListenerRequest request)
        {
            var raw = request.Headers[OperationTimeoutHeader];
            if (!int.TryParse(raw, out var value) || value < 1) return null;
            return Math.Min(value, 86_400);
        }

        private static string Key(string method, string path) => method.ToUpperInvariant() + " " + path;

        public static async Task WriteJson(HttpListenerContext ctx, int statusCode, object body)
        {
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            var json = JsonSerializer.Serialize(body, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }

        private readonly struct RouteHandler
        {
            public bool RequiresAuth { get; }
            public Func<RequestContext, CancellationToken, Task<HttpResult>> Handler { get; }
            public RouteHandler(bool requiresAuth, Func<RequestContext, CancellationToken, Task<HttpResult>> handler)
            { RequiresAuth = requiresAuth; Handler = handler; }
        }
    }

    public sealed class RequestContext
    {
        public HttpListenerContext Inner { get; }
        public RequestContext(HttpListenerContext inner) { Inner = inner; }

        public async Task<string> ReadStringAsync()
        {
            using var reader = new System.IO.StreamReader(Inner.Request.InputStream, Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }

        public async Task<TBody?> ReadJsonAsync<TBody>(JsonSerializerOptions? options = null) where TBody : class
        {
            var raw = await ReadStringAsync();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            options ??= new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<TBody>(raw, options);
        }
    }

    public sealed class HttpResult
    {
        public int StatusCode { get; }
        public object Body { get; }
        public HttpResult(int statusCode, object body) { StatusCode = statusCode; Body = body; }

        public static HttpResult Ok(object body) => new(200, body);
        public static HttpResult BadRequest(string errorCode, string message)
            => new(400, new { ok = false, errorCode, message });
        public static HttpResult NotFound(string errorCode, string message)
            => new(404, new { ok = false, errorCode, message });
        public static HttpResult Conflict(string errorCode, string message, object? extras = null)
            => new(409, new { ok = false, errorCode, message, extras });
        public static HttpResult ServerError(string errorCode, string message)
            => new(500, new { ok = false, errorCode, message });
        public static HttpResult ServiceUnavailable(string errorCode, string message)
            => new(503, new { ok = false, errorCode, message });
    }
}
