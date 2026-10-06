// POST /expert-attributes/ifc-attribution/{scan,apply}
// Reads Platform base-construction UDA values from the current Tekla model and,
// after preview, writes the mapped IFC entity override back to Tekla objects.

#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Platform.Bridge.Desktop.Tekla.Logging;
using Platform.Bridge.Desktop.Tekla.Tekla;
using Tekla.Structures.Model;

namespace Platform.Bridge.Desktop.Tekla.Http
{
    public sealed class IfcAttributionHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        private static readonly ModelObject.ModelObjectEnum[] PartEnums = new[]
        {
            ModelObject.ModelObjectEnum.BEAM,
            ModelObject.ModelObjectEnum.POLYBEAM,
            ModelObject.ModelObjectEnum.CONTOURPLATE,
            ModelObject.ModelObjectEnum.BREP,
            ModelObject.ModelObjectEnum.CUSTOM_PART,
            ModelObject.ModelObjectEnum.BENT_PLATE,
            ModelObject.ModelObjectEnum.LOFTED_PLATE,
            ModelObject.ModelObjectEnum.SPIRAL_BEAM,
        };

        // Tekla's visible IFC entity field is proIfcEntityOvrd; IFC_ENTITY_OVERRIDE
        // can keep a stale value after platform-side writes, so it is only fallback.
        private static readonly string[] CurrentIfcPropertyCandidates = new[]
        {
            "proIfcEntityOvrd",
            "IFC_ENTITY_OVERRIDE",
            "IFC_ENTITY",
        };

        private readonly TeklaWorker _worker;
        private readonly JsonLineLogger _log;

        public IfcAttributionHandler(TeklaWorker worker, JsonLineLogger log)
        {
            _worker = worker;
            _log = log;
        }

        public Task<HttpResult> ScanAsync(RequestContext ctx, CancellationToken ct)
            => HandleAsync(ctx, apply: false, ct);

        public Task<HttpResult> ApplyAsync(RequestContext ctx, CancellationToken ct)
            => HandleAsync(ctx, apply: true, ct);

        private async Task<HttpResult> HandleAsync(RequestContext ctx, bool apply, CancellationToken ct)
        {
            string raw;
            try { raw = await ctx.ReadStringAsync(); }
            catch (Exception ex) { return HttpResult.BadRequest("BODY_READ_FAILED", ex.Message); }

            IfcAttributionRequest? request;
            try { request = string.IsNullOrWhiteSpace(raw) ? new IfcAttributionRequest() : JsonSerializer.Deserialize<IfcAttributionRequest>(raw, JsonOptions); }
            catch (JsonException ex) { return HttpResult.BadRequest("INVALID_JSON", ex.Message); }

            var validation = ValidateRequest(request);
            if (validation is not null) return validation;

            try
            {
                var result = await _worker.RunAsync(model => Process(model, request!, apply), ct);
                return HttpResult.Ok(result);
            }
            catch (TeklaDisconnectedException ex)
            {
                return HttpResult.ServiceUnavailable("TEKLA_DISCONNECTED", ex.Message);
            }
            catch (TeklaStaleHandleException ex)
            {
                _log.Warn("ifc.attribution.stale", new { inner = ex.InnerException?.GetType().Name });
                return HttpResult.ServiceUnavailable("BRIDGE_STALE_TEKLA",
                    "Tekla remote references became stale after Tekla restart; in-process reconnect failed. Connector should hard-restart Bridge.Desktop.");
            }
            catch (Exception ex)
            {
                _log.Error("ifc.attribution.unhandled", new { err = ex.Message, type = ex.GetType().Name, apply });
                return HttpResult.ServerError("IFC_ATTRIBUTION_FAILED", ex.Message);
            }
        }

        private IfcAttributionResult Process(Model model, IfcAttributionRequest request, bool apply)
        {
            var diagnostics = new TeklaOperationDiagnostics();
            var operationId = Guid.NewGuid().ToString("N");
            var info = diagnostics.MeasureStage("modelInfo", () =>
            {
                diagnostics.CountApi("model.getInfo");
                return model.GetInfo();
            });
            var modelName = info?.ModelName ?? string.Empty;
            var modelPath = info?.ModelPath ?? string.Empty;
            var started = DateTime.UtcNow;

            var scope = NormalizeScope(request.Scope);
            var objectLevel = NormalizeObjectLevel(request.ObjectLevel);
            var candidates = request.BaseAttributeCandidates ?? Array.Empty<BaseAttributeCandidate>();
            var scanContext = new TeklaScanContext(model, diagnostics, BuildStringReportPropertyNames(candidates));
            _log.Info("ifc.attribution.start", new { operationId, apply, modelName, modelPath, scope, objectLevel, diagnostics = diagnostics.Snapshot() });

            var ifcWriteProperties = NormalizeIfcWriteProperties(request.IfcUserProperties);
            if (apply && request.ApplyTargets is { Count: > 0 })
            {
                return ApplyPreparedTargets(model, request, scope, objectLevel, ifcWriteProperties, modelName, modelPath, started, operationId, diagnostics);
            }

            var resolveStarted = DateTime.UtcNow;
            var objects = diagnostics.MeasureStage("resolveTargets", () => ResolveTargetObjects(model, scope, objectLevel, scanContext).ToArray());
            _log.Info("ifc.attribution.targets", new
            {
                operationId,
                apply,
                scope,
                objectLevel,
                total = objects.Length,
                parts = objects.Count(static obj => obj is Part),
                assemblies = objects.Count(static obj => obj is Assembly),
                resolveMs = (int)(DateTime.UtcNow - resolveStarted).TotalMilliseconds,
                assemblyPartsCacheHits = scanContext.AssemblyPartsCacheHits,
                assemblyPartsCacheMisses = scanContext.AssemblyPartsCacheMisses,
                diagnostics = diagnostics.Snapshot(),
            });

            var mapping = diagnostics.MeasureStage("buildMapping", () => BuildMapping(request.Mappings ?? Array.Empty<IfcMappingRow>()));
            var rows = new List<IfcAttributionItem>(objects.Length);
            var changed = 0;
            var failed = 0;
            var fastPartPreview = !apply && scope == "selected" && objectLevel == "parts";
            var fastPartCandidates = diagnostics.MeasureStage("prepareCandidates", () => fastPartPreview
                ? candidates.Where(static candidate => CandidateAppliesToObjectLevel(candidate, "part")).ToArray()
                : Array.Empty<BaseAttributeCandidate>());

            var rowBuildStarted = DateTime.UtcNow;
            diagnostics.MeasureStage("indexAssemblyParts", () => scanContext.IndexAssemblyParts(objects.OfType<Assembly>()));
            diagnostics.MeasureStage("buildRows", () =>
            {
                foreach (var obj in objects)
                {
                    var row = fastPartPreview && obj is Part
                        ? BuildFastPartPreviewRow(obj, fastPartCandidates, mapping, scanContext)
                        : BuildPreviewRow(model, obj, candidates, mapping, scanContext);
                    rows.Add(row);
                }
            });

            if (apply && !scanContext.IsComplete)
            {
                for (var index = 0; index < rows.Count; index++)
                {
                    var row = rows[index];
                    if (row.Status != "ready" || string.IsNullOrWhiteSpace(row.TargetIfcClass)) continue;
                    failed++;
                    rows[index] = row with
                    {
                        Status = "failed",
                        Message = $"Scan is incomplete; no IFC values were written. {scanContext.IncompleteReason}",
                    };
                }
            }
            else if (apply)
            {
                diagnostics.MeasureStage("applyWrites", () =>
                {
                    for (var index = 0; index < rows.Count; index++)
                    {
                        var row = rows[index];
                        if (row.Status != "ready" || string.IsNullOrWhiteSpace(row.TargetIfcClass)) continue;
                        var obj = objects[index];
                        try
                        {
                            foreach (var property in ifcWriteProperties)
                            {
                                scanContext.SetUserProperty(obj, property, row.TargetIfcClass);
                            }

                            if (scanContext.Modify(obj))
                            {
                                changed++;
                                row = row with { Status = "applied", Message = "IFC entity was written to Tekla.", WrittenProperties = ifcWriteProperties };
                            }
                            else
                            {
                                failed++;
                                row = row with { Status = "failed", Message = "Tekla Modify() returned false." };
                            }
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            row = row with { Status = "failed", Message = $"{ex.GetType().Name}: {ex.Message}" };
                        }
                        rows[index] = row;
                    }
                });
            }
            var rowBuildMs = (int)(DateTime.UtcNow - rowBuildStarted).TotalMilliseconds;
            _log.Info("ifc.attribution.rows.built", new
            {
                operationId,
                apply,
                scope,
                objectLevel,
                fastPartPreview,
                total = rows.Count,
                rowBuildMs,
                assemblyPartsCacheHits = scanContext.AssemblyPartsCacheHits,
                assemblyPartsCacheMisses = scanContext.AssemblyPartsCacheMisses,
                incomplete = !scanContext.IsComplete,
                incompleteReason = scanContext.IncompleteReason,
                diagnostics = diagnostics.Snapshot(),
            });

            var commitOk = true;
            if (apply && changed > 0)
            {
                commitOk = diagnostics.MeasureStage("commitChanges", () =>
                {
                    diagnostics.CountApi("model.commitChanges");
                    return model.CommitChanges();
                });
                if (!commitOk)
                {
                    rows = rows
                        .Select(row => row.Status == "applied"
                            ? row with { Status = "failed", Message = "Tekla CommitChanges() returned false." }
                            : row)
                        .ToList();
                    failed += changed;
                    changed = 0;
                }
            }

            var counts = rows
                .GroupBy(row => row.Status)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

            _log.Info("ifc.attribution.completed", new
            {
                operationId,
                apply,
                modelName,
                modelPath,
                scope,
                objectLevel,
                total = rows.Count,
                changed,
                failed,
                commitOk,
                incomplete = !scanContext.IsComplete,
                incompleteReason = scanContext.IncompleteReason,
                assemblyPartsCacheHits = scanContext.AssemblyPartsCacheHits,
                assemblyPartsCacheMisses = scanContext.AssemblyPartsCacheMisses,
                durationMs = (int)Math.Round((DateTime.UtcNow - started).TotalMilliseconds),
                diagnostics = diagnostics.Snapshot(),
            });

            return new IfcAttributionResult(
                Ok: failed == 0 && commitOk && scanContext.IsComplete,
                Applied: apply,
                ModelName: modelName,
                ModelPath: modelPath,
                Scope: scope,
                ObjectLevel: objectLevel,
                Total: rows.Count,
                Changed: changed,
                Failed: failed,
                Counts: counts,
                Items: rows,
                DurationMs: (int)Math.Round((DateTime.UtcNow - started).TotalMilliseconds),
                Incomplete: !scanContext.IsComplete,
                IncompleteReason: scanContext.IncompleteReason);
        }

