// Phase 10: PierAdapter — second concrete adapter, доказывающий generic
// архитектуру. Структура почти 1:1 с BridgeGirderAdapter; различия:
//   - schema PierSchemaV1 (другие fields)
//   - placement: single-point anchor (не два-точечная axis)
//   - capability check: ищет PierPlugin.dll вместо BridgeGirderPlugin.dll
//
// Никаких правок в ComponentRuntime/, Http/ComponentHandler.cs, Program.cs
// (кроме одной строки registry.Register) и тем более server/connector/web —
// это и есть проверка plan §11 phase 10 ("<2 часа без рефакторинга").

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Platform.Bridge.Desktop.Tekla.ComponentRuntime;
using Tekla.Structures;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace Platform.Bridge.Desktop.Tekla.Components.Pier
{
    public sealed class PierAdapter : ITeklaComponentAdapter
    {
        private readonly string _teklaVersion;

        public PierAdapter(string teklaVersion = "2025.0")
        {
            _teklaVersion = teklaVersion;
        }

        public string ComponentType => PierSchemaV1.ComponentType;
        public int SchemaVersion => PierSchemaV1.SchemaVersion;

        public ComponentSchema GetSchema() => PierSchemaV1.Instance;

        public ComponentCapabilities GetCapabilities(Model? model)
        {
            var pluginPath = $@"C:\TeklaStructures\{_teklaVersion}\Environments\common\Extensions\PierComponent\PierPlugin.dll";
            var installed = File.Exists(pluginPath);

            return new ComponentCapabilities
            {
                ComponentType = ComponentType,
                SchemaVersion = SchemaVersion,
                Operations = new List<string> { "insert", "modify", "upsert", "delete", "read" },
                TeklaPluginInstalled = installed,
                Reason = installed ? null
                    : $"PierPlugin.dll not found for Tekla {_teklaVersion}.",
            };
        }

        public ValidationResult Validate(ComponentOperationRequest request)
        {
            var result = ValidationResult.Success();
            if (string.IsNullOrEmpty(request.ExternalObjectId))
                result.AddError("externalObjectId", "required");
            if (string.IsNullOrEmpty(request.IdempotencyKey))
                result.AddError("idempotencyKey", "required");

            var isModify = string.Equals(request.Operation, "modify", StringComparison.OrdinalIgnoreCase);
            var schema = GetSchema();
            foreach (var field in schema.Fields)
            {
                var present = request.Parameters.TryGetValue(field.Name, out var value);
                if (isModify && !present) continue;
                var err = field.Validate(value);
                if (err is not null)
                    result.AddError($"parameters.{field.Name}", err);
            }

            // Pier: placement = одна точка-якорь (центр основания опоры).
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
                case "anchor":
                    // Use Start as the anchor point; End/AxisObjectId не используются.
                    if (placement.Start is null)
                        r.AddError("placement.start", "required for kind=anchor (pier anchor point)");
                    break;
                default:
                    r.AddError("placement.kind", $"Pier supports only 'anchor', got '{placement.Kind}'");
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
                    $"Pier for externalObjectId='{request.ExternalObjectId}' not found by guid/id/UDA scan.",
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
                return TeklaReadResult.Failure("TEKLA_OBJECT_NOT_FOUND", "Pier not found by guid/id.", sw.ElapsedMilliseconds);

            var schema = GetSchema();
            var parameters = PluginComponentMapping.ReadParameters(comp, schema);

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
                Name = PierSchemaV1.TeklaPluginName,
                Number = PierSchemaV1.TeklaPluginNumber,
            };

            var input = BuildComponentInput(request.Placement!);
            component.SetComponentInput(input);

            var schema = GetSchema();
            var (applied, skipped) = PluginComponentMapping.ApplyParameters(component, schema, request);

            if (!component.Insert())
                return TeklaOperationResult.Failure("TEKLA_COMPONENT_INSERT_FAILED",
                    "Component.Insert() returned false. Most likely PierPlugin.dll is not installed in Tekla — check /capabilities.",
                    request, sw.ElapsedMilliseconds);
            if (!model.CommitChanges())
                return TeklaOperationResult.Failure("TEKLA_COMPONENT_INSERT_FAILED",
                    "CommitChanges() after Insert returned false.", request, sw.ElapsedMilliseconds);

            // STRUCTURA_* UDA only persist after Insert+Commit (см. BridgeGirderAdapter
            // для деталей). Без этого /selection не найдёт компонент как нашего.
            PluginComponentMapping.ApplyServiceUdas(component, request, isInsert: true);
            if (!model.CommitChanges())
                return TeklaOperationResult.Failure("TEKLA_COMPONENT_INSERT_FAILED",
                    "CommitChanges() after writing STRUCTURA_* UDA returned false.", request, sw.ElapsedMilliseconds);

            return BuildSuccessResult(model, component, request, applied, skipped, sw.ElapsedMilliseconds, "upsert");
        }

        private TeklaOperationResult ModifyExisting(Model model, BaseComponent existing, ComponentOperationRequest request)
        {
            var sw = Stopwatch.StartNew();
            var schema = GetSchema();
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

        private TeklaOperationResult BuildSuccessResult(Model model, BaseComponent component,
            ComponentOperationRequest request, List<string> applied, List<string> skipped,
            long durationMs, string operationLabel)
        {
            var componentId = component.Identifier.ID;
            var componentGuid = model.GetGUIDByIdentifier(component.Identifier);

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
            catch { /* GetChildren может не работать на не-Component'ах; не критично. */ }

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

        private BaseComponent? LookupComponent(Model model, ComponentOperationRequest request)
        {
            // 1. By GUID
            if (!string.IsNullOrEmpty(request.Target?.TeklaComponentGuid))
            {
                var id = model.GetIdentifierByGUID(request.Target!.TeklaComponentGuid!);
                if (id is not null)
                {
                    var obj = model.SelectModelObject(id) as BaseComponent;
                    if (obj is not null) return obj;
                }
            }
            // 2. By Id hint
            if (request.Target?.TeklaComponentId is int idHint && idHint > 0)
            {
                var obj = model.SelectModelObject(new Identifier(idHint)) as BaseComponent;
                if (obj is not null && IsOurComponent(obj, request)) return obj;
            }
            // 3. UDA scan по STRUCTURA_EXTERNAL_OBJECT_ID
            if (!string.IsNullOrEmpty(request.ExternalObjectId))
            {
                var found = ScanByServiceUda(model, request);
                if (found is not null) return found;
            }
            return null;
        }

        private static BaseComponent? ScanByServiceUda(Model model, ComponentOperationRequest request)
        {
            var selector = model.GetModelObjectSelector();
            var enumerator = selector.GetAllObjectsWithType(ModelObject.ModelObjectEnum.COMPONENT);
            while (enumerator.MoveNext())
            {
                if (enumerator.Current is not BaseComponent comp) continue;
                if (!IsOurComponent(comp, request)) continue;
                return comp;
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

        private static ComponentInput BuildComponentInput(ComponentPlacement placement)
        {
            var input = new ComponentInput();
            switch (placement.Kind)
            {
                case "anchor":
                {
                    var anchor = ToPoint(placement.Start!);
                    input.AddOneInputPosition(anchor);
                    break;
                }
                default:
                    throw new InvalidOperationException($"Pier: unsupported placement.kind '{placement.Kind}'");
            }
            return input;
        }

        private static Point ToPoint(PointDto p) => new(p.X, p.Y, p.Z);
    }
}
