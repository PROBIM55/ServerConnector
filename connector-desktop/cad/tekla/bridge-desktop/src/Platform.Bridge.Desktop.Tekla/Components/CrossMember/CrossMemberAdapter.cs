// Desktop adapter для CrossMember v1. Это generic PluginBase-boundary:
// web присылает typed payloadJson + counters, desktop валидирует контракт и
// передаёт данные CrossMemberPlugin через tb_* attributes.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using Platform.Bridge.Desktop.Tekla.ComponentRuntime;
using Tekla.Structures;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace Platform.Bridge.Desktop.Tekla.Components.CrossMember
{
    public sealed class CrossMemberAdapter : ITeklaComponentAdapter
    {
        private readonly string _teklaVersion;
        private const int InlinePayloadJsonMaxBytes = 8000;
        private const int PayloadCacheRetentionDays = 14;

        public CrossMemberAdapter(string teklaVersion = "2025.0")
        {
            _teklaVersion = teklaVersion;
        }

        public string ComponentType => CrossMemberSchemaV1.ComponentType;
        public int SchemaVersion => CrossMemberSchemaV1.SchemaVersion;

        public ComponentSchema GetSchema() => CrossMemberSchemaV1.Instance;

        public ComponentCapabilities GetCapabilities(Model? model)
        {
            var installed = TryFindPlugin(out _);

            return new ComponentCapabilities
            {
                ComponentType = ComponentType,
                SchemaVersion = SchemaVersion,
                Operations = new List<string> { "insert", "modify", "upsert", "delete", "read" },
                TeklaPluginInstalled = installed,
                Reason = installed ? null
                    : MissingPluginMessage(),
            };
        }

        public ValidationResult Validate(ComponentOperationRequest request)
        {
            var result = ValidationResult.Success();

            if (string.IsNullOrEmpty(request.ExternalObjectId))
                result.AddError("externalObjectId", "required");
            if (string.IsNullOrEmpty(request.IdempotencyKey))
                result.AddError("idempotencyKey", "required");

            var schema = GetSchema();
            foreach (var field in schema.Fields)
            {
                var present = request.Parameters.TryGetValue(field.Name, out var value);
                var err = field.Validate(present ? value : null);
                if (err is not null)
                    result.AddError($"parameters.{field.Name}", err);
            }

            if (string.Equals(request.Operation, "upsert", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(request.Operation, "insert", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(request.Operation, "modify", StringComparison.OrdinalIgnoreCase))
            {
                if (request.Placement is null)
                    result.AddError("placement", "required for CrossMember upsert/insert/modify");
                else
                    ValidatePlacement(request.Placement, result);
            }

            ValidatePayloadJson(request, result);
            return result;
        }

        private static void ValidatePlacement(ComponentPlacement placement, ValidationResult r)
        {
            if (!string.Equals(placement.Kind, "axis", StringComparison.Ordinal))
            {
                r.AddError("placement.kind", $"CrossMember supports only 'axis', got '{placement.Kind}'");
                return;
            }
            if (placement.Start is null) r.AddError("placement.start", "required for kind=axis");
            if (placement.End is null) r.AddError("placement.end", "required for kind=axis");
        }

        private static void ValidatePayloadJson(ComponentOperationRequest request, ValidationResult result)
        {
            var payloadJson = GetStringParam(request, "payloadJson");
            if (string.IsNullOrWhiteSpace(payloadJson)) return;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(payloadJson!); }
            catch (JsonException ex)
            {
                result.AddError("parameters.payloadJson", $"invalid JSON: {ex.Message}");
                return;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    result.AddError("parameters.payloadJson", "expected object");
                    return;
                }

                var payloadLineId = GetStringProperty(root, "lineId");
                var requestLineId = GetStringParam(request, "lineId");
                if (!string.IsNullOrEmpty(payloadLineId) && !string.IsNullOrEmpty(requestLineId) &&
                    !string.Equals(payloadLineId, requestLineId, StringComparison.Ordinal))
                {
                    result.AddError("parameters.payloadJson.lineId", $"expected '{requestLineId}', got '{payloadLineId}'");
                }

                CheckArrayCount(root, "hosts", request, "hostCount", result);
                CheckArrayCount(root, "segments", request, "segmentCount", result);
                CheckArrayCount(root, "cutouts", request, "cutoutCount", result);
                CheckArrayCount(root, "splices", request, "spliceCount", result);

                if (root.TryGetProperty("plates", out var plates) && plates.ValueKind == JsonValueKind.Object)
                {
                    CheckArrayCount(plates, "web", request, "webPlateCount", result);
                    CheckArrayCount(plates, "flange", request, "flangePlateCount", result);
                    CheckArrayCount(plates, "spliceCover", request, "spliceCoverPlateCount", result);
                }
                else
                {
                    result.AddError("parameters.payloadJson.plates", "required object");
                }

                var boltCount = CountSpliceBolts(root);
                if (TryGetIntParam(request, "boltCount", out var expectedBolts) && boltCount != expectedBolts)
                {
                    result.AddError("parameters.boltCount", $"expected {boltCount} from payloadJson, got {expectedBolts}");
                }
            }
        }

        private static void CheckArrayCount(
            JsonElement owner,
            string propertyName,
            ComponentOperationRequest request,
            string parameterName,
            ValidationResult result)
        {
            if (!owner.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            {
                result.AddError($"parameters.payloadJson.{propertyName}", "required array");
                return;
            }
            if (TryGetIntParam(request, parameterName, out var expected) && property.GetArrayLength() != expected)
            {
                result.AddError($"parameters.{parameterName}", $"expected {property.GetArrayLength()} from payloadJson, got {expected}");
            }
        }

        private static int CountSpliceBolts(JsonElement root)
        {
            if (!root.TryGetProperty("splices", out var splices) || splices.ValueKind != JsonValueKind.Array)
                return 0;
            var count = 0;
            foreach (var splice in splices.EnumerateArray())
            {
                if (splice.ValueKind != JsonValueKind.Object) continue;
                if (!splice.TryGetProperty("bolts", out var bolts) || bolts.ValueKind != JsonValueKind.Array)
                    continue;
                count += bolts.GetArrayLength();
            }
            return count;
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
                    $"CrossMember for externalObjectId='{request.ExternalObjectId}' not found by guid/id/UDA scan.",
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
                return TeklaReadResult.Failure("TEKLA_OBJECT_NOT_FOUND", "CrossMember not found by guid/id.", sw.ElapsedMilliseconds);

            var parameters = PluginComponentMapping.ReadParameters(comp, GetSchema());

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

        private TeklaOperationResult InsertNew(Model model, ComponentOperationRequest request)
        {
            var sw = Stopwatch.StartNew();
            if (!TryFindPlugin(out _))
                return TeklaOperationResult.Failure("TEKLA_PLUGIN_NOT_INSTALLED", MissingPluginMessage(), request, sw.ElapsedMilliseconds);

            var component = new Component
            {
                Name = CrossMemberSchemaV1.TeklaPluginName,
                Number = CrossMemberSchemaV1.TeklaPluginNumber,
            };

            component.SetComponentInput(BuildComponentInput(request.Placement!));

            if (!TryWithPayloadCache(request, out var runtimeRequest, out var payloadCacheError))
                return TeklaOperationResult.Failure("TEKLA_PAYLOAD_CACHE_FAILED", payloadCacheError, request, sw.ElapsedMilliseconds);

            var schema = GetSchema();
            var (applied, skipped) = PluginComponentMapping.ApplyParameters(component, schema, runtimeRequest);

            if (!component.Insert())
                return TeklaOperationResult.Failure("TEKLA_COMPONENT_INSERT_FAILED",
                    "Component.Insert() returned false. Most likely CrossMemberPlugin.dll is not installed yet — check /capabilities.",
                    request, sw.ElapsedMilliseconds);
            if (!model.CommitChanges())
                return TeklaOperationResult.Failure("TEKLA_COMPONENT_INSERT_FAILED",
                    "CommitChanges() after Insert returned false.", request, sw.ElapsedMilliseconds);

            PluginComponentMapping.ApplyServiceUdas(component, runtimeRequest, isInsert: true);
            if (!model.CommitChanges())
                return TeklaOperationResult.Failure("TEKLA_COMPONENT_INSERT_FAILED",
                    "CommitChanges() after writing STRUCTURA_* UDA returned false.", request, sw.ElapsedMilliseconds);

            return BuildSuccessResult(model, component, runtimeRequest, applied, skipped, sw.ElapsedMilliseconds, "upsert");
        }

        private TeklaOperationResult ModifyExisting(Model model, BaseComponent existing, ComponentOperationRequest request)
        {
            var sw = Stopwatch.StartNew();
            if (!TryFindPlugin(out _))
                return TeklaOperationResult.Failure("TEKLA_PLUGIN_NOT_INSTALLED", MissingPluginMessage(), request, sw.ElapsedMilliseconds);

            if (!TryWithPayloadCache(request, out var runtimeRequest, out var payloadCacheError))
                return TeklaOperationResult.Failure("TEKLA_PAYLOAD_CACHE_FAILED", payloadCacheError, request, sw.ElapsedMilliseconds);

            var (applied, skipped) = PluginComponentMapping.ApplyParameters(existing, GetSchema(), runtimeRequest);
            PluginComponentMapping.ApplyServiceUdas(existing, runtimeRequest, isInsert: false);

            if (!existing.Modify())
                return TeklaOperationResult.Failure("TEKLA_COMPONENT_MODIFY_FAILED",
                    "Component.Modify() returned false.", request, sw.ElapsedMilliseconds);
            if (!model.CommitChanges())
                return TeklaOperationResult.Failure("TEKLA_COMPONENT_MODIFY_FAILED",
                    "CommitChanges() after Modify returned false.", request, sw.ElapsedMilliseconds);

            return BuildSuccessResult(model, existing, runtimeRequest, applied, skipped, sw.ElapsedMilliseconds, "modify");
        }

        private TeklaOperationResult BuildSuccessResult(
            Model model,
            BaseComponent component,
            ComponentOperationRequest request,
            List<string> applied,
            List<string> skipped,
            long durationMs,
            string operationLabel)
        {
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
            catch { /* non-critical diagnostics */ }

            return new TeklaOperationResult
            {
                Ok = true,
                Operation = operationLabel,
                ComponentType = ComponentType,
                SchemaVersion = SchemaVersion,
                ExternalObjectId = request.ExternalObjectId,
                TeklaComponentId = component.Identifier.ID,
                TeklaComponentGuid = model.GetGUIDByIdentifier(component.Identifier),
                CreatedChildIds = childIds,
                CreatedChildGuids = childGuids,
                AppliedParameters = applied,
                SkippedParameters = skipped,
                DurationMs = durationMs,
            };
        }

        private BaseComponent? LookupComponent(Model model, ComponentOperationRequest request)
        {
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
                catch { /* stale guid — continue */ }
            }

            if (request.Target?.TeklaComponentId is int idHint && idHint > 0)
            {
                try
                {
                    var obj = model.SelectModelObject(new Identifier(idHint)) as BaseComponent;
                    if (obj is not null && IsOurComponent(obj, request)) return obj;
                }
                catch { /* stale id — continue */ }
            }

            if (!string.IsNullOrEmpty(request.ExternalObjectId))
            {
                var found = ScanByServiceUda(model, request);
                if (found is not null) return found;
            }
            return null;
        }

        private static BaseComponent? ScanByServiceUda(Model model, ComponentOperationRequest request)
        {
            ModelObjectEnumerator? enumerator = null;
            try
            {
                var selector = model.GetModelObjectSelector();
                enumerator = selector.GetAllObjectsWithType(ModelObject.ModelObjectEnum.COMPONENT);
            }
            catch { return null; }
            if (enumerator is null) return null;

            while (true)
            {
                bool hasMore;
                try { hasMore = enumerator.MoveNext(); }
                catch { break; }
                if (!hasMore) break;

                BaseComponent? comp;
                try { comp = enumerator.Current as BaseComponent; }
                catch { continue; }
                if (comp is null) continue;

                bool match;
                try { match = IsOurComponent(comp, request); }
                catch { continue; }
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
            return sv == 0 || sv == request.SchemaVersion;
        }

        private static ComponentInput BuildComponentInput(ComponentPlacement placement)
        {
            var input = new ComponentInput();
            var start = ToPoint(placement.Start!);
            var end = ToPoint(placement.End!);
            input.AddTwoInputPositions(start, end);
            return input;
        }

        private static Point ToPoint(PointDto p) => new(p.X, p.Y, p.Z);

        private static string? GetStringProperty(JsonElement owner, string propertyName)
        {
            if (!owner.TryGetProperty(propertyName, out var property)) return null;
            return property.ValueKind == JsonValueKind.String ? property.GetString() : property.GetRawText();
        }

        private static string? GetStringParam(ComponentOperationRequest request, string parameterName)
        {
            if (!request.Parameters.TryGetValue(parameterName, out var raw) || raw is null) return null;
            return raw switch
            {
                string s => s,
                JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
                JsonElement je => je.GetRawText(),
                _ => raw.ToString(),
            };
        }

        private static bool TryWithPayloadCache(
            ComponentOperationRequest request,
            out ComponentOperationRequest runtimeRequest,
            out string error)
        {
            runtimeRequest = request;
            error = "";
            var payloadJson = GetStringParam(request, "payloadJson");
            if (string.IsNullOrWhiteSpace(payloadJson)) return true;

            try
            {
                var sourceHash = GetStringParam(request, "sourceHash") ?? "payload";
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Platform",
                    "Bridge",
                    "payloads",
                    "cross-member");
                Directory.CreateDirectory(root);
                CleanupPayloadCache(root);

                var fileName = SafeFileName(request.ExternalObjectId) + "." + SafeFileName(sourceHash) + ".json";
                var payloadPath = Path.Combine(root, fileName);
                File.WriteAllText(payloadPath, payloadJson!, new UTF8Encoding(false));
                request.Parameters["payloadPath"] = payloadPath;
                if (Encoding.UTF8.GetByteCount(payloadJson!) > InlinePayloadJsonMaxBytes)
                {
                    request.Parameters["payloadJson"] =
                        $"{{\"payloadPath\":\"{EscapeJsonString(payloadPath)}\",\"sourceHash\":\"{EscapeJsonString(sourceHash)}\"}}";
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "Failed to write CrossMember payload cache: " + ex.Message;
                return false;
            }
        }

        private static string SafeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "payload";
            var chars = new char[Math.Min(value.Length, 120)];
            for (var i = 0; i < chars.Length; i++)
            {
                var ch = value[i];
                chars[i] = char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_';
            }
            return new string(chars);
        }

        private static void CleanupPayloadCache(string root)
        {
            try
            {
                var threshold = DateTime.UtcNow.AddDays(-PayloadCacheRetentionDays);
                foreach (var file in Directory.GetFiles(root, "*.json"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(file) < threshold) File.Delete(file);
                    }
                    catch { /* payload cache cleanup must not block Tekla operations */ }
                }
            }
            catch { /* payload cache cleanup must not block Tekla operations */ }
        }

        private static string EscapeJsonString(string value)
            => value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"");

        private static bool TryGetIntParam(ComponentOperationRequest request, string parameterName, out int value)
        {
            value = 0;
            return request.Parameters.TryGetValue(parameterName, out var raw) &&
                raw is not null &&
                ComponentField.TryParseInt(raw, out value);
        }

        private bool TryFindPlugin(out string? pluginPath)
        {
            var candidate = $@"C:\TeklaStructures\{_teklaVersion}\Environments\common\Extensions\BridgeComponent\CrossMemberPlugin.dll";
            if (File.Exists(candidate))
            {
                pluginPath = candidate;
                return true;
            }
            pluginPath = null;
            return false;
        }

        private string MissingPluginMessage()
            => $"CrossMemberPlugin.dll not found for Tekla {_teklaVersion}.";
    }
}