        private IfcAttributionResult ApplyPreparedTargets(
            Model model,
            IfcAttributionRequest request,
            string scope,
            string objectLevel,
            IReadOnlyList<string> ifcWriteProperties,
            string modelName,
            string modelPath,
            DateTime started,
            string operationId,
            TeklaOperationDiagnostics diagnostics)
        {
            var preparedTargets = diagnostics.MeasureStage("prepareTargets", () => (request.ApplyTargets ?? Array.Empty<IfcAttributionApplyTarget>())
                .Where(static target => target.ObjectId > 0 && !string.IsNullOrWhiteSpace(target.TargetIfcClass))
                .GroupBy(static target => target.ObjectId)
                .Select(static group => group.First())
                .ToArray());

            var scanContext = new TeklaScanContext(model, diagnostics, Array.Empty<string>());
            var rows = new List<IfcAttributionItem>(preparedTargets.Length);
            var changed = 0;
            var failed = 0;
            var resolved = 0;
            var writeStarted = DateTime.UtcNow;
            var resolvedObjects = new Dictionary<int, ModelObject?>();
            var resolutionErrors = new Dictionary<int, Exception>();
            var assembliesToAudit = new List<Assembly>();

            diagnostics.MeasureStage("preparedResolve", () =>
            {
                foreach (var target in preparedTargets)
                {
                    try
                    {
                        var obj = scanContext.SelectModelObject(model, target.ObjectId);
                        resolvedObjects[target.ObjectId] = obj;
                        if (obj is Assembly assembly)
                        {
                            resolved++;
                            assembliesToAudit.Add(assembly);
                        }
                        else if (obj is Part part)
                        {
                            resolved++;
                            try
                            {
                                var parentAssembly = scanContext.GetPartAssembly(part);
                                if (parentAssembly is not null) assembliesToAudit.Add(parentAssembly);
                            }
                            catch
                            {
                                // GetPartAssembly records the incomplete hierarchy; writes are gated below.
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        resolutionErrors[target.ObjectId] = ex;
                    }
                }
            });
            diagnostics.MeasureStage("preparedAuditAssemblyParts", () => scanContext.IndexAssemblyParts(assembliesToAudit));

            diagnostics.MeasureStage("preparedWrite", () =>
            {
                foreach (var target in preparedTargets)
                {
                    var targetIfcClass = target.TargetIfcClass?.Trim() ?? string.Empty;
                    if (resolutionErrors.TryGetValue(target.ObjectId, out var resolutionError))
                    {
                        failed++;
                        rows.Add(BuildApplyResultItem(
                            target.ObjectId,
                            objectType: string.Empty,
                            objectLevel: "part",
                            targetIfcClass,
                            status: "failed",
                            message: $"{resolutionError.GetType().Name}: {resolutionError.Message}",
                            ifcWriteProperties));
                        continue;
                    }

                    resolvedObjects.TryGetValue(target.ObjectId, out var obj);
                    if (obj is not (Part or Assembly))
                    {
                        failed++;
                        rows.Add(BuildApplyResultItem(
                            target.ObjectId,
                            objectType: obj?.GetType().Name ?? string.Empty,
                            objectLevel: obj is Assembly ? "assembly" : "part",
                            targetIfcClass,
                            status: "failed",
                            message: "Tekla object was not found or is not a supported part/assembly.",
                            ifcWriteProperties));
                        continue;
                    }

                    if (!scanContext.IsComplete)
                    {
                        failed++;
                        rows.Add(BuildApplyResultItem(
                            target.ObjectId,
                            obj.GetType().Name,
                            obj is Assembly ? "assembly" : "part",
                            targetIfcClass,
                            status: "failed",
                            message: $"Scan is incomplete; no IFC values were written. {scanContext.IncompleteReason}",
                            ifcWriteProperties));
                        continue;
                    }

                    try
                    {
                        foreach (var property in ifcWriteProperties)
                        {
                            scanContext.SetUserProperty(obj, property, targetIfcClass);
                        }

                        if (scanContext.Modify(obj))
                        {
                            changed++;
                            rows.Add(BuildApplyResultItem(
                                target.ObjectId,
                                obj.GetType().Name,
                                obj is Assembly ? "assembly" : "part",
                                targetIfcClass,
                                status: "applied",
                                message: "IFC entity was written to Tekla.",
                                ifcWriteProperties));
                        }
                        else
                        {
                            failed++;
                            rows.Add(BuildApplyResultItem(
                                target.ObjectId,
                                obj.GetType().Name,
                                obj is Assembly ? "assembly" : "part",
                                targetIfcClass,
                                status: "failed",
                                message: "Tekla Modify() returned false.",
                                ifcWriteProperties));
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        rows.Add(BuildApplyResultItem(
                            target.ObjectId,
                            objectType: string.Empty,
                            objectLevel: "part",
                            targetIfcClass,
                            status: "failed",
                            message: $"{ex.GetType().Name}: {ex.Message}",
                            ifcWriteProperties));
                    }
                }
            });

            var commitOk = true;
            if (changed > 0)
            {
                commitOk = diagnostics.MeasureStage("commitChanges", () =>
                {
                    diagnostics.CountApi("model.commitChanges");
                    return model.CommitChanges();
                });
                if (!commitOk)
                {
                    rows = rows
                        .Select(row => row.Status == "applied"
                            ? row with { Status = "failed", Message = "Tekla CommitChanges() returned false." }
                            : row)
                        .ToList();
                    failed += changed;
                    changed = 0;
                }
            }

            var counts = rows
                .GroupBy(row => row.Status)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

            _log.Info("ifc.attribution.apply.prepared.completed", new
            {
                operationId,
                modelName,
                modelPath,
                scope,
                objectLevel,
                prepared = preparedTargets.Length,
                resolved,
                changed,
                failed,
                commitOk,
                incomplete = !scanContext.IsComplete,
                incompleteReason = scanContext.IncompleteReason,
                writeMs = (int)(DateTime.UtcNow - writeStarted).TotalMilliseconds,
                durationMs = (int)Math.Round((DateTime.UtcNow - started).TotalMilliseconds),
                diagnostics = diagnostics.Snapshot(),
            });

            return new IfcAttributionResult(
                Ok: failed == 0 && commitOk && scanContext.IsComplete,
                Applied: true,
                ModelName: modelName,
                ModelPath: modelPath,
                Scope: scope,
                ObjectLevel: objectLevel,
                Total: rows.Count,
                Changed: changed,
                Failed: failed,
                Counts: counts,
                Items: rows,
                DurationMs: (int)Math.Round((DateTime.UtcNow - started).TotalMilliseconds),
                Incomplete: !scanContext.IsComplete,
                IncompleteReason: scanContext.IncompleteReason);
        }

        private static IfcAttributionItem BuildApplyResultItem(
            int objectId,
            string objectType,
            string objectLevel,
            string targetIfcClass,
            string status,
            string message,
            IReadOnlyList<string> writtenProperties)
            => new(
                ObjectId: objectId,
                ObjectGuid: string.Empty,
                ObjectType: objectType,
                ObjectLevel: objectLevel,
                ParentAssemblyId: null,
                ParentAssemblyGuid: string.Empty,
                ParentAssemblyName: string.Empty,
                AssemblyChildCount: 0,
                Name: string.Empty,
                Class: string.Empty,
                CurrentIfcClass: string.Empty,
                TargetIfcClass: targetIfcClass,
                Status: status,
                Message: message,
                BaseAttribute: null,
                FilledBaseAttributes: Array.Empty<BaseAttributeValue>(),
                ClassifierCode: string.Empty,
                ClassifierName: string.Empty,
                ClassifierPath: string.Empty,
                MappingBaseName: string.Empty,
                WrittenProperties: writtenProperties,
                ForceWriteIfc: false);

        private static IfcAttributionItem BuildFastPartPreviewRow(
            ModelObject obj,
            IReadOnlyList<BaseAttributeCandidate> partCandidates,
            MappingIndex mapping,
            TeklaScanContext scanContext)
        {
            var objectId = obj.Identifier?.ID ?? 0;
            var objectType = obj.GetType().Name;
            var currentIfc = ReadFirstUserProperty(obj, CurrentIfcPropertyCandidates, scanContext);
            var filled = ReadFilledBaseAttributesFast(obj, partCandidates, scanContext);

            if (filled.Count == 0)
            {
                return BaseItem("missing-base", "Base construction UDA is empty.");
            }

            if (filled.Count > 1)
            {
                return BaseItem("conflict", "Several base construction UDA fields are filled; resolve the conflict in Tekla.")
                    with { FilledBaseAttributes = filled };
            }

            var baseValue = filled[0];
            var match = mapping.Resolve(baseValue.AttributeId, baseValue.MatchValues);
            if (match is null || string.IsNullOrWhiteSpace(match.IfcClass))
            {
                return BaseItem("unmapped", "No expert-attributes mapping was found for this base construction.")
                    with { BaseAttribute = baseValue };
            }

            return BaseItem("ready", "Ready to assign IFC entity.")
                with
                {
                    BaseAttribute = baseValue,
                    TargetIfcClass = match.IfcClass ?? string.Empty,
                    ClassifierCode = match.ClassifierCode ?? string.Empty,
                    ClassifierName = match.ClassifierName ?? string.Empty,
                    ClassifierPath = match.ClassifierPath ?? string.Empty,
                    MappingBaseName = match.BaseName ?? string.Empty,
                };

            IfcAttributionItem BaseItem(string status, string message) => new(
                ObjectId: objectId,
                ObjectGuid: string.Empty,
                ObjectType: objectType,
                ObjectLevel: "part",
                ParentAssemblyId: null,
                ParentAssemblyGuid: string.Empty,
                ParentAssemblyName: string.Empty,
                AssemblyChildCount: 0,
                Name: string.Empty,
                Class: string.Empty,
                CurrentIfcClass: currentIfc,
                TargetIfcClass: string.Empty,
                Status: status,
                Message: message,
                BaseAttribute: null,
                FilledBaseAttributes: Array.Empty<BaseAttributeValue>(),
                ClassifierCode: string.Empty,
                ClassifierName: string.Empty,
                ClassifierPath: string.Empty,
                MappingBaseName: string.Empty,
                WrittenProperties: Array.Empty<string>(),
                ForceWriteIfc: false);
        }

        private static IfcAttributionItem BuildPreviewRow(
            Model model,
            ModelObject obj,
            IReadOnlyList<BaseAttributeCandidate> candidates,
            MappingIndex mapping,
            TeklaScanContext scanContext)
        {
            var objectId = obj.Identifier?.ID ?? 0;
            var objectGuid = scanContext.GetGuid(model, obj);
            var objectType = obj.GetType().Name;
            var objectLevel = obj is Assembly ? "assembly" : "part";
            var hierarchy = BuildHierarchy(model, obj, scanContext);
            var objectName = ReadObjectName(obj);
            var objectClass = ReadObjectClass(obj, scanContext);
            var currentIfc = ReadFirstUserProperty(obj, CurrentIfcPropertyCandidates, scanContext);
            var filled = ReadFilledBaseAttributes(obj, candidates, objectLevel, scanContext);
            var inheritedFromSinglePart = false;

            if (filled.Count == 0 &&
                obj is Assembly assembly &&
                TryReadSinglePartBaseAttributes(assembly, candidates, scanContext, out var inheritedBaseAttributes))
            {
                filled = inheritedBaseAttributes;
                inheritedFromSinglePart = true;
            }

            if (filled.Count == 0)
            {
                return BaseItem("missing-base", "Base construction UDA is empty.");
            }

            if (filled.Count > 1)
            {
                var conflictMessage = inheritedFromSinglePart
                    ? "The only part in this assembly has several base construction UDA fields filled; resolve the conflict on the part in Tekla."
                    : "Several base construction UDA fields are filled; resolve the conflict in Tekla.";
                return BaseItem("conflict", conflictMessage)
                    with { FilledBaseAttributes = filled };
            }

            var baseValue = filled[0];
            var match = mapping.Resolve(baseValue.AttributeId, baseValue.MatchValues);
            if (match is null || string.IsNullOrWhiteSpace(match.IfcClass))
            {
                var unmappedMessage = inheritedFromSinglePart
                    ? "No expert-attributes mapping was found for the base construction inherited from the only part in this assembly."
                    : "No expert-attributes mapping was found for this base construction.";
                return BaseItem("unmapped", unmappedMessage)
                    with { BaseAttribute = baseValue };
            }

            var readyMessage = inheritedFromSinglePart
                ? "Ready to assign IFC entity. Base construction was inherited from the only part in this assembly."
                : "Ready to assign IFC entity.";
            return BaseItem("ready", readyMessage)
                with
                {
                    BaseAttribute = baseValue,
                    TargetIfcClass = match.IfcClass ?? string.Empty,
                    ClassifierCode = match.ClassifierCode ?? string.Empty,
                    ClassifierName = match.ClassifierName ?? string.Empty,
                    ClassifierPath = match.ClassifierPath ?? string.Empty,
                    MappingBaseName = match.BaseName ?? string.Empty,
                    ForceWriteIfc = inheritedFromSinglePart,
                };

            IfcAttributionItem BaseItem(string status, string message) => new(
                ObjectId: objectId,
                ObjectGuid: objectGuid,
                ObjectType: objectType,
                ObjectLevel: objectLevel,
                ParentAssemblyId: hierarchy.ParentAssemblyId,
                ParentAssemblyGuid: hierarchy.ParentAssemblyGuid,
                ParentAssemblyName: hierarchy.ParentAssemblyName,
                AssemblyChildCount: hierarchy.AssemblyChildCount,
                Name: objectName,
                Class: objectClass,
                CurrentIfcClass: currentIfc,
                TargetIfcClass: string.Empty,
                Status: status,
                Message: message,
                BaseAttribute: null,
                FilledBaseAttributes: Array.Empty<BaseAttributeValue>(),
                ClassifierCode: string.Empty,
                ClassifierName: string.Empty,
                ClassifierPath: string.Empty,
                MappingBaseName: string.Empty,
                WrittenProperties: Array.Empty<string>(),
                ForceWriteIfc: false);
        }

        private static List<BaseAttributeValue> ReadFilledBaseAttributes(
            ModelObject obj,
            IReadOnlyList<BaseAttributeCandidate> candidates,
            string objectLevel,
            TeklaScanContext scanContext)
        {
            var result = new List<BaseAttributeValue>();
            foreach (var candidate in candidates.Where(candidate => CandidateAppliesToObjectLevel(candidate, objectLevel)))
            {
                if (string.IsNullOrWhiteSpace(candidate.Id)) continue;
                var value = ReadBaseAttributeValue(obj, candidate, scanContext);
                if (value is not null) result.Add(value);
            }
            return result;
        }

        private static List<BaseAttributeValue> ReadFilledBaseAttributesFast(
            ModelObject obj,
            IReadOnlyList<BaseAttributeCandidate> candidates,
            TeklaScanContext scanContext)
        {
            var result = new List<BaseAttributeValue>();
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate.Id)) continue;
                var value = ReadBaseAttributeValueFast(obj, candidate, scanContext);
                if (value is not null) result.Add(value);
            }
            return result;
        }

