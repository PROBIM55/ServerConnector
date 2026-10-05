// POST /component/upsert | /component/modify | /component/delete | /component/read
// Точка входа от Connector'а. Обрабатывает: idempotency-replay, payload-hash
// конфликт, ObjectMap pre-fill (externalObjectId → guid), schema validation,
// dispatch на TeklaWorker, persist результата в ObjectMap+IdempotencyStore.
//
// Все Tekla-вызовы идут через TeklaWorker.RunAsync — единственный STA-thread.
// HTTP-обработчик никогда не трогает Tekla.Model напрямую.

#nullable enable

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Platform.Bridge.Desktop.Tekla.ComponentRuntime;
using Platform.Bridge.Desktop.Tekla.Idempotency;
using Platform.Bridge.Desktop.Tekla.Logging;
using Platform.Bridge.Desktop.Tekla.Tekla;

namespace Platform.Bridge.Desktop.Tekla.Http
{
    public sealed class ComponentHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly ComponentAdapterRegistry _registry;
        private readonly TeklaWorker _worker;
        private readonly ObjectMap _objectMap;
        private readonly IdempotencyStore _idempotency;
        private readonly JsonLineLogger _log;

        public ComponentHandler(ComponentAdapterRegistry registry, TeklaWorker worker,
            ObjectMap objectMap, IdempotencyStore idempotency, JsonLineLogger log)
        {
            _registry = registry;
            _worker = worker;
            _objectMap = objectMap;
            _idempotency = idempotency;
            _log = log;
        }

        // ───────── routes ─────────

        public Task<HttpResult> UpsertAsync(RequestContext ctx, CancellationToken ct)
            => RunWriteAsync(ctx, "upsert", ct);

        public Task<HttpResult> ModifyAsync(RequestContext ctx, CancellationToken ct)
            => RunWriteAsync(ctx, "modify", ct);

        public Task<HttpResult> DeleteAsync(RequestContext ctx, CancellationToken ct)
            => RunWriteAsync(ctx, "delete", ct);

        public async Task<HttpResult> ReadAsync(RequestContext ctx, CancellationToken ct)
        {
            ComponentOperationRequest? req;
            try { req = JsonSerializer.Deserialize<ComponentOperationRequest>(await ctx.ReadStringAsync(), JsonOptions); }
            catch (JsonException ex) { return HttpResult.BadRequest("INVALID_JSON", ex.Message); }

            if (req is null) return HttpResult.BadRequest("EMPTY_BODY", "Request body required.");
            req.Operation = "read";

            if (string.IsNullOrEmpty(req.ComponentType) || req.SchemaVersion <= 0)
                return HttpResult.BadRequest("MISSING_COMPONENT_KEY", "componentType and schemaVersion required.");

            if (!_registry.TryResolve(req.ComponentType, req.SchemaVersion, out var adapter))
                return HttpResult.BadRequest("COMPONENT_TYPE_NOT_SUPPORTED",
                    $"No adapter for ({req.ComponentType} v{req.SchemaVersion}).");

            // Lookup приоритет: target.guid → target.id → ObjectMap by externalObjectId
            var objRef = ResolveObjectRef(req);
            if (!objRef.HasGuid && objRef.Id is null)
                return HttpResult.BadRequest("MISSING_TARGET",
                    "read requires target.teklaComponentGuid, target.teklaComponentId or known externalObjectId.");

            try
            {
                var result = await _worker.RunAsync(model => adapter.Read(model, objRef, ct), ct);
                if (!result.Ok)
                    return MapReadFailure(result);
                return HttpResult.Ok(result);
            }
            catch (TeklaDisconnectedException ex)
            {
                return HttpResult.ServerError("TEKLA_DISCONNECTED", ex.Message);
            }
            catch (TeklaStaleHandleException ex)
            {
                _log.Warn("component.read.stale", new { inner = ex.InnerException?.GetType().Name });
                return HttpResult.ServiceUnavailable("BRIDGE_STALE_TEKLA",
                    "Tekla remote references became stale after Tekla restart; in-process reconnect failed. Connector should hard-restart Bridge.Desktop.");
            }
            catch (Exception ex)
            {
                _log.Error("component.read.unhandled", new { err = ex.Message, type = ex.GetType().Name });
                return HttpResult.ServerError("INTERNAL_ERROR", ex.Message);
            }
        }

