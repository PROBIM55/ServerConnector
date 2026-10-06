// POST /model/changes/apply - atomic semantic write boundary for a validated
// Platform change set. Render meshes and client transforms are never accepted.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Platform.Bridge.Desktop.Tekla.Components.Fachwerk;
using Platform.Bridge.Desktop.Tekla.Tekla;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace Platform.Bridge.Desktop.Tekla.Http
{
    public sealed class ModelChangeSetHandler
    {
        private const int MaxCommands = 1000;
        private const string FachwerkColumnPluginName = "FachwerkColumnPlugin";
        private const string FachwerkRigelPluginName = "FachwerkRigelPlugin";
        private const string FachwerkPartPrefix = "515-60.";
        private const string FachwerkRigelPartName = "РИГЕЛЬ";
        private readonly TeklaWorker _worker;
        private readonly string _teklaVersion;

        public ModelChangeSetHandler(TeklaWorker worker, string teklaVersion)
        {
            _worker = worker;
            _teklaVersion = teklaVersion;
        }

        public async Task<HttpResult> ApplyAsync(RequestContext request, CancellationToken ct)
        {
            ModelChangeSetRequest? body;
            try
            {
                body = await request.ReadJsonAsync<ModelChangeSetRequest>();
            }
            catch (Exception ex)
            {
                return HttpResult.BadRequest("MODEL_CHANGE_SET_REQUEST_INVALID", ex.Message);
            }

            var validation = ValidateRequest(body);
            if (validation is not null) return validation;
            try
            {
                var result = await _worker.RunAsync(model => Apply(model, body!, _teklaVersion, ct), ct);
                return HttpResult.Ok(new
                {
                    ok = true,
                    changeSetId = body!.ChangeSetId,
                    applied = true,
                    commandCount = result.Length,
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
            catch (ChangeSetConflictException ex)
            {
                return HttpResult.Conflict(ex.ErrorCode, ex.Message);
            }
            catch (Exception ex)
            {
                return HttpResult.ServerError("MODEL_CHANGE_SET_APPLY_FAILED", $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static HttpResult? ValidateRequest(ModelChangeSetRequest? request)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.ChangeSetId))
                return HttpResult.BadRequest("MODEL_CHANGE_SET_ID_MISSING", "changeSetId is required.");
            if (string.IsNullOrWhiteSpace(request.ModelFingerprint) ||
                string.IsNullOrWhiteSpace(request.ExpectedTeklaVersion) ||
                string.IsNullOrWhiteSpace(request.ExpectedModelName) ||
                string.IsNullOrWhiteSpace(request.ExpectedModelPath))
            {
                return HttpResult.BadRequest(
                    "MODEL_CHANGE_SET_MODEL_IDENTITY_MISSING",
                    "modelFingerprint, expectedTeklaVersion, expectedModelName and expectedModelPath are required.");
            }
            if (request.Items is null || request.Items.Length == 0)
                return HttpResult.BadRequest("MODEL_CHANGE_SET_ITEMS_MISSING", "items must contain at least one command.");
            if (request.Items.Length > MaxCommands)
                return HttpResult.BadRequest("MODEL_CHANGE_SET_TOO_LARGE", $"At most {MaxCommands} commands are allowed.");
            if (request.Items.Any(static item => item is null))
                return HttpResult.BadRequest("MODEL_CHANGE_SET_ITEM_INVALID", "items cannot contain null commands.");
            var duplicate = request.Items
                .GroupBy(static item => item.ItemNo)
                .FirstOrDefault(static group => group.Count() > 1);
            if (duplicate is not null)
                return HttpResult.BadRequest("MODEL_CHANGE_SET_ITEM_DUPLICATE", $"Duplicate itemNo {duplicate.Key}.");
            return null;
        }

        private static AppliedChangeItem[] Apply(
            Model model,
            ModelChangeSetRequest request,
            string actualTeklaVersion,
            CancellationToken ct)
        {
            ValidateModelIdentity(model, request, actualTeklaVersion);
            var prepared = new List<PreparedModelChange>(request.Items!.Length);
            foreach (var item in request.Items.OrderBy(static item => item.ItemNo))
            {
                ct.ThrowIfCancellationRequested();
                if (string.Equals(item.CommandKind, "beam.modify", StringComparison.OrdinalIgnoreCase))
                {
                    var target = ResolveRequiredObject(model, item.ItemNo, item.TargetGuid, "target");
                    EnsureRigelIsMutable(item.ItemNo, target, "target");
                    if (target is not Beam beam || target is PolyBeam)
                    {
                        throw new ChangeSetConflictException(
                            "MODEL_CHANGE_SET_TARGET_TYPE_MISMATCH",
                            $"Object '{item.TargetGuid}' is not a Beam.");
                    }
                    var payload = DeserializePayload<BeamModifyPayload>(item, "Beam");
                    ValidateBeamPayload(item.ItemNo, payload);
                    prepared.Add(new PreparedBeamChange(
                        item.ItemNo,
                        item.TargetGuid!,
                        beam,
                        payload,
                        BeamSnapshot.Capture(beam)));
                    continue;
                }

                if (string.Equals(item.CommandKind, "poly-beam.modify", StringComparison.OrdinalIgnoreCase))
                {
                    var target = ResolveRequiredObject(model, item.ItemNo, item.TargetGuid, "target");
                    EnsureRigelIsMutable(item.ItemNo, target, "target");
                    if (target is not PolyBeam polyBeam)
                    {
                        throw new ChangeSetConflictException(
                            "MODEL_CHANGE_SET_TARGET_TYPE_MISMATCH",
                            $"Object '{item.TargetGuid}' is not a PolyBeam.");
                    }
                    var payload = DeserializePayload<PolyBeamModifyPayload>(item, "PolyBeam");
                    ValidatePolyBeamPayload(item.ItemNo, payload);
                    prepared.Add(new PreparedPolyBeamChange(
                        item.ItemNo,
                        item.TargetGuid!,
                        polyBeam,
                        payload,
                        PolyBeamSnapshot.Capture(polyBeam)));
                    continue;
                }

                if (string.Equals(item.CommandKind, "poly-beam.split", StringComparison.OrdinalIgnoreCase))
                {
                    var target = ResolveRequiredObject(model, item.ItemNo, item.TargetGuid, "target");
                    EnsureRigelIsMutable(item.ItemNo, target, "target");
                    if (target is not PolyBeam polyBeam)
                    {
                        throw new ChangeSetConflictException(
                            "MODEL_CHANGE_SET_TARGET_TYPE_MISMATCH",
                            $"Object '{item.TargetGuid}' is not a PolyBeam.");
                    }
                    var payload = DeserializePayload<PolyBeamSplitPayload>(item, "PolyBeam split");
                    ValidatePolyBeamSplitPayload(item.ItemNo, payload);
                    prepared.Add(new PreparedPolyBeamSplitChange(
                        item.ItemNo,
                        item.TargetGuid!,
                        polyBeam,
                        payload,
                        PolyBeamSnapshot.Capture(polyBeam)));
                    continue;
                }

                if (string.Equals(item.CommandKind, "fitting.upsert", StringComparison.OrdinalIgnoreCase))
                {
                    var father = ResolveRequiredPart(model, item.ItemNo, item.TargetGuid, "target");
                    EnsureRigelIsMutable(item.ItemNo, father, "target");
                    var payload = DeserializePayload<FittingUpsertPayload>(item, "Fitting");
                    ValidateFittingPayload(item.ItemNo, payload);
                    prepared.Add(new PreparedFittingChange(
                        item.ItemNo,
                        item.TargetGuid!,
                        father,
                        payload));
                    continue;
                }

                if (string.Equals(item.CommandKind, "boolean-cut.create", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(item.TargetGuid))
                    {
                        throw new ChangeSetConflictException(
                            "MODEL_CHANGE_SET_CREATE_TARGET_PRESENT",
                            $"Item {item.ItemNo} creates a BooleanPart and cannot have targetGuid.");
                    }
                    var payload = DeserializePayload<BooleanCutCreatePayload>(item, "BooleanPart");
                    ValidateBooleanCutPayload(item.ItemNo, payload);
                    var father = ResolveRequiredPart(model, item.ItemNo, payload.FatherGuid, "father");
                    var source = ResolveRequiredPart(model, item.ItemNo, payload.SourcePartGuid, "sourcePart");
                    EnsureRigelIsMutable(item.ItemNo, father, "father");
                    EnsureRigelIsMutable(item.ItemNo, source, "sourcePart");
                    if (source is not Beam sourceBeam || source is PolyBeam)
                    {
                        throw new ChangeSetConflictException(
                            "MODEL_CHANGE_SET_SOURCE_TYPE_MISMATCH",
                            $"Item {item.ItemNo} sourcePart '{payload.SourcePartGuid}' must be a straight Beam.");
                    }
                    prepared.Add(new PreparedBooleanCutChange(
                        item.ItemNo,
                        father,
                        sourceBeam,
                        payload));
                    continue;
                }

                if (string.Equals(item.CommandKind, "weld.create", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(item.TargetGuid))
                    {
                        throw new ChangeSetConflictException(
                            "MODEL_CHANGE_SET_CREATE_TARGET_PRESENT",
                            $"Item {item.ItemNo} creates a Weld and cannot have targetGuid.");
                    }
                    var payload = DeserializePayload<WeldCreatePayload>(item, "Weld");
                    ValidateWeldPayload(item.ItemNo, payload);
                    var main = ResolveRequiredPart(model, item.ItemNo, payload.MainGuid, "main");
                    var secondary = ResolveRequiredPart(model, item.ItemNo, payload.SecondaryGuid, "secondary");
                    EnsureRigelIsMutable(item.ItemNo, main, "main");
                    EnsureRigelIsMutable(item.ItemNo, secondary, "secondary");
                    prepared.Add(new PreparedWeldChange(
                        item.ItemNo,
                        main,
                        secondary,
                        payload));
                    continue;
                }

                if (string.Equals(item.CommandKind, "fachwerk-column.upsert", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(item.TargetGuid))
                    {
                        throw new ChangeSetConflictException(
                            "MODEL_CHANGE_SET_CREATE_TARGET_PRESENT",
                            $"Item {item.ItemNo} upserts a Fachwerk column by externalObjectId and cannot have targetGuid.");
                    }
                    var payload = DeserializePayload<FachwerkColumnUpsertPayload>(item, "Fachwerk column");
                    ValidateFachwerkColumnPayload(item.ItemNo, payload);
                    prepared.Add(new PreparedFachwerkComponentUpsertChange(
                        item.ItemNo,
                        model,
                        FachwerkColumnPluginName,
                        payload.ExternalObjectId!,
                        BuildColumnComponent,
                        payload));
                    continue;
                }

                if (string.Equals(item.CommandKind, "fachwerk-rigel.upsert", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(item.TargetGuid))
                    {
                        throw new ChangeSetConflictException(
                            "MODEL_CHANGE_SET_CREATE_TARGET_PRESENT",
                            $"Item {item.ItemNo} upserts a Fachwerk rigel by externalObjectId and cannot have targetGuid.");
                    }
                    var payload = DeserializePayload<FachwerkRigelUpsertPayload>(item, "Fachwerk rigel");
                    ValidateFachwerkRigelPayload(item.ItemNo, payload);
                    EnsureRigelPayloadIsMutable(item.ItemNo, payload);
                    Beam? sourceBeam = null;
                    if (!string.IsNullOrWhiteSpace(payload.SourceGuid))
                    {
                        var source = TrySelectModelObjectByGuid(model, payload.SourceGuid!);
                        if (source is not null)
                        {
                            if (source is not Beam beam || source is PolyBeam)
                            {
                                throw new ChangeSetConflictException(
                                    "MODEL_CHANGE_SET_SOURCE_TYPE_MISMATCH",
                                    $"Item {item.ItemNo} Fachwerk rigel source '{payload.SourceGuid}' must be a straight Beam.");
                            }
                            EnsureRigelIsMutable(item.ItemNo, beam, "source");
                            sourceBeam = beam;
                        }
                    }
                    prepared.Add(new PreparedFachwerkRigelPartUpsertChange(
                        item.ItemNo,
                        model,
                        sourceBeam,
                        payload));
                    continue;
                }

                if (string.Equals(item.CommandKind, "fachwerk-node.upsert", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(item.TargetGuid))
                    {
                        throw new ChangeSetConflictException(
                            "MODEL_CHANGE_SET_CREATE_TARGET_PRESENT",
                            $"Item {item.ItemNo} upserts a Fachwerk node by externalObjectId and cannot have targetGuid.");
                    }
                    var payload = DeserializePayload<FachwerkNodeUpsertPayload>(item, "Fachwerk node");
                    ValidateFachwerkNodePayload(item.ItemNo, payload);
                    var column = FindFachwerkComponent(
                        model,
                        FachwerkColumnPluginName,
                        payload.ColumnExternalObjectId!);
                    if (column is null)
                    {
                        throw new ChangeSetConflictException(
                            "FACHWERK_NODE_COLUMN_NOT_FOUND",
                            $"Item {item.ItemNo} cannot find Fachwerk column '{payload.ColumnExternalObjectId}'.");
                    }
                    prepared.Add(new PreparedFachwerkNodeUpsertChange(
                        item.ItemNo,
                        model,
                        column,
                        payload));
                    continue;
                }

                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_COMMAND_UNSUPPORTED",
                    $"Command '{item.CommandKind}' is not supported by this bridge revision.");
            }

            var modified = new List<PreparedModelChange>(prepared.Count);
            try
            {
                foreach (var change in prepared)
                {
                    ct.ThrowIfCancellationRequested();
                    change.Apply();
                    modified.Add(change);
                }
                if (!model.CommitChanges())
                    throw new InvalidOperationException("Tekla CommitChanges() returned false.");
            }
            catch
            {
                Restore(model, modified);
                throw;
            }

            return prepared.Select(static change => new AppliedChangeItem(
                change.ItemNo,
                change.TargetGuid,
                change.ResultObjectGuid,
                change.TeklaId,
                "applied")).ToArray();
        }

        private static void ValidateModelIdentity(
            Model model,
            ModelChangeSetRequest request,
            string actualTeklaVersion)
        {
            if (!string.Equals(
                    MajorVersion(actualTeklaVersion),
                    MajorVersion(request.ExpectedTeklaVersion),
                    StringComparison.Ordinal))
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_TEKLA_VERSION_MISMATCH",
                    $"Connected bridge targets Tekla {actualTeklaVersion}, expected {request.ExpectedTeklaVersion}.");
            }

            var info = model.GetInfo();
            var actualName = info?.ModelName ?? string.Empty;
            var actualPath = NormalizeModelPath(info?.ModelPath);
            var expectedPath = NormalizeModelPath(request.ExpectedModelPath);
            if (!string.Equals(actualName.Trim(), request.ExpectedModelName!.Trim(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_MODEL_IDENTITY_MISMATCH",
                    $"Connected model is '{actualName}' at '{actualPath}', expected " +
                    $"'{request.ExpectedModelName}' at '{expectedPath}'.");
            }
        }

        private static string MajorVersion(string? value)
        {
            var normalized = (value ?? string.Empty).Trim();
            var separator = normalized.IndexOf('.');
            return separator < 0 ? normalized : normalized.Substring(0, separator);
        }

        private static string NormalizeModelPath(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var normalized = value!;
            try
            {
                return Path.GetFullPath(normalized.Trim())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return normalized.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }

        private static T DeserializePayload<T>(ModelChangeItem item, string label)
        {
            try
            {
                return item.Payload.Deserialize<T>(new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                }) ?? throw new JsonException($"{label} payload is empty.");
            }
            catch (Exception ex)
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                    $"Item {item.ItemNo}: {ex.Message}");
            }
        }

        private static void ValidateBeamPayload(int itemNo, BeamModifyPayload payload)
        {
            if (!ValidPoint(payload.Start) || !ValidPoint(payload.End))
                throw new ChangeSetConflictException("MODEL_CHANGE_SET_PAYLOAD_INVALID", $"Item {itemNo} has an invalid start or end point.");
            var length = Math.Sqrt(
                Square(payload.End![0] - payload.Start![0]) +
                Square(payload.End[1] - payload.Start[1]) +
                Square(payload.End[2] - payload.Start[2]));
            if (length < 1)
                throw new ChangeSetConflictException("MODEL_CHANGE_SET_BEAM_TOO_SHORT", $"Item {itemNo} beam length is below 1 mm.");
            if (string.IsNullOrWhiteSpace(payload.Profile) || string.IsNullOrWhiteSpace(payload.Material) ||
                string.IsNullOrWhiteSpace(payload.ClassName))
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                    $"Item {itemNo} requires profile, material and className.");
            }
        }

        private static void ValidatePolyBeamPayload(int itemNo, PolyBeamModifyPayload payload)
        {
            if (payload.Points is null || payload.Points.Length < 3 || payload.Points.Length > 1000)
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                    $"Item {itemNo} requires between 3 and 1000 PolyBeam contour points.");
            }
            for (var index = 0; index < payload.Points.Length; index++)
            {
                var point = payload.Points[index];
                if (point is null || !ValidPoint(point.Point) ||
                    !IsFinite(point.ChamferX) || !IsFinite(point.ChamferY) ||
                    !IsFinite(point.ChamferDz1) || !IsFinite(point.ChamferDz2) ||
                    !TryParseChamferType(point.ChamferType, out _))
                {
                    throw new ChangeSetConflictException(
                        "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                        $"Item {itemNo} has invalid PolyBeam point {index}.");
                }
            }
            if (string.IsNullOrWhiteSpace(payload.Profile) || string.IsNullOrWhiteSpace(payload.Material) ||
                string.IsNullOrWhiteSpace(payload.ClassName))
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                    $"Item {itemNo} requires profile, material and className.");
            }
        }

        private static void ValidateBooleanCutPayload(int itemNo, BooleanCutCreatePayload payload)
        {
            if (string.IsNullOrWhiteSpace(payload.FatherGuid) || string.IsNullOrWhiteSpace(payload.SourcePartGuid))
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                    $"Item {itemNo} requires fatherGuid and sourcePartGuid.");
            }
            if (string.Equals(payload.FatherGuid, payload.SourcePartGuid, StringComparison.OrdinalIgnoreCase))
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                    $"Item {itemNo} cannot cut a part with itself.");
            }
        }

        private static void ValidateFittingPayload(int itemNo, FittingUpsertPayload payload)
        {
            if (payload.Plane is null ||
                !ValidPoint(payload.Plane.Origin) ||
                !ValidPoint(payload.Plane.AxisX) ||
                !ValidPoint(payload.Plane.AxisY))
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                    $"Item {itemNo} requires a valid fitting plane origin, axisX and axisY.");
            }

            var axisX = payload.Plane.AxisX!;
            var axisY = payload.Plane.AxisY!;
            var axisXLength = VectorLength(axisX);
            var axisYLength = VectorLength(axisY);
            var crossLength = Math.Sqrt(
                Square(axisX[1] * axisY[2] - axisX[2] * axisY[1]) +
                Square(axisX[2] * axisY[0] - axisX[0] * axisY[2]) +
                Square(axisX[0] * axisY[1] - axisX[1] * axisY[0]));
            if (axisXLength < 1e-9 || axisYLength < 1e-9 ||
                crossLength / (axisXLength * axisYLength) < 1e-6)
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                    $"Item {itemNo} fitting plane axes must be non-zero and non-parallel.");
            }
        }

        private static void ValidatePolyBeamSplitPayload(int itemNo, PolyBeamSplitPayload payload)
        {
            if (payload.TopPlane is null || payload.BottomPlane is null)
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                    $"Item {itemNo} requires topPlane and bottomPlane.");
            }
            ValidatePlane(itemNo, payload.TopPlane, "topPlane");
            ValidatePlane(itemNo, payload.BottomPlane, "bottomPlane");
            var topNormal = Cross(payload.TopPlane.AxisX!, payload.TopPlane.AxisY!);
            var bottomNormal = Cross(payload.BottomPlane.AxisX!, payload.BottomPlane.AxisY!);
            if (Math.Abs(Dot(Normalize(topNormal), Normalize(bottomNormal))) < 0.999999)
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                    $"Item {itemNo} split planes must be parallel.");
            }
        }

        private static void ValidatePlane(int itemNo, FittingPlanePayload plane, string role)
        {
            if (!ValidPoint(plane.Origin) || !ValidPoint(plane.AxisX) || !ValidPoint(plane.AxisY))
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                    $"Item {itemNo} requires a valid {role}.");
            }
            var axisX = plane.AxisX!;
            var axisY = plane.AxisY!;
            var axisXLength = VectorLength(axisX);
            var axisYLength = VectorLength(axisY);
            if (axisXLength < 1e-9 || axisYLength < 1e-9 ||
                VectorLength(Cross(axisX, axisY)) / (axisXLength * axisYLength) < 1e-6)
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                    $"Item {itemNo} {role} axes must be non-zero and non-parallel.");
            }
        }

        private static void ValidateWeldPayload(int itemNo, WeldCreatePayload payload)
        {
            if (string.IsNullOrWhiteSpace(payload.MainGuid) || string.IsNullOrWhiteSpace(payload.SecondaryGuid))
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                    $"Item {itemNo} requires mainGuid and secondaryGuid.");
            }
            if (string.Equals(payload.MainGuid, payload.SecondaryGuid, StringComparison.OrdinalIgnoreCase))
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                    $"Item {itemNo} cannot weld a part to itself.");
            }
            if (!IsFinite(payload.SizeAbove) || payload.SizeAbove < 0 ||
                !IsFinite(payload.SizeBelow) || payload.SizeBelow < 0 ||
                !IsFinite(payload.AngleAbove) || !IsFinite(payload.AngleBelow) ||
                !TryParseEnum(payload.TypeAbove, out BaseWeld.WeldTypeEnum _) ||
                !TryParseEnum(payload.TypeBelow, out BaseWeld.WeldTypeEnum _) ||
                !TryParseEnum(payload.Preparation, out BaseWeld.WeldPreparationTypeEnum _) ||
                !TryParseEnum(payload.Placement, out BaseWeld.WeldPlacementTypeEnum _))
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                    $"Item {itemNo} has invalid weld parameters.");
            }
            if (payload.PolygonPoints is null) return;
            if (payload.PolygonPoints.Length < 2 || payload.PolygonPoints.Length > 1000 ||
                payload.PolygonPoints.Any(static point => !ValidPoint(point)))
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                    $"Item {itemNo} polygonPoints must contain between 2 and 1000 valid points.");
            }
        }

        private static void ValidateFachwerkColumnPayload(int itemNo, FachwerkColumnUpsertPayload payload)
        {
            if (string.IsNullOrWhiteSpace(payload.ExternalObjectId) ||
                string.IsNullOrWhiteSpace(payload.ProfileKey) ||
                string.IsNullOrWhiteSpace(payload.Mark) ||
                string.IsNullOrWhiteSpace(payload.Material) ||
                string.IsNullOrWhiteSpace(payload.ClassName) ||
                !ValidPoint(payload.Insertion) ||
                !ValidPoint(payload.Orientation) ||
                !IsFinite(payload.RotationDeg))
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                    $"Item {itemNo} has an invalid Fachwerk column payload.");
            }
            if (Distance(payload.Insertion!, payload.Orientation!) < 1)
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                    $"Item {itemNo} Fachwerk column orientation is shorter than 1 mm.");
            }
            if (payload.Breaks is { Length: > 8 })
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                    $"Item {itemNo} Fachwerk column supports at most eight break levels.");
            }
            if (payload.Breaks is null) return;
            foreach (var item in payload.Breaks)
            {
                if (item is null || !IsFinite(item.Elevation) || Math.Abs(item.Elevation) < 1e-7 ||
                    !string.Equals(item.Mode, "ALL_FOUR", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ChangeSetConflictException(
                        "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                        $"Item {itemNo} contains an invalid Fachwerk column break.");
                }
            }
        }

        private static void ValidateFachwerkRigelPayload(int itemNo, FachwerkRigelUpsertPayload payload)
        {
            if (string.IsNullOrWhiteSpace(payload.ExternalObjectId) ||
                string.IsNullOrWhiteSpace(payload.Code) ||
                string.IsNullOrWhiteSpace(payload.Mark) ||
                string.IsNullOrWhiteSpace(payload.Profile) ||
                string.IsNullOrWhiteSpace(payload.Material) ||
                string.IsNullOrWhiteSpace(payload.ClassName) ||
                !ValidPoint(payload.StartTop) ||
                !ValidPoint(payload.EndTop) ||
                !IsFinite(payload.Height) || payload.Height <= 0 ||
                !IsFinite(payload.Thickness) || payload.Thickness <= 0)
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                    $"Item {itemNo} has an invalid Fachwerk rigel payload.");
            }
            if (Distance(payload.StartTop!, payload.EndTop!) < 1)
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                    $"Item {itemNo} Fachwerk rigel is shorter than 1 mm.");
            }
        }

        private static void ValidateFachwerkNodePayload(int itemNo, FachwerkNodeUpsertPayload payload)
        {
            var supportedHeight = Math.Abs(payload.HeightMm - 520) <= 0.1 ||
                Math.Abs(payload.HeightMm - 400) <= 0.1;
            if (string.IsNullOrWhiteSpace(payload.ExternalObjectId) ||
                string.IsNullOrWhiteSpace(payload.ColumnExternalObjectId) ||
                !IsFinite(payload.TopElevationMm) ||
                !IsFinite(payload.HeightMm) ||
                !supportedHeight ||
                !IsFinite(payload.ProjectionFromBottomInnerFlangeMm) ||
                payload.ProjectionFromBottomInnerFlangeMm <= 0 ||
                string.IsNullOrWhiteSpace(payload.Material) ||
                string.IsNullOrWhiteSpace(payload.ClassName))
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_PAYLOAD_INVALID",
                    $"Item {itemNo} has an invalid Fachwerk node payload. " +
                    "The first revision supports explicit 520 mm and 400 mm heights.");
            }
        }

        private static bool ValidPoint(double[]? value)
            => value is { Length: 3 } && value.All(IsFinite);

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private static double Square(double value) => value * value;

        private static double Distance(double[] first, double[] second) => Math.Sqrt(
            Square(second[0] - first[0]) +
            Square(second[1] - first[1]) +
            Square(second[2] - first[2]));

        private static double VectorLength(double[] value) => Math.Sqrt(
            Square(value[0]) + Square(value[1]) + Square(value[2]));

        private static double[] Cross(double[] left, double[] right) => new[]
        {
            left[1] * right[2] - left[2] * right[1],
            left[2] * right[0] - left[0] * right[2],
            left[0] * right[1] - left[1] * right[0],
        };

        private static double Dot(double[] left, double[] right) =>
            left[0] * right[0] + left[1] * right[1] + left[2] * right[2];

        private static double[] Normalize(double[] value)
        {
            var length = VectorLength(value);
            return length < 1e-12 ? throw new ArgumentException("Zero vector.") :
                new[] { value[0] / length, value[1] / length, value[2] / length };
        }

        private static void ApplyPayload(Beam beam, BeamModifyPayload payload)
        {
            beam.StartPoint = Point(payload.Start!);
            beam.EndPoint = Point(payload.End!);
            beam.Profile.ProfileString = payload.Profile!.Trim();
            beam.Material.MaterialString = payload.Material!.Trim();
            beam.Class = payload.ClassName!.Trim();
        }

        private static void ApplyPayload(PolyBeam polyBeam, PolyBeamModifyPayload payload)
        {
            var contour = new Contour();
            foreach (var value in payload.Points!)
            {
                TryParseChamferType(value.ChamferType, out var chamferType);
                var chamfer = new Chamfer
                {
                    Type = chamferType,
                    X = value.ChamferX,
                    Y = value.ChamferY,
                    DZ1 = value.ChamferDz1,
                    DZ2 = value.ChamferDz2,
                };
                contour.AddContourPoint(new ContourPoint(Point(value.Point!), chamfer));
            }
            polyBeam.Contour = contour;
            polyBeam.Profile.ProfileString = payload.Profile!.Trim();
            polyBeam.Material.MaterialString = payload.Material!.Trim();
            polyBeam.Class = payload.ClassName!.Trim();
        }

        private static bool TryParseChamferType(string? value, out Chamfer.ChamferTypeEnum chamferType)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                chamferType = Chamfer.ChamferTypeEnum.CHAMFER_NONE;
                return true;
            }
            return Enum.TryParse(value!.Trim(), true, out chamferType);
        }

        private static bool TryParseEnum<TEnum>(string? value, out TEnum result) where TEnum : struct
        {
            result = default;
            return !string.IsNullOrWhiteSpace(value) && Enum.TryParse(value!.Trim(), true, out result);
        }

        private static Point Point(double[] value) => new(value[0], value[1], value[2]);

        private static Vector DirectionVector(double[] value)
        {
            var scale = 1000 / VectorLength(value);
            return new Vector(value[0] * scale, value[1] * scale, value[2] * scale);
        }

        private static void Restore(Model model, IReadOnlyList<PreparedModelChange> modified)
        {
            foreach (var change in modified.Reverse())
            {
                try
                {
                    change.Restore();
                }
                catch
                {
                    // Continue restoring the remaining objects. The caller still receives
                    // a hard failure and can rescan the model before another attempt.
                }
            }
            try { model.CommitChanges(); } catch { }
        }

        private static ModelObject? TrySelectModelObjectByGuid(Model model, string guid)
        {
            try
            {
                var identifier = model.GetIdentifierByGUID(guid);
                return identifier is not null && identifier.ID != 0
                    ? model.SelectModelObject(identifier)
                    : null;
            }
            catch { return null; }
        }

        private static ModelObject ResolveRequiredObject(
            Model model,
            int itemNo,
            string? guid,
            string role)
        {
            if (string.IsNullOrWhiteSpace(guid))
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_TARGET_MISSING",
                    $"Item {itemNo} has no {role} GUID.");
            }
            var value = TrySelectModelObjectByGuid(model, guid!);
            if (value is null)
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_TARGET_NOT_FOUND",
                    $"Item {itemNo} {role} object '{guid}' does not exist in the connected Tekla model.");
            }
            var actualGuid = GetGuid(model, value);
            if (!string.Equals(actualGuid, guid, StringComparison.OrdinalIgnoreCase))
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_TARGET_IDENTITY_MISMATCH",
                    $"Item {itemNo} {role} resolved as '{actualGuid}', expected '{guid}'.");
            }
            return value;
        }

        private static Part ResolveRequiredPart(Model model, int itemNo, string? guid, string role)
        {
            var value = ResolveRequiredObject(model, itemNo, guid, role);
            if (value is Part part) return part;
            throw new ChangeSetConflictException(
                "MODEL_CHANGE_SET_TARGET_TYPE_MISMATCH",
                $"Item {itemNo} {role} object '{guid}' is not a Part.");
        }

        private static void EnsureRigelPayloadIsMutable(int itemNo, FachwerkRigelUpsertPayload payload)
        {
            if (IsFrozenRigelMarker(payload.Code) ||
                IsFrozenRigelMarker(payload.Mark) ||
                IsFrozenRigelMarker(payload.ExternalObjectId))
            {
                throw FrozenRigelConflict(itemNo, "payload", FirstFrozenRigelMarker(
                    payload.Code,
                    payload.Mark,
                    payload.ExternalObjectId));
            }
        }

        private static void EnsureRigelIsMutable(int itemNo, ModelObject modelObject, string role)
        {
            if (modelObject is not Part part) return;

            var markers = new List<string?>
            {
                part.Name,
                part.PartNumber?.Prefix,
            };

            foreach (var propertyName in new[]
            {
                "FK_RIGEL_CODE",
                "FK_MARK",
                "STRUCTURA_EXTERNAL_OBJECT_ID",
            })
            {
                var value = string.Empty;
                try { part.GetUserProperty(propertyName, ref value); }
                catch { value = string.Empty; }
                markers.Add(value);
            }

            try
            {
                var assembly = part.GetAssembly();
                if (assembly is not null)
                {
                    markers.Add(assembly.Name);
                    markers.Add(assembly.AssemblyNumber?.Prefix);
                }
            }
            catch { }

            var frozenMarker = FirstFrozenRigelMarker(markers.ToArray());
            if (!string.IsNullOrWhiteSpace(frozenMarker))
                throw FrozenRigelConflict(itemNo, role, frozenMarker!);
        }

        private static ChangeSetConflictException FrozenRigelConflict(
            int itemNo,
            string role,
            string? marker) =>
            new ChangeSetConflictException(
                "MODEL_CHANGE_SET_RIGEL_FROZEN",
                $"Item {itemNo} cannot modify manually adjusted rigel RS2 ({role}, marker '{marker ?? "RS2"}').");

        private static string? FirstFrozenRigelMarker(params string?[] values) =>
            values.FirstOrDefault(IsFrozenRigelMarker);

        private static bool IsFrozenRigelMarker(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            var normalized = NormalizeRigelMarker(value!);
            if (IsFrozenRigelToken(normalized)) return true;

            var token = new List<char>();
            foreach (var character in value!)
            {
                if (char.IsLetterOrDigit(character))
                {
                    token.Add(char.ToUpperInvariant(character));
                    continue;
                }

                if (IsFrozenRigelToken(new string(token.ToArray()))) return true;
                token.Clear();
            }
            return IsFrozenRigelToken(new string(token.ToArray()));
        }

        private static string NormalizeRigelMarker(string value) =>
            new string(value
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());

        private static bool IsFrozenRigelToken(string value) =>
            value == "2" ||
            value == "RS2" ||
            value == "РС2" ||
            value.EndsWith("RS2", StringComparison.Ordinal) ||
            value.EndsWith("РС2", StringComparison.Ordinal);

        private static void BuildColumnComponent(Component component, object rawPayload)
        {
            var payload = (FachwerkColumnUpsertPayload)rawPayload;
            SetTwoPointInput(component, payload.Insertion!, payload.Orientation!);
            component.SetAttribute("fk_external_id", payload.ExternalObjectId ?? string.Empty);
            component.SetAttribute("fk_profile_key", payload.ProfileKey ?? string.Empty);
            component.SetAttribute("fk_mark", payload.Mark ?? string.Empty);
            component.SetAttribute("fk_material", payload.Material ?? string.Empty);
            component.SetAttribute("fk_class", payload.ClassName ?? string.Empty);
            component.SetAttribute("fk_rotation_deg", payload.RotationDeg);
            component.SetAttribute("fk_catalog_path", payload.CatalogPath ?? string.Empty);
            component.SetAttribute("fk_bevel_profile", payload.BevelProfile ?? string.Empty);
            var breaks = payload.Breaks ?? Array.Empty<FachwerkColumnBreakPayload>();
            for (var index = 0; index < 8; index++)
            {
                var item = index < breaks.Length ? breaks[index] : null;
                component.SetAttribute($"fk_break_{index + 1}_z", item?.Elevation ?? 0);
                component.SetAttribute($"fk_break_{index + 1}_mode", item?.Mode ?? "ALL_FOUR");
            }
        }

        private static void SetTwoPointInput(Component component, double[] first, double[] second)
        {
            var input = new ComponentInput();
            input.AddTwoInputPositions(Point(first), Point(second));
            if (!component.SetComponentInput(input))
                throw new InvalidOperationException("Component.SetComponentInput() returned false.");
        }

        private static Component? FindFachwerkComponent(Model model, string pluginName, string externalObjectId)
        {
            var matches = new List<Component>();
            ModelObjectEnumerator? enumerator;
            try
            {
                enumerator = model.GetModelObjectSelector()
                    .GetAllObjectsWithType(ModelObject.ModelObjectEnum.COMPONENT);
            }
            catch
            {
                return null;
            }

            while (true)
            {
                bool hasMore;
                try { hasMore = enumerator.MoveNext(); }
                catch { break; }
                if (!hasMore) break;
                Component? component;
                try { component = enumerator.Current as Component; }
                catch { continue; }
                if (component is null || !string.Equals(component.Name, pluginName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var candidate = string.Empty;
                try { component.GetAttribute("fk_external_id", ref candidate); }
                catch { continue; }
                if (string.Equals(candidate, externalObjectId, StringComparison.Ordinal)) matches.Add(component);
            }

            if (matches.Count > 1)
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_COMPONENT_IDENTITY_DUPLICATE",
                    $"Component '{pluginName}' has {matches.Count} instances with externalObjectId '{externalObjectId}'.");
            }
            return matches.SingleOrDefault();
        }

        private static Beam? FindFachwerkRigelPart(Model model, string externalObjectId)
        {
            var matches = new List<Beam>();
            ModelObjectEnumerator? enumerator;
            try
            {
                enumerator = model.GetModelObjectSelector()
                    .GetAllObjectsWithType(ModelObject.ModelObjectEnum.BEAM);
            }
            catch
            {
                return null;
            }

            while (true)
            {
                bool hasMore;
                try { hasMore = enumerator.MoveNext(); }
                catch { break; }
                if (!hasMore) break;
                Beam? beam;
                try { beam = enumerator.Current as Beam; }
                catch { continue; }
                if (beam is null) continue;
                var candidate = string.Empty;
                try { beam.GetUserProperty("STRUCTURA_EXTERNAL_OBJECT_ID", ref candidate); }
                catch { continue; }
                if (string.Equals(candidate, externalObjectId, StringComparison.Ordinal)) matches.Add(beam);
            }

            if (matches.Count > 1)
            {
                throw new ChangeSetConflictException(
                    "MODEL_CHANGE_SET_PART_IDENTITY_DUPLICATE",
                    $"Straight Beam has {matches.Count} instances with externalObjectId '{externalObjectId}'.");
            }
            return matches.SingleOrDefault();
        }

        private static bool IsFachwerkColumnPluginName(string pluginName) =>
            string.Equals(pluginName, FachwerkColumnPluginName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(pluginName, "FachwerkColumnPlugin", StringComparison.OrdinalIgnoreCase);

        private static string GetGuid(Model model, ModelObject obj)
        {
            try
            {
                var direct = obj.Identifier?.GUID ?? Guid.Empty;
                if (direct != Guid.Empty) return direct.ToString();
            }
            catch { }
            try { return model.GetGUIDByIdentifier(obj.Identifier) ?? string.Empty; }
            catch { return string.Empty; }
        }

        public sealed class ModelChangeSetRequest
        {
            public string? ChangeSetId { get; set; }
            public string? ModelFingerprint { get; set; }
            public string? ExpectedTeklaVersion { get; set; }
            public string? ExpectedModelName { get; set; }
            public string? ExpectedModelPath { get; set; }
            public ModelChangeItem[]? Items { get; set; }
        }

        public sealed class ModelChangeItem
        {
            public int ItemNo { get; set; }
            public string? CommandKind { get; set; }
            public string? TargetGuid { get; set; }
            public JsonElement Payload { get; set; }
        }

        public sealed class BeamModifyPayload
        {
            public double[]? Start { get; set; }
            public double[]? End { get; set; }
            public string? Profile { get; set; }
            public string? Material { get; set; }
            public string? ClassName { get; set; }
        }

        public sealed class PolyBeamModifyPayload
        {
            public PolyBeamPointPayload[]? Points { get; set; }
            public string? Profile { get; set; }
            public string? Material { get; set; }
            public string? ClassName { get; set; }
        }

        public sealed class PolyBeamSplitPayload
        {
            public FittingPlanePayload? TopPlane { get; set; }
            public FittingPlanePayload? BottomPlane { get; set; }
            public string? ConnectionKey { get; set; }
        }

        public sealed class PolyBeamPointPayload
        {
            public double[]? Point { get; set; }
            public string? ChamferType { get; set; }
            public double ChamferX { get; set; }
            public double ChamferY { get; set; }
            public double ChamferDz1 { get; set; }
            public double ChamferDz2 { get; set; }
        }

        public sealed class BooleanCutCreatePayload
        {
            public string? FatherGuid { get; set; }
            public string? SourcePartGuid { get; set; }
            public string? ConnectionKey { get; set; }
        }

        public sealed class FittingUpsertPayload
        {
            public FittingPlanePayload? Plane { get; set; }
            public string? ConnectionKey { get; set; }
        }

        public sealed class FittingPlanePayload
        {
            public double[]? Origin { get; set; }
            public double[]? AxisX { get; set; }
            public double[]? AxisY { get; set; }
        }

        public sealed class WeldCreatePayload
        {
            public string? MainGuid { get; set; }
            public string? SecondaryGuid { get; set; }
            public double SizeAbove { get; set; }
            public double SizeBelow { get; set; }
            public double AngleAbove { get; set; } = 45;
            public double AngleBelow { get; set; }
            public string? TypeAbove { get; set; }
            public string? TypeBelow { get; set; }
            public string? Preparation { get; set; }
            public string? Placement { get; set; }
            public bool ShopWeld { get; set; }
            public bool AroundWeld { get; set; }
            public bool StitchWeld { get; set; }
            public bool ConnectAssemblies { get; set; }
            public double[][]? PolygonPoints { get; set; }
            public string? ConnectionKey { get; set; }
        }

        public sealed class FachwerkColumnUpsertPayload
        {
            public string? ExternalObjectId { get; set; }
            public double[]? Insertion { get; set; }
            public double[]? Orientation { get; set; }
            public string? ProfileKey { get; set; }
            public string? Mark { get; set; }
            public string? Material { get; set; }
            public string? ClassName { get; set; }
            public double RotationDeg { get; set; }
            public string? CatalogPath { get; set; }
            public string? BevelProfile { get; set; }
            public FachwerkColumnBreakPayload[]? Breaks { get; set; }
        }

        public sealed class FachwerkColumnBreakPayload
        {
            public double Elevation { get; set; }
            public string? Mode { get; set; }
        }

        public sealed class FachwerkRigelUpsertPayload
        {
            public string? ExternalObjectId { get; set; }
            public string? SourceGuid { get; set; }
            public double[]? StartTop { get; set; }
            public double[]? EndTop { get; set; }
            public string? Code { get; set; }
            public string? Mark { get; set; }
            public string? Profile { get; set; }
            public double Height { get; set; }
            public double Thickness { get; set; }
            public string? Material { get; set; }
            public string? ClassName { get; set; }
        }

        public sealed class FachwerkNodeUpsertPayload
        {
            public string? ExternalObjectId { get; set; }
            public string? ColumnExternalObjectId { get; set; }
            public double TopElevationMm { get; set; }
            public double HeightMm { get; set; }
            public double ProjectionFromBottomInnerFlangeMm { get; set; } = 220;
            public string? Material { get; set; } = "C355-5";
            public string? ClassName { get; set; } = "3";
        }

        private abstract class PreparedModelChange
        {
            protected PreparedModelChange(int itemNo, string? targetGuid)
            {
                ItemNo = itemNo;
                TargetGuid = targetGuid;
            }

            public int ItemNo { get; }
            public string? TargetGuid { get; }
            public virtual string? ResultObjectGuid => TargetGuid;
            public abstract int TeklaId { get; }
            public abstract void Apply();
            public abstract void Restore();
        }

        private sealed class PreparedFachwerkNodeUpsertChange : PreparedModelChange
        {
            private readonly Model _model;
            private readonly FachwerkNodeTeklaChange _change;

            public PreparedFachwerkNodeUpsertChange(
                int itemNo,
                Model model,
                Component column,
                FachwerkNodeUpsertPayload payload) : base(itemNo, null)
            {
                _model = model;
                try
                {
                    _change = InGlobalPlane(model, () => FachwerkNodeTeklaChange.Prepare(
                        model,
                        column,
                        new FachwerkNodeCommand
                        {
                            NodeId = payload.ExternalObjectId!,
                            ColumnExternalObjectId = payload.ColumnExternalObjectId!,
                            TopElevationMm = payload.TopElevationMm,
                            HeightMm = payload.HeightMm,
                            ProjectionFromBottomInnerFlangeMm = payload.ProjectionFromBottomInnerFlangeMm,
                            Material = payload.Material!,
                            ClassName = payload.ClassName!,
                        }));
                }
                catch (FachwerkNodeAdapterException ex)
                {
                    throw new ChangeSetConflictException(ex.ErrorCode, ex.Message);
                }
            }

            public override string? ResultObjectGuid => _change.ResultObjectGuid;
            public override int TeklaId => _change.TeklaId;

            public override void Apply() => InGlobalPlane(_model, _change.Apply);
            public override void Restore() => InGlobalPlane(_model, _change.Restore);

            private static T InGlobalPlane<T>(Model model, Func<T> operation)
            {
                var handler = model.GetWorkPlaneHandler();
                var previous = handler.GetCurrentTransformationPlane();
                try
                {
                    if (!handler.SetCurrentTransformationPlane(new TransformationPlane()))
                        throw new InvalidOperationException("Tekla rejected the global transformation plane.");
                    return operation();
                }
                finally
                {
                    handler.SetCurrentTransformationPlane(previous);
                }
            }

            private static void InGlobalPlane(Model model, Action operation) =>
                InGlobalPlane(model, () =>
                {
                    operation();
                    return true;
                });
        }

        private sealed class PreparedFachwerkComponentUpsertChange : PreparedModelChange
        {
            private readonly Model _model;
            private readonly string _pluginName;
            private readonly string _externalObjectId;
            private readonly Action<Component, object> _configure;
            private readonly object _payload;
            private readonly Component? _existing;
            private readonly FachwerkComponentSnapshot? _snapshot;
            private Component? _result;
            private bool _created;

            public PreparedFachwerkComponentUpsertChange(
                int itemNo,
                Model model,
                string pluginName,
                string externalObjectId,
                Action<Component, object> configure,
                object payload) : base(itemNo, null)
            {
                _model = model;
                _pluginName = pluginName;
                _externalObjectId = externalObjectId;
                _configure = configure;
                _payload = payload;
                _existing = FindFachwerkComponent(model, pluginName, externalObjectId);
                _snapshot = _existing is null
                    ? null
                    : InGlobalPlane(model, () => FachwerkComponentSnapshot.Capture(_existing, pluginName));
            }

            public override string? ResultObjectGuid => ReadIdentifierGuid(_result);
            public override int TeklaId => _result?.Identifier?.ID ?? 0;

            public override void Apply()
            {
                InGlobalPlane(_model, () =>
                {
                    var component = _existing ?? new Component
                    {
                        Name = _pluginName,
                        Number = BaseComponent.PLUGIN_OBJECT_NUMBER,
                    };
                    _configure(component, _payload);
                    var applied = _existing is null ? component.Insert() : component.Modify();
                    if (!applied)
                    {
                        throw new InvalidOperationException(
                            (_existing is null ? "Component.Insert()" : "Component.Modify()") +
                            $" returned false for '{_pluginName}/{_externalObjectId}'.");
                    }
                    _created = _existing is null;
                    _result = component;
                    component.SetUserProperty("STRUCTURA_EXTERNAL_OBJECT_ID", _externalObjectId);
                    component.SetUserProperty("STRUCTURA_COMPONENT_TYPE", _pluginName);
                    component.SetUserProperty("STRUCTURA_SCHEMA_VERSION", 1);
                });
            }

            public override void Restore()
            {
                InGlobalPlane(_model, () =>
                {
                    if (_created && _result is not null)
                    {
                        _result.Delete();
                        return;
                    }
                    if (_existing is null || _snapshot is null) return;
                    _snapshot.Restore(_existing);
                    _existing.Modify();
                });
            }

            private static T InGlobalPlane<T>(Model model, Func<T> operation)
            {
                var handler = model.GetWorkPlaneHandler();
                var previous = handler.GetCurrentTransformationPlane();
                try
                {
                    if (!handler.SetCurrentTransformationPlane(new TransformationPlane()))
                        throw new InvalidOperationException("Tekla rejected the global transformation plane.");
                    return operation();
                }
                finally
                {
                    handler.SetCurrentTransformationPlane(previous);
                }
            }

            private static void InGlobalPlane(Model model, Action operation) =>
                InGlobalPlane(model, () =>
                {
                    operation();
                    return true;
                });
        }

        private sealed class PreparedFachwerkRigelPartUpsertChange : PreparedModelChange
        {
            private readonly Model _model;
            private readonly Beam _beam;
            private readonly FachwerkRigelUpsertPayload _payload;
            private readonly FachwerkRigelPartSnapshot? _snapshot;
            private readonly Component? _legacyComponent;
            private readonly FachwerkComponentSnapshot? _legacySnapshot;
            private readonly bool _created;
            private bool _inserted;
            private bool _legacyDeleted;

            public PreparedFachwerkRigelPartUpsertChange(
                int itemNo,
                Model model,
                Beam? sourceBeam,
                FachwerkRigelUpsertPayload payload) : base(itemNo, null)
            {
                _model = model;
                _payload = payload;
                var existing = FindFachwerkRigelPart(model, payload.ExternalObjectId!);
                _beam = existing ?? sourceBeam ?? new Beam();
                _created = existing is null && sourceBeam is null;
                _snapshot = _created ? null : FachwerkRigelPartSnapshot.Capture(_beam);
                _legacyComponent = FindFachwerkComponent(
                    model,
                    FachwerkRigelPluginName,
                    payload.ExternalObjectId!);
                _legacySnapshot = _legacyComponent is null
                    ? null
                    : InGlobalPlane(model, () => FachwerkComponentSnapshot.Capture(
                        _legacyComponent,
                        FachwerkRigelPluginName));
            }

            public override string? ResultObjectGuid => ReadIdentifierGuid(_beam);
            public override int TeklaId => _beam.Identifier?.ID ?? 0;

            public override void Apply()
            {
                InGlobalPlane(_model, () =>
                {
                    try
                    {
                        _beam.StartPoint = Point(_payload.StartTop!);
                        _beam.EndPoint = Point(_payload.EndTop!);
                        _beam.Profile.ProfileString = _payload.Profile!.Trim();
                        _beam.Material.MaterialString = _payload.Material!.Trim();
                        _beam.Class = _payload.ClassName!.Trim();
                        _beam.Name = FachwerkRigelPartName;
                        var partNumber = _beam.PartNumber ?? new NumberingSeries(string.Empty, 1);
                        partNumber.Prefix = FachwerkPartPrefix;
                        _beam.PartNumber = partNumber;
                        // The design path is the physical top-centre line of the
                        // rigel. Keep the section centred on it so the two facade
                        // clearances are equal; transverseOffsetMm translates the
                        // complete centred chain before it reaches this handler.
                        _beam.Position.Plane = Position.PlaneEnum.MIDDLE;
                        _beam.Position.PlaneOffset = 0;
                        _beam.Position.Depth = Position.DepthEnum.MIDDLE;
                        _beam.Position.DepthOffset = 0;
                        _beam.Position.Rotation = Position.RotationEnum.FRONT;
                        _beam.Position.RotationOffset = 0;
                        if (_created)
                        {
                            if (!_beam.Insert())
                            {
                                throw new InvalidOperationException(
                                    $"Beam.Insert() returned false for Fachwerk rigel '{_payload.ExternalObjectId}'.");
                            }
                            _inserted = true;
                        }
                        _beam.SetUserProperty("STRUCTURA_EXTERNAL_OBJECT_ID", _payload.ExternalObjectId!);
                        _beam.SetUserProperty("STRUCTURA_COMPONENT_TYPE", "FachwerkRigelPart");
                        _beam.SetUserProperty("STRUCTURA_SCHEMA_VERSION", 1);
                        if (!_beam.Modify())
                        {
                            throw new InvalidOperationException(
                                $"Beam.Modify() returned false for Fachwerk rigel '{_payload.ExternalObjectId}'.");
                        }
                        ApplyRigelAssemblyAttributes(_beam, _payload.Mark!);

                        if (_legacyComponent is not null)
                        {
                            if (!_legacyComponent.Delete())
                            {
                                throw new InvalidOperationException(
                                    $"Legacy component delete failed for Fachwerk rigel '{_payload.ExternalObjectId}'.");
                            }
                            _legacyDeleted = true;
                        }
                    }
                    catch
                    {
                        RestoreCore();
                        throw;
                    }
                });
            }

            public override void Restore() => InGlobalPlane(_model, RestoreCore);

            private void RestoreCore()
            {
                if (_created)
                {
                    if (_inserted) _beam.Delete();
                    _inserted = false;
                }
                else if (_snapshot is not null)
                {
                    _snapshot.RestorePart(_beam);
                    if (!_beam.Modify())
                        throw new InvalidOperationException("Beam.Modify() failed while restoring a Fachwerk rigel.");
                    _snapshot.RestoreAssembly(_beam);
                }
                if (!_legacyDeleted || _legacySnapshot is null) return;
                _legacySnapshot.Recreate(FachwerkRigelPluginName);
                _legacyDeleted = false;
            }

            private static void ApplyRigelAssemblyAttributes(Beam beam, string mark)
            {
                var assembly = beam.GetAssembly();
                if (assembly is null)
                    throw new InvalidOperationException("Tekla did not return an assembly for a Fachwerk rigel.");

                var assemblyNumber = assembly.AssemblyNumber ?? new NumberingSeries(string.Empty, 1);
                assembly.Name = FachwerkRigelPartName;
                assemblyNumber.Prefix = BuildRigelAssemblyPrefix(mark);
                assembly.AssemblyNumber = assemblyNumber;
                if (!assembly.Modify())
                    throw new InvalidOperationException("Assembly.Modify() failed for a Fachwerk rigel.");
            }

            private static string BuildRigelAssemblyPrefix(string mark)
            {
                var normalized = mark.Trim();
                const string legacyPrefix = "515-59.";
                if (normalized.StartsWith(FachwerkPartPrefix, StringComparison.OrdinalIgnoreCase))
                    normalized = normalized.Substring(FachwerkPartPrefix.Length);
                else if (normalized.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase))
                    normalized = normalized.Substring(legacyPrefix.Length);

                var key = normalized.ToUpperInvariant().Replace("РС", "RS");
                var assemblyMark = key switch
                {
                    "101" or "RS1-101" => "РС3-",
                    "103" or "RS1-103" or "RS2" or "RS-2" => "РС2-",
                    _ => normalized,
                };
                return FachwerkPartPrefix + assemblyMark;
            }

            private static T InGlobalPlane<T>(Model model, Func<T> operation)
            {
                var handler = model.GetWorkPlaneHandler();
                var previous = handler.GetCurrentTransformationPlane();
                try
                {
                    if (!handler.SetCurrentTransformationPlane(new TransformationPlane()))
                        throw new InvalidOperationException("Tekla rejected the global transformation plane.");
                    return operation();
                }
                finally
                {
                    handler.SetCurrentTransformationPlane(previous);
                }
            }

            private static void InGlobalPlane(Model model, Action operation) =>
                InGlobalPlane(model, () =>
                {
                    operation();
                    return true;
                });
        }

        private sealed class PreparedBeamChange : PreparedModelChange
        {
            private readonly Beam _beam;
            private readonly BeamModifyPayload _payload;
            private readonly BeamSnapshot _snapshot;

            public PreparedBeamChange(
                int itemNo,
                string targetGuid,
                Beam beam,
                BeamModifyPayload payload,
                BeamSnapshot snapshot) : base(itemNo, targetGuid)
            {
                _beam = beam;
                _payload = payload;
                _snapshot = snapshot;
            }

            public override int TeklaId => _beam.Identifier?.ID ?? 0;

            public override void Apply()
            {
                ApplyPayload(_beam, _payload);
                if (!_beam.Modify())
                    throw new InvalidOperationException($"Beam.Modify() returned false for {TargetGuid}.");
            }

            public override void Restore()
            {
                _snapshot.Restore(_beam);
                _beam.Modify();
            }
        }

        private sealed class PreparedPolyBeamChange : PreparedModelChange
        {
            private readonly PolyBeam _polyBeam;
            private readonly PolyBeamModifyPayload _payload;
            private readonly PolyBeamSnapshot _snapshot;

            public PreparedPolyBeamChange(
                int itemNo,
                string targetGuid,
                PolyBeam polyBeam,
                PolyBeamModifyPayload payload,
                PolyBeamSnapshot snapshot) : base(itemNo, targetGuid)
            {
                _polyBeam = polyBeam;
                _payload = payload;
                _snapshot = snapshot;
            }

            public override int TeklaId => _polyBeam.Identifier?.ID ?? 0;

            public override void Apply()
            {
                ApplyPayload(_polyBeam, _payload);
                if (!_polyBeam.Modify())
                    throw new InvalidOperationException($"PolyBeam.Modify() returned false for {TargetGuid}.");
            }

            public override void Restore()
            {
                _snapshot.Restore(_polyBeam);
                _polyBeam.Modify();
            }
        }

        // A curved Tekla PolyBeam has no native Operation.Split overload. Keep the
        // original GUID as one side, insert the other side from the same control
        // contour and place it in the same assembly. The rigel band is omitted.
        private sealed class PreparedPolyBeamSplitChange : PreparedModelChange
        {
            private readonly PolyBeam _source;
            private readonly PolyBeamSplitPayload _payload;
            private readonly PolyBeamSnapshot _snapshot;
            private readonly Assembly? _assembly;
            private PolyBeam? _created;

            public PreparedPolyBeamSplitChange(
                int itemNo,
                string targetGuid,
                PolyBeam source,
                PolyBeamSplitPayload payload,
                PolyBeamSnapshot snapshot) : base(itemNo, targetGuid)
            {
                _source = source;
                _payload = payload;
                _snapshot = snapshot;
                _assembly = source.GetAssembly();
            }

            public override int TeklaId => _source.Identifier?.ID ?? 0;

            public override void Apply()
            {
                var result = SplitContour(_snapshot.Points, _payload.TopPlane!, _payload.BottomPlane!);
                var sibling = CreateSibling(_source, result.Secondary);
                if (!sibling.Insert())
                    throw new InvalidOperationException($"PolyBeam split insert failed for '{TargetGuid}'.");
                try
                {
                    if (_assembly is not null && !_assembly.Add(sibling))
                        throw new InvalidOperationException($"PolyBeam split assembly add failed for '{TargetGuid}'.");
                    ApplyPayload(_source, new PolyBeamModifyPayload
                    {
                        Points = result.Primary,
                        Profile = _snapshot.Profile,
                        Material = _snapshot.Material,
                        ClassName = _snapshot.ClassName,
                    });
                    if (!_source.Modify())
                        throw new InvalidOperationException($"PolyBeam split modify failed for '{TargetGuid}'.");
                    _created = sibling;
                }
                catch
                {
                    try { sibling.Delete(); } catch { }
                    throw;
                }
            }

            public override void Restore()
            {
                if (_created is not null) _created.Delete();
                _snapshot.Restore(_source);
                _source.Modify();
            }

            private static PolyBeam CreateSibling(PolyBeam source, PolyBeamPointPayload[] points)
            {
                var copy = new PolyBeam(source.Type)
                {
                    Name = source.Name,
                    Finish = source.Finish,
                    Class = source.Class,
                };
                copy.Profile.ProfileString = source.Profile.ProfileString;
                copy.Material.MaterialString = source.Material.MaterialString;
                copy.Position.Plane = source.Position.Plane;
                copy.Position.PlaneOffset = source.Position.PlaneOffset;
                copy.Position.Depth = source.Position.Depth;
                copy.Position.DepthOffset = source.Position.DepthOffset;
                copy.Position.Rotation = source.Position.Rotation;
                copy.Position.RotationOffset = source.Position.RotationOffset;
                copy.DeformingData.Angle = source.DeformingData.Angle;
                copy.DeformingData.Angle2 = source.DeformingData.Angle2;
                copy.DeformingData.Cambering = source.DeformingData.Cambering;
                copy.DeformingData.Shortening = source.DeformingData.Shortening;
                ApplyPayload(copy, new PolyBeamModifyPayload
                {
                    Points = points,
                    Profile = source.Profile.ProfileString,
                    Material = source.Material.MaterialString,
                    ClassName = source.Class,
                });
                return copy;
            }

            private static PolyBeamSplitResult SplitContour(
                PolyBeamPointPayload[] points,
                FittingPlanePayload topPlane,
                FittingPlanePayload bottomPlane)
            {
                var top = FindPlaneCrossing(points, topPlane, "top");
                var bottom = FindPlaneCrossing(points, bottomPlane, "bottom");
                if (Math.Abs(top.PathParameter - bottom.PathParameter) < 1e-7)
                    throw new InvalidOperationException("PolyBeam split planes intersect the same contour point.");
                var first = top.PathParameter < bottom.PathParameter ? top : bottom;
                var second = ReferenceEquals(first, top) ? bottom : top;
                var primary = BuildPrefix(points, first);
                var secondary = BuildSuffix(points, second);
                if (primary.Length < 2 || secondary.Length < 2)
                    throw new InvalidOperationException("PolyBeam split would create a degenerate part.");
                return new(primary, secondary);
            }

            private static PolyBeamCrossing FindPlaneCrossing(
                IReadOnlyList<PolyBeamPointPayload> points,
                FittingPlanePayload plane,
                string label)
            {
                var origin = plane.Origin!;
                var normal = Normalize(Cross(plane.AxisX!, plane.AxisY!));
                PolyBeamCrossing? result = null;
                for (var index = 0; index < points.Count - 1; index++)
                {
                    var start = points[index].Point!;
                    var end = points[index + 1].Point!;
                    var startDistance = Dot(Subtract(start, origin), normal);
                    var endDistance = Dot(Subtract(end, origin), normal);
                    if (Math.Abs(startDistance) < 1e-6)
                    {
                        result = RequireSingleCrossing(result, new(index, 0, start));
                        continue;
                    }
                    if (Math.Abs(endDistance) < 1e-6)
                    {
                        result = RequireSingleCrossing(result, new(index, 1, end));
                        continue;
                    }
                    if (startDistance * endDistance < 0)
                    {
                        var factor = startDistance / (startDistance - endDistance);
                        result = RequireSingleCrossing(result, new(index, factor, new[]
                        {
                            start[0] + (end[0] - start[0]) * factor,
                            start[1] + (end[1] - start[1]) * factor,
                            start[2] + (end[2] - start[2]) * factor,
                        }));
                    }
                }
                return result ?? throw new InvalidOperationException($"PolyBeam does not cross the {label} rigel face.");
            }

            private static PolyBeamCrossing RequireSingleCrossing(PolyBeamCrossing? current, PolyBeamCrossing next)
            {
                if (current is not null && Math.Abs(current.PathParameter - next.PathParameter) > 1e-6)
                    throw new InvalidOperationException("PolyBeam crosses one rigel face more than once.");
                return current ?? next;
            }

            private static PolyBeamPointPayload[] BuildPrefix(IReadOnlyList<PolyBeamPointPayload> source, PolyBeamCrossing crossing)
            {
                var result = source.Take(crossing.SegmentIndex + 1).Select(ClonePoint).ToList();
                if (crossing.LocalT > 1e-6) result.Add(IntersectionPoint(crossing.Point));
                return result.ToArray();
            }

            private static PolyBeamPointPayload[] BuildSuffix(IReadOnlyList<PolyBeamPointPayload> source, PolyBeamCrossing crossing)
            {
                var result = new List<PolyBeamPointPayload> { IntersectionPoint(crossing.Point) };
                result.AddRange(source.Skip(crossing.SegmentIndex + 1).Select(ClonePoint));
                return result.ToArray();
            }

            private static PolyBeamPointPayload ClonePoint(PolyBeamPointPayload source) => new()
            {
                Point = source.Point!.ToArray(), ChamferType = source.ChamferType,
                ChamferX = source.ChamferX, ChamferY = source.ChamferY,
                ChamferDz1 = source.ChamferDz1, ChamferDz2 = source.ChamferDz2,
            };

            private static PolyBeamPointPayload IntersectionPoint(double[] point) => new()
            {
                Point = point, ChamferType = "CHAMFER_NONE",
            };

            private static double[] Subtract(double[] left, double[] right) => new[]
            {
                left[0] - right[0], left[1] - right[1], left[2] - right[2],
            };

            private sealed record PolyBeamCrossing(int SegmentIndex, double LocalT, double[] Point)
            {
                public double PathParameter => SegmentIndex + LocalT;
            }

            private sealed record PolyBeamSplitResult(PolyBeamPointPayload[] Primary, PolyBeamPointPayload[] Secondary);
        }

        private sealed class PreparedBooleanCutChange : PreparedModelChange
        {
            private readonly Part _father;
            private readonly Beam _source;
            private readonly BooleanCutCreatePayload _payload;
            private BooleanPart? _created;

            public PreparedBooleanCutChange(
                int itemNo,
                Part father,
                Beam source,
                BooleanCutCreatePayload payload) : base(itemNo, null)
            {
                _father = father;
                _source = source;
                _payload = payload;
            }

            public override string? ResultObjectGuid => ReadIdentifierGuid(_created);
            public override int TeklaId => _created?.Identifier?.ID ?? 0;

            public override void Apply()
            {
                var cutter = CloneBeamAsBooleanOperativePart(_source);
                if (!cutter.Insert())
                {
                    throw new InvalidOperationException(
                        $"Boolean cutter insert failed for '{_payload.ConnectionKey ?? _payload.FatherGuid}'.");
                }
                try
                {
                    var boolean = new BooleanPart
                    {
                        Father = _father,
                        Type = BooleanPart.BooleanTypeEnum.BOOLEAN_CUT,
                    };
                    boolean.SetOperativePart(cutter);
                    if (!boolean.Insert())
                    {
                        throw new InvalidOperationException(
                            $"BooleanPart.Insert() returned false for '{_payload.ConnectionKey ?? _payload.FatherGuid}'.");
                    }
                    _created = boolean;
                }
                finally
                {
                    try { cutter.Delete(); } catch { }
                }
            }

            public override void Restore()
            {
                if (_created is not null) _created.Delete();
            }
        }

        private sealed class PreparedFittingChange : PreparedModelChange
        {
            private readonly Part _father;
            private readonly FittingUpsertPayload _payload;
            private Fitting? _created;

            public PreparedFittingChange(
                int itemNo,
                string targetGuid,
                Part father,
                FittingUpsertPayload payload) : base(itemNo, targetGuid)
            {
                _father = father;
                _payload = payload;
            }

            public override string? ResultObjectGuid => ReadIdentifierGuid(_created);
            public override int TeklaId => _created?.Identifier?.ID ?? 0;

            public override void Apply()
            {
                var plane = _payload.Plane!;
                var fitting = new Fitting
                {
                    Father = _father,
                    Plane = new Plane
                    {
                        Origin = Point(plane.Origin!),
                        AxisX = DirectionVector(plane.AxisX!),
                        AxisY = DirectionVector(plane.AxisY!),
                    },
                };
                if (!fitting.Insert())
                {
                    throw new InvalidOperationException(
                        $"Fitting.Insert() returned false for '{_payload.ConnectionKey ?? TargetGuid}'.");
                }
                _created = fitting;
            }

            public override void Restore()
            {
                if (_created is not null) _created.Delete();
            }
        }

        private sealed class PreparedWeldChange : PreparedModelChange
        {
            private readonly Part _main;
            private readonly Part _secondary;
            private readonly WeldCreatePayload _payload;
            private BaseWeld? _created;

            public PreparedWeldChange(
                int itemNo,
                Part main,
                Part secondary,
                WeldCreatePayload payload) : base(itemNo, null)
            {
                _main = main;
                _secondary = secondary;
                _payload = payload;
            }

            public override string? ResultObjectGuid => ReadIdentifierGuid(_created);
            public override int TeklaId => _created?.Identifier?.ID ?? 0;

            public override void Apply()
            {
                BaseWeld weld;
                if (_payload.PolygonPoints is { Length: >= 2 } polygonPoints)
                {
                    var polygon = new Polygon();
                    foreach (var point in polygonPoints) polygon.Points.Add(Point(point));
                    weld = new PolygonWeld { Polygon = polygon };
                }
                else
                {
                    weld = new Weld();
                }

                ApplyWeldPayload(weld, _main, _secondary, _payload);
                if (!weld.Insert())
                {
                    throw new InvalidOperationException(
                        $"Weld.Insert() returned false for '{_payload.ConnectionKey ?? _payload.SecondaryGuid}'.");
                }
                _created = weld;
            }

            public override void Restore()
            {
                if (_created is not null) _created.Delete();
            }
        }

        private static Beam CloneBeamAsBooleanOperativePart(Beam source)
        {
            var cutter = new Beam(
                new Point(source.StartPoint.X, source.StartPoint.Y, source.StartPoint.Z),
                new Point(source.EndPoint.X, source.EndPoint.Y, source.EndPoint.Z))
            {
                Name = "PLATFORM_RIGEL_CUTTER",
                Finish = source.Finish,
                Class = BooleanPart.BooleanOperativeClassName,
            };
            cutter.Profile.ProfileString = source.Profile.ProfileString;
            cutter.Material.MaterialString = source.Material.MaterialString;

            cutter.Position.Plane = source.Position.Plane;
            cutter.Position.PlaneOffset = source.Position.PlaneOffset;
            cutter.Position.Depth = source.Position.Depth;
            cutter.Position.DepthOffset = source.Position.DepthOffset;
            cutter.Position.Rotation = source.Position.Rotation;
            cutter.Position.RotationOffset = source.Position.RotationOffset;

            cutter.StartPointOffset.Dx = source.StartPointOffset.Dx;
            cutter.StartPointOffset.Dy = source.StartPointOffset.Dy;
            cutter.StartPointOffset.Dz = source.StartPointOffset.Dz;
            cutter.EndPointOffset.Dx = source.EndPointOffset.Dx;
            cutter.EndPointOffset.Dy = source.EndPointOffset.Dy;
            cutter.EndPointOffset.Dz = source.EndPointOffset.Dz;

            cutter.DeformingData.Angle = source.DeformingData.Angle;
            cutter.DeformingData.Angle2 = source.DeformingData.Angle2;
            cutter.DeformingData.Cambering = source.DeformingData.Cambering;
            cutter.DeformingData.Shortening = source.DeformingData.Shortening;
            return cutter;
        }

        private static void ApplyWeldPayload(
            BaseWeld weld,
            Part main,
            Part secondary,
            WeldCreatePayload payload)
        {
            TryParseEnum(payload.TypeAbove, out BaseWeld.WeldTypeEnum typeAbove);
            TryParseEnum(payload.TypeBelow, out BaseWeld.WeldTypeEnum typeBelow);
            TryParseEnum(payload.Preparation, out BaseWeld.WeldPreparationTypeEnum preparation);
            TryParseEnum(payload.Placement, out BaseWeld.WeldPlacementTypeEnum placement);
            weld.MainObject = main;
            weld.SecondaryObject = secondary;
            weld.SizeAbove = payload.SizeAbove;
            weld.SizeBelow = payload.SizeBelow;
            weld.AngleAbove = payload.AngleAbove;
            weld.AngleBelow = payload.AngleBelow;
            weld.TypeAbove = typeAbove;
            weld.TypeBelow = typeBelow;
            weld.Preparation = preparation;
            weld.Placement = placement;
            weld.ShopWeld = payload.ShopWeld;
            weld.AroundWeld = payload.AroundWeld;
            weld.IntermittentType = BaseWeld.WeldIntermittentTypeEnum.CONTINUOUS;
            weld.ConnectAssemblies = payload.ConnectAssemblies;
        }

        private static string? ReadIdentifierGuid(ModelObject? value)
        {
            try
            {
                var guid = value?.Identifier?.GUID ?? Guid.Empty;
                return guid == Guid.Empty ? null : guid.ToString();
            }
            catch { return null; }
        }

        private sealed class FachwerkComponentSnapshot
        {
            private static readonly string[] ColumnStringAttributes =
            {
                "fk_external_id", "fk_profile_key", "fk_mark", "fk_material", "fk_class",
                "fk_catalog_path", "fk_bevel_profile",
                "fk_break_1_mode", "fk_break_2_mode", "fk_break_3_mode", "fk_break_4_mode",
                "fk_break_5_mode", "fk_break_6_mode", "fk_break_7_mode", "fk_break_8_mode",
            };

            private static readonly string[] ColumnDoubleAttributes =
            {
                "fk_rotation_deg",
                "fk_break_1_z", "fk_break_2_z", "fk_break_3_z", "fk_break_4_z",
                "fk_break_5_z", "fk_break_6_z", "fk_break_7_z", "fk_break_8_z",
            };

            private static readonly string[] RigelStringAttributes =
            {
                "fk_external_id", "fk_rigel_code", "fk_mark", "fk_material", "fk_class",
            };

            private static readonly string[] RigelDoubleAttributes =
            {
                "fk_height", "fk_thickness",
            };

            private readonly ComponentInput _input;
            private readonly Dictionary<string, string> _strings;
            private readonly Dictionary<string, double> _doubles;

            private FachwerkComponentSnapshot(
                ComponentInput input,
                Dictionary<string, string> strings,
                Dictionary<string, double> doubles)
            {
                _input = input;
                _strings = strings;
                _doubles = doubles;
            }

            public static FachwerkComponentSnapshot Capture(Component component, string pluginName)
            {
                var input = component.GetComponentInput();
                if (input is null)
                    throw new InvalidOperationException($"Component '{pluginName}' has no input to snapshot.");

                var strings = new Dictionary<string, string>(StringComparer.Ordinal);
                var doubles = new Dictionary<string, double>(StringComparer.Ordinal);
                var stringAttributes = IsFachwerkColumnPluginName(pluginName)
                    ? ColumnStringAttributes
                    : RigelStringAttributes;
                var doubleAttributes = IsFachwerkColumnPluginName(pluginName)
                    ? ColumnDoubleAttributes
                    : RigelDoubleAttributes;

                foreach (var name in stringAttributes)
                {
                    var value = string.Empty;
                    if (component.GetAttribute(name, ref value)) strings[name] = value;
                }
                foreach (var name in doubleAttributes)
                {
                    var value = 0.0;
                    if (component.GetAttribute(name, ref value)) doubles[name] = value;
                }
                return new FachwerkComponentSnapshot(input, strings, doubles);
            }

            public void Restore(Component component)
            {
                if (!component.SetComponentInput(_input))
                    throw new InvalidOperationException("Component.SetComponentInput() failed while restoring a Fachwerk component.");
                foreach (var item in _strings) component.SetAttribute(item.Key, item.Value);
                foreach (var item in _doubles) component.SetAttribute(item.Key, item.Value);
            }

            public Component Recreate(string pluginName)
            {
                var component = new Component
                {
                    Name = pluginName,
                    Number = BaseComponent.PLUGIN_OBJECT_NUMBER,
                };
                Restore(component);
                if (!component.Insert())
                    throw new InvalidOperationException($"Component.Insert() failed while restoring '{pluginName}'.");
                return component;
            }
        }

        private sealed class FachwerkRigelPartSnapshot
        {
            private readonly Point _start;
            private readonly Point _end;
            private readonly string _profile;
            private readonly string _material;
            private readonly string _className;
            private readonly string _name;
            private readonly Position.PlaneEnum _plane;
            private readonly double _planeOffset;
            private readonly Position.DepthEnum _depth;
            private readonly double _depthOffset;
            private readonly Position.RotationEnum _rotation;
            private readonly double _rotationOffset;
            private readonly string _partPrefix;
            private readonly int _partStartNumber;
            private readonly bool _hasAssembly;
            private readonly string _assemblyName;
            private readonly string _assemblyPrefix;
            private readonly int _assemblyStartNumber;
            private readonly string _externalObjectId;
            private readonly string _componentType;
            private readonly int _schemaVersion;

            private FachwerkRigelPartSnapshot(
                Beam beam,
                string externalObjectId,
                string componentType,
                int schemaVersion)
            {
                _start = new Point(beam.StartPoint.X, beam.StartPoint.Y, beam.StartPoint.Z);
                _end = new Point(beam.EndPoint.X, beam.EndPoint.Y, beam.EndPoint.Z);
                _profile = beam.Profile.ProfileString;
                _material = beam.Material.MaterialString;
                _className = beam.Class;
                _name = beam.Name;
                _plane = beam.Position.Plane;
                _planeOffset = beam.Position.PlaneOffset;
                _depth = beam.Position.Depth;
                _depthOffset = beam.Position.DepthOffset;
                _rotation = beam.Position.Rotation;
                _rotationOffset = beam.Position.RotationOffset;
                var partNumber = beam.PartNumber ?? new NumberingSeries(string.Empty, 1);
                _partPrefix = partNumber.Prefix ?? string.Empty;
                _partStartNumber = partNumber.StartNumber;
                Assembly? assembly = null;
                try { assembly = beam.GetAssembly(); } catch { }
                _hasAssembly = assembly is not null;
                _assemblyName = assembly?.Name ?? string.Empty;
                var assemblyNumber = assembly?.AssemblyNumber ?? new NumberingSeries(string.Empty, 1);
                _assemblyPrefix = assemblyNumber.Prefix ?? string.Empty;
                _assemblyStartNumber = assemblyNumber.StartNumber;
                _externalObjectId = externalObjectId;
                _componentType = componentType;
                _schemaVersion = schemaVersion;
            }

            public static FachwerkRigelPartSnapshot Capture(Beam beam)
            {
                var externalObjectId = string.Empty;
                var componentType = string.Empty;
                var schemaVersion = 0;
                try { beam.GetUserProperty("STRUCTURA_EXTERNAL_OBJECT_ID", ref externalObjectId); } catch { }
                try { beam.GetUserProperty("STRUCTURA_COMPONENT_TYPE", ref componentType); } catch { }
                try { beam.GetUserProperty("STRUCTURA_SCHEMA_VERSION", ref schemaVersion); } catch { }
                return new FachwerkRigelPartSnapshot(beam, externalObjectId, componentType, schemaVersion);
            }

            public void RestorePart(Beam beam)
            {
                beam.StartPoint = new Point(_start.X, _start.Y, _start.Z);
                beam.EndPoint = new Point(_end.X, _end.Y, _end.Z);
                beam.Profile.ProfileString = _profile;
                beam.Material.MaterialString = _material;
                beam.Class = _className;
                beam.Name = _name;
                beam.Position.Plane = _plane;
                beam.Position.PlaneOffset = _planeOffset;
                beam.Position.Depth = _depth;
                beam.Position.DepthOffset = _depthOffset;
                beam.Position.Rotation = _rotation;
                beam.Position.RotationOffset = _rotationOffset;
                var partNumber = beam.PartNumber ?? new NumberingSeries(string.Empty, 1);
                partNumber.Prefix = _partPrefix;
                partNumber.StartNumber = _partStartNumber;
                beam.PartNumber = partNumber;
                beam.SetUserProperty("STRUCTURA_EXTERNAL_OBJECT_ID", _externalObjectId);
                beam.SetUserProperty("STRUCTURA_COMPONENT_TYPE", _componentType);
                beam.SetUserProperty("STRUCTURA_SCHEMA_VERSION", _schemaVersion);
            }

            public void RestoreAssembly(Beam beam)
            {
                if (!_hasAssembly) return;
                var assembly = beam.GetAssembly();
                if (assembly is null)
                    throw new InvalidOperationException("Tekla did not return the original Fachwerk rigel assembly during rollback.");
                var assemblyNumber = assembly.AssemblyNumber ?? new NumberingSeries(string.Empty, 1);
                assembly.Name = _assemblyName;
                assemblyNumber.Prefix = _assemblyPrefix;
                assemblyNumber.StartNumber = _assemblyStartNumber;
                assembly.AssemblyNumber = assemblyNumber;
                if (!assembly.Modify())
                    throw new InvalidOperationException("Assembly.Modify() failed while restoring a Fachwerk rigel.");
            }
        }

        private sealed record BeamSnapshot(
            Point Start,
            Point End,
            string Profile,
            string Material,
            string ClassName)
        {
            public static BeamSnapshot Capture(Beam beam) => new(
                new Point(beam.StartPoint.X, beam.StartPoint.Y, beam.StartPoint.Z),
                new Point(beam.EndPoint.X, beam.EndPoint.Y, beam.EndPoint.Z),
                beam.Profile.ProfileString,
                beam.Material.MaterialString,
                beam.Class);

            public void Restore(Beam beam)
            {
                beam.StartPoint = new Point(Start.X, Start.Y, Start.Z);
                beam.EndPoint = new Point(End.X, End.Y, End.Z);
                beam.Profile.ProfileString = Profile;
                beam.Material.MaterialString = Material;
                beam.Class = ClassName;
            }
        }

        private sealed record PolyBeamSnapshot(
            PolyBeamPointPayload[] Points,
            string Profile,
            string Material,
            string ClassName)
        {
            public static PolyBeamSnapshot Capture(PolyBeam polyBeam)
            {
                var points = polyBeam.Contour?.ContourPoints?
                    .OfType<ContourPoint>()
                    .Select(static point => SnapshotPoint(point))
                    .ToArray() ?? Array.Empty<PolyBeamPointPayload>();
                return new(
                    points,
                    polyBeam.Profile.ProfileString,
                    polyBeam.Material.MaterialString,
                    polyBeam.Class);
            }

            public void Restore(PolyBeam polyBeam) => ApplyPayload(polyBeam, new PolyBeamModifyPayload
            {
                Points = Points,
                Profile = Profile,
                Material = Material,
                ClassName = ClassName,
            });

            private static PolyBeamPointPayload SnapshotPoint(ContourPoint point)
            {
                var chamfer = point.Chamfer;
                return new()
                {
                    Point = new[] { point.X, point.Y, point.Z },
                    ChamferType = chamfer?.Type.ToString() ?? "CHAMFER_NONE",
                    ChamferX = chamfer?.X ?? 0,
                    ChamferY = chamfer?.Y ?? 0,
                    ChamferDz1 = chamfer?.DZ1 ?? 0,
                    ChamferDz2 = chamfer?.DZ2 ?? 0,
                };
            }
        }

        private sealed record AppliedChangeItem(
            int ItemNo,
            string? TargetGuid,
            string? ResultObjectGuid,
            int TeklaId,
            string Status);

        private sealed class ChangeSetConflictException : Exception
        {
            public ChangeSetConflictException(string errorCode, string message) : base(message)
            {
                ErrorCode = errorCode;
            }

            public string ErrorCode { get; }
        }
    }
}