        private static BaseAttributeValue? ReadBaseAttributeValue(
            ModelObject obj,
            BaseAttributeCandidate candidate,
            TeklaScanContext scanContext)
        {
            var readId = candidate.Id?.Trim() ?? string.Empty;
            if (readId.Length == 0) return null;

            var rawValues = new List<string>();
            var matchValues = new List<string>();

            string stringValue = string.Empty;
            if (scanContext.GetUserProperty(obj, readId, ref stringValue) && !string.IsNullOrWhiteSpace(stringValue))
            {
                rawValues.Add(stringValue.Trim());
                AddValue(matchValues, stringValue);
            }

            // GetAllUserProperties is the authoritative typed snapshot for UDA values.
            // Report-property fallback is only needed on Tekla versions/objects where
            // that batch API is unavailable; otherwise it turns an empty assembly UDA
            // into dozens of remote calls per object.
            var reportValue = scanContext.HasCompleteUserPropertySnapshot(obj)
                ? string.Empty
                : ReadReportUserProperty(obj, readId, scanContext);
            if (!string.IsNullOrWhiteSpace(reportValue))
            {
                rawValues.Add(reportValue.Trim());
                AddValue(matchValues, reportValue);
            }

            int intValue = 0;
            if (scanContext.GetUserProperty(obj, readId, ref intValue) && intValue != 0)
            {
                rawValues.Add(intValue.ToString());
                var primary = ResolveOptionByOneBasedIndex(candidate.Options, intValue);
                var fallback = ResolveOptionByZeroBasedIndex(candidate.Options, intValue);
                AddValue(matchValues, primary);
                AddValue(matchValues, fallback);
                AddValue(matchValues, intValue.ToString());
            }

            double doubleValue = 0;
            if (scanContext.GetUserProperty(obj, readId, ref doubleValue) && Math.Abs(doubleValue) > double.Epsilon)
            {
                rawValues.Add(doubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
                AddValue(matchValues, doubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (matchValues.Count == 0) return null;
            var display = matchValues.FirstOrDefault(v => !IsNumericText(v)) ?? matchValues[0];
            var logicalId = string.IsNullOrWhiteSpace(candidate.LogicalId) ? readId : candidate.LogicalId!.Trim();
            return new BaseAttributeValue(
                AttributeId: logicalId,
                AttributeLabel: candidate.Label ?? logicalId,
                RawValue: string.Join(", ", rawValues.Distinct(StringComparer.OrdinalIgnoreCase)),
                DisplayValue: display,
                MatchValues: matchValues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }

        private static BaseAttributeValue? ReadBaseAttributeValueFast(
            ModelObject obj,
            BaseAttributeCandidate candidate,
            TeklaScanContext scanContext)
        {
            var readId = candidate.Id?.Trim() ?? string.Empty;
            if (readId.Length == 0) return null;

            var rawValues = new List<string>();
            var matchValues = new List<string>();

            string stringValue = string.Empty;
            if (scanContext.TryGetUserProperty(obj, readId, ref stringValue) && !string.IsNullOrWhiteSpace(stringValue))
            {
                rawValues.Add(stringValue.Trim());
                AddValue(matchValues, stringValue);
            }

            int intValue = 0;
            if (scanContext.TryGetUserProperty(obj, readId, ref intValue) && intValue != 0)
            {
                rawValues.Add(intValue.ToString());
                var primary = ResolveOptionByOneBasedIndex(candidate.Options, intValue);
                var fallback = ResolveOptionByZeroBasedIndex(candidate.Options, intValue);
                AddValue(matchValues, primary);
                AddValue(matchValues, fallback);
                AddValue(matchValues, intValue.ToString());
            }

            if (matchValues.Count == 0) return null;
            var display = matchValues.FirstOrDefault(v => !IsNumericText(v)) ?? matchValues[0];
            var logicalId = string.IsNullOrWhiteSpace(candidate.LogicalId) ? readId : candidate.LogicalId!.Trim();
            return new BaseAttributeValue(
                AttributeId: logicalId,
                AttributeLabel: candidate.Label ?? logicalId,
                RawValue: string.Join(", ", rawValues.Distinct(StringComparer.OrdinalIgnoreCase)),
                DisplayValue: display,
                MatchValues: matchValues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }

        private static bool CandidateAppliesToObjectLevel(BaseAttributeCandidate candidate, string objectLevel)
        {
            var level = (candidate.ObjectLevel ?? "both").Trim().ToLowerInvariant();
            return level switch
            {
                "" or "both" => true,
                "part" or "parts" => string.Equals(objectLevel, "part", StringComparison.OrdinalIgnoreCase),
                "assembly" or "assemblies" => string.Equals(objectLevel, "assembly", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
        }

        private static bool TryReadSinglePartBaseAttributes(
            Assembly assembly,
            IReadOnlyList<BaseAttributeCandidate> candidates,
            TeklaScanContext scanContext,
            out List<BaseAttributeValue> filled)
        {
            filled = new List<BaseAttributeValue>();
            var parts = scanContext.GetAssemblyParts(assembly);
            if (parts.Count != 1)
            {
                return false;
            }

            filled = ReadFilledBaseAttributes(parts[0], candidates, "part", scanContext);
            return filled.Count > 0;
        }

        private static IEnumerable<ModelObject> ResolveTargetObjects(
            Model model,
            string scope,
            string objectLevel,
            TeklaScanContext scanContext)
        {
            var seen = new HashSet<int>();
            var result = new List<ModelObject>();

            if (scope == "selected")
            {
                var selector = new global::Tekla.Structures.Model.UI.ModelObjectSelector();
                AddFromEnumerator(scanContext.GetSelectedObjects(selector), result, seen, scanContext);
                if (objectLevel is "assemblies" or "both")
                {
                    AddParentAssembliesForSelectedParts(result, seen, scanContext);
                }
                if (objectLevel == "both")
                {
                    AddPartsForSelectedAssemblies(result, seen, scanContext);
                }
                return FilterByObjectLevel(result, objectLevel);
            }

            var modelSelector = scanContext.GetModelObjectSelector(model);
            if (objectLevel is "parts" or "both")
            {
                if (scanContext.TryGetAllPartsByBaseType(modelSelector, out var parts))
                {
                    foreach (var part in parts)
                    {
                        AddObject(part, result, seen);
                    }
                }
                else
                {
                    foreach (var partEnum in PartEnums)
                    {
                        AddFromEnumerator(scanContext.GetAllObjectsWithType(modelSelector, partEnum), result, seen, scanContext);
                    }
                }
            }
            if (objectLevel is "assemblies" or "both")
            {
                AddFromEnumerator(scanContext.GetAllObjectsWithType(modelSelector, ModelObject.ModelObjectEnum.ASSEMBLY), result, seen, scanContext);
            }

            return result;
        }

        private static void AddFromEnumerator(
            ModelObjectEnumerator enumerator,
            List<ModelObject> result,
            HashSet<int> seen,
            TeklaScanContext scanContext)
        {
            while (scanContext.MoveNext(enumerator))
            {
                if (scanContext.Current(enumerator) is not ModelObject obj) continue;
                AddObject(obj, result, seen);
            }
        }

        private static void AddObject(ModelObject? obj, List<ModelObject> result, HashSet<int> seen)
        {
            if (obj is not (Part or Assembly)) return;
            var id = obj.Identifier?.ID ?? 0;
            if (id == 0 || !seen.Add(id)) return;
            result.Add(obj);
        }

        private static void AddRelatedObjectsForHierarchy(
            List<ModelObject> result,
            HashSet<int> seen,
            string objectLevel,
            TeklaScanContext scanContext)
        {
            if (objectLevel is "assemblies" or "both")
            {
                AddParentAssembliesForSelectedParts(result, seen, scanContext);
            }

            if (objectLevel is "parts" or "both")
            {
                AddPartsForSelectedAssemblies(result, seen, scanContext);
            }
        }

        private static void AddParentAssembliesForSelectedParts(
            List<ModelObject> result,
            HashSet<int> seen,
            TeklaScanContext scanContext)
        {
            foreach (var obj in result.ToArray())
            {
                if (obj is Part part)
                {
                    try { AddObject(scanContext.GetPartAssembly(part), result, seen); }
                    catch { /* best-effort hierarchy expansion */ }
                }
            }
        }

        private static void AddPartsForSelectedAssemblies(
            List<ModelObject> result,
            HashSet<int> seen,
            TeklaScanContext scanContext)
        {
            foreach (var assembly in result.OfType<Assembly>().ToArray())
            {
                AddAssemblyParts(assembly, result, seen, scanContext);
            }
        }

        private static IEnumerable<ModelObject> FilterByObjectLevel(IEnumerable<ModelObject> objects, string objectLevel)
            => objectLevel switch
            {
                "parts" => objects.Where(static obj => obj is Part),
                "assemblies" => objects.Where(static obj => obj is Assembly),
                _ => objects
            };

        private static void AddAssemblyParts(
            Assembly assembly,
            List<ModelObject> result,
            HashSet<int> seen,
            TeklaScanContext scanContext)
        {
            foreach (var part in scanContext.GetAssemblyParts(assembly))
            {
                AddObject(part, result, seen);
            }
        }

        private static TeklaObjectHierarchy BuildHierarchy(Model model, ModelObject obj, TeklaScanContext scanContext)
        {
            if (obj is Part part)
            {
                try
                {
                    var assembly = scanContext.GetPartAssembly(part);
                    if (assembly is not null)
                    {
                        return new TeklaObjectHierarchy(
                            ParentAssemblyId: assembly.Identifier?.ID,
                            ParentAssemblyGuid: scanContext.GetGuid(model, assembly),
                            ParentAssemblyName: ReadObjectName(assembly),
                            AssemblyChildCount: CountAssemblyParts(assembly, scanContext));
                    }
                }
                catch
                {
                    // Best-effort metadata only.
                }
            }

            if (obj is Assembly ownAssembly)
            {
                return new TeklaObjectHierarchy(
                    ParentAssemblyId: null,
                    ParentAssemblyGuid: string.Empty,
                    ParentAssemblyName: string.Empty,
                    AssemblyChildCount: CountAssemblyParts(ownAssembly, scanContext));
            }

            return new TeklaObjectHierarchy(null, string.Empty, string.Empty, 0);
        }

        private static int CountAssemblyParts(Assembly assembly, TeklaScanContext scanContext)
            => scanContext.GetAssemblyParts(assembly).Count;

        private static string ReadFirstUserProperty(
            ModelObject obj,
            IReadOnlyList<string> names,
            TeklaScanContext scanContext)
        {
            foreach (var name in names)
            {
                string stringValue = string.Empty;
                if (scanContext.TryGetUserProperty(obj, name, ref stringValue) && !string.IsNullOrWhiteSpace(stringValue))
                {
                    return stringValue.Trim();
                }

                int intValue = 0;
                if (scanContext.TryGetUserProperty(obj, name, ref intValue) && intValue != 0)
                {
                    return intValue.ToString();
                }
            }
            return string.Empty;
        }

        private static string ReadReportUserProperty(ModelObject obj, string? name, TeklaScanContext scanContext)
        {
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            var trimmedName = name?.Trim() ?? string.Empty;
            foreach (var propertyName in new[] { trimmedName, $"USERDEFINED.{trimmedName}" })
            {
                string value = string.Empty;
                try
                {
                    if (scanContext.TryGetReportProperty(obj, propertyName, ref value) && !string.IsNullOrWhiteSpace(value))
                    {
                        return value.Trim();
                    }
                }
                catch
                {
                    // Report properties are Tekla-version dependent; fall through to the next candidate.
                }
            }

            return string.Empty;
        }

        private static string ReadObjectName(ModelObject obj)
        {
            try
            {
                return obj switch
                {
                    Part part => part.Name?.Trim() ?? string.Empty,
                    Assembly assembly => assembly.Name?.Trim() ?? string.Empty,
                    _ => string.Empty,
                };
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ReadObjectClass(ModelObject obj, TeklaScanContext scanContext)
        {
            try
            {
                if (obj is Part part) return part.Class?.Trim() ?? string.Empty;
                if (obj is Assembly assembly)
                {
                    return scanContext.GetAssemblyParts(assembly).FirstOrDefault()?.Class?.Trim() ?? string.Empty;
                }
            }
            catch
            {
                // Metadata is best-effort and must not make the scan incomplete.
            }
            return string.Empty;
        }

        private static string? ResolveOptionByOneBasedIndex(IReadOnlyList<string>? options, int index)
        {
            if (options is null || index <= 0) return null;
            var optionIndex = index - 1;
            return optionIndex >= 0 && optionIndex < options.Count ? options[optionIndex] : null;
        }

        private static string? ResolveOptionByZeroBasedIndex(IReadOnlyList<string>? options, int index)
        {
            if (options is null || index < 0) return null;
            return index >= 0 && index < options.Count ? options[index] : null;
        }

        private static void AddValue(List<string> values, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var trimmed = value?.Trim() ?? string.Empty;
            if (!values.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) values.Add(trimmed);
        }

        private static string[] NormalizeIfcWriteProperties(IReadOnlyList<string>? values)
        {
            var source = values is { Count: > 0 } ? values : new[] { "proIfcEntityOvrd", "IFC_ENTITY_OVERRIDE" };
            var result = source
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value?.Trim() ?? string.Empty)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return result.Length > 0 ? result : new[] { "proIfcEntityOvrd", "IFC_ENTITY_OVERRIDE" };
        }

        private static string[] BuildStringReportPropertyNames(IReadOnlyList<BaseAttributeCandidate> candidates)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                var name = candidate.Id?.Trim() ?? string.Empty;
                if (name.Length == 0) continue;
                Add(name);
                Add($"USERDEFINED.{name}");
            }

            return result.ToArray();

            void Add(string name)
            {
                if (seen.Add(name)) result.Add(name);
            }
        }

        private static HttpResult? ValidateRequest(IfcAttributionRequest? request)
        {
            if (request is null) return HttpResult.BadRequest("EMPTY_BODY", "Request body required.");
            if (request.BaseAttributeCandidates is null || request.BaseAttributeCandidates.Count == 0)
            {
                return HttpResult.BadRequest("BASE_ATTRIBUTE_CANDIDATES_REQUIRED", "baseAttributeCandidates is required.");
            }
            if (request.Mappings is null || request.Mappings.Count == 0)
            {
                return HttpResult.BadRequest("IFC_MAPPINGS_REQUIRED", "mappings is required.");
            }
            return null;
        }

        private static string NormalizeScope(string? value)
            => string.Equals(value, "selected", StringComparison.OrdinalIgnoreCase) ? "selected" : "all";

        private static string NormalizeObjectLevel(string? value)
            => string.Equals(value, "assemblies", StringComparison.OrdinalIgnoreCase)
                ? "assemblies"
                : string.Equals(value, "both", StringComparison.OrdinalIgnoreCase)
                    ? "both"
                    : "parts";

        private static bool SameText(string? left, string? right)
            => string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

        private static bool IsNumericText(string value)
            => double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _);

        private static string NormalizeKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var normalized = (value?.Trim() ?? string.Empty).Replace('ё', 'е').Replace('Ё', 'Е').ToLowerInvariant();
            normalized = Regex.Replace(normalized, "\\s+", " ", RegexOptions.CultureInvariant);
            return normalized;
        }

        private static MappingIndex BuildMapping(IReadOnlyList<IfcMappingRow> rows)
            => new(rows);

        public sealed class IfcAttributionRequest
        {
            public string? Scope { get; set; }
            public string? ObjectLevel { get; set; }
            public IReadOnlyList<string>? IfcUserProperties { get; set; }
            public IReadOnlyList<BaseAttributeCandidate>? BaseAttributeCandidates { get; set; }
            public IReadOnlyList<IfcMappingRow>? Mappings { get; set; }
            public IReadOnlyList<IfcAttributionApplyTarget>? ApplyTargets { get; set; }
        }

        public sealed class IfcAttributionApplyTarget
        {
            public int ObjectId { get; set; }
            public string? TargetIfcClass { get; set; }
        }

        public sealed class BaseAttributeCandidate
        {
            public string? Id { get; set; }
            public string? LogicalId { get; set; }
            public string? ObjectLevel { get; set; }
            public string? Label { get; set; }
            public IReadOnlyList<string>? Options { get; set; }
        }

        public sealed class IfcMappingRow
        {
            public string? BaseAttributeId { get; set; }
            public string? BaseAttributeLabel { get; set; }
            public string? BaseValue { get; set; }
            public string? BaseName { get; set; }
            public string? ClassifierCode { get; set; }
            public string? ClassifierName { get; set; }
            public string? ClassifierPath { get; set; }
            public string? IfcClass { get; set; }
        }

        public sealed record BaseAttributeValue(
            string AttributeId,
            string AttributeLabel,
            string RawValue,
            string DisplayValue,
            IReadOnlyList<string> MatchValues);

        public sealed record IfcAttributionItem(
            int ObjectId,
            string ObjectGuid,
            string ObjectType,
            string ObjectLevel,
            int? ParentAssemblyId,
            string ParentAssemblyGuid,
            string ParentAssemblyName,
            int AssemblyChildCount,
            string Name,
            string Class,
            string CurrentIfcClass,
            string TargetIfcClass,
            string Status,
            string Message,
            BaseAttributeValue? BaseAttribute,
            IReadOnlyList<BaseAttributeValue> FilledBaseAttributes,
            string ClassifierCode,
            string ClassifierName,
            string ClassifierPath,
            string MappingBaseName,
            IReadOnlyList<string> WrittenProperties,
            bool ForceWriteIfc);

        private sealed record TeklaObjectHierarchy(
            int? ParentAssemblyId,
            string ParentAssemblyGuid,
            string ParentAssemblyName,
            int AssemblyChildCount);

        private sealed class TeklaOperationDiagnostics
        {
            private readonly Dictionary<string, long> _stageMs = new(StringComparer.Ordinal);
            private readonly Dictionary<string, long> _apiCalls = new(StringComparer.Ordinal);
            private readonly Dictionary<string, long> _cache = new(StringComparer.Ordinal);
            private readonly Dictionary<string, long> _batch = new(StringComparer.Ordinal);

            public T MeasureStage<T>(string name, Func<T> action)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    return action();
                }
                finally
                {
                    Add(_stageMs, name, sw.ElapsedMilliseconds);
                }
            }

            public void MeasureStage(string name, Action action)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    action();
                }
                finally
                {
                    Add(_stageMs, name, sw.ElapsedMilliseconds);
                }
            }

            public void CountApi(string name, long delta = 1)
                => Add(_apiCalls, name, delta);

            public void CountCache(string name, long delta = 1)
                => Add(_cache, name, delta);

            public void CountBatch(string name, long delta = 1)
                => Add(_batch, name, delta);

            public object Snapshot()
                => new
                {
                    stageMs = new SortedDictionary<string, long>(_stageMs, StringComparer.Ordinal),
                    teklaApiCalls = new SortedDictionary<string, long>(_apiCalls, StringComparer.Ordinal),
                    cache = new SortedDictionary<string, long>(_cache, StringComparer.Ordinal),
                    batch = new SortedDictionary<string, long>(_batch, StringComparer.Ordinal),
                };

            private static void Add(Dictionary<string, long> values, string name, long delta)
            {
                if (values.TryGetValue(name, out var existing))
                {
                    values[name] = existing + delta;
                    return;
                }
                values.Add(name, delta);
            }
        }