        // ───────── core write pipeline ─────────

        private async Task<HttpResult> RunWriteAsync(RequestContext ctx, string operation, CancellationToken ct)
        {
            string raw;
            try { raw = await ctx.ReadStringAsync(); }
            catch (Exception ex) { return HttpResult.BadRequest("BODY_READ_FAILED", ex.Message); }
            if (string.IsNullOrWhiteSpace(raw))
                return HttpResult.BadRequest("EMPTY_BODY", "Request body required.");

            ComponentOperationRequest? req;
            try { req = JsonSerializer.Deserialize<ComponentOperationRequest>(raw, JsonOptions); }
            catch (JsonException ex) { return HttpResult.BadRequest("INVALID_JSON", ex.Message); }
            if (req is null) return HttpResult.BadRequest("EMPTY_BODY", "Request body required.");

            req.Operation = operation;
            if (string.IsNullOrEmpty(req.ComponentType) || req.SchemaVersion <= 0)
                return HttpResult.BadRequest("MISSING_COMPONENT_KEY", "componentType and schemaVersion required.");
            if (string.IsNullOrEmpty(req.ExternalObjectId))
                return HttpResult.BadRequest("MISSING_EXTERNAL_OBJECT_ID", "externalObjectId required.");
            if (string.IsNullOrEmpty(req.IdempotencyKey))
                return HttpResult.BadRequest("MISSING_IDEMPOTENCY_KEY", "idempotencyKey required.");

            if (!_registry.TryResolve(req.ComponentType, req.SchemaVersion, out var adapter))
                return HttpResult.BadRequest("COMPONENT_TYPE_NOT_SUPPORTED",
                    $"No adapter for ({req.ComponentType} v{req.SchemaVersion}).");

            // ─── Idempotency replay / conflict ───
            var payloadHash = IdempotencyStore.HashPayload(raw);
            var prior = _idempotency.FindByKey(req.IdempotencyKey);
            if (prior is not null)
            {
                if (prior.Status == IdempotencyStatus.Success)
                {
                    if (string.Equals(prior.PayloadHash, payloadHash, StringComparison.Ordinal))
                    {
                        _log.Info("component.idempotent.replay", new { req.IdempotencyKey, operation });
                        if (!string.IsNullOrEmpty(prior.ResponseBody))
                        {
                            var replayed = JsonSerializer.Deserialize<JsonElement>(prior.ResponseBody!);
                            return HttpResult.Ok(replayed);
                        }
                        return HttpResult.Ok(new
                        {
                            ok = true,
                            replayed = true,
                            operation,
                            externalObjectId = req.ExternalObjectId,
                            teklaComponentGuid = prior.TeklaComponentGuid,
                            teklaComponentId = prior.TeklaComponentId,
                        });
                    }
                    return HttpResult.Conflict("IDEMPOTENCY_CONFLICT",
                        "idempotencyKey was already used with a different payload.",
                        new { idempotencyKey = req.IdempotencyKey });
                }
                // Failed/Partial: re-execute (адаптер сам повторно найдёт уже-вставленный
                // объект по STRUCTURA_EXTERNAL_OBJECT_ID UDA scan).
            }

            // ─── ObjectMap pre-fill ─── (если target пустой, но мы знаем guid)
            var existingMap = _objectMap.FindByExternalId(req.ExternalObjectId, req.ComponentType);
            if (existingMap is not null)
            {
                req.Target ??= new ComponentTarget();
                if (string.IsNullOrEmpty(req.Target.TeklaComponentGuid))
                    req.Target.TeklaComponentGuid = existingMap.TeklaComponentGuid;
                if (req.Target.TeklaComponentId is null)
                    req.Target.TeklaComponentId = existingMap.TeklaComponentId;
            }

            // ─── Schema validation ─── (для delete параметры можно не требовать —
            // адаптер сам игнорирует через schema-driven mapping, но required-only
            // поля он всё равно проверит. Для delete пропускаем full schema check.)
            if (operation != "delete")
            {
                var validation = adapter.Validate(req);
                if (!validation.Ok)
                {
                    return new HttpResult(400, new
                    {
                        ok = false,
                        errorCode = "INVALID_COMPONENT_PARAMETERS",
                        message = "Schema validation failed.",
                        errors = validation.Errors,
                    });
                }
            }

            // ─── Dispatch на TeklaWorker ───
            TeklaOperationResult result;
            try
            {
                result = await _worker.RunAsync<TeklaOperationResult>(model =>
                {
                    return operation switch
                    {
                        "upsert" => adapter.Upsert(model, req, ct),
                        "modify" => adapter.Modify(model, req, ct),
                        "delete" => adapter.Delete(model, req, ct),
                        _        => throw new InvalidOperationException($"unknown op {operation}"),
                    };
                }, ct);
            }
            catch (TeklaDisconnectedException ex)
            {
                _idempotency.Append(new IdempotencyRecord
                {
                    IdempotencyKey = req.IdempotencyKey,
                    Operation = operation,
                    ComponentType = req.ComponentType,
                    SchemaVersion = req.SchemaVersion,
                    ExternalObjectId = req.ExternalObjectId,
                    PayloadHash = payloadHash,
                    Status = IdempotencyStatus.Failed,
                });
                return HttpResult.ServerError("TEKLA_DISCONNECTED", ex.Message);
            }
            catch (TeklaStaleHandleException ex)
            {
                _idempotency.Append(new IdempotencyRecord
                {
                    IdempotencyKey = req.IdempotencyKey,
                    Operation = operation,
                    ComponentType = req.ComponentType,
                    SchemaVersion = req.SchemaVersion,
                    ExternalObjectId = req.ExternalObjectId,
                    PayloadHash = payloadHash,
                    Status = IdempotencyStatus.Failed,
                });
                _log.Warn("component.write.stale", new { operation, req.ComponentType, inner = ex.InnerException?.GetType().Name });
                return HttpResult.ServiceUnavailable("BRIDGE_STALE_TEKLA",
                    "Tekla remote references became stale after Tekla restart; in-process reconnect failed. Connector should hard-restart Bridge.Desktop.");
            }
            catch (Exception ex)
            {
                // Unwrap AggregateException — иначе message="Произошла одна или несколько
                // ошибок" без какой-либо инфы о реальной причине. Также сохраняем stack
                // и chain inner exceptions: TeklaWorker.RunAsync может обернуть exception
                // адаптера в AggregateException или TargetInvocationException.
                var (innerType, innerMessage, fullChain) = UnwrapException(ex);
                _log.Error("component.write.unhandled", new
                {
                    operation,
                    req.ComponentType,
                    err = innerMessage,
                    type = innerType,
                    outerType = ex.GetType().Name,
                    stack = ex.ToString(),
                    chain = fullChain,
                });
                _idempotency.Append(new IdempotencyRecord
                {
                    IdempotencyKey = req.IdempotencyKey,
                    Operation = operation,
                    ComponentType = req.ComponentType,
                    SchemaVersion = req.SchemaVersion,
                    ExternalObjectId = req.ExternalObjectId,
                    PayloadHash = payloadHash,
                    Status = IdempotencyStatus.Failed,
                });
                return HttpResult.ServerError("INTERNAL_ERROR", $"{innerType}: {innerMessage}");
            }

            // ─── Persist + return ───
            if (result.Ok)
            {
                if (operation == "delete")
                {
                    _objectMap.Remove(req.ExternalObjectId, req.ComponentType);
                }
                else if (!string.IsNullOrEmpty(result.TeklaComponentGuid))
                {
                    _objectMap.Upsert(new ObjectMapEntry
                    {
                        ExternalObjectId = req.ExternalObjectId,
                        ComponentType = req.ComponentType,
                        SchemaVersion = req.SchemaVersion,
                        TeklaComponentGuid = result.TeklaComponentGuid!,
                        TeklaComponentId = result.TeklaComponentId,
                        LastOperationId = req.IdempotencyKey,
                        LastAppliedAtUtc = DateTime.UtcNow,
                    });
                }

                var responseJson = JsonSerializer.Serialize(result, JsonOptions);
                _idempotency.Append(new IdempotencyRecord
                {
                    IdempotencyKey = req.IdempotencyKey,
                    Operation = operation,
                    ComponentType = req.ComponentType,
                    SchemaVersion = req.SchemaVersion,
                    ExternalObjectId = req.ExternalObjectId,
                    PayloadHash = payloadHash,
                    Status = IdempotencyStatus.Success,
                    TeklaComponentGuid = result.TeklaComponentGuid,
                    TeklaComponentId = result.TeklaComponentId,
                    ResponseBody = responseJson,
                });
                return HttpResult.Ok(result);
            }
            else
            {
                _idempotency.Append(new IdempotencyRecord
                {
                    IdempotencyKey = req.IdempotencyKey,
                    Operation = operation,
                    ComponentType = req.ComponentType,
                    SchemaVersion = req.SchemaVersion,
                    ExternalObjectId = req.ExternalObjectId,
                    PayloadHash = payloadHash,
                    Status = IdempotencyStatus.Failed,
                });
                return MapWriteFailure(result);
            }
        }

