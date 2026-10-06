#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Platform.Bridge.Desktop.Tekla.ConstructiveRuntime;
using Platform.Bridge.Desktop.Tekla.Tekla;
using Platform.Contracts.TeklaPlan;

namespace Platform.Bridge.Desktop.Tekla.Http
{
    public sealed class TeklaPlanApplyHandler
    {
        private const int MaxBodyBytes = 16 * 1024 * 1024;
        private readonly TeklaWorker _worker;
        private readonly string _teklaVersion;
        private readonly TeklaNativeCapabilityProvider _capabilities;

        public TeklaPlanApplyHandler(
            TeklaWorker worker,
            string teklaVersion,
            TeklaNativeCapabilityProvider capabilities)
        {
            _worker = worker;
            _teklaVersion = teklaVersion;
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
                return HttpResult.BadRequest("TEKLA_PLAN_BODY_MISSING", "TeklaPlan apply request body is required.");
            if (Encoding.UTF8.GetByteCount(raw) > MaxBodyBytes)
            {
                return new HttpResult(413, new
                {
                    ok = false,
                    errorCode = "TEKLA_PLAN_BODY_TOO_LARGE",
                    message = $"TeklaPlan body cannot exceed {MaxBodyBytes} bytes.",
                });
            }

            TeklaPlanApplyRequest? body;
            try
            {
                using var json = JsonDocument.Parse(raw);
                if (TeklaPlanValidator.ContainsForbiddenGeometry(json.RootElement))
                {
                    return HttpResult.BadRequest(
                        "TEKLA_PLAN_RENDER_GEOMETRY_FORBIDDEN",
                        "TeklaPlan cannot contain mesh, BRep, triangle or inline render geometry.");
                }
                body = JsonSerializer.Deserialize<TeklaPlanApplyRequest>(raw, TeklaPlanJson.Options);
            }
            catch (JsonException ex)
            {
                return HttpResult.BadRequest("TEKLA_PLAN_JSON_INVALID", ex.Message);
            }

            var requestError = ValidateRequest(body);
            if (requestError is not null) return requestError;

            var preparation = TeklaPlanPreparer.Prepare(
                body!.Plan!,
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

            try
            {
                var result = await _worker.RunAsync(model =>
                {
                    var identity = TeklaModelIdentity.Capture(model, _teklaVersion);
                    identity.RequireMatch(
                        body.ModelFingerprint!,
                        body.ExpectedTeklaVersion!,
                        body.ExpectedModelName!,
                        body.ExpectedModelPath!);

                    var mutations = new List<ITeklaPreparedMutation<TeklaNativeCommandReadback>>(
                        body.Plan!.Commands.Length);
                    foreach (var command in body.Plan.Commands)
                    {
                        ct.ThrowIfCancellationRequested();
                        mutations.Add(_capabilities.NativeExecutors.Prepare(
                            model,
                            body.Plan,
                            command,
                            body.OperationId!));
                    }

                    ct.ThrowIfCancellationRequested();
                    return TeklaPlanTransaction.Execute(
                        mutations,
                        () =>
                        {
                            if (!model.CommitChanges())
                                throw new TeklaNativeExecutionException(
                                    "TEKLA_PLAN_COMMIT_FAILED",
                                    "Tekla CommitChanges() returned false.");
                        });
                }, ct);

                return HttpResult.Ok(new
                {
                    ok = true,
                    applied = true,
                    operationId = body.OperationId,
                    modelFingerprint = body.ModelFingerprint,
                    sourceHash = body.Plan!.Source.SourceHash,
                    constructiveContentHash = body.Plan.Source.ConstructiveContentHash,
                    commandCount = result.Count,
                    items = result,
                });
            }
            catch (TeklaDisconnectedException ex)
            {
                return HttpResult.ServiceUnavailable("TEKLA_DISCONNECTED", ex.Message);
            }
            catch (TeklaStaleHandleException)
            {
                return HttpResult.ServiceUnavailable(
                    "BRIDGE_STALE_TEKLA",
                    "Tekla remote references became stale; reconnect failed.");
            }
            catch (TeklaPlanRollbackException ex)
            {
                return HttpResult.ServerError(
                    "TEKLA_PLAN_ROLLBACK_FAILED",
                    $"{ex.TransactionError.GetType().Name}: {ex.TransactionError.Message}; " +
                    $"rollback errors: {ex.RollbackErrors.Count}.");
            }
            catch (TeklaNativeExecutionException ex)
            {
                return HttpResult.Conflict(ex.ErrorCode, ex.Message);
            }
            catch (Exception ex)
            {
                return HttpResult.ServerError("TEKLA_PLAN_APPLY_FAILED", $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static HttpResult? ValidateRequest(TeklaPlanApplyRequest? request)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.OperationId))
                return HttpResult.BadRequest("TEKLA_PLAN_OPERATION_ID_MISSING", "operationId is required.");
            if (string.IsNullOrWhiteSpace(request.ModelFingerprint) ||
                string.IsNullOrWhiteSpace(request.ExpectedTeklaVersion) ||
                string.IsNullOrWhiteSpace(request.ExpectedModelName) ||
                string.IsNullOrWhiteSpace(request.ExpectedModelPath))
            {
                return HttpResult.BadRequest(
                    "TEKLA_PLAN_MODEL_IDENTITY_MISSING",
                    "modelFingerprint, expectedTeklaVersion, expectedModelName and expectedModelPath are required.");
            }
            if (request.Plan is null)
                return HttpResult.BadRequest("TEKLA_PLAN_MISSING", "plan is required.");
            if (request.Plan.Commands is null || request.Plan.Commands.Length == 0)
                return HttpResult.BadRequest("TEKLA_PLAN_COMMANDS_MISSING", "plan.commands must contain at least one command.");
            return null;
        }

        private sealed class TeklaPlanApplyRequest
        {
            public string? OperationId { get; set; }
            public string? ModelFingerprint { get; set; }
            public string? ExpectedTeklaVersion { get; set; }
            public string? ExpectedModelName { get; set; }
            public string? ExpectedModelPath { get; set; }
            public TeklaPlanDocument? Plan { get; set; }
        }
    }
}