        private readonly struct CachedStringRead
        {
            public bool Found { get; }
            public string Value { get; }

            public CachedStringRead(bool found, string value)
            {
                Found = found;
                Value = value;
            }
        }

        private readonly struct CachedIntRead
        {
            public bool Found { get; }
            public int Value { get; }

            public CachedIntRead(bool found, int value)
            {
                Found = found;
                Value = value;
            }
        }

        private readonly struct CachedDoubleRead
        {
            public bool Found { get; }
            public double Value { get; }

            public CachedDoubleRead(bool found, double value)
            {
                Found = found;
                Value = value;
            }
        }

        private sealed class TeklaScanContext
        {
            private static readonly IReadOnlyDictionary<string, object> EmptyUserProperties =
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            private static readonly IReadOnlyDictionary<string, string> EmptyStringReportProperties =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            private readonly Model _model;
            private readonly TeklaOperationDiagnostics _diagnostics;
            private readonly ArrayList _stringReportPropertyNames;
            private readonly Dictionary<int, IReadOnlyList<Part>> _assemblyPartsById = new();
            private readonly Dictionary<int, Assembly?> _assemblyByPartId = new();
            private readonly Dictionary<int, string> _guidByObjectId = new();
            private readonly Dictionary<int, IReadOnlyDictionary<string, object>?> _allUserPropertiesByObjectId = new();
            private readonly Dictionary<int, IReadOnlyDictionary<string, string>?> _stringReportPropertiesByObjectId = new();
            private readonly Dictionary<string, CachedStringRead> _userStringByObjectAndName = new(StringComparer.Ordinal);
            private readonly Dictionary<string, CachedIntRead> _userIntByObjectAndName = new(StringComparer.Ordinal);
            private readonly Dictionary<string, CachedDoubleRead> _userDoubleByObjectAndName = new(StringComparer.Ordinal);
            private readonly Dictionary<string, CachedStringRead> _reportStringByObjectAndName = new(StringComparer.Ordinal);
            private readonly HashSet<int> _assembliesAtSecondaryLimit = new();
            private IReadOnlyList<Part>? _allPartsByBaseType;
            private bool _baseTypePartSelectionUnavailable;
            private bool _completePartAssemblyIndexAttempted;

