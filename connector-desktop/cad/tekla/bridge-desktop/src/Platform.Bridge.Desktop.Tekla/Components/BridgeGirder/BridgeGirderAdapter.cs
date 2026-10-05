// Полная имплементация ITeklaComponentAdapter для BridgeGirder.
// Insert/Modify/Delete/Read через Tekla.Structures.Model API напрямую.
//
// Lookup-приоритет (plan §5.3):
//   1. target.teklaComponentGuid → Model.GetIdentifierByGUID → SelectModelObject
//   2. target.teklaComponentId hint → SelectModelObject(new Identifier(id))
//   3. ObjectMap by externalObjectId (внешний lookup, не делается тут — handler
//      пробрасывает через TeklaObjectRef если нашёл)
//   4. STRUCTURA_EXTERNAL_OBJECT_ID UDA scan по всем BaseComponent'ам модели

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Platform.Bridge.Desktop.Tekla.ComponentRuntime;
using Tekla.Structures;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace Platform.Bridge.Desktop.Tekla.Components.BridgeGirder
{
    public sealed class BridgeGirderAdapter : ITeklaComponentAdapter
    {
        private readonly string _teklaVersion;

        public BridgeGirderAdapter(string teklaVersion = "2025.0")
        {
            _teklaVersion = teklaVersion;
        }

        public string ComponentType => BridgeGirderSchemaV1.ComponentType;
        public int SchemaVersion => BridgeGirderSchemaV1.SchemaVersion;

        public ComponentSchema GetSchema() => BridgeGirderSchemaV1.Instance;

        public ComponentCapabilities GetCapabilities(Model? model)
        {
            var pluginPath = $@"C:\TeklaStructures\{_teklaVersion}\Environments\common\Extensions\BridgeComponent\BridgeGirderPlugin.dll";
            var installed = File.Exists(pluginPath);

            return new ComponentCapabilities
            {
                ComponentType = ComponentType,
                SchemaVersion = SchemaVersion,
                Operations = new List<string> { "insert", "modify", "upsert", "delete", "read" },
                TeklaPluginInstalled = installed,
                Reason = installed ? null
                    : $"BridgeGirderPlugin.dll not found for Tekla {_teklaVersion}.",
            };
        }

        public ValidationResult Validate(ComponentOperationRequest request)
        {
            var result = ValidationResult.Success();

            if (string.IsNullOrEmpty(request.ExternalObjectId))
                result.AddError("externalObjectId", "required");
            if (string.IsNullOrEmpty(request.IdempotencyKey))
                result.AddError("idempotencyKey", "required");

            // Schema-driven validation. Для modify — partial update: проверяем
            // только присутствующие поля, "required" не триггерим для absent.
            // Для insert/upsert — full validation.
            var isModify = string.Equals(request.Operation, "modify", StringComparison.OrdinalIgnoreCase);
            var schema = GetSchema();
            foreach (var field in schema.Fields)
            {
                var present = request.Parameters.TryGetValue(field.Name, out var value);
                if (isModify && !present) continue; // partial update — skip absent fields

                var err = field.Validate(value);
                if (err is not null)
                    result.AddError($"parameters.{field.Name}", err);
            }

            // Placement обязателен для insert/upsert; modify/read могут обойтись target'ом.
            if (string.Equals(request.Operation, "upsert", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(request.Operation, "insert", StringComparison.OrdinalIgnoreCase))
            {
                if (request.Placement is null)
                    result.AddError("placement", "required for upsert/insert");
                else
                    ValidatePlacement(request.Placement, result);
            }

            return result;
        }

        private static void ValidatePlacement(ComponentPlacement placement, ValidationResult r)
        {
            switch (placement.Kind)
            {
                case "axis":
                    if (placement.Start is null) r.AddError("placement.start", "required for kind=axis");
                    if (placement.End is null) r.AddError("placement.end", "required for kind=axis");
                    break;
                case "axisObject":
                    if (placement.AxisObjectId is null or <= 0)
                        r.AddError("placement.axisObjectId", "required >0 for kind=axisObject");
                    break;
                default:
                    r.AddError("placement.kind", $"unknown '{placement.Kind}'; expected 'axis' or 'axisObject'");
                    break;
            }
        }

        public TeklaOperationResult Upsert(Model model, ComponentOperationRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var existing = LookupComponent(model, request);
            return existing is not null
                ? ModifyExisting(model, existing, request)
                : InsertNew(model, request);
        }

        public TeklaOperationResult Modify(Model model, ComponentOperationRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var existing = LookupComponent(model, request);
            if (existing is null)
                return TeklaOperationResult.Failure("TEKLA_OBJECT_NOT_FOUND",
                    $"Component for externalObjectId='{request.ExternalObjectId}' not found by guid/id/UDA scan.",
                    request);
            return ModifyExisting(model, existing, request);
        }

        public TeklaOperationResult Delete(Model model, ComponentOperationRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            var existing = LookupComponent(model, request);
            if (existing is null)
            {
                // Идемпотентно: уже удалён — Ok=true, без componentGuid.
                return new TeklaOperationResult
                {
                    Ok = true,
                    Operation = "delete",
                    ComponentType = ComponentType,
                    SchemaVersion = SchemaVersion,
                    ExternalObjectId = request.ExternalObjectId,
                    DurationMs = sw.ElapsedMilliseconds,
                };
            }

            var guid = model.GetGUIDByIdentifier(existing.Identifier);
            var id = existing.Identifier.ID;
            if (!existing.Delete())
                return TeklaOperationResult.Failure("TEKLA_COMPONENT_DELETE_FAILED",
                    "Component.Delete() returned false.", request, sw.ElapsedMilliseconds);

            if (!model.CommitChanges())
                return TeklaOperationResult.Failure("TEKLA_COMPONENT_DELETE_FAILED",
                    "CommitChanges() after Delete returned false.", request, sw.ElapsedMilliseconds);

            return new TeklaOperationResult
            {
                Ok = true,
                Operation = "delete",
                ComponentType = ComponentType,
                SchemaVersion = SchemaVersion,
                ExternalObjectId = request.ExternalObjectId,
                TeklaComponentId = id,
                TeklaComponentGuid = guid,
                DurationMs = sw.ElapsedMilliseconds,
            };
        }

        public TeklaReadResult Read(Model model, TeklaObjectRef objectRef, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();

            BaseComponent? comp = null;
            if (objectRef.HasGuid)
            {
                var id = model.GetIdentifierByGUID(objectRef.Guid!);
                comp = id is not null ? model.SelectModelObject(id) as BaseComponent : null;
            }
            if (comp is null && objectRef.Id is int idInt)
            {
                comp = model.SelectModelObject(new Identifier(idInt)) as BaseComponent;
            }
            if (comp is null)
                return TeklaReadResult.Failure("TEKLA_OBJECT_NOT_FOUND", "Component not found by guid/id.", sw.ElapsedMilliseconds);

            var schema = GetSchema();
            var parameters = PluginComponentMapping.ReadParameters(comp, schema);

            // External id из STRUCTURA_* UDA — single source of truth.
            string ext = "";
            comp.GetUserProperty(StructuraServiceUdas.ExternalObjectId, ref ext);

            return new TeklaReadResult
            {
                Ok = true,
                ComponentType = ComponentType,
                SchemaVersion = SchemaVersion,
                TeklaComponentId = comp.Identifier.ID,
                TeklaComponentGuid = model.GetGUIDByIdentifier(comp.Identifier),
                ExternalObjectId = string.IsNullOrEmpty(ext) ? null : ext,
                Parameters = parameters,
                DurationMs = sw.ElapsedMilliseconds,
            };
        }

        // ───────── private: insert/modify pipelines ──────────

        private TeklaOperationResult InsertNew(Model model, ComponentOperationRequest request)
        {
            var sw = Stopwatch.StartNew();
            var component = new Component
            {
                Name = BridgeGirderSchemaV1.TeklaPluginName,
                Number = BridgeGirderSchemaV1.TeklaPluginNumber,
            };

            var input = BuildComponentInput(model, request.Placement!);
            component.SetComponentInput(input);

            var schema = GetSchema();
            var (applied, skipped) = PluginComponentMapping.ApplyParameters(component, schema, request);

            // tb_* schema-fields через SetAttribute сохраняются ДО Insert (plugin
            // читает их при первом Run). Service UDA через SetUserProperty РАБОТАЮТ
            // ТОЛЬКО на committed objects — поэтому ставим их ПОСЛЕ Insert+Commit
            // и делаем второй CommitChanges чтобы сохранить.

            if (!component.Insert())
                return TeklaOperationResult.Failure("TEKLA_COMPONENT_INSERT_FAILED",
                    "Component.Insert() returned false. Check Tekla error log + plugin trace at %TEMP%/bridge_plugin_trace.txt.",
                    request, sw.ElapsedMilliseconds);

            if (!model.CommitChanges())
                return TeklaOperationResult.Failure("TEKLA_COMPONENT_INSERT_FAILED",
                    "CommitChanges() after Insert returned false.", request, sw.ElapsedMilliseconds);

            // Phase 11 fix: записываем STRUCTURA_* UDA ПОСЛЕ commit. Иначе
            // SetUserProperty silently игнорируется (Tekla требует чтобы object
            // существовал в DB перед SetUserProperty), и /selection не находит
            // балку как нашу.
            PluginComponentMapping.ApplyServiceUdas(component, request, isInsert: true);
            if (!model.CommitChanges())
                return TeklaOperationResult.Failure("TEKLA_COMPONENT_INSERT_FAILED",
                    "CommitChanges() after writing STRUCTURA_* UDA returned false.", request, sw.ElapsedMilliseconds);

            return BuildSuccessResult(model, component, request, applied, skipped, sw.ElapsedMilliseconds, "upsert");
        }

        private TeklaOperationResult ModifyExisting(Model model, BaseComponent existing,
            ComponentOperationRequest request)
        {
            var sw = Stopwatch.StartNew();
            var schema = GetSchema();

            // Plugin BridgeGirderPlugin кэширует payload в стандартных UDA
            // USER_FIELD_2..USER_FIELD_6 (chunks по 240 байт каждый, ровно
            // PayloadUserFields[] из plugin DLL). При modify он СНАЧАЛА читает
            // эти UDA как payload-override и пересобирает геометрию из старых
            // значений, ИГНОРИРУЯ наши свежие tb_* SetAttribute. Чистим cache
            // перед Modify() — plugin upadёт обратно на свежие tb_* fields.
            ClearPluginPayloadCache(existing);

            var (applied, skipped) = PluginComponentMapping.ApplyParameters(existing, schema, request);
            PluginComponentMapping.ApplyServiceUdas(existing, request, isInsert: false);

            if (!existing.Modify())
                return TeklaOperationResult.Failure("TEKLA_COMPONENT_MODIFY_FAILED",
                    "Component.Modify() returned false.", request, sw.ElapsedMilliseconds);

            if (!model.CommitChanges())
                return TeklaOperationResult.Failure("TEKLA_COMPONENT_MODIFY_FAILED",
                    "CommitChanges() after Modify returned false.", request, sw.ElapsedMilliseconds);

            return BuildSuccessResult(model, existing, request, applied, skipped, sw.ElapsedMilliseconds, "modify");
        }

        /// <summary>
        /// Очищает USER_FIELD_2..USER_FIELD_6 на компоненте — plugin использует их
        /// как payload-override cache (240 bytes each, total ~1200 bytes payload).
        /// После очистки plugin при ReadPayloadOverrides не найдёт ничего и
        /// будет читать свежие tb_* fields через DefineInput's [StructuresField].
        /// </summary>
        private static void ClearPluginPayloadCache(BaseComponent comp)
        {
            for (int i = 2; i <= 6; i++)
            {
                try { comp.SetUserProperty($"USER_FIELD_{i}", ""); } catch { }
            }
        }

        private TeklaOperationResult BuildSuccessResult(Model model, BaseComponent component,
            ComponentOperationRequest request, List<string> applied, List<string> skipped,
            long durationMs, string operationLabel)
        {
            var componentId = component.Identifier.ID;
            var componentGuid = model.GetGUIDByIdentifier(component.Identifier);

            // Children — для insert важно; для modify тоже полезно (геометрия
            // могла перестроиться, но id'ы могут совпадать).
            var childIds = new List<int>();
            var childGuids = new List<string>();
            try
            {
                if (component is Component fullComp)
                {
                    var children = fullComp.GetChildren();
                    while (children.MoveNext())
                    {
                        var child = children.Current;
                        if (child?.Identifier is null) continue;
                        childIds.Add(child.Identifier.ID);
                        try { childGuids.Add(model.GetGUIDByIdentifier(child.Identifier)); }
                        catch { /* ignore unobtainable child guid */ }
                    }
                }
            }
            catch
            {
                // GetChildren может не работать на не-Component'ах; не критично для результата.
            }

            return new TeklaOperationResult
            {
                Ok = true,
                Operation = operationLabel,
                ComponentType = ComponentType,
                SchemaVersion = SchemaVersion,
                ExternalObjectId = request.ExternalObjectId,
                TeklaComponentId = componentId,
                TeklaComponentGuid = componentGuid,
                CreatedChildIds = childIds,
                CreatedChildGuids = childGuids,
                AppliedParameters = applied,
                SkippedParameters = skipped,
                DurationMs = durationMs,
            };
        }

        // ───────── private: lookup ──────────

        /// <summary>
        /// Lookup приоритет: target.guid → target.id → STRUCTURA_EXTERNAL_OBJECT_ID UDA scan.
        /// Возвращает null если объект не найден ни одним из путей.
        /// </summary>
        private BaseComponent? LookupComponent(Model model, ComponentOperationRequest request)
        {
            // 1. By GUID. GUID может быть stale (объект удалён из модели но web
            // ещё держит ссылку) — Tekla бросит InvalidOperationException
            // "Cannot find remote object". Глотаем и идём дальше — это
            // не "ошибка", а "не нашли".
            if (!string.IsNullOrEmpty(request.Target?.TeklaComponentGuid))
            {
                try
                {
                    var id = model.GetIdentifierByGUID(request.Target!.TeklaComponentGuid!);
                    if (id is not null)
                    {
                        var obj = model.SelectModelObject(id) as BaseComponent;
                        if (obj is not null) return obj;
                    }
                }
                catch { /* stale guid / битая remote ref — пропускаем */ }
            }

            // 2. By Id hint (runtime id; not stable but cheap)
            if (request.Target?.TeklaComponentId is int idHint && idHint > 0)
            {
                try
                {
                    var obj = model.SelectModelObject(new Identifier(idHint)) as BaseComponent;
                    if (obj is not null && IsOurComponent(obj, request)) return obj;
                }
                catch { /* stale id — пропускаем */ }
            }

            // 3. STRUCTURA_EXTERNAL_OBJECT_ID UDA scan — slow but reliable.
            //    Iterate всех BaseComponent в модели, фильтруем по UDA.
            if (!string.IsNullOrEmpty(request.ExternalObjectId))
            {
                var found = ScanByServiceUda(model, request);
                if (found is not null) return found;
            }

            return null;
        }

        private static BaseComponent? ScanByServiceUda(Model model, ComponentOperationRequest request)
        {
            // ModelObjectEnum.COMPONENT покрывает PluginBase-производные плагины
            // (отдельного "PLUGIN" значения в Tekla 2025 SDK нет — проверено
            // Reflection'ом). BridgeGirderPlugin сюда попадает.
            //
            // Defensive guards: модель может содержать stale remote references
            // (удалённые объекты, undo, redeploy plugin DLL без save). Любой шаг
            // — GetModelObjectSelector / GetAllObjectsWithType / enumerator.MoveNext /
            // enumerator.Current / IsOurComponent — может бросить
            // InvalidOperationException "Cannot find remote object with id ...".
            // Пропускаем битый объект и продолжаем перебор; если whole scan
            // падает — возвращаем null (= "не нашли"), upstream сделает Insert.
            ModelObjectEnumerator? enumerator = null;
            try
            {
                var selector = model.GetModelObjectSelector();
                enumerator = selector.GetAllObjectsWithType(ModelObject.ModelObjectEnum.COMPONENT);
            }
            catch
            {
                return null;
            }
            if (enumerator is null) return null;

            while (true)
            {
                bool hasMore;
                try { hasMore = enumerator.MoveNext(); }
                catch { break; } // enumerator вышел из строя — прекращаем scan
                if (!hasMore) break;

                BaseComponent? comp;
                try { comp = enumerator.Current as BaseComponent; }
                catch { continue; } // один битый — следующий

                if (comp is null) continue;

                bool match;
                try { match = IsOurComponent(comp, request); }
                catch { continue; } // UDA-чтение упало — пропустить

                if (match) return comp;
            }
            return null;
        }

        private static bool IsOurComponent(BaseComponent comp, ComponentOperationRequest request)
        {
            string ext = "";
            comp.GetUserProperty(StructuraServiceUdas.ExternalObjectId, ref ext);
            if (!string.Equals(ext, request.ExternalObjectId, StringComparison.Ordinal)) return false;

            string ct = "";
            comp.GetUserProperty(StructuraServiceUdas.ComponentType, ref ct);
            if (!string.Equals(ct, request.ComponentType, StringComparison.Ordinal)) return false;

            int sv = 0;
            comp.GetUserProperty(StructuraServiceUdas.SchemaVersion, ref sv);
            if (sv != 0 && sv != request.SchemaVersion) return false;

            return true;
        }

        private static ComponentInput BuildComponentInput(Model model, ComponentPlacement placement)
        {
            var input = new ComponentInput();
            switch (placement.Kind)
            {
                case "axis":
                {
                    var s = ToPoint(placement.Start!);
                    var e = ToPoint(placement.End!);
                    input.AddTwoInputPositions(s, e);
                    break;
                }
                case "axisObject":
                {
                    var axisObj = model.SelectModelObject(new Identifier(placement.AxisObjectId!.Value))
                        ?? throw new InvalidOperationException(
                            $"Axis source object id={placement.AxisObjectId} not found in model.");
                    input.AddInputObject(axisObj);
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unknown placement.kind '{placement.Kind}'");
            }
            return input;
        }

        private static Point ToPoint(PointDto p) => new(p.X, p.Y, p.Z);
    }
}
