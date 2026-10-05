// POST /plugins/fachwerk/columns/refresh
// Re-runs the currently loaded Fachwerk column plugin for explicitly named
// existing components. No component input or attributes are rewritten here.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Platform.Bridge.Desktop.Tekla.Tekla;
using Tekla.Structures.Model;
using TeklaBoolean = Tekla.Structures.Model.Boolean;

namespace Platform.Bridge.Desktop.Tekla.Http
{
    public sealed class FachwerkColumnRefreshHandler
    {
        private const string PluginName = "FachwerkColumnPlugin";
        private const string BevelProfileAttribute = "fk_bevel_profile";
        private const string SupportedBevelProfile = "TRI_A14*14";
        private const int MaxComponents = 47;
        private readonly TeklaWorker _worker;

        public FachwerkColumnRefreshHandler(TeklaWorker worker)
        {
            _worker = worker;
        }

        public async Task<HttpResult> HandleAsync(RequestContext request, CancellationToken ct)
        {
            RefreshRequest? body;
            try
            {
                body = await request.ReadJsonAsync<RefreshRequest>();
            }
            catch (Exception ex)
            {
                return HttpResult.BadRequest("FACHWERK_REFRESH_REQUEST_INVALID", ex.Message);
            }

            var validation = Validate(body);
            if (validation is not null) return validation;

            try
            {
                var result = await _worker.RunAsync(
                    model => Refresh(model, body!, ct),
                    ct);
                return HttpResult.Ok(new
                {
                    ok = true,
                    applied = body!.Apply,
                    requestedBevelProfile = body.BevelProfile,
                    model = result.Model,
                    componentCount = result.Components.Length,
                    components = result.Components,
                });
            }
            catch (TeklaDisconnectedException ex)
            {
                return HttpResult.ServiceUnavailable("TEKLA_DISCONNECTED", ex.Message);
            }
            catch (TeklaStaleHandleException ex)
            {
                return HttpResult.ServiceUnavailable("BRIDGE_STALE_TEKLA", ex.Message);
            }
            catch (RefreshConflictException ex)
            {
                return HttpResult.Conflict(ex.ErrorCode, ex.Message);
            }
            catch (Exception ex)
            {
                return HttpResult.ServerError(
                    "FACHWERK_REFRESH_FAILED",
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static HttpResult? Validate(RefreshRequest? request)
        {
            if (request is null ||
                string.IsNullOrWhiteSpace(request.ExpectedModelName) ||
                string.IsNullOrWhiteSpace(request.ExpectedModelPath))
            {
                return HttpResult.BadRequest(
                    "FACHWERK_REFRESH_MODEL_IDENTITY_MISSING",
                    "expectedModelName and expectedModelPath are required.");
            }

            var keys = NormalizeKeys(request.ProfileKeys);
            if (keys.Length == 0 || keys.Length > MaxComponents)
            {
                return HttpResult.BadRequest(
                    "FACHWERK_REFRESH_PROFILE_KEYS_INVALID",
                    $"profileKeys must contain between 1 and {MaxComponents} unique values.");
            }

            if (request.BevelProfile is not null &&
                !string.Equals(
                    request.BevelProfile.Trim(),
                    SupportedBevelProfile,
                    StringComparison.OrdinalIgnoreCase))
            {
                return HttpResult.BadRequest(
                    "FACHWERK_REFRESH_BEVEL_PROFILE_INVALID",
                    $"bevelProfile must be omitted or equal to '{SupportedBevelProfile}'.");
            }

            return null;
        }

        private static RefreshResult Refresh(Model model, RefreshRequest request, CancellationToken ct)
        {
            var info = model.GetInfo();
            var modelName = info?.ModelName ?? string.Empty;
            var modelPath = NormalizePath(info?.ModelPath);
            if (!string.Equals(modelName.Trim(), request.ExpectedModelName!.Trim(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(modelPath, NormalizePath(request.ExpectedModelPath), StringComparison.OrdinalIgnoreCase))
            {
                throw new RefreshConflictException(
                    "FACHWERK_REFRESH_MODEL_IDENTITY_MISMATCH",
                    $"Connected model is '{modelName}' at '{modelPath}', expected " +
                    $"'{request.ExpectedModelName}' at '{NormalizePath(request.ExpectedModelPath)}'.");
            }

            var requestedKeys = NormalizeKeys(request.ProfileKeys);
            var all = EnumerateColumns(model).ToArray();
            var resolved = new List<ColumnIdentity>(requestedKeys.Length);
            foreach (var key in requestedKeys)
            {
                ct.ThrowIfCancellationRequested();
                var matches = all.Where(column => column.Matches(key)).ToArray();
                if (matches.Length == 0)
                {
                    throw new RefreshConflictException(
                        "FACHWERK_REFRESH_COMPONENT_NOT_FOUND",
                        $"Fachwerk column '{key}' was not found.");
                }
                if (matches.Length > 1)
                {
                    throw new RefreshConflictException(
                        "FACHWERK_REFRESH_COMPONENT_DUPLICATE",
                        $"Fachwerk column '{key}' resolved to {matches.Length} components.");
                }
                resolved.Add(matches[0]);
            }

            var rows = new List<ComponentRefreshResult>(resolved.Count);
            foreach (var column in resolved)
            {
                ct.ThrowIfCancellationRequested();
                var before = CaptureChildren(model, column.Component);
                var beforeBevelProfile = ReadAttribute(column.Component, BevelProfileAttribute);
                if (request.Apply && request.BevelProfile is not null)
                {
                    column.Component.SetAttribute(BevelProfileAttribute, SupportedBevelProfile);
                }
                if (request.Apply && !column.Component.Modify())
                {
                    throw new RefreshConflictException(
                        "FACHWERK_REFRESH_MODIFY_FALSE",
                        $"Component.Modify() returned false for '{column.ProfileKey}'.");
                }
                var after = request.Apply ? CaptureChildren(model, column.Component) : before;
                var afterBevelProfile = request.Apply
                    ? ReadAttribute(column.Component, BevelProfileAttribute)
                    : beforeBevelProfile;
                rows.Add(new ComponentRefreshResult(
                    column.ProfileKey,
                    column.Mark,
                    column.ExternalObjectId,
                    ReadGuid(model, column.Component),
                    column.Component.Identifier?.ID ?? 0,
                    beforeBevelProfile,
                    afterBevelProfile,
                    before,
                    after,
                    before.PartGuids.Intersect(after.PartGuids, StringComparer.OrdinalIgnoreCase).Count(),
                    before.PartGuids.Except(after.PartGuids, StringComparer.OrdinalIgnoreCase).ToArray(),
                    after.PartGuids.Except(before.PartGuids, StringComparer.OrdinalIgnoreCase).ToArray()));
            }

            if (request.Apply && !model.CommitChanges())
                throw new InvalidOperationException("Tekla CommitChanges() returned false.");

            return new RefreshResult(
                new ModelIdentity(modelName, modelPath),
                rows.ToArray());
        }

        private static IEnumerable<ColumnIdentity> EnumerateColumns(Model model)
        {
            ModelObjectEnumerator enumerator;
            try
            {
                enumerator = model.GetModelObjectSelector()
                    .GetAllObjectsWithType(ModelObject.ModelObjectEnum.COMPONENT);
            }
            catch
            {
                yield break;
            }

            while (true)
            {
                bool moved;
                try { moved = enumerator.MoveNext(); }
                catch { yield break; }
                if (!moved) yield break;
                if (enumerator.Current is not Component component ||
                    !string.Equals(component.Name, PluginName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return new ColumnIdentity(
                    component,
                    ReadAttribute(component, "fk_profile_key"),
                    ReadAttribute(component, "fk_mark"),
                    ReadAttribute(component, "fk_external_id"));
            }
        }

        private static ChildSummary CaptureChildren(Model model, Component component)
        {
            var parts = new List<Part>();
            var weldCount = 0;
            var booleanCount = 0;
            var children = component.GetChildren();
            while (children.MoveNext())
            {
                switch (children.Current)
                {
                    case Part part:
                        parts.Add(part);
                        break;
                    case BaseWeld:
                        weldCount++;
                        break;
                    case TeklaBoolean:
                        booleanCount++;
                        break;
                }
            }

            var assemblyGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mainPartCount = 0;
            foreach (var part in parts)
            {
                try
                {
                    var assembly = part.GetAssembly();
                    if (assembly is null) continue;
                    var assemblyGuid = ReadGuid(model, assembly);
                    if (!string.IsNullOrWhiteSpace(assemblyGuid)) assemblyGuids.Add(assemblyGuid);
                    if (assembly.GetMainPart() is Part main &&
                        main.Identifier?.ID == part.Identifier?.ID)
                    {
                        mainPartCount++;
                    }
                }
                catch { }
            }

            return new ChildSummary(
                parts.Count,
                parts.GroupBy(static part => part.GetType().Name)
                    .ToDictionary(static group => group.Key, static group => group.Count()),
                parts.Select(part => ReadGuid(model, part))
                    .Where(static guid => !string.IsNullOrWhiteSpace(guid))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static guid => guid, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                parts.Select(part => CapturePartSemantic(model, part))
                    .OrderBy(static part => part.Role, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static part => part.BreakIndex)
                    .ThenBy(static part => part.MinZ)
                    .ToArray(),
                assemblyGuids.Count,
                mainPartCount,
                weldCount,
                booleanCount);
        }

        private static PartSemanticSnapshot CapturePartSemantic(Model model, Part part)
        {
            var solid = part.GetSolid();
            return new PartSemanticSnapshot(
                ReadGuid(model, part),
                part.Identifier?.ID ?? 0,
                part.GetType().Name,
                part.Profile?.ProfileString?.Trim() ?? string.Empty,
                ReadUserProperty(part, "FK_OWNER"),
                ReadUserProperty(part, "FK_MARK"),
                ReadUserProperty(part, "FK_ROLE"),
                ReadUserProperty(part, "FK_GEOMETRY"),
                ReadUserProperty(part, "FK_BREAK", 0),
                solid.MinimumPoint.Z,
                solid.MaximumPoint.Z,
                ReadCenterLine(part));
        }

        private static PointSnapshot[] ReadCenterLine(Part part)
        {
            try
            {
                return part.GetCenterLine(false)
                    .OfType<global::Tekla.Structures.Geometry3d.Point>()
                    .Select(static point => new PointSnapshot(point.X, point.Y, point.Z))
                    .ToArray();
            }
            catch
            {
                return Array.Empty<PointSnapshot>();
            }
        }

        private static string ReadUserProperty(ModelObject value, string name)
        {
            var result = string.Empty;
            try { value.GetUserProperty(name, ref result); }
            catch { return string.Empty; }
            return result?.Trim() ?? string.Empty;
        }

        private static int ReadUserProperty(ModelObject value, string name, int fallback)
        {
            var result = fallback;
            try { value.GetUserProperty(name, ref result); }
            catch { return fallback; }
            return result;
        }

        private static string ReadAttribute(Component component, string name)
        {
            var value = string.Empty;
            try { component.GetAttribute(name, ref value); }
            catch { return string.Empty; }
            return value?.Trim() ?? string.Empty;
        }

        private static string ReadGuid(Model model, ModelObject value)
        {
            try
            {
                var direct = value.Identifier?.GUID ?? Guid.Empty;
                if (direct != Guid.Empty) return direct.ToString();
            }
            catch { }
            try { return model.GetGUIDByIdentifier(value.Identifier) ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static string[] NormalizeKeys(IEnumerable<string>? values) =>
            (values ?? Array.Empty<string>())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        private static string NormalizePath(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var trimmed = value!.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            try { return Path.GetFullPath(trimmed); }
            catch { return trimmed; }
        }

        public sealed class RefreshRequest
        {
            public string? ExpectedModelName { get; set; }
            public string? ExpectedModelPath { get; set; }
            public string[]? ProfileKeys { get; set; }
            public string? BevelProfile { get; set; }
            public bool Apply { get; set; }
        }

        private sealed record ColumnIdentity(
            Component Component,
            string ProfileKey,
            string Mark,
            string ExternalObjectId)
        {
            public bool Matches(string key) =>
                string.Equals(ProfileKey, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Mark, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ExternalObjectId, key, StringComparison.OrdinalIgnoreCase);
        }

        private sealed record ModelIdentity(string Name, string Path);
        private sealed record RefreshResult(ModelIdentity Model, ComponentRefreshResult[] Components);
        private sealed record ChildSummary(
            int PartCount,
            IReadOnlyDictionary<string, int> PartTypes,
            string[] PartGuids,
            PartSemanticSnapshot[] Parts,
            int AssemblyCount,
            int MainPartCount,
            int WeldCount,
            int BooleanCount);
        private sealed record PointSnapshot(double X, double Y, double Z);
        private sealed record PartSemanticSnapshot(
            string Guid,
            int TeklaId,
            string ObjectType,
            string Profile,
            string Owner,
            string Mark,
            string Role,
            string Geometry,
            int BreakIndex,
            double MinZ,
            double MaxZ,
            PointSnapshot[] CenterLine);
        private sealed record ComponentRefreshResult(
            string ProfileKey,
            string Mark,
            string ExternalObjectId,
            string ComponentGuid,
            int ComponentTeklaId,
            string BeforeBevelProfile,
            string AfterBevelProfile,
            ChildSummary Before,
            ChildSummary After,
            int RetainedPartGuidCount,
            string[] RemovedPartGuids,
            string[] AddedPartGuids);

        private sealed class RefreshConflictException : Exception
        {
            public RefreshConflictException(string errorCode, string message) : base(message)
            {
                ErrorCode = errorCode;
            }

            public string ErrorCode { get; }
        }
    }
}