            public TeklaScanContext(
                Model model,
                TeklaOperationDiagnostics diagnostics,
                IReadOnlyList<string> stringReportPropertyNames)
            {
                _model = model;
                _diagnostics = diagnostics;
                _stringReportPropertyNames = new ArrayList(stringReportPropertyNames.Count);
                foreach (var name in stringReportPropertyNames)
                {
                    _stringReportPropertyNames.Add(name);
                }
            }

            public int AssemblyPartsCacheHits { get; private set; }
            public int AssemblyPartsCacheMisses { get; private set; }
            public bool IsComplete { get; private set; } = true;
            public string IncompleteReason { get; private set; } = string.Empty;

            public global::Tekla.Structures.Model.ModelObjectSelector GetModelObjectSelector(Model model)
            {
                _diagnostics.CountApi("model.getModelObjectSelector");
                return model.GetModelObjectSelector();
            }

            public ModelObjectEnumerator GetAllObjectsWithType(
                global::Tekla.Structures.Model.ModelObjectSelector selector,
                ModelObject.ModelObjectEnum objectType)
            {
                _diagnostics.CountApi("modelObjectSelector.getAllObjectsWithType");
                return selector.GetAllObjectsWithType(objectType);
            }

            public bool TryGetAllPartsByBaseType(
                global::Tekla.Structures.Model.ModelObjectSelector selector,
                out IReadOnlyList<Part> parts)
            {
                if (_allPartsByBaseType is not null)
                {
                    _diagnostics.CountCache("partSelection.baseType.hit");
                    parts = _allPartsByBaseType;
                    return true;
                }
                if (_baseTypePartSelectionUnavailable)
                {
                    parts = Array.Empty<Part>();
                    return false;
                }

                _diagnostics.CountBatch("partSelection.baseTypeAttempt");
                _diagnostics.CountApi("modelObjectSelector.getAllObjectsWithType.baseType");
                try
                {
                    var result = new List<Part>();
                    var enumerator = selector.GetAllObjectsWithType(new[] { typeof(Part) });
                    while (MoveNext(enumerator))
                    {
                        if (Current(enumerator) is Part part)
                        {
                            result.Add(part);
                        }
                    }

                    _allPartsByBaseType = result;
                    parts = result;
                    _diagnostics.CountBatch("partSelection.baseTypeSuccess");
                    return true;
                }
                catch
                {
                    _baseTypePartSelectionUnavailable = true;
                    parts = Array.Empty<Part>();
                    _diagnostics.CountBatch("partSelection.enumFallback");
                    return false;
                }
            }