        private TeklaObjectRef ResolveObjectRef(ComponentOperationRequest req)
        {
            if (!string.IsNullOrEmpty(req.Target?.TeklaComponentGuid))
                return TeklaObjectRef.FromGuid(req.Target!.TeklaComponentGuid!);
            if (req.Target?.TeklaComponentId is int id && id > 0)
                return TeklaObjectRef.FromId(id);
            if (!string.IsNullOrEmpty(req.ExternalObjectId))
            {
                var entry = _objectMap.FindByExternalId(req.ExternalObjectId, req.ComponentType);
                if (entry is not null)
                {
                    var r = TeklaObjectRef.FromGuid(entry.TeklaComponentGuid);
                    r.Id = entry.TeklaComponentId;
                    return r;
                }
            }
            return new TeklaObjectRef();
        }

        private static HttpResult MapWriteFailure(TeklaOperationResult result)
        {
            var status = result.ErrorCode switch
            {
                "TEKLA_OBJECT_NOT_FOUND" => 404,
                "TEKLA_DISCONNECTED"     => 503,
                _                        => 500,
            };
            return new HttpResult(status, new
            {
                ok = false,
                errorCode = result.ErrorCode ?? "INTERNAL_ERROR",
                message = result.Message ?? "",
                operation = result.Operation,
                externalObjectId = result.ExternalObjectId,
                durationMs = result.DurationMs,
            });
        }

