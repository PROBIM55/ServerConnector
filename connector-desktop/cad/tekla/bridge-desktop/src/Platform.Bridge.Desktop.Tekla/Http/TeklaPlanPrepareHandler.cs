#nullable enable

using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Platform.Bridge.Desktop.Tekla.ConstructiveRuntime;
using Platform.Contracts.TeklaPlan;

namespace Platform.Bridge.Desktop.Tekla.Http
{
    public sealed class TeklaPlanPrepareHandler
    {
        private const int MaxBodyBytes = 16 * 1024 * 1024;
        private readonly TeklaNativeCapabilityProvider _capabilities;

        public TeklaPlanPrepareHandler(TeklaNativeCapabilityProvider capabilities)
        {
            _capabilities = capabilities;
        }

        public async Task<HttpResult> HandleAsync(RequestContext request, CancellationToken ct)
        {
            string raw;
            try
            {
                raw = await request.ReadStringAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return HttpResult.BadRequest("TEKLA_PLAN_BODY_READ_FAILED", ex.Message);
            }

            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(raw))
                return HttpResult.BadRequest("TEKLA_PLAN_BODY_MISSING", "TeklaPlan request body is required.");
            if (Encoding.UTF8.GetByteCount(raw) > MaxBodyBytes)
                return new HttpResult(413, new
                {
                    ok = false,
                    errorCode = "TEKLA_PLAN_BODY_TOO_LARGE",
                    message = $"TeklaPlan body cannot exceed {MaxBodyBytes} bytes.",
                });

            TeklaPlanDocument plan;
            try
            {
                using var json = JsonDocument.Parse(raw);
                if (TeklaPlanValidator.ContainsForbiddenGeometry(json.RootElement))
                {
                    return HttpResult.BadRequest(
                        "TEKLA_PLAN_RENDER_GEOMETRY_FORBIDDEN",
                        "TeklaPlan cannot contain mesh, BRep, triangle or inline render geometry.");
                }
                plan = TeklaPlanJson.Deserialize(raw);
            }
            catch (JsonException ex)
            {
                return HttpResult.BadRequest("TEKLA_PLAN_JSON_INVALID", ex.Message);
            }

            var preparation = TeklaPlanPreparer.Prepare(
                plan,
                _capabilities.Runtime,
                _capabilities.Executors);
            if (!preparation.Prepared)
            {
                return new HttpResult(422, new
                {
                    ok = false,
                    prepared = false,
                    errorCode = "TEKLA_PLAN_VALIDATION_FAILED",
                    diagnostics = preparation.Diagnostics,
                });
            }

            return HttpResult.Ok(new
            {
                ok = true,
                prepared = true,
                schemaVersion = plan.SchemaVersion,
                plan.Source.SourceHash,
                plan.Source.ConstructiveContentHash,
                commandCount = plan.Commands.Length,
                executionOrder = preparation.Bindings.Select(static item => new
                {
                    item.CommandId,
                    item.CommandKind,
                    item.ExecutorId,
                }).ToArray(),
                diagnostics = Array.Empty<object>(),
            });
        }
    }
}