            public ModelObjectEnumerator GetSelectedObjects(global::Tekla.Structures.Model.UI.ModelObjectSelector selector)
            {
                _diagnostics.CountApi("uiModelObjectSelector.getSelectedObjects");
                return selector.GetSelectedObjects();
            }

            public bool MoveNext(ModelObjectEnumerator enumerator)
            {
                _diagnostics.CountApi("modelObjectEnumerator.moveNext");
                return enumerator.MoveNext();
            }

            public object? Current(ModelObjectEnumerator enumerator)
            {
                _diagnostics.CountApi("modelObjectEnumerator.current");
                return enumerator.Current;
            }

            public ModelObject? SelectModelObject(Model model, int objectId)
            {
                _diagnostics.CountApi("model.selectModelObject");
                return model.SelectModelObject(new global::Tekla.Structures.Identifier(objectId));
            }

            public void SetUserProperty(ModelObject obj, string name, string value)
            {
                _diagnostics.CountApi("modelObject.setUserProperty.string");
                obj.SetUserProperty(name, value);
                InvalidateUserProperty(obj, name);
            }

            public bool Modify(ModelObject obj)
            {
                _diagnostics.CountApi("modelObject.modify");
                return obj.Modify();
            }

            public string GetGuid(Model model, ModelObject obj)
            {
                var id = obj.Identifier?.ID ?? 0;
                if (id != 0 && _guidByObjectId.TryGetValue(id, out var cached))
                {
                    _diagnostics.CountCache("guid.hit");
                    return cached;
                }

                if (id != 0) _diagnostics.CountCache("guid.miss");
                try
                {
                    var identifierGuid = obj.Identifier?.GUID ?? Guid.Empty;
                    if (identifierGuid != Guid.Empty)
                    {
                        var directValue = identifierGuid.ToString();
                        if (id != 0) _guidByObjectId[id] = directValue;
                        _diagnostics.CountBatch("guid.identifier");
                        return directValue;
                    }
                }
                catch
                {
                    // Older remote handles may not populate Identifier.GUID.
                }
                _diagnostics.CountApi("model.getGUIDByIdentifier");
                try
                {
                    var value = model.GetGUIDByIdentifier(obj.Identifier) ?? string.Empty;
                    if (id != 0) _guidByObjectId[id] = value;
                    return value;
                }
                catch
                {
                    return string.Empty;
                }
            }

            public Assembly? GetPartAssembly(Part part)
            {
                var id = part.Identifier?.ID ?? 0;
                if (id != 0 && _assemblyByPartId.TryGetValue(id, out var cached))
                {
                    _diagnostics.CountCache("partAssembly.hit");
                    return cached;
                }

                if (id != 0) _diagnostics.CountCache("partAssembly.miss");
                _diagnostics.CountApi("part.getAssembly");
                try
                {
                    var assembly = part.GetAssembly();
                    if (id != 0) _assemblyByPartId[id] = assembly;
                    return assembly;
                }
                catch
                {
                    MarkIncomplete("Part-to-assembly relation could not be read completely.");
                    throw;
                }
            }

            public void IndexAssemblyParts(IEnumerable<Assembly> assemblies)
            {
                var seen = new HashSet<int>();
                foreach (var assembly in assemblies)
                {
                    try
                    {
                        var assemblyId = assembly.Identifier?.ID ?? 0;
                        if (assemblyId == 0 || !seen.Add(assemblyId)) continue;
                        GetAssemblyParts(assembly);
                    }
                    catch
                    {
                        MarkIncomplete("Assembly-to-parts index could not be built completely.");
                    }
                }
            }

            public bool TryGetUserProperty(ModelObject obj, string name, ref string value)
                => ReadUserProperty(obj, name, ref value, suppressErrors: true);

            public bool TryGetUserProperty(ModelObject obj, string name, ref int value)
                => ReadUserProperty(obj, name, ref value, suppressErrors: true);

