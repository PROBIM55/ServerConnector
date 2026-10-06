// POST /model/selection — snapshot произвольных объектов Tekla.
// scope=selection читает текущее выделение, scope=model — все Part модели.
// В отличие от legacy /selection этот endpoint не требует платформенного
// component UDA и возвращает фактическую solid-топологию.

#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Platform.Bridge.Desktop.Tekla.Tekla;
using Tekla.Structures;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using Tekla.Structures.Solid;
using TeklaBoolean = Tekla.Structures.Model.Boolean;

namespace Platform.Bridge.Desktop.Tekla.Http
{
    public sealed class ModelSelectionHandler
    {
        private const int DefaultMaxObjects = 250;
        private const int AbsoluteMaxObjects = 1000;
        private const int DefaultMaxFeatures = 500;
        private const int AbsoluteMaxFeatures = 2000;
        private const int DefaultMaxVerticesPerObject = 250000;
        private const string SelectionScope = "selection";
        private const string ModelScope = "model";

        // typeof(Part) also matches BooleanPart operative geometry in Tekla.
        // Enumerate physical catalog types explicitly so a full-model scan has
        // the same meaning as the Tekla part count and keeps cuts as features.
        private static readonly ModelObject.ModelObjectEnum[] PhysicalPartTypes =
        {
            ModelObject.ModelObjectEnum.BEAM,
            ModelObject.ModelObjectEnum.POLYBEAM,
            ModelObject.ModelObjectEnum.CONTOURPLATE,
            ModelObject.ModelObjectEnum.BREP,
            ModelObject.ModelObjectEnum.CUSTOM_PART,
            ModelObject.ModelObjectEnum.BENT_PLATE,
            ModelObject.ModelObjectEnum.SPIRAL_BEAM,
            ModelObject.ModelObjectEnum.LOFTED_PLATE,
        };

        private readonly TeklaWorker _worker;
        private SelectionIndex? _selectionIndex;

        public ModelSelectionHandler(TeklaWorker worker)
        {
            _worker = worker;
        }