        private static HttpResult MapReadFailure(TeklaReadResult result)
        {
            var status = result.ErrorCode switch
            {
                "TEKLA_OBJECT_NOT_FOUND" => 404,
                "TEKLA_DISCONNECTED"     => 503,
                _                        => 500,
            };
            return new HttpResult(status, new
            {
                ok = false,
                errorCode = result.ErrorCode ?? "INTERNAL_ERROR",
                message = result.Message ?? "",
                durationMs = result.DurationMs,
            });
        }

        // Разворачивает AggregateException / TargetInvocationException до самой
        // нижней innerException и собирает chain «outer → inner → ...» для лога.
        // Возвращает (innermostType, innermostMessage, chainString).
        private static (string type, string message, string chain) UnwrapException(Exception ex)
        {
            var chain = new List<string>();
            Exception current = ex;
            // Hard cap вместо identity-set: net48 не выставляет ReferenceEqualityComparer
            // как public. 8 уровней с запасом — типичный stack 1-2.
            for (var depth = 0; depth < 8; depth++)
            {
                chain.Add($"{current.GetType().Name}: {current.Message}");
                if (current is AggregateException agg && agg.InnerExceptions.Count > 0)
                {
                    // Берём первый inner (типично один; multi-inner редкость в нашем
                    // single-thread flow). Если их больше — chain покажет первый,
                    // остальные потеряются — но и так лучше чем «несколько ошибок».
                    current = agg.InnerExceptions[0];
                    continue;
                }
                if (current.InnerException is not null)
                {
                    current = current.InnerException;
                    continue;
                }
                break;
            }
            return (current.GetType().Name, current.Message, string.Join(" -> ", chain));
        }
    }
}