            public bool GetUserProperty(ModelObject obj, string name, ref string value)
                => ReadUserProperty(obj, name, ref value, suppressErrors: false);

            public bool GetUserProperty(ModelObject obj, string name, ref int value)
                => ReadUserProperty(obj, name, ref value, suppressErrors: false);

            public bool GetUserProperty(ModelObject obj, string name, ref double value)
                => ReadUserProperty(obj, name, ref value, suppressErrors: false);

            public bool HasCompleteUserPropertySnapshot(ModelObject obj)
                => TryGetBatchedUserProperties(obj, out _);

            public bool TryGetReportProperty(ModelObject obj, string name, ref string value)
            {
                var key = ObjectPropertyKey(obj, name);
                if (key.Length > 0 && _reportStringByObjectAndName.TryGetValue(key, out var cached))
                {
                    _diagnostics.CountCache("reportProperty.string.hit");
                    if (cached.Found) value = cached.Value;
                    return cached.Found;
                }

                if (key.Length > 0) _diagnostics.CountCache("reportProperty.string.miss");
                if (TryGetBatchedStringReportProperties(obj, out var batched))
                {
                    var found = batched.TryGetValue(name, out var batchValue);
                    if (found) value = batchValue;
                    if (key.Length > 0)
                    {
                        _reportStringByObjectAndName[key] = new CachedStringRead(found, batchValue ?? string.Empty);
                    }
                    return found;
                }

                _diagnostics.CountBatch("reportProperties.individualFallback");
                _diagnostics.CountApi("modelObject.getReportProperty.string");
                try
                {
                    var found = obj.GetReportProperty(name, ref value);
                    if (key.Length > 0)
                    {
                        _reportStringByObjectAndName[key] = new CachedStringRead(found, value ?? string.Empty);
                    }
                    return found;
                }
                catch
                {
                    return false;
                }
            }

            public IReadOnlyList<Part> GetAssemblyParts(Assembly assembly)
            {
                var id = assembly.Identifier?.ID ?? 0;
                if (id != 0 && _assemblyPartsById.TryGetValue(id, out var cached))
                {
                    AssemblyPartsCacheHits++;
                    _diagnostics.CountCache("assemblyParts.hit");
                    IndexPartsForAssembly(assembly, cached);
                    return cached;
                }

                AssemblyPartsCacheMisses++;
                if (id != 0) _diagnostics.CountCache("assemblyParts.miss");
                var parts = ReadAssemblyParts(assembly);
                if (id != 0)
                {
                    _assemblyPartsById[id] = parts;
                }

                if (id != 0 && _assembliesAtSecondaryLimit.Contains(id))
                {
                    EnsureCompletePartAssemblyIndex();
                    if (_assemblyPartsById.TryGetValue(id, out var completeParts))
                    {
                        parts = completeParts;
                    }
                }

                IndexPartsForAssembly(assembly, parts);
                return parts;
            }

            private bool TryGetBatchedUserProperties(
                ModelObject obj,
                out IReadOnlyDictionary<string, object> properties)
            {
                properties = EmptyUserProperties;
                var id = obj.Identifier?.ID ?? 0;
                if (id == 0) return false;

                if (_allUserPropertiesByObjectId.TryGetValue(id, out var cached))
                {
                    if (cached is null) return false;
                    properties = cached;
                    return true;
                }

                _diagnostics.CountBatch("userProperties.batchAttempt");
                _diagnostics.CountApi("modelObject.getAllUserProperties");
                try
                {
                    var raw = new Hashtable();
                    if (!obj.GetAllUserProperties(ref raw))
                    {
                        _allUserPropertiesByObjectId[id] = null;
                        _diagnostics.CountBatch("userProperties.batchUnavailable");
                        return false;
                    }

                    var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (DictionaryEntry entry in raw)
                    {
                        if (entry.Key is string name && entry.Value is not null)
                        {
                            result[name] = entry.Value;
                        }
                    }

                    _allUserPropertiesByObjectId[id] = result;
                    _diagnostics.CountBatch("userProperties.batchSuccess");
                    properties = result;
                    return true;
                }
                catch
                {
                    _allUserPropertiesByObjectId[id] = null;
                    _diagnostics.CountBatch("userProperties.batchUnavailable");
                    return false;
                }
            }

            private bool TryGetBatchedStringReportProperties(
                ModelObject obj,
                out IReadOnlyDictionary<string, string> properties)
            {
                properties = EmptyStringReportProperties;
                var id = obj.Identifier?.ID ?? 0;
                if (id == 0 || _stringReportPropertyNames.Count == 0) return false;

                if (_stringReportPropertiesByObjectId.TryGetValue(id, out var cached))
                {
                    if (cached is null) return false;
                    properties = cached;
                    return true;
                }

                _diagnostics.CountBatch("reportProperties.batchAttempt");
                _diagnostics.CountApi("modelObject.getStringReportProperties");
                try
                {
                    var raw = new Hashtable();
                    var anyResolved = obj.GetStringReportProperties(_stringReportPropertyNames, ref raw);

                    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (DictionaryEntry entry in raw)
                    {
                        if (entry.Key is string name && entry.Value is string stringValue)
                        {
                            result[name] = stringValue;
                        }
                    }

                    // Tekla returns false when none of the requested values were resolved.
                    // That is a valid empty batch; only exceptions require individual fallback.
                    _stringReportPropertiesByObjectId[id] = result;
                    _diagnostics.CountBatch(anyResolved || result.Count > 0
                        ? "reportProperties.batchSuccess"
                        : "reportProperties.batchEmpty");
                    if (!anyResolved && result.Count > 0)
                    {
                        _diagnostics.CountBatch("reportProperties.batchInconsistent");
                    }
                    properties = result;
                    return true;
                }
                catch
                {
                    _stringReportPropertiesByObjectId[id] = null;
                    _diagnostics.CountBatch("reportProperties.batchUnavailable");
                    return false;
                }
            }

            private bool ReadUserProperty(ModelObject obj, string name, ref string value, bool suppressErrors)
            {
                var key = ObjectPropertyKey(obj, name);
                if (key.Length > 0 && _userStringByObjectAndName.TryGetValue(key, out var cached))
                {
                    _diagnostics.CountCache("userProperty.string.hit");
                    if (cached.Found) value = cached.Value;
                    return cached.Found;
                }

                if (key.Length > 0) _diagnostics.CountCache("userProperty.string.miss");
                if (TryGetBatchedUserProperties(obj, out var batched))
                {
                    var found = batched.TryGetValue(name, out var batchValue) && batchValue is string;
                    var stringValue = found ? (string)batchValue! : string.Empty;
                    if (found) value = stringValue;
                    if (key.Length > 0)
                    {
                        _userStringByObjectAndName[key] = new CachedStringRead(found, stringValue);
                    }
                    return found;
                }

                _diagnostics.CountBatch("userProperties.individualFallback");
                _diagnostics.CountApi("modelObject.getUserProperty.string");
                try
                {
                    var found = obj.GetUserProperty(name, ref value);
                    if (key.Length > 0)
                    {
                        _userStringByObjectAndName[key] = new CachedStringRead(found, value ?? string.Empty);
                    }
                    return found;
                }
                catch
                {
                    if (suppressErrors) return false;
                    throw;
                }
            }

            private bool ReadUserProperty(ModelObject obj, string name, ref int value, bool suppressErrors)
            {
                var key = ObjectPropertyKey(obj, name);
                if (key.Length > 0 && _userIntByObjectAndName.TryGetValue(key, out var cached))
                {
                    _diagnostics.CountCache("userProperty.int.hit");
                    if (cached.Found) value = cached.Value;
                    return cached.Found;
                }

                if (key.Length > 0) _diagnostics.CountCache("userProperty.int.miss");
                if (TryGetBatchedUserProperties(obj, out var batched))
                {
                    var found = batched.TryGetValue(name, out var batchValue) && batchValue is int;
                    var intValue = found ? (int)batchValue! : 0;
                    if (found) value = intValue;
                    if (key.Length > 0)
                    {
                        _userIntByObjectAndName[key] = new CachedIntRead(found, intValue);
                    }
                    return found;
                }

                _diagnostics.CountBatch("userProperties.individualFallback");
                _diagnostics.CountApi("modelObject.getUserProperty.int");
                try
                {
                    var found = obj.GetUserProperty(name, ref value);
                    if (key.Length > 0)
                    {
                        _userIntByObjectAndName[key] = new CachedIntRead(found, value);
                    }
                    return found;
                }
                catch
                {
                    if (suppressErrors) return false;
                    throw;
                }
            }