        public async Task<HttpResult> HandleAsync(RequestContext request, CancellationToken ct)
        {
            ModelSelectionRequest? options;
            try
            {
                options = await request.ReadJsonAsync<ModelSelectionRequest>();
            }
            catch (Exception ex)
            {
                return HttpResult.BadRequest("MODEL_SELECTION_REQUEST_INVALID", ex.Message);
            }

            options ??= new ModelSelectionRequest();
            var maxObjects = Math.Min(Math.Max(options.MaxObjects ?? DefaultMaxObjects, 1), AbsoluteMaxObjects);
            var offset = Math.Max(options.Offset ?? 0, 0);
            var maxFeatures = Math.Min(Math.Max(options.MaxFeatures ?? DefaultMaxFeatures, 1), AbsoluteMaxFeatures);
            var featureOffset = Math.Max(options.FeatureOffset ?? 0, 0);
            var maxVertices = Math.Max(1000, options.MaxVerticesPerObject ?? DefaultMaxVerticesPerObject);
            var includeGeometry = options.IncludeGeometry ?? true;
            var includeSolidBounds = includeGeometry || (options.IncludeSolidBounds ?? false);
            var includeReportProperties = options.IncludeReportProperties ?? false;
            var includeRelations = options.IncludeRelations ?? true;
            var includePlacement = options.IncludePlacement ?? true;
            var includeCoordinateSystem = options.IncludeCoordinateSystem ?? includePlacement;
            var includeFeatures = options.IncludeFeatures ?? false;
            var scope = NormalizeScope(options.Scope);
            if (scope is null)
            {
                return HttpResult.BadRequest(
                    "MODEL_SELECTION_SCOPE_INVALID",
                    "scope must be either 'selection' or 'model'.");
            }

            try
            {
                var snapshot = await _worker.RunAsync(model =>
                    InGlobalTransformationPlane(model, () =>
                        WithModelObjectAutoFetch(() =>
                            ReadSelection(
                                model,
                                includeGeometry,
                                includeSolidBounds,
                                includeReportProperties,
                                includeRelations,
                                includePlacement,
                                includeCoordinateSystem,
                                includeFeatures,
                                options.IndexRelations ?? false,
                                scope,
                                options.RefreshSelectionIndex ?? false,
                                options.SnapshotId,
                                options.TeklaIds,
                                options.FeaturePartTeklaIds,
                                offset,
                                maxObjects,
                                featureOffset,
                                maxFeatures,
                                maxVertices,
                                ct))), ct);

                if (snapshot.SelectedObjectCount == 0)
                {
                    return HttpResult.NotFound(
                        "MODEL_OBJECTS_NOT_SELECTED",
                        snapshot.Scope == ModelScope
                            ? "The Tekla model contains no readable parts."
                            : "No model objects are selected in Tekla.");
                }

                var compactGeometry = includeGeometry && (options.CompactGeometry ?? true);
                var geometryPayload = compactGeometry
                    ? EncodeGeometryPayload(snapshot.Objects)
                    : null;
                var responseObjects = compactGeometry
                    ? snapshot.Objects.Select(static item => item with { Geometry = null }).ToArray()
                    : snapshot.Objects.ToArray();

                return HttpResult.Ok(new
                {
                    ok = true,
                    scope = snapshot.Scope,
                    model = snapshot.Model,
                    snapshotId = snapshot.SnapshotId,
                    selectedObjectCount = snapshot.SelectedObjectCount,
                    expandedObjectCount = snapshot.ExpandedObjectCount,
                    totalPartCount = snapshot.TotalPartCount,
                    totalFeatureCount = snapshot.TotalFeatureCount,
                    offset = snapshot.Offset,
                    partCount = snapshot.Objects.Count,
                    truncated = snapshot.HasMore,
                    hasMore = snapshot.HasMore,
                    nextOffset = snapshot.Offset + snapshot.Objects.Count,
                    featureOffset = snapshot.FeatureOffset,
                    featureCount = snapshot.Features.Count,
                    featureHasMore = snapshot.FeatureHasMore,
                    nextFeatureOffset = snapshot.FeatureOffset + snapshot.Features.Count,
                    unsupported = snapshot.Unsupported,
                    geometryEncoding = compactGeometry ? "gzip+base64+json-v1" : null,
                    geometryPayload,
                    objects = responseObjects,
                    features = snapshot.Features,
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
            catch (Exception ex)
            {
                return HttpResult.ServerError("MODEL_SELECTION_FAILED", $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private SelectionSnapshot ReadSelection(
            Model model,
            bool includeGeometry,
            bool includeSolidBounds,
            bool includeReportProperties,
            bool includeRelations,
            bool includePlacement,
            bool includeCoordinateSystem,
            bool includeFeatures,
            bool indexRelations,
            string scope,
            bool refreshSelectionIndex,
            string? snapshotId,
            int[]? requestedTeklaIds,
            int[]? featurePartTeklaIds,
            int offset,
            int maxObjects,
            int featureOffset,
            int maxFeatures,
            int maxVerticesPerObject,
            CancellationToken ct)
        {
            var featurePartBatch = featurePartTeklaIds is { Length: > 0 };
            var index = GetSelectionIndex(
                model,
                scope,
                refreshSelectionIndex,
                snapshotId,
                includeFeatures && !featurePartBatch,
                indexRelations,
                ct);
            var orderedPartIds = index.PartTeklaIds;
            var idBatch = requestedTeklaIds is { Length: > 0 };
            int[] pagePartIds;
            var requestWasTruncated = false;
            if (idBatch)
            {
                var ids = requestedTeklaIds!
                    .Where(static id => id != 0)
                    .Distinct()
                    .Take(maxObjects + 1)
                    .ToArray();
                requestWasTruncated = ids.Length > maxObjects;
                var wanted = new HashSet<int>(ids.Take(maxObjects));
                pagePartIds = orderedPartIds
                    .Where(wanted.Contains)
                    .ToArray();
            }
            else
            {
                pagePartIds = orderedPartIds
                    .Skip(offset)
                    .Take(maxObjects)
                    .ToArray();
            }
            // Enumerator-backed Part proxies make the catalog pass cheap, but in
            // Tekla 2020 they become dramatically slower for later remoting calls
            // such as GetAssembly/GetCoordinateSystem/GetSolid. Resolve fresh
            // objects by stable Tekla id for every rich read and retain the cache
            // only for the property-only catalog page.
            var catalogOnly = !includeGeometry &&
                              !includeSolidBounds &&
                              !includeReportProperties &&
                              !includeRelations &&
                              !includePlacement;
            var page = ResolveParts(
                model,
                pagePartIds,
                catalogOnly ? index.PartCache : null);
            var snapshots = new List<ModelPartSnapshot>(page.Length);
            foreach (var part in page)
            {
                ct.ThrowIfCancellationRequested();
                snapshots.Add(ReadPart(
                    model,
                    part,
                    includeGeometry,
                    includeSolidBounds,
                    includeReportProperties,
                    includeRelations,
                    includePlacement,
                    includeCoordinateSystem,
                    maxVerticesPerObject,
                    index.AssemblyCache,
                    index.ComponentCache,
                    index.Relations));
            }

            var featureEntries = includeFeatures && featurePartBatch
                ? IndexFeaturesForParts(
                    ResolveParts(model, featurePartTeklaIds!, index.PartCache),
                    ct)
                : index.Features;
            var featurePage = includeFeatures
                ? featureEntries.Skip(featureOffset).Take(maxFeatures).ToArray()
                : Array.Empty<FeatureIndexEntry>();
            var featureSnapshots = new List<ModelFeatureSnapshot>(featurePage.Length);
            foreach (var feature in featurePage)
            {
                ct.ThrowIfCancellationRequested();
                var snapshot = ReadFeature(model, feature, includeReportProperties, index.PartCache);
                if (snapshot is not null) featureSnapshots.Add(snapshot);
            }

            var info = model.GetInfo();
            return new SelectionSnapshot(
                index.Scope,
                new ModelIdentity(info?.ModelName ?? string.Empty, info?.ModelPath ?? string.Empty),
                index.SnapshotId,
                index.SelectedObjectIds.Length,
                index.ExpandedObjectCount,
                orderedPartIds.Length,
                featureEntries.Length,
                offset,
                idBatch ? requestWasTruncated : offset + pagePartIds.Length < orderedPartIds.Length,
                featureOffset,
                includeFeatures && featureOffset + featurePage.Length < featureEntries.Length,
                index.Unsupported,
                snapshots,
                featureSnapshots);
        }

        private SelectionIndex GetSelectionIndex(
            Model model,
            string scope,
            bool refresh,
            string? snapshotId,
            bool includeFeatures,
            bool indexRelations,
            CancellationToken ct)
        {
            var modelInfo = model.GetInfo();
            var modelName = modelInfo?.ModelName ?? string.Empty;
            var modelPath = modelInfo?.ModelPath ?? string.Empty;
            if (!refresh && _selectionIndex is not null &&
                string.Equals(scope, _selectionIndex.Scope, StringComparison.Ordinal) &&
                _selectionIndex.BelongsTo(model, modelName, modelPath) &&
                (!includeFeatures || _selectionIndex.FeaturesIndexed) &&
                (!indexRelations || _selectionIndex.Relations is not null) &&
                (scope == ModelScope ||
                 (!string.IsNullOrWhiteSpace(snapshotId) &&
                  string.Equals(snapshotId, _selectionIndex.SnapshotId, StringComparison.Ordinal))))
            {
                return _selectionIndex;
            }

            var selectedObjects = scope == ModelScope
                ? ReadPhysicalModelParts(model, ct)
                : ReadUiSelection(ct);

            var selectedIds = selectedObjects
                .Select(static obj => obj.Identifier?.ID ?? 0)
                .Distinct()
                .OrderBy(static id => id)
                .ToArray();
            if (!refresh && _selectionIndex is not null &&
                string.Equals(scope, _selectionIndex.Scope, StringComparison.Ordinal) &&
                selectedIds.SequenceEqual(_selectionIndex.SelectedObjectIds) &&
                _selectionIndex.BelongsTo(model, modelName, modelPath) &&
                (!includeFeatures || _selectionIndex.FeaturesIndexed) &&
                (!indexRelations || _selectionIndex.Relations is not null))
            {
                return _selectionIndex;
            }

            var queue = new Queue<ModelObject>(selectedObjects);
            var expandedIds = new HashSet<int>();
            var parts = new List<Part>();
            var directlySelectedFeatures = new List<ModelObject>();
            var unsupported = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var obj = queue.Dequeue();
                var id = obj.Identifier?.ID ?? 0;
                if (id == 0 || !expandedIds.Add(id)) continue;

                if (obj is Part part)
                {
                    parts.Add(part);
                    continue;
                }

                if (IsSupportedFeature(obj))
                {
                    directlySelectedFeatures.Add(obj);
                    EnqueueFeatureParts(obj, queue);
                    continue;
                }

                var childrenAdded = obj switch
                {
                    Assembly assembly => EnqueueAssemblyParts(assembly, queue),
                    BaseComponent => EnqueueChildren(obj, queue),
                    _ => false,
                };
                if (!childrenAdded) Increment(unsupported, obj.GetType().Name);
            }

            var orderedParts = parts
                .GroupBy(static part => part.Identifier?.ID ?? 0)
                .Select(static group => group.First())
                .OrderBy(static part => part.Identifier?.ID ?? 0)
                .ToArray();
            var orderedPartIds = orderedParts
                .Select(static part => part.Identifier?.ID ?? 0)
                .Where(static id => id != 0)
                .ToArray();
            var features = includeFeatures
                ? IndexFeatures(model, orderedParts, directlySelectedFeatures, ct)
                : Array.Empty<FeatureIndexEntry>();
            var relations = indexRelations
                ? IndexPartRelations(model, orderedPartIds, ct)
                : null;

            // Enriching an existing selection index with features or relations
            // must not change its identity. The server keeps this snapshot id for
            // the subsequent geometry pass; assigning a new id here would force
            // every feature page to rebuild the same native Tekla index.
            var preservedSnapshotId = _selectionIndex is not null &&
                                      string.Equals(scope, _selectionIndex.Scope, StringComparison.Ordinal) &&
                                      _selectionIndex.BelongsTo(model, modelName, modelPath) &&
                                      selectedIds.SequenceEqual(_selectionIndex.SelectedObjectIds) &&
                                      (scope == ModelScope ||
                                       (!string.IsNullOrWhiteSpace(snapshotId) &&
                                        string.Equals(snapshotId, _selectionIndex.SnapshotId, StringComparison.Ordinal)))
                ? _selectionIndex.SnapshotId
                : Guid.NewGuid().ToString("N");

            _selectionIndex = new SelectionIndex(
                preservedSnapshotId,
                scope,
                modelName,
                modelPath,
                selectedIds,
                expandedIds.Count,
                orderedPartIds,
                model,
                orderedParts.ToDictionary(
                    static part => part.Identifier?.ID ?? 0,
                    static part => part),
                includeFeatures,
                features,
                unsupported.Select(static pair => new UnsupportedObjectType(pair.Key, pair.Value)).ToArray(),
                new Dictionary<int, AssemblyInfo>(),
                new Dictionary<int, ComponentInfo>(),
                relations);
            return _selectionIndex;
        }

        private static List<ModelObject> ReadPhysicalModelParts(Model model, CancellationToken ct)
        {
            var selector = model.GetModelObjectSelector();
            var partsById = new Dictionary<int, ModelObject>();
            foreach (var objectType in PhysicalPartTypes)
            {
                var objects = selector.GetAllObjectsWithType(objectType);
                while (objects is not null && objects.MoveNext())
                {
                    ct.ThrowIfCancellationRequested();
                    if (objects.Current is not Part part) continue;
                    var id = part.Identifier?.ID ?? 0;
                    if (id != 0) partsById[id] = part;
                }
            }

            return partsById
                .OrderBy(static pair => pair.Key)
                .Select(static pair => pair.Value)
                .ToList();
        }

        private static List<ModelObject> ReadUiSelection(CancellationToken ct)
        {
            var selectedObjects = new List<ModelObject>();
            var selected = new global::Tekla.Structures.Model.UI.ModelObjectSelector().GetSelectedObjects();
            while (selected is not null && selected.MoveNext())
            {
                ct.ThrowIfCancellationRequested();
                if (selected.Current is ModelObject obj && (obj.Identifier?.ID ?? 0) != 0)
                {
                    selectedObjects.Add(obj);
                }
            }

            return selectedObjects;
        }

        private static string? NormalizeScope(string? scope)
        {
            var normalized = string.IsNullOrWhiteSpace(scope)
                ? SelectionScope
                : scope!.Trim().ToLowerInvariant();
            return normalized is SelectionScope or ModelScope ? normalized : null;
        }

        // Native proxies are safe only inside the Model instance that created the
        // index. GetSelectionIndex rejects the cache after a reconnect; missing
        // objects still fall back to stable Tekla IDs.
        private static Part[] ResolveParts(
            Model model,
            IEnumerable<int> ids,
            IReadOnlyDictionary<int, Part>? partCache = null)
            => ids
                .Where(static id => id != 0)
                .Distinct()
                .Select(id => partCache is not null && partCache.TryGetValue(id, out var cached)
                    ? cached
                    : model.SelectModelObject(new Identifier(id)) as Part)
                .Where(static part => part is not null)
                .Cast<Part>()
                .ToArray();

        private static ModelObject? ResolveModelObject(Model model, int teklaId)
            => teklaId == 0 ? null : model.SelectModelObject(new Identifier(teklaId));

        private static bool IsSupportedFeature(ModelObject obj)
            => obj is BoltGroup or TeklaBoolean or BaseWeld;

        private static void EnqueueFeatureParts(ModelObject feature, Queue<ModelObject> queue)
        {
            try
            {
                switch (feature)
                {
                    case BoltGroup bolt:
                        if (bolt.PartToBoltTo is ModelObject boltTo) queue.Enqueue(boltTo);
                        if (bolt.PartToBeBolted is ModelObject bolted) queue.Enqueue(bolted);
                        foreach (var item in bolt.GetOtherPartsToBolt().OfType<ModelObject>()) queue.Enqueue(item);
                        break;
                    case TeklaBoolean boolean when boolean.Father is ModelObject father:
                        queue.Enqueue(father);
                        break;
                    case BaseWeld weld:
                        if (weld.MainObject is ModelObject main) queue.Enqueue(main);
                        if (weld.SecondaryObject is ModelObject secondary) queue.Enqueue(secondary);
                        break;
                }
            }
            catch
            {
                // A feature can remain readable even if one related remote handle is stale.
            }
        }

        private static FeatureIndexEntry[] IndexFeatures(
            Model model,
            IReadOnlyList<Part> parts,
            IReadOnlyList<ModelObject> directlySelected,
            CancellationToken ct)
        {
            var indexed = new Dictionary<int, FeatureIndexEntry>();
            foreach (var feature in directlySelected)
            {
                ct.ThrowIfCancellationRequested();
                AddFeature(indexed, feature, ResolveFeatureOwner(feature));
            }

            // Enumerate model features once, but retain only operations connected
            // to the expanded UI selection. This avoids reading weld polygons and
            // operative-part contours for unrelated model regions on every page.
            // The retained array remains pageable and cached by snapshot id.
            var selectedPartIds = parts
                .Select(static part => part.Identifier?.ID ?? 0)
                .Where(static id => id != 0)
                .ToHashSet();
            if (TryIndexFeaturesFromModel(model, indexed, selectedPartIds, ct))
                return indexed.Values.OrderBy(static item => item.FeatureTeklaId).ToArray();

            // Compatibility fallback for Tekla versions where a model-wide
            // type-filtered enumeration is unavailable.
            foreach (var part in parts)
            {
                ct.ThrowIfCancellationRequested();
                AddFeatures(indexed, TryEnumerate(part.GetBolts), part);
                AddFeatures(indexed, TryEnumerate(part.GetBooleans), part);
                AddFeatures(indexed, TryEnumerate(part.GetWelds), part);
            }

            return indexed.Values.OrderBy(static item => item.FeatureTeklaId).ToArray();
        }

        private static FeatureIndexEntry[] IndexFeaturesForParts(
            IReadOnlyList<Part> parts,
            CancellationToken ct)
        {
            var indexed = new Dictionary<int, FeatureIndexEntry>();
            foreach (var part in parts)
            {
                ct.ThrowIfCancellationRequested();
                AddFeatures(indexed, TryEnumerate(part.GetBolts), part);
                AddFeatures(indexed, TryEnumerate(part.GetBooleans), part);
                AddFeatures(indexed, TryEnumerate(part.GetWelds), part);
            }
            return indexed.Values.OrderBy(static item => item.FeatureTeklaId).ToArray();
        }

        private static bool TryIndexFeaturesFromModel(
            Model model,
            IDictionary<int, FeatureIndexEntry> indexed,
            ISet<int> selectedPartIds,
            CancellationToken ct)
        {
            ModelObjectEnumerator? enumerator;
            try
            {
                enumerator = model.GetModelObjectSelector().GetAllObjectsWithType(new[]
                {
                    typeof(BoltGroup),
                    typeof(TeklaBoolean),
                    // Tekla 2020 does not expand the abstract BaseWeld type in a
                    // model-wide selector, so every concrete weld type is explicit.
                    typeof(Weld),
                    typeof(PolygonWeld),
                    typeof(LogicalWeld),
                });
            }
            catch
            {
                return false;
            }

            if (enumerator is null) return false;
            try
            {
                while (enumerator.MoveNext())
                {
                    ct.ThrowIfCancellationRequested();
                    if (enumerator.Current is not ModelObject feature || !IsSupportedFeature(feature)) continue;
                    var owner = ResolveFeatureOwnerInSelection(feature, selectedPartIds);
                    if (owner is not null) AddFeature(indexed, feature, owner);
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        private static Part? ResolveFeatureOwnerInSelection(
            ModelObject feature,
            ISet<int> selectedPartIds)
        {
            static Part? Selected(ModelObject? value, ISet<int> ids)
                => value is Part part && ids.Contains(part.Identifier?.ID ?? 0) ? part : null;

            try
            {
                switch (feature)
                {
                    case BoltGroup bolt:
                        var boltTo = Selected(bolt.PartToBoltTo, selectedPartIds);
                        if (boltTo is not null) return boltTo;
                        var bolted = Selected(bolt.PartToBeBolted, selectedPartIds);
                        if (bolted is not null) return bolted;
                        foreach (var item in bolt.GetOtherPartsToBolt().OfType<ModelObject>())
                        {
                            var other = Selected(item, selectedPartIds);
                            if (other is not null) return other;
                        }
                        return null;

                    case TeklaBoolean boolean:
                        return Selected(boolean.Father, selectedPartIds);

                    case BaseWeld weld:
                        return Selected(weld.MainObject, selectedPartIds)
                            ?? Selected(weld.SecondaryObject, selectedPartIds);

                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<ModelObject> TryEnumerate(Func<ModelObjectEnumerator> factory)
        {
            ModelObjectEnumerator? enumerator;
            try { enumerator = factory(); }
            catch { yield break; }
            if (enumerator is null) yield break;
            while (true)
            {
                bool moved;
                try { moved = enumerator.MoveNext(); }
                catch { yield break; }
                if (!moved) yield break;
                if (enumerator.Current is ModelObject current) yield return current;
            }
        }

        private static void AddFeatures(
            IDictionary<int, FeatureIndexEntry> indexed,
            IEnumerable<ModelObject> features,
            Part owner)
        {
            foreach (var feature in features)
                AddFeature(indexed, feature, ResolveFeatureOwner(feature) ?? owner);
        }

        private static void AddFeature(
            IDictionary<int, FeatureIndexEntry> indexed,
            ModelObject feature,
            Part? owner)
        {
            if (!IsSupportedFeature(feature)) return;
            var id = feature.Identifier?.ID ?? 0;
            if (id == 0) return;
            if (!indexed.ContainsKey(id)) indexed[id] = new(id, owner?.Identifier?.ID);
        }

        private static Part? ResolveFeatureOwner(ModelObject feature)
        {
            try
            {
                return feature switch
                {
                    BoltGroup bolt => bolt.PartToBoltTo ?? bolt.PartToBeBolted,
                    TeklaBoolean boolean => boolean.Father as Part,
                    BaseWeld weld => weld.MainObject as Part ?? weld.SecondaryObject as Part,
                    _ => null,
                };
            }
            catch { return null; }
        }

        private static bool EnqueueAssemblyParts(Assembly assembly, Queue<ModelObject> queue)
        {
            var added = false;
            try
            {
                if (assembly.GetMainPart() is ModelObject main)
                {
                    queue.Enqueue(main);
                    added = true;
                }
                foreach (var item in assembly.GetSecondaries().OfType<ModelObject>())
                {
                    queue.Enqueue(item);
                    added = true;
                }
                foreach (var item in assembly.GetSubAssemblies().OfType<ModelObject>())
                {
                    queue.Enqueue(item);
                    added = true;
                }
            }
            catch
            {
                // A partially loaded assembly can still expose children below.
            }
            return EnqueueChildren(assembly, queue) || added;
        }

        private static bool EnqueueChildren(ModelObject obj, Queue<ModelObject> queue)
        {
            try
            {
                var enumerator = obj.GetChildren();
                if (enumerator is null) return false;
                var added = false;
                while (enumerator.MoveNext())
                {
                    if (enumerator.Current is not ModelObject child) continue;
                    queue.Enqueue(child);
                    added = true;
                }
                return added;
            }
            catch
            {
                return false;
            }
        }

        private static ModelPartSnapshot ReadPart(
            Model model,
            Part part,
            bool includeGeometry,
            bool includeSolidBounds,
            bool includeReportProperties,
            bool includeRelations,
            bool includePlacement,
            bool includeCoordinateSystem,
            int maxVerticesPerObject,
            IDictionary<int, AssemblyInfo> assemblyCache,
            IDictionary<int, ComponentInfo> componentCache,
            PartRelationIndex? relationIndex)
        {
            var guid = GetGuid(model, part);
            var partId = part.Identifier?.ID ?? 0;
            var assemblyInfo = includeRelations
                ? ReadIndexedOrNativeAssemblyInfo(model, part, partId, assemblyCache, relationIndex)
                : AssemblyInfo.Empty;
            var componentInfo = includeRelations
                ? ReadIndexedOrNativeComponentInfo(model, part, partId, componentCache, relationIndex)
                : ComponentInfo.Empty;
            var authoringPaths = includePlacement
                ? ReadAuthoringPaths(part)
                : Array.Empty<AuthoringPathSnapshot>();
            var coordinateSystem = includePlacement && includeCoordinateSystem
                ? TryGetCoordinateSystem(part)
                : null;
            var centerLine = includePlacement
                ? ReadCenterLine(part, authoringPaths)
                : Array.Empty<PointSnapshot>();

            Solid? solid = null;
            string? geometryError = null;
            if (includeSolidBounds)
            {
                try
                {
                    solid = part.GetSolid();
                    if (solid is null || !solid.IsValid())
                    {
                        geometryError = "PART_SOLID_INVALID";
                        solid = null;
                    }
                }
                catch (Exception ex)
                {
                    geometryError = $"{ex.GetType().Name}: {ex.Message}";
                }
            }

            var bbox = solid is null
                ? null
                : new BoxSnapshot(ToPoint(solid.MinimumPoint), ToPoint(solid.MaximumPoint));
            var geometry = includeGeometry && solid is not null
                ? ReadSolidTopology(solid, maxVerticesPerObject, out geometryError)
                : null;

            var report = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            // Placement is authoring data. It is intentionally absent from the
            // fast catalog pass so a slow native property on one part cannot stall
            // the complete selection snapshot.
            if (includePlacement)
            {
                AddPartPlacementProperties(report, part);
            }
            if (includeReportProperties)
            {
                AddPartReportProperties(part, report);
            }

            return new ModelPartSnapshot(
                guid,
                part.Identifier?.ID ?? 0,
                part.GetType().Name,
                part.Name?.Trim() ?? string.Empty,
                part.Profile?.ProfileString?.Trim() ?? string.Empty,
                part.Material?.MaterialString?.Trim() ?? string.Empty,
                part.Class?.Trim() ?? string.Empty,
                part.Finish?.Trim() ?? string.Empty,
                assemblyInfo.Guid,
                assemblyInfo.TeklaId,
                assemblyInfo.Name,
                assemblyInfo.MainPartTeklaId == part.Identifier?.ID,
                componentInfo.Guid,
                componentInfo.TeklaId,
                componentInfo.Name,
                componentInfo.Number,
                componentInfo.Type,
                coordinateSystem,
                centerLine,
                authoringPaths,
                bbox,
                geometry,
                geometryError,
                report);
        }

        private static ModelFeatureSnapshot? ReadFeature(
            Model model,
            FeatureIndexEntry indexed,
            bool includeReportProperties,
            IReadOnlyDictionary<int, Part>? partCache)
        {
            var feature = ResolveModelObject(model, indexed.FeatureTeklaId);
            if (feature is null || !IsSupportedFeature(feature)) return null;

            var owner = indexed.OwnerPartTeklaId is int indexedOwnerTeklaId
                ? ResolveParts(model, new[] { indexedOwnerTeklaId }, partCache).FirstOrDefault()
                : ResolveFeatureOwner(feature);
            var teklaId = feature.Identifier?.ID ?? 0;
            var guid = GetGuid(model, feature);
            if (string.IsNullOrWhiteSpace(guid)) guid = $"tekla-id-{teklaId}";
            var ownerGuid = GetGuid(model, owner);
            var ownerTeklaId = owner?.Identifier?.ID;
            var points = new List<FeaturePointSnapshot>();
            var authoringPaths = new List<AuthoringPathSnapshot>();
            var relations = new List<FeatureRelationSnapshot>();
            var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var kind = feature.GetType().Name;

            AddRelation(model, relations, "owner", owner);
            AddFeatureProperty(properties, "TEKLA_ID", () => teklaId);

            switch (feature)
            {
                case BoltGroup bolt:
                    kind = bolt.Bolt ? "bolt-group" : "hole-group";
                    AddPoint(points, "first-position", 0, bolt.FirstPosition);
                    AddPoint(points, "second-position", 0, bolt.SecondPosition);
                    AddPoints(points, "bolt-position", bolt.BoltPositions);
                    AddRelation(model, relations, "part-to-bolt-to", bolt.PartToBoltTo);
                    AddRelation(model, relations, "part-to-be-bolted", bolt.PartToBeBolted);
                    var otherIndex = 0;
                    foreach (var item in bolt.GetOtherPartsToBolt().OfType<ModelObject>())
                        AddRelation(model, relations, $"other-part-{otherIndex++}", item);
                    AddBoltProperties(properties, bolt);
                    break;

                case CutPlane cutPlane:
                    kind = "cut-plane";
                    AddRelation(model, relations, "father", cutPlane.Father);
                    AddPlanePoints(points, cutPlane.Plane);
                    break;

                case Fitting fitting:
                    kind = "fitting";
                    AddRelation(model, relations, "father", fitting.Father);
                    AddPlanePoints(points, fitting.Plane);
                    break;

                case EdgeChamfer edgeChamfer:
                    kind = "edge-chamfer";
                    AddRelation(model, relations, "father", edgeChamfer.Father);
                    AddPoint(points, "first-end", 0, edgeChamfer.FirstEnd);
                    AddPoint(points, "second-end", 0, edgeChamfer.SecondEnd);
                    AddFeatureProperty(properties, "firstChamferEndType", () => edgeChamfer.FirstChamferEndType.ToString());
                    AddFeatureProperty(properties, "secondChamferEndType", () => edgeChamfer.SecondChamferEndType.ToString());
                    AddFeatureProperty(properties, "firstBevelDimension", () => edgeChamfer.FirstBevelDimension);
                    AddFeatureProperty(properties, "secondBevelDimension", () => edgeChamfer.SecondBevelDimension);
                    AddFeatureProperty(properties, "chamferType", () => edgeChamfer.Chamfer?.Type.ToString());
                    AddFeatureProperty(properties, "chamferX", () => edgeChamfer.Chamfer?.X);
                    AddFeatureProperty(properties, "chamferY", () => edgeChamfer.Chamfer?.Y);
                    AddFeatureProperty(properties, "chamferDz1", () => edgeChamfer.Chamfer?.DZ1);
                    AddFeatureProperty(properties, "chamferDz2", () => edgeChamfer.Chamfer?.DZ2);
                    break;

                case BooleanPart booleanPart:
                    kind = booleanPart.Type.ToString().IndexOf("CUT", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "boolean-cut"
                        : "boolean-part";
                    AddRelation(model, relations, "father", booleanPart.Father);
                    AddRelation(model, relations, "operative-part", booleanPart.OperativePart);
                    AddFeatureProperty(properties, "booleanType", () => booleanPart.Type.ToString());
                    AddOperativePartAuthoringGeometry(
                        authoringPaths,
                        points,
                        properties,
                        booleanPart.OperativePart);
                    break;

                case BaseWeld weld:
                    kind = "weld";
                    AddRelation(model, relations, "main-object", weld.MainObject);
                    AddRelation(model, relations, "secondary-object", weld.SecondaryObject);
                    if (weld is PolygonWeld polygonWeld) AddPolygonPoints(points, "weld-polygon", polygonWeld.Polygon);
                    AddWeldGeometryPoints(points, weld);
                    AddWeldProperties(properties, weld);
                    if (weld is Weld straightWeld)
                    {
                        AddFeatureProperty(properties, "position", () => straightWeld.Position.ToString());
                        AddFeatureProperty(properties, "directionX", () => straightWeld.Direction.X);
                        AddFeatureProperty(properties, "directionY", () => straightWeld.Direction.Y);
                        AddFeatureProperty(properties, "directionZ", () => straightWeld.Direction.Z);
                    }
                    break;
            }

            if (includeReportProperties)
            {
                AddReportString(feature, properties, "NAME");
                AddReportString(feature, properties, "GUID");
            }

            return new(
                guid,
                teklaId,
                kind,
                feature.GetType().Name,
                ownerGuid,
                ownerTeklaId,
                TryGetCoordinateSystem(feature),
                authoringPaths.ToArray(),
                points.ToArray(),
                relations
                    .GroupBy(static item => $"{item.Role}|{item.ObjectGuid}|{item.TeklaId}", StringComparer.OrdinalIgnoreCase)
                    .Select(static group => group.First())
                    .ToArray(),
                properties);
        }

        private static void AddOperativePartAuthoringGeometry(
            ICollection<AuthoringPathSnapshot> authoringPaths,
            ICollection<FeaturePointSnapshot> fallbackPoints,
            IDictionary<string, object?> properties,
            Part? operativePart)
        {
            if (operativePart is null) return;

            var pathCount = 0;
            try
            {
                foreach (var path in ReadAuthoringPaths(operativePart))
                {
                    authoringPaths.Add(path with { Role = $"operative-{path.Role}" });
                    pathCount++;
                }
            }
            catch
            {
                // Some legacy custom parts do not expose a native authoring path.
            }

            if (pathCount == 0) AddPartAuthoringPoints(fallbackPoints, operativePart);

            AddFeatureProperty(properties, "operative.objectType", () => operativePart.GetType().Name);
            AddFeatureProperty(properties, "operative.name", () => operativePart.Name);
            AddFeatureProperty(properties, "operative.profile", () => operativePart.Profile?.ProfileString);
            AddFeatureProperty(properties, "operative.material", () => operativePart.Material?.MaterialString);
            AddFeatureProperty(properties, "operative.class", () => operativePart.Class);
            AddFeatureProperty(properties, "operative.finish", () => operativePart.Finish);

            var placement = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            AddPartPlacementProperties(placement, operativePart);
            foreach (var item in placement) properties[$"operative.{item.Key}"] = item.Value;
        }

        private static void AddBoltProperties(IDictionary<string, object?> target, BoltGroup bolt)
        {
            AddFeatureProperty(target, "hasPhysicalBolts", () => bolt.Bolt);
            AddFeatureProperty(target, "boltSize", () => bolt.BoltSize);
            AddFeatureProperty(target, "boltStandard", () => bolt.BoltStandard);
            AddFeatureProperty(target, "boltType", () => bolt.BoltType.ToString());
            AddFeatureProperty(target, "threadInMaterial", () => bolt.ThreadInMaterial.ToString());
            AddFeatureProperty(target, "length", () => bolt.Length);
            AddFeatureProperty(target, "cutLength", () => bolt.CutLength);
            AddFeatureProperty(target, "extraLength", () => bolt.ExtraLength);
            AddFeatureProperty(target, "tolerance", () => bolt.Tolerance);
            AddFeatureProperty(target, "holeType", () => bolt.HoleType.ToString());
            AddFeatureProperty(target, "slottedHoleX", () => bolt.SlottedHoleX);
            AddFeatureProperty(target, "slottedHoleY", () => bolt.SlottedHoleY);
            AddFeatureProperty(target, "rotateSlots", () => bolt.RotateSlots.ToString());
            AddFeatureProperty(target, "washer1", () => bolt.Washer1);
            AddFeatureProperty(target, "washer2", () => bolt.Washer2);
            AddFeatureProperty(target, "washer3", () => bolt.Washer3);
            AddFeatureProperty(target, "nut1", () => bolt.Nut1);
            AddFeatureProperty(target, "nut2", () => bolt.Nut2);
            AddFeatureProperty(target, "hole1", () => bolt.Hole1);
            AddFeatureProperty(target, "hole2", () => bolt.Hole2);
            AddFeatureProperty(target, "hole3", () => bolt.Hole3);
            AddFeatureProperty(target, "hole4", () => bolt.Hole4);
            AddFeatureProperty(target, "hole5", () => bolt.Hole5);
            AddFeatureProperty(target, "connectAssemblies", () => bolt.ConnectAssemblies);
        }

        private static void AddWeldProperties(IDictionary<string, object?> target, BaseWeld weld)
        {
            AddFeatureProperty(target, "sizeAbove", () => weld.SizeAbove);
            AddFeatureProperty(target, "additionalSizeAbove", () => weld.AdditionalSizeAbove);
            AddFeatureProperty(target, "typeAbove", () => weld.TypeAbove.ToString());
            AddFeatureProperty(target, "angleAbove", () => weld.AngleAbove);
            AddFeatureProperty(target, "lengthAbove", () => weld.LengthAbove);
            AddFeatureProperty(target, "contourAbove", () => weld.ContourAbove.ToString());
            AddFeatureProperty(target, "finishAbove", () => weld.FinishAbove.ToString());
            AddFeatureProperty(target, "pitchAbove", () => weld.PitchAbove);
            AddFeatureProperty(target, "rootOpeningAbove", () => weld.RootOpeningAbove);
            AddFeatureProperty(target, "rootFaceAbove", () => weld.RootFaceAbove);
            AddFeatureProperty(target, "effectiveThroatAbove", () => weld.EffectiveThroatAbove);
            AddFeatureProperty(target, "sizeBelow", () => weld.SizeBelow);
            AddFeatureProperty(target, "additionalSizeBelow", () => weld.AdditionalSizeBelow);
            AddFeatureProperty(target, "typeBelow", () => weld.TypeBelow.ToString());
            AddFeatureProperty(target, "angleBelow", () => weld.AngleBelow);
            AddFeatureProperty(target, "lengthBelow", () => weld.LengthBelow);
            AddFeatureProperty(target, "contourBelow", () => weld.ContourBelow.ToString());
            AddFeatureProperty(target, "finishBelow", () => weld.FinishBelow.ToString());
            AddFeatureProperty(target, "pitchBelow", () => weld.PitchBelow);
            AddFeatureProperty(target, "rootOpeningBelow", () => weld.RootOpeningBelow);
            AddFeatureProperty(target, "rootFaceBelow", () => weld.RootFaceBelow);
            AddFeatureProperty(target, "effectiveThroatBelow", () => weld.EffectiveThroatBelow);
            AddFeatureProperty(target, "shopWeld", () => weld.ShopWeld);
            AddFeatureProperty(target, "aroundWeld", () => weld.AroundWeld);
            AddFeatureProperty(target, "stitchWeld", () => weld.StitchWeld);
            AddFeatureProperty(target, "preparation", () => weld.Preparation.ToString());
            AddFeatureProperty(target, "placement", () => weld.Placement.ToString());
            AddFeatureProperty(target, "processType", () => weld.ProcessType.ToString());
            AddFeatureProperty(target, "intermittentType", () => weld.IntermittentType.ToString());
            AddFeatureProperty(target, "connectAssemblies", () => weld.ConnectAssemblies);
            AddFeatureProperty(target, "referenceText", () => weld.ReferenceText);
            AddFeatureProperty(target, "standard", () => weld.Standard);
            AddFeatureProperty(target, "weldNumber", () => weld.WeldNumber);
            AddFeatureProperty(target, "weldNumberPrefix", () => weld.WeldNumberPrefix);
        }

        private static void AddFeatureProperty(
            IDictionary<string, object?> target,
            string name,
            Func<object?> read)
        {
            try
            {
                var value = read();
                if (value is not null) target[name] = value;
            }
            catch { }
        }

        private static void AddRelation(
            Model model,
            ICollection<FeatureRelationSnapshot> target,
            string role,
            ModelObject? obj)
        {
            if (obj is null) return;
            var id = obj.Identifier?.ID ?? 0;
            var guid = GetGuid(model, obj);
            if (id == 0 && string.IsNullOrWhiteSpace(guid)) return;
            target.Add(new(role, guid, id == 0 ? null : id));
        }

        private static void AddPoint(
            ICollection<FeaturePointSnapshot> target,
            string role,
            int index,
            Point? point)
        {
            if (point is not null) target.Add(new(role, index, ToPoint(point)));
        }

        private static void AddPoints(
            ICollection<FeaturePointSnapshot> target,
            string role,
            IEnumerable points)
        {
            var index = 0;
            foreach (var point in points.OfType<Point>()) AddPoint(target, role, index++, point);
        }

        private static void AddPlanePoints(ICollection<FeaturePointSnapshot> target, Plane? plane)
        {
            if (plane?.Origin is null || plane.AxisX is null || plane.AxisY is null) return;
            var origin = plane.Origin;
            AddPoint(target, "plane-origin", 0, origin);
            AddPoint(target, "plane-axis-x", 0, new Point(
                origin.X + plane.AxisX.X, origin.Y + plane.AxisX.Y, origin.Z + plane.AxisX.Z));
            AddPoint(target, "plane-axis-y", 0, new Point(
                origin.X + plane.AxisY.X, origin.Y + plane.AxisY.Y, origin.Z + plane.AxisY.Z));
        }

        private static void AddPartAuthoringPoints(ICollection<FeaturePointSnapshot> target, Part? part)
        {
            if (part is null) return;
            try { AddPoints(target, "operative-center-line", part.GetCenterLine(false)); }
            catch { }
        }

        private static void AddPolygonPoints(
            ICollection<FeaturePointSnapshot> target,
            string role,
            Polygon? polygon)
        {
            if (polygon?.Points is null) return;
            AddPoints(target, role, polygon.Points);
        }

        private static void AddWeldGeometryPoints(
            ICollection<FeaturePointSnapshot> target,
            BaseWeld weld)
        {
            try
            {
                var geometryIndex = 0;
                foreach (var geometry in weld.GetWeldGeometries()
                             .OfType<global::Tekla.Structures.Model.Welding.WeldGeometry>())
                {
                    var polygonIndex = 0;
                    foreach (var polygon in geometry.Polygons.OfType<Polygon>())
                        AddPolygonPoints(target, $"weld-geometry-{geometryIndex}-polygon-{polygonIndex++}", polygon);
                    geometryIndex++;
                }
            }
            catch { }
        }

        private static PartRelationIndex IndexPartRelations(
            Model model,
            IEnumerable<int> partTeklaIds,
            CancellationToken ct)
        {
            var wanted = new HashSet<int>(partTeklaIds.Where(static id => id != 0));
            var assemblyByPartId = new Dictionary<int, AssemblyInfo>();
            var componentByPartId = new Dictionary<int, ComponentInfo>();
            var assemblyCache = new Dictionary<int, AssemblyInfo>();
            var componentCache = new Dictionary<int, ComponentInfo>();
            var selector = model.GetModelObjectSelector();

            var assemblies = selector.GetAllObjectsWithType(ModelObject.ModelObjectEnum.ASSEMBLY);
            while (assemblies is not null && assemblies.MoveNext())
            {
                ct.ThrowIfCancellationRequested();
                if (assemblies.Current is not Assembly assembly) continue;
                var info = ReadAssemblyInfo(model, assembly, assemblyCache);
                if (info == AssemblyInfo.Empty) continue;
                try
                {
                    if (assembly.GetMainPart() is Part mainPart)
                        AddIndexedRelation(assemblyByPartId, wanted, mainPart, info);
                    foreach (var secondary in assembly.GetSecondaries().OfType<Part>())
                        AddIndexedRelation(assemblyByPartId, wanted, secondary, info);
                }
                catch
                {
                    // A damaged assembly must not block the complete model index.
                }
            }

            var components = selector.GetAllObjectsWithType(ModelObject.ModelObjectEnum.COMPONENT);
            while (components is not null && components.MoveNext())
            {
                ct.ThrowIfCancellationRequested();
                if (components.Current is not BaseComponent component) continue;
                var info = ReadComponentInfo(model, component, componentCache);
                if (info == ComponentInfo.Empty) continue;
                try
                {
                    var children = component.GetChildren();
                    while (children is not null && children.MoveNext())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (children.Current is Part childPart)
                            AddIndexedRelation(componentByPartId, wanted, childPart, info);
                    }
                }
                catch
                {
                    // Components with stale children remain addressable themselves.
                }
            }

            return new PartRelationIndex(assemblyByPartId, componentByPartId);
        }

        private static void AddIndexedRelation<T>(
            IDictionary<int, T> target,
            ISet<int> wanted,
            Part part,
            T value)
        {
            var id = part.Identifier?.ID ?? 0;
            if (id != 0 && wanted.Contains(id)) target[id] = value;
        }

        private static AssemblyInfo ReadIndexedOrNativeAssemblyInfo(
            Model model,
            Part part,
            int partId,
            IDictionary<int, AssemblyInfo> cache,
            PartRelationIndex? relationIndex)
        {
            if (relationIndex is not null)
                return relationIndex.AssemblyByPartId.TryGetValue(partId, out var indexed)
                    ? indexed
                    : AssemblyInfo.Empty;
            return ReadAssemblyInfo(model, TryGetAssembly(part), cache);
        }

        private static ComponentInfo ReadIndexedOrNativeComponentInfo(
            Model model,
            Part part,
            int partId,
            IDictionary<int, ComponentInfo> cache,
            PartRelationIndex? relationIndex)
        {
            if (relationIndex is not null)
                return relationIndex.ComponentByPartId.TryGetValue(partId, out var indexed)
                    ? indexed
                    : ComponentInfo.Empty;
            return ReadComponentInfo(model, TryGetFatherComponent(part), cache);
        }

        private static AssemblyInfo ReadAssemblyInfo(
            Model model,
            Assembly? assembly,
            IDictionary<int, AssemblyInfo> cache)
        {
            var id = assembly?.Identifier?.ID ?? 0;
            if (id == 0) return AssemblyInfo.Empty;
            if (cache.TryGetValue(id, out var existing)) return existing;
            var mainPart = TryGetMainPart(assembly);
            var info = new AssemblyInfo(
                GetGuid(model, assembly),
                id,
                assembly?.Name?.Trim() ?? string.Empty,
                mainPart?.Identifier?.ID);
            cache[id] = info;
            return info;
        }

        private static ComponentInfo ReadComponentInfo(
            Model model,
            BaseComponent? component,
            IDictionary<int, ComponentInfo> cache)
        {
            var id = component?.Identifier?.ID ?? 0;
            if (id == 0) return ComponentInfo.Empty;
            if (cache.TryGetValue(id, out var existing)) return existing;
            var info = new ComponentInfo(
                GetGuid(model, component),
                id,
                component?.Name?.Trim() ?? string.Empty,
                component?.Number,
                component?.GetType().Name ?? string.Empty);
            cache[id] = info;
            return info;
        }

        private static SolidTopology? ReadSolidTopology(
            Solid solid,
            int maxVertices,
            out string? error)
        {
            error = null;
            try
            {
                var faces = new List<SolidFaceSnapshot>();
                var vertexCount = 0;
                var faceEnumerator = solid.GetFaceEnumerator();
                while (faceEnumerator.MoveNext())
                {
                    if (faceEnumerator.Current is not Face face) continue;
                    var loops = new List<PointSnapshot[]>();
                    var loopEnumerator = face.GetLoopEnumerator();
                    while (loopEnumerator.MoveNext())
                    {
                        if (loopEnumerator.Current is not Loop loop) continue;
                        var points = new List<PointSnapshot>();
                        var vertexEnumerator = loop.GetVertexEnumerator();
                        while (vertexEnumerator.MoveNext())
                        {
                            if (vertexEnumerator.Current is not Point point) continue;
                            var converted = ToPoint(point);
                            if (points.Count == 0 || !SamePoint(points[points.Count - 1], converted))
                            {
                                points.Add(converted);
                                vertexCount++;
                            }
                            if (vertexCount > maxVertices)
                            {
                                error = $"SOLID_VERTEX_LIMIT_EXCEEDED:{maxVertices}";
                                return null;
                            }
                        }
                        if (points.Count > 2 && SamePoint(points[0], points[points.Count - 1])) points.RemoveAt(points.Count - 1);
                        if (points.Count >= 3) loops.Add(points.ToArray());
                    }
                    if (loops.Count > 0)
                    {
                        faces.Add(new SolidFaceSnapshot(ToVector(face.Normal), loops.ToArray()));
                    }
                }
                return new SolidTopology(faces.ToArray(), vertexCount);
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        private static string EncodeGeometryPayload(IEnumerable<ModelPartSnapshot> objects)
        {
            var payload = objects.Select(static item => new GeometryPayloadItem(
                item.Guid,
                item.TeklaId,
                item.Bbox,
                item.Geometry,
                item.GeometryError));
            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, jsonOptions);
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(bytes, 0, bytes.Length);
            }
            return Convert.ToBase64String(output.ToArray());
        }

        private static Assembly? TryGetAssembly(Part part)
        {
            try { return part.GetAssembly(); }
            catch { return null; }
        }

        private static ModelObject? TryGetMainPart(Assembly? assembly)
        {
            try { return assembly?.GetMainPart(); }
            catch { return null; }
        }

        private static BaseComponent? TryGetFatherComponent(ModelObject obj)
        {
            try { return obj.GetFatherComponent(); }
            catch { return null; }
        }

        private static CoordinateSystemSnapshot? TryGetCoordinateSystem(ModelObject obj)
        {
            try
            {
                var cs = obj.GetCoordinateSystem();
                return cs is null
                    ? null
                    : new CoordinateSystemSnapshot(ToPoint(cs.Origin), ToVector(cs.AxisX), ToVector(cs.AxisY));
            }
            catch { return null; }
        }

        private static PointSnapshot[] ReadCenterLine(
            Part part,
            IReadOnlyCollection<AuthoringPathSnapshot> authoringPaths)
        {
            var controlAxis = authoringPaths.FirstOrDefault(static path =>
                string.Equals(path.Role, "control-axis", StringComparison.OrdinalIgnoreCase));
            if (controlAxis is not null && controlAxis.Points.Length > 0)
                return controlAxis.Points.Select(static point => point.Point).ToArray();

            try
            {
                return part.GetCenterLine(false)
                    .OfType<Point>()
                    .Select(ToPoint)
                    .ToArray();
            }
            catch { return Array.Empty<PointSnapshot>(); }
        }

        private static AuthoringPathSnapshot[] ReadAuthoringPaths(Part part)
        {
            var paths = new List<AuthoringPathSnapshot>();
            switch (part)
            {
                case Beam beam:
                    paths.Add(new AuthoringPathSnapshot(
                        "control-axis",
                        0,
                        "linear-axis",
                        false,
                        new[]
                        {
                            ToAuthoringPoint(0, beam.StartPoint),
                            ToAuthoringPoint(1, beam.EndPoint),
                        }));
                    break;

                case PolyBeam polyBeam:
                    AddContourAuthoringPath(paths, "control-axis", 0, "polyline-axis", false, polyBeam.Contour);
                    break;

                case ContourPlate contourPlate:
                    AddContourAuthoringPath(paths, "control-contour", 0, "contour", true, contourPlate.Contour);
                    break;

                case LoftedPlate loftedPlate:
                    var curveIndex = 0;
                    foreach (var curve in loftedPlate.BaseCurves)
                    {
                        paths.Add(new AuthoringPathSnapshot(
                            "base-curve",
                            curveIndex++,
                            "linear-axis",
                            false,
                            new[]
                            {
                                ToAuthoringPoint(0, curve.StartPoint),
                                ToAuthoringPoint(1, curve.EndPoint),
                            }));
                    }
                    break;
            }

            return paths
                .Where(static path => path.Points.Length > 0)
                .ToArray();
        }

        private static void AddContourAuthoringPath(
            ICollection<AuthoringPathSnapshot> target,
            string role,
            int pathIndex,
            string kind,
            bool closed,
            Contour? contour)
        {
            if (contour?.ContourPoints is null) return;
            var points = contour.ContourPoints
                .OfType<ContourPoint>()
                .Select((point, pointIndex) => ToAuthoringPoint(pointIndex, point))
                .ToArray();
            if (points.Length > 0) target.Add(new(role, pathIndex, kind, closed, points));
        }

        private static AuthoringPointSnapshot ToAuthoringPoint(int index, Point point)
        {
            if (point is not ContourPoint contourPoint || contourPoint.Chamfer is null)
                return new(index, ToPoint(point), null, null, null, null, null);
            var chamfer = contourPoint.Chamfer;
            return new(
                index,
                ToPoint(point),
                chamfer.Type.ToString(),
                chamfer.X,
                chamfer.Y,
                chamfer.DZ1,
                chamfer.DZ2);
        }

        private static void AddPartPlacementProperties(IDictionary<string, object?> target, Part part)
        {
            try
            {
                var position = part.Position;
                target["positionPlane"] = position.Plane.ToString();
                target["positionPlaneOffset"] = position.PlaneOffset;
                target["positionDepth"] = position.Depth.ToString();
                target["positionDepthOffset"] = position.DepthOffset;
                target["positionRotation"] = position.Rotation.ToString();
                target["positionRotationOffset"] = position.RotationOffset;
            }
            catch { }

            try
            {
                var deforming = part.DeformingData;
                target["deformingAngle"] = deforming.Angle;
                target["deformingAngle2"] = deforming.Angle2;
                target["deformingCambering"] = deforming.Cambering;
                target["deformingShortening"] = deforming.Shortening;
            }
            catch { }

            if (part is not Beam beam) return;
            try
            {
                var start = beam.StartPointOffset;
                target["startOffsetDx"] = start.Dx;
                target["startOffsetDy"] = start.Dy;
                target["startOffsetDz"] = start.Dz;
            }
            catch { }
            try
            {
                var end = beam.EndPointOffset;
                target["endOffsetDx"] = end.Dx;
                target["endOffsetDy"] = end.Dy;
                target["endOffsetDz"] = end.Dz;
            }
            catch { }
        }

        private static void AddPartReportProperties(ModelObject obj, IDictionary<string, object?> target)
        {
            try
            {
                var stringNames = new ArrayList { "PART_POS", "ASSEMBLY_POS" };
                var doubleNames = new ArrayList { "LENGTH", "WEIGHT", "AREA" };
                var integerNames = new ArrayList();
                var values = new Hashtable();
                if (obj.GetAllReportProperties(stringNames, doubleNames, integerNames, ref values))
                {
                    foreach (DictionaryEntry entry in values)
                    {
                        if (entry.Key is not string key || entry.Value is null) continue;
                        target[key] = entry.Value is string text ? text.Trim() : entry.Value;
                    }
                    return;
                }
            }
            catch
            {
                // Older or model-specific Tekla handles may reject a batch read.
            }

            AddReportString(obj, target, "PART_POS");
            AddReportString(obj, target, "ASSEMBLY_POS");
            AddReportDouble(obj, target, "LENGTH");
            AddReportDouble(obj, target, "WEIGHT");
            AddReportDouble(obj, target, "AREA");
        }

        private static T InGlobalTransformationPlane<T>(Model model, Func<T> read)
        {
            var handler = model.GetWorkPlaneHandler();
            var previous = handler.GetCurrentTransformationPlane();
            try
            {
                handler.SetCurrentTransformationPlane(new TransformationPlane());
                return read();
            }
            finally
            {
                handler.SetCurrentTransformationPlane(previous);
            }
        }

        private static T WithModelObjectAutoFetch<T>(Func<T> read)
        {
            var previous = ModelObjectEnumerator.AutoFetch;
            try
            {
                // Tekla otherwise resolves every native property getter through
                // remoting. Prefetch keeps large part/feature pages batch-oriented.
                ModelObjectEnumerator.AutoFetch = true;
                return read();
            }
            finally
            {
                ModelObjectEnumerator.AutoFetch = previous;
            }
        }

        private static string GetGuid(Model model, ModelObject? obj)
        {
            if (obj?.Identifier is null) return string.Empty;
            try
            {
                var direct = obj.Identifier.GUID;
                if (direct != Guid.Empty) return direct.ToString();
            }
            catch
            {
                // Tekla 2020 can leave GUID empty on some remote handles.
            }
            try { return model.GetGUIDByIdentifier(obj.Identifier).ToString(); }
            catch { return string.Empty; }
        }

        private static void AddReportString(ModelObject obj, IDictionary<string, object?> target, string name)
        {
            try
            {
                var value = string.Empty;
                if (obj.GetReportProperty(name, ref value) && !string.IsNullOrWhiteSpace(value)) target[name] = value.Trim();
            }
            catch { }
        }

        private static void AddReportDouble(ModelObject obj, IDictionary<string, object?> target, string name)
        {
            try
            {
                var value = 0.0;
                if (obj.GetReportProperty(name, ref value)) target[name] = value;
            }
            catch { }
        }

        private static void Increment(IDictionary<string, int> counts, string key)
            => counts[key] = counts.TryGetValue(key, out var current) ? current + 1 : 1;

        private static bool SamePoint(PointSnapshot a, PointSnapshot b)
            => Math.Abs(a.X - b.X) <= 1e-6 && Math.Abs(a.Y - b.Y) <= 1e-6 && Math.Abs(a.Z - b.Z) <= 1e-6;

        private static PointSnapshot ToPoint(Point point) => new(point.X, point.Y, point.Z);
        private static VectorSnapshot ToVector(Vector vector) => new(vector.X, vector.Y, vector.Z);

        public sealed class ModelSelectionRequest
        {
            public string? Scope { get; set; }
            public bool? IncludeGeometry { get; set; }
            public bool? CompactGeometry { get; set; }
            public bool? IncludeSolidBounds { get; set; }
            public bool? IncludeReportProperties { get; set; }
            public bool? IncludeRelations { get; set; }
            public bool? IncludePlacement { get; set; }
            public bool? IncludeCoordinateSystem { get; set; }
            public bool? IncludeFeatures { get; set; }
            public bool? IndexRelations { get; set; }
            public bool? RefreshSelectionIndex { get; set; }
            public string? SnapshotId { get; set; }
            public int[]? TeklaIds { get; set; }
            public int[]? FeaturePartTeklaIds { get; set; }
            public int? Offset { get; set; }
            public int? MaxObjects { get; set; }
            public int? FeatureOffset { get; set; }
            public int? MaxFeatures { get; set; }
            public int? MaxVerticesPerObject { get; set; }
        }

        private sealed record SelectionSnapshot(
            string Scope,
            ModelIdentity Model,
            string SnapshotId,
            int SelectedObjectCount,
            int ExpandedObjectCount,
            int TotalPartCount,
            int TotalFeatureCount,
            int Offset,
            bool HasMore,
            int FeatureOffset,
            bool FeatureHasMore,
            UnsupportedObjectType[] Unsupported,
            List<ModelPartSnapshot> Objects,
            List<ModelFeatureSnapshot> Features);

        private sealed record ModelIdentity(string Name, string Path);
        private sealed record SelectionIndex(
            string SnapshotId,
            string Scope,
            string ModelName,
            string ModelPath,
            int[] SelectedObjectIds,
            int ExpandedObjectCount,
            int[] PartTeklaIds,
            Model ModelSession,
            Dictionary<int, Part> PartCache,
            bool FeaturesIndexed,
            FeatureIndexEntry[] Features,
            UnsupportedObjectType[] Unsupported,
            Dictionary<int, AssemblyInfo> AssemblyCache,
            Dictionary<int, ComponentInfo> ComponentCache,
            PartRelationIndex? Relations)
        {
            public bool BelongsTo(Model model, string modelName, string modelPath)
                => ReferenceEquals(ModelSession, model) &&
                   string.Equals(ModelName, modelName, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(ModelPath, modelPath, StringComparison.OrdinalIgnoreCase);
        }
        private sealed record PartRelationIndex(
            Dictionary<int, AssemblyInfo> AssemblyByPartId,
            Dictionary<int, ComponentInfo> ComponentByPartId);
        private sealed record FeatureIndexEntry(int FeatureTeklaId, int? OwnerPartTeklaId);
        private sealed record UnsupportedObjectType(string ObjectType, int Count);
        private sealed record PointSnapshot(double X, double Y, double Z);
        private sealed record VectorSnapshot(double X, double Y, double Z);
        private sealed record BoxSnapshot(PointSnapshot Min, PointSnapshot Max);
        private sealed record CoordinateSystemSnapshot(PointSnapshot Origin, VectorSnapshot AxisX, VectorSnapshot AxisY);
        private sealed record AuthoringPointSnapshot(
            int Index,
            PointSnapshot Point,
            string? ChamferType,
            double? ChamferX,
            double? ChamferY,
            double? ChamferDz1,
            double? ChamferDz2);
        private sealed record AuthoringPathSnapshot(
            string Role,
            int Index,
            string Kind,
            bool Closed,
            AuthoringPointSnapshot[] Points);
        private sealed record AssemblyInfo(string Guid, int? TeklaId, string Name, int? MainPartTeklaId)
        {
            public static readonly AssemblyInfo Empty = new(string.Empty, null, string.Empty, null);
        }
        private sealed record ComponentInfo(string Guid, int? TeklaId, string Name, int? Number, string Type)
        {
            public static readonly ComponentInfo Empty = new(string.Empty, null, string.Empty, null, string.Empty);
        }
        private sealed record SolidFaceSnapshot(VectorSnapshot Normal, PointSnapshot[][] Loops);
        private sealed record SolidTopology(SolidFaceSnapshot[] Faces, int VertexCount);
        private sealed record GeometryPayloadItem(
            string Guid,
            int TeklaId,
            BoxSnapshot? Bbox,
            SolidTopology? Geometry,
            string? GeometryError);
        private sealed record FeaturePointSnapshot(string Role, int Index, PointSnapshot Point);
        private sealed record FeatureRelationSnapshot(string Role, string ObjectGuid, int? TeklaId);
        private sealed record ModelFeatureSnapshot(
            string Guid,
            int TeklaId,
            string FeatureKind,
            string ObjectType,
            string OwnerGuid,
            int? OwnerTeklaId,
            CoordinateSystemSnapshot? CoordinateSystem,
            AuthoringPathSnapshot[] AuthoringPaths,
            FeaturePointSnapshot[] Points,
            FeatureRelationSnapshot[] Relations,
            IDictionary<string, object?> Properties);

        private sealed record ModelPartSnapshot(
            string Guid,
            int TeklaId,
            string ObjectType,
            string Name,
            string Profile,
            string Material,
            string ClassName,
            string Finish,
            string AssemblyGuid,
            int? AssemblyTeklaId,
            string AssemblyName,
            bool IsAssemblyMainPart,
            string ComponentGuid,
            int? ComponentTeklaId,
            string ComponentName,
            int? ComponentNumber,
            string ComponentType,
            CoordinateSystemSnapshot? CoordinateSystem,
            PointSnapshot[] CenterLine,
            AuthoringPathSnapshot[] AuthoringPaths,
            BoxSnapshot? Bbox,
            SolidTopology? Geometry,
            string? GeometryError,
            IDictionary<string, object?> Properties);
    }
}
