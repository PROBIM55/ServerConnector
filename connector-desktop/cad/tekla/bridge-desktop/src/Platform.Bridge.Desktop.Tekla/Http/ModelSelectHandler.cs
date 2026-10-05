// POST /model/select - пакетно заменяет текущее выделение Tekla объектами по GUID/ID.
// Геометрия через этот endpoint не передаётся: web использует стабильные ссылки
// опубликованного поколения PostgreSQL, а Bridge только разрешает их в живой модели.

#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Platform.Bridge.Desktop.Tekla.Tekla;
using Tekla.Structures;
using Tekla.Structures.Model;

namespace Platform.Bridge.Desktop.Tekla.Http
{
    public sealed class ModelSelectHandler
    {
        private const int MaxObjects = 10000;
        private readonly TeklaWorker _worker;

        public ModelSelectHandler(TeklaWorker worker)
        {
            _worker = worker;
        }

        public async Task<HttpResult> HandleAsync(RequestContext request, CancellationToken ct)
        {
            ModelSelectRequest? options;
            try
            {
                options = await request.ReadJsonAsync<ModelSelectRequest>();
            }
            catch (Exception ex)
            {
                return HttpResult.BadRequest("MODEL_SELECT_REQUEST_INVALID", ex.Message);
            }

            options ??= new ModelSelectRequest();
            var objectGuids = (options.ObjectGuids ?? Array.Empty<string>())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var teklaIds = (options.TeklaIds ?? Array.Empty<int>())
                .Where(static value => value != 0)
                .Distinct()
                .ToArray();
            var objects = (options.Objects ?? Array.Empty<ModelSelectObjectRef>())
                .Where(static value => value is not null)
                .Select(static value => new ModelSelectObjectRef
                {
                    Guid = value.Guid?.Trim(),
                    TeklaId = value.TeklaId,
                })
                .Where(static value => value.TeklaId != 0 || !string.IsNullOrWhiteSpace(value.Guid))
                .GroupBy(static value => $"{value.TeklaId}:{value.Guid}", StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.First())
                .ToArray();
            var requestedCount = objects.Length + objectGuids.Length + teklaIds.Length;
            var clear = options.Clear ?? false;

            if (!clear && requestedCount == 0)
            {
                return HttpResult.BadRequest(
                    "MODEL_SELECT_OBJECTS_MISSING",
                    "objectGuids or teklaIds must contain at least one object.");
            }
            if (requestedCount > MaxObjects)
            {
                return HttpResult.BadRequest(
                    "MODEL_SELECT_TOO_MANY_OBJECTS",
                    $"A single selection may contain at most {MaxObjects} objects.");
            }

            try
            {
                var result = await _worker.RunAsync(
                    model => ApplySelection(
                        model,
                        clear ? Array.Empty<ModelSelectObjectRef>() : objects,
                        clear ? Array.Empty<string>() : objectGuids,
                        clear ? Array.Empty<int>() : teklaIds,
                        options.ShowDimensions ?? false,
                        ct),
                    ct);

                if (!clear && result.Objects.Length == 0)
                {
                    return HttpResult.NotFound(
                        "MODEL_SELECT_OBJECTS_NOT_FOUND",
                        "None of the requested objects exists in the connected Tekla model.");
                }

                return HttpResult.Ok(new
                {
                    ok = true,
                    applied = result.Applied,
                    cleared = clear,
                    requestedCount,
                    selectedCount = result.Objects.Length,
                    missingCount = result.MissingCount,
                    missingGuids = result.MissingGuids,
                    missingTeklaIds = result.MissingTeklaIds,
                    objects = result.Objects,
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
                return HttpResult.ServerError("MODEL_SELECT_FAILED", $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static ModelSelectResult ApplySelection(
            Model model,
            IReadOnlyList<ModelSelectObjectRef> objects,
            IReadOnlyList<string> objectGuids,
            IReadOnlyList<int> teklaIds,
            bool showDimensions,
            CancellationToken ct)
        {
            var selectedObjects = new ArrayList();
            var selectedIds = new HashSet<int>();
            var selected = new List<ModelSelectedObject>();
            var missingGuids = new List<string>();
            var missingTeklaIds = new List<int>();
            var missingCount = 0;

            foreach (var objectRef in objects)
            {
                ct.ThrowIfCancellationRequested();
                var expectedGuid = objectRef.Guid ?? string.Empty;
                var obj = objectRef.TeklaId != 0
                    ? TrySelectByTeklaId(model, objectRef.TeklaId)
                    : null;
                var actualGuid = obj is null ? string.Empty : GetGuid(model, obj);

                // Tekla ID is the fast session-local address. GUID remains the
                // persistent identity and prevents selecting a recycled ID.
                if (obj is not null && !string.IsNullOrWhiteSpace(expectedGuid)
                    && !string.Equals(actualGuid, expectedGuid, StringComparison.OrdinalIgnoreCase))
                {
                    obj = null;
                    actualGuid = string.Empty;
                }

                if (obj is null && !string.IsNullOrWhiteSpace(expectedGuid))
                {
                    obj = TrySelectByGuid(model, expectedGuid);
                    actualGuid = obj is null ? string.Empty : GetGuid(model, obj);
                }

                if (obj is null || (obj.Identifier?.ID ?? 0) == 0)
                {
                    missingCount++;
                    if (!string.IsNullOrWhiteSpace(expectedGuid)) missingGuids.Add(expectedGuid);
                    if (objectRef.TeklaId != 0) missingTeklaIds.Add(objectRef.TeklaId);
                    continue;
                }

                AddObject(model, obj, selectedObjects, selectedIds, selected, actualGuid);
            }

            foreach (var guid in objectGuids)
            {
                ct.ThrowIfCancellationRequested();
                var obj = TrySelectByGuid(model, guid);

                if (obj is null || (obj.Identifier?.ID ?? 0) == 0)
                {
                    missingCount++;
                    missingGuids.Add(guid);
                    continue;
                }
                AddObject(model, obj, selectedObjects, selectedIds, selected, guid);
            }

            foreach (var teklaId in teklaIds)
            {
                ct.ThrowIfCancellationRequested();
                var obj = TrySelectByTeklaId(model, teklaId);

                if (obj is null || (obj.Identifier?.ID ?? 0) == 0)
                {
                    missingCount++;
                    missingTeklaIds.Add(teklaId);
                    continue;
                }
                AddObject(model, obj, selectedObjects, selectedIds, selected);
            }

            var shouldApply = objects.Count == 0 && objectGuids.Count == 0 && teklaIds.Count == 0
                || selectedObjects.Count > 0;
            var applied = shouldApply && new global::Tekla.Structures.Model.UI.ModelObjectSelector()
                .Select(selectedObjects, showDimensions);

            return new ModelSelectResult(
                applied,
                selected.ToArray(),
                missingCount,
                missingGuids.ToArray(),
                missingTeklaIds.ToArray());
        }

        private static ModelObject? TrySelectByTeklaId(Model model, int teklaId)
        {
            if (teklaId == 0) return null;
            try { return model.SelectModelObject(new Identifier(teklaId)); }
            catch { return null; }
        }

        private static ModelObject? TrySelectByGuid(Model model, string guid)
        {
            if (string.IsNullOrWhiteSpace(guid)) return null;
            try
            {
                var identifier = model.GetIdentifierByGUID(guid);
                return identifier is not null && identifier.ID != 0
                    ? model.SelectModelObject(identifier)
                    : null;
            }
            catch { return null; }
        }

        private static void AddObject(
            Model model,
            ModelObject obj,
            ArrayList selectedObjects,
            HashSet<int> selectedIds,
            List<ModelSelectedObject> selected,
            string? knownGuid = null)
        {
            var teklaId = obj.Identifier?.ID ?? 0;
            if (teklaId == 0 || !selectedIds.Add(teklaId)) return;

            selectedObjects.Add(obj);
            var guid = string.IsNullOrWhiteSpace(knownGuid) ? GetGuid(model, obj) : knownGuid!;
            selected.Add(new ModelSelectedObject(guid, teklaId, obj.GetType().Name));
        }

        private static string GetGuid(Model model, ModelObject obj)
        {
            try
            {
                var direct = obj.Identifier?.GUID ?? Guid.Empty;
                if (direct != Guid.Empty) return direct.ToString();
            }
            catch
            {
                // Tekla 2020 can leave GUID empty on some remote handles.
            }
            try { return model.GetGUIDByIdentifier(obj.Identifier) ?? string.Empty; }
            catch { return string.Empty; }
        }

        public sealed class ModelSelectRequest
        {
            public ModelSelectObjectRef[]? Objects { get; set; }
            public string[]? ObjectGuids { get; set; }
            public int[]? TeklaIds { get; set; }
            public bool? Clear { get; set; }
            public bool? ShowDimensions { get; set; }
        }

        public sealed class ModelSelectObjectRef
        {
            public string? Guid { get; set; }
            public int TeklaId { get; set; }
        }

        private sealed record ModelSelectResult(
            bool Applied,
            ModelSelectedObject[] Objects,
            int MissingCount,
            string[] MissingGuids,
            int[] MissingTeklaIds);

        private sealed record ModelSelectedObject(string Guid, int TeklaId, string ObjectType);
    }
}