            private bool ReadUserProperty(ModelObject obj, string name, ref double value, bool suppressErrors)
            {
                var key = ObjectPropertyKey(obj, name);
                if (key.Length > 0 && _userDoubleByObjectAndName.TryGetValue(key, out var cached))
                {
                    _diagnostics.CountCache("userProperty.double.hit");
                    if (cached.Found) value = cached.Value;
                    return cached.Found;
                }

                if (key.Length > 0) _diagnostics.CountCache("userProperty.double.miss");
                if (TryGetBatchedUserProperties(obj, out var batched))
                {
                    var found = batched.TryGetValue(name, out var batchValue) && batchValue is double;
                    var doubleValue = found ? (double)batchValue! : 0;
                    if (found) value = doubleValue;
                    if (key.Length > 0)
                    {
                        _userDoubleByObjectAndName[key] = new CachedDoubleRead(found, doubleValue);
                    }
                    return found;
                }

                _diagnostics.CountBatch("userProperties.individualFallback");
                _diagnostics.CountApi("modelObject.getUserProperty.double");
                try
                {
                    var found = obj.GetUserProperty(name, ref value);
                    if (key.Length > 0)
                    {
                        _userDoubleByObjectAndName[key] = new CachedDoubleRead(found, value);
                    }
                    return found;
                }
                catch
                {
                    if (suppressErrors) return false;
                    throw;
                }
            }

            private void IndexPartsForAssembly(Assembly assembly, IReadOnlyList<Part> parts)
            {
                foreach (var part in parts)
                {
                    var partId = part.Identifier?.ID ?? 0;
                    if (partId == 0) continue;
                    if (_assemblyByPartId.TryGetValue(partId, out var existing) && existing is not null) continue;
                    _assemblyByPartId[partId] = assembly;
                    _diagnostics.CountCache("partAssembly.indexed");
                }
            }

            private void EnsureCompletePartAssemblyIndex()
            {
                if (_completePartAssemblyIndexAttempted) return;
                _completePartAssemblyIndexAttempted = true;
                _diagnostics.CountBatch("assemblyParts.reverseIndexAttempt");

                IReadOnlyList<Part> allParts;
                try
                {
                    var selector = GetModelObjectSelector(_model);
                    if (!TryGetAllPartsByBaseType(selector, out allParts))
                    {
                        MarkIncomplete("Assembly has at least 2048 secondaries, and a complete all-Part enumeration is unavailable.");
                        return;
                    }
                }
                catch
                {
                    MarkIncomplete("Assembly has at least 2048 secondaries, and a complete all-Part enumeration is unavailable.");
                    return;
                }

                var partsByAssemblyId = new Dictionary<int, List<Part>>();
                var seenPartIds = new HashSet<int>();
                foreach (var part in allParts)
                {
                    int partId;
                    int assemblyId;
                    Assembly? assembly;
                    try
                    {
                        partId = part.Identifier?.ID ?? 0;
                        if (partId == 0)
                        {
                            MarkIncomplete("Assembly has at least 2048 secondaries, and an enumerated part has no stable identifier.");
                            return;
                        }
                        if (!seenPartIds.Add(partId)) continue;

                        _diagnostics.CountApi("part.getAssembly.reverseIndex");
                        assembly = part.GetAssembly();
                        assemblyId = assembly?.Identifier?.ID ?? 0;
                    }
                    catch
                    {
                        MarkIncomplete("Assembly has at least 2048 secondaries, and the complete part-to-assembly reverse index could not be built.");
                        return;
                    }

                    if (assemblyId == 0)
                    {
                        MarkIncomplete("Assembly has at least 2048 secondaries, and an enumerated part has no readable assembly.");
                        return;
                    }
                    _assemblyByPartId[partId] = assembly;
                    if (!partsByAssemblyId.TryGetValue(assemblyId, out var assemblyParts))
                    {
                        assemblyParts = new List<Part>();
                        partsByAssemblyId[assemblyId] = assemblyParts;
                    }
                    assemblyParts.Add(part);
                }

                foreach (var assemblyId in _assembliesAtSecondaryLimit)
                {
                    if (!partsByAssemblyId.TryGetValue(assemblyId, out var completeParts))
                    {
                        MarkIncomplete("Assembly has at least 2048 secondaries, but the complete reverse index did not contain that assembly.");
                        return;
                    }
                    _assemblyPartsById[assemblyId] = completeParts;
                }

                foreach (var pair in partsByAssemblyId)
                {
                    _assemblyPartsById[pair.Key] = pair.Value;
                }
                _diagnostics.CountBatch("assemblyParts.reverseIndexSuccess");
            }

            private void MarkIncomplete(string reason)
            {
                if (!IsComplete) return;
                IsComplete = false;
                IncompleteReason = reason;
                _diagnostics.CountBatch("scan.incomplete");
            }

            private IReadOnlyList<Part> ReadAssemblyParts(Assembly assembly)
            {
                var result = new List<Part>();
                var seen = new HashSet<int>();
                var assemblyId = 0;
                try
                {
                    assemblyId = assembly.Identifier?.ID ?? 0;
                }
                catch
                {
                    MarkIncomplete("Assembly identifier could not be read completely.");
                }

                try
                {
                    _diagnostics.CountApi("assembly.getMainPart");
                    AddPart(assembly.GetMainPart() as Part);
                }
                catch
                {
                    MarkIncomplete("Assembly main part could not be read completely.");
                }
                try
                {
                    _diagnostics.CountApi("assembly.getSecondaries");
                    var secondaries = assembly.GetSecondaries();
                    if (secondaries.Count == 2048)
                    {
                        _diagnostics.CountBatch("assemblyParts.secondariesLimitReached");
                        if (assemblyId == 0)
                        {
                            MarkIncomplete("Assembly has at least 2048 secondaries but no stable identifier for a complete reverse index.");
                        }
                        else
                        {
                            _assembliesAtSecondaryLimit.Add(assemblyId);
                        }
                    }
                    foreach (var item in secondaries)
                    {
                        AddPart(item as Part);
                    }
                }
                catch
                {
                    MarkIncomplete("Assembly secondaries could not be read completely.");
                }

                return result;

                void AddPart(Part? part)
                {
                    if (part is null) return;
                    var partId = part.Identifier?.ID ?? 0;
                    if (partId == 0 || !seen.Add(partId)) return;
                    result.Add(part);
                }
            }

            private void InvalidateUserProperty(ModelObject obj, string name)
            {
                var key = ObjectPropertyKey(obj, name);
                if (key.Length == 0) return;
                _userStringByObjectAndName.Remove(key);
                _userIntByObjectAndName.Remove(key);
                _userDoubleByObjectAndName.Remove(key);

                var id = obj.Identifier?.ID ?? 0;
                if (id == 0) return;
                if (_allUserPropertiesByObjectId.ContainsKey(id))
                {
                    _allUserPropertiesByObjectId[id] = null;
                }
                if (_stringReportPropertiesByObjectId.ContainsKey(id))
                {
                    _stringReportPropertiesByObjectId[id] = null;
                }
            }

            private static string ObjectPropertyKey(ModelObject obj, string name)
            {
                var id = obj.Identifier?.ID ?? 0;
                return id == 0 ? string.Empty : $"{id}\u001f{name}";
            }
        }

        public sealed record IfcAttributionResult(
            bool Ok,
            bool Applied,
            string ModelName,
            string ModelPath,
            string Scope,
            string ObjectLevel,
            int Total,
            int Changed,
            int Failed,
            IReadOnlyDictionary<string, int> Counts,
            IReadOnlyList<IfcAttributionItem> Items,
            int DurationMs,
            bool Incomplete,
            string IncompleteReason);

        private sealed class MappingIndex
        {
            private readonly Dictionary<string, IfcMappingRow> _byAttributeAndValue = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, IfcMappingRow> _byValue = new(StringComparer.OrdinalIgnoreCase);

            public MappingIndex(IReadOnlyList<IfcMappingRow> rows)
            {
                foreach (var row in rows)
                {
                    if (string.IsNullOrWhiteSpace(row.IfcClass)) continue;
                    foreach (var value in new[] { row.BaseValue, row.BaseName, row.ClassifierName }.Where(static v => !string.IsNullOrWhiteSpace(v)))
                    {
                        var key = NormalizeKey(value);
                        if (key.Length == 0) continue;
                        if (!_byValue.ContainsKey(key)) _byValue.Add(key, row);
                        var attributeId = row.BaseAttributeId;
                        if (!string.IsNullOrWhiteSpace(attributeId))
                        {
                            var attrKey = AttributeKey(attributeId!, key);
                            if (!_byAttributeAndValue.ContainsKey(attrKey)) _byAttributeAndValue.Add(attrKey, row);
                        }
                    }
                }
            }

            public IfcMappingRow? Resolve(string attributeId, IReadOnlyList<string> values)
            {
                foreach (var value in values)
                {
                    var key = NormalizeKey(value);
                    if (key.Length == 0) continue;
                    if (!string.IsNullOrWhiteSpace(attributeId) &&
                        _byAttributeAndValue.TryGetValue(AttributeKey(attributeId, key), out var byAttr))
                    {
                        return byAttr;
                    }
                }

                foreach (var value in values)
                {
                    var key = NormalizeKey(value);
                    if (key.Length == 0) continue;
                    if (_byValue.TryGetValue(key, out var byValue)) return byValue;
                }

                return null;
            }

            private static string AttributeKey(string attributeId, string normalizedValue)
                => $"{attributeId.Trim().ToUpperInvariant()}::{normalizedValue}";
        }
    }
}
