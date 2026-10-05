#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Platform.Bridge.Desktop.Tekla.ComponentRuntime;
using Platform.Contracts.TeklaPlan;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using Tekla.Structures.Solid;

namespace Platform.Bridge.Desktop.Tekla.ConstructiveRuntime
{
    /// <summary>
    /// Native executor for one open, consistently directed, line-only PolyBeam.
    /// DevelopedPlate stock is inserted in the first explicit station frame.
    /// Processed readback verifies its canonical contour after all planned cuts.
    /// </summary>
    public sealed class TeklaCreatePolyBeamExecutor : ITeklaNativeCommandExecutor
    {
        private const string ExecutorId = "tekla-native.create-poly-beam.v1";
        private const string PathComponentType = "ConstructivePolyBeamV1";
        private const string DevelopedPlateComponentType = "ConstructiveDevelopedPlatePolyBeamV1";

        public TeklaPlanExecutorRegistration Registration { get; } = new(
            ExecutorId,
            "create-poly-beam",
            new[] { "polyBeam", "developedPlate", "developedPlatePolyBeamStationFrameV1", "udaStamp" },
            applyReady: true);

        public ITeklaPreparedMutation<TeklaNativeCommandReadback> Prepare(
            Model model,
            TeklaPlanDocument plan,
            TeklaPlanCommand command,
            string operationId)
        {
            var parsed = TeklaPlanPayloads.ParseCreatePolyBeam(command);
            if (!parsed.Success || parsed.Value is null)
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_POLYBEAM_PAYLOAD_INVALID",
                    string.Join(" ", parsed.Diagnostics.Select(static item => item.Message)));
            }
            if (parsed.Value.Path.Segments.Any(static segment => segment.Kind != "line"))
                throw new TeklaNativeExecutionException("TEKLA_PLAN_POLYBEAM_ARCS_UNSUPPORTED", "Native line-only PolyBeam executor does not support arc segments.");
            if (parsed.Value.Bends.Count > 0)
                throw new TeklaNativeExecutionException("TEKLA_PLAN_POLYBEAM_BENDS_UNSUPPORTED", "Native line-only PolyBeam executor does not support explicit bend radii.");
            var evidenceDiagnostics = TeklaDevelopedPlateReadback.ValidateReferences(plan, command);
            if (evidenceDiagnostics.Count > 0)
                throw new TeklaNativeExecutionException(evidenceDiagnostics[0].Code, evidenceDiagnostics[0].Message);

            var externalObjectId = TeklaPlanOwnershipIdentity.Create(plan, command);
            var componentType = parsed.Value.DevelopedPlate is null
                ? PathComponentType
                : DevelopedPlateComponentType;
            var existing = FindExisting(model, externalObjectId, componentType);
            return new PreparedPolyBeamMutation(
                model,
                plan,
                command,
                parsed.Value,
                operationId,
                externalObjectId,
                componentType,
                existing);
        }

        private static PolyBeam? FindExisting(Model model, string externalObjectId, string expectedComponentType)
        {
            var matches = new List<PolyBeam>();
            var enumerator = model.GetModelObjectSelector()
                .GetAllObjectsWithType(ModelObject.ModelObjectEnum.POLYBEAM);
            while (enumerator.MoveNext())
            {
                if (enumerator.Current is not PolyBeam polyBeam) continue;
                var candidate = string.Empty;
                if (!polyBeam.GetUserProperty(StructuraServiceUdas.ExternalObjectId, ref candidate)) continue;
                if (string.Equals(candidate, externalObjectId, StringComparison.Ordinal)) matches.Add(polyBeam);
            }

            if (matches.Count > 1)
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_NATIVE_IDENTITY_DUPLICATE",
                    $"PolyBeam has {matches.Count} instances with ownership id '{externalObjectId}'.");
            }

            var existing = matches.SingleOrDefault();
            if (existing is null) return null;
            var componentType = string.Empty;
            existing.GetUserProperty(StructuraServiceUdas.ComponentType, ref componentType);
            if (!string.Equals(componentType, expectedComponentType, StringComparison.Ordinal))
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_NATIVE_OWNERSHIP_CONFLICT",
                    $"Ownership id '{externalObjectId}' belongs to component type '{componentType}', not '{expectedComponentType}'.");
            }
            return existing;
        }

        private sealed class PreparedPolyBeamMutation : ITeklaPreparedMutation<TeklaNativeCommandReadback>
        {
            private const double Tolerance = 1e-3;
            private const double SolidSectionToleranceMm = 0.25;
            private readonly Model _model;
            private readonly TeklaPlanDocument _plan;
            private readonly TeklaPlanCommand _command;
            private readonly TeklaCreatePolyBeamPayload _payload;
            private readonly string _operationId;
            private readonly string _externalObjectId;
            private readonly string _componentType;
            private readonly PolyBeam _polyBeam;
            private readonly PolyBeamSnapshot? _snapshot;
            private readonly bool _created;
            private readonly PolyBeamPosition _position;
            private readonly string _profile;
            private readonly string _material;
            private readonly IReadOnlyList<TeklaPathNode> _orderedNodes;
            private readonly IReadOnlyList<TeklaPathNode> _globalNodes;
            private readonly TransformationPlane _workPlane;
            private bool _inserted;
            private bool _applied;

            public PreparedPolyBeamMutation(
                Model model,
                TeklaPlanDocument plan,
                TeklaPlanCommand command,
                TeklaCreatePolyBeamPayload payload,
                string operationId,
                string externalObjectId,
                string componentType,
                PolyBeam? existing)
            {
                _model = model;
                _plan = plan;
                _command = command;
                _payload = payload;
                _operationId = operationId;
                _externalObjectId = externalObjectId;
                _componentType = componentType;
                _created = existing is null;
                _polyBeam = existing ?? new PolyBeam(PolyBeam.PolyBeamTypeEnum.BEAM);
                _snapshot = existing is null
                    ? null
                    : InPlane(model, new TransformationPlane(), () =>
                    {
                        if (!existing.Select())
                            throw new TeklaNativeExecutionException("TEKLA_PLAN_POLYBEAM_EXISTING_SELECT_FAILED", "Existing PolyBeam.Select() returned false during prepare.");
                        return PolyBeamSnapshot.Capture(existing);
                    });
                _position = PolyBeamPosition.From(payload.Placement, payload.DevelopedPlate);
                _profile = ResolveProfile(payload.Profile);
                _material = ResolveMaterial(payload.Material);
                _globalNodes = OrderedNodes(payload.Path);
                var developedFrame = DevelopedPlateWorkPlane.Create(payload.DevelopedPlate, _globalNodes);
                _workPlane = developedFrame?.TransformationPlane ?? new TransformationPlane();
                _orderedNodes = developedFrame is null
                    ? _globalNodes
                    : _globalNodes.Select(developedFrame.ToLocal).ToArray();
            }

            public string CommandId => _command.CommandId;

            public void Apply()
            {
                try
                {
                    InPlane(_model, _workPlane, () =>
                    {
                        if (!_created && !_polyBeam.Select())
                            throw Failure("TEKLA_PLAN_POLYBEAM_EXISTING_SELECT_FAILED", "Existing PolyBeam.Select() returned false during apply.");
                        ConfigurePolyBeam();
                        if (_created)
                        {
                            if (!_polyBeam.Insert())
                                throw Failure("TEKLA_PLAN_POLYBEAM_INSERT_FAILED", "PolyBeam.Insert() returned false.");
                            _inserted = true;
                        }

                        StampOwnership();
                        if (!_polyBeam.Modify())
                            throw Failure("TEKLA_PLAN_POLYBEAM_MODIFY_FAILED", "PolyBeam.Modify() returned false.");
                    });
                    _applied = true;
                }
                catch
                {
                    RestorePartialApply();
                    throw;
                }
            }

            public TeklaNativeCommandReadback Readback()
            {
                if (!_applied)
                    throw Failure("TEKLA_PLAN_POLYBEAM_NOT_APPLIED", "Readback was requested before apply completed.");

                return InPlane(_model, new TransformationPlane(), () =>
                {
                    if (!_polyBeam.Select())
                        throw Failure("TEKLA_PLAN_POLYBEAM_READBACK_SELECT_FAILED", "PolyBeam.Select() returned false during readback.");
                    var actualPoints = ReadContourPoints(_polyBeam);
                    if (actualPoints.Count != _globalNodes.Count)
                        throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native PolyBeam path point count does not match the plan.");
                    for (var index = 0; index < actualPoints.Count; index++)
                    {
                        VerifyPoint($"path[{index}]", actualPoints[index], _globalNodes[index].Point);
                        if (actualPoints[index].Chamfer?.Type != Chamfer.ChamferTypeEnum.CHAMFER_NONE)
                            throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native PolyBeam path[{index}] has an unexpected chamfer.");
                    }
                    VerifyEqual("profile", _polyBeam.Profile.ProfileString, _profile);
                    VerifyEqual("material", _polyBeam.Material.MaterialString, _material);
                    VerifyPosition();
                    if (_payload.DevelopedPlate is not null) VerifyDevelopedPlateSolid();

                    var externalObjectId = ReadStringUda(StructuraServiceUdas.ExternalObjectId);
                    var componentType = ReadStringUda(StructuraServiceUdas.ComponentType);
                    var operationId = ReadStringUda(StructuraServiceUdas.LastOperationId);
                    var schemaVersion = ReadIntUda(StructuraServiceUdas.SchemaVersion);
                    VerifyEqual("ownership id", externalObjectId, _externalObjectId);
                    VerifyEqual("component type", componentType, _componentType);
                    VerifyEqual("last operation id", operationId, _operationId);
                    if (schemaVersion != _plan.SchemaVersion)
                        throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native schema version does not match the plan.");

                    var guid = _model.GetGUIDByIdentifier(_polyBeam.Identifier) ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(guid) || _polyBeam.Identifier.ID <= 0)
                        throw Failure("TEKLA_PLAN_READBACK_IDENTITY_MISSING", "Tekla did not return a persistent PolyBeam identity.");

                    return new TeklaNativeCommandReadback
                    {
                        CommandId = _command.CommandId,
                        CommandKind = _command.Kind,
                        Action = _created ? "created" : "modified",
                        ExternalObjectId = _externalObjectId,
                        TeklaGuid = guid,
                        TeklaId = _polyBeam.Identifier.ID,
                        Start = PointArray(actualPoints[0]),
                        End = PointArray(actualPoints[actualPoints.Count - 1]),
                        Path = actualPoints.Select(static point => PointArray(point)).ToArray(),
                        Profile = _polyBeam.Profile.ProfileString,
                        Material = _polyBeam.Material.MaterialString,
                        Plane = _polyBeam.Position.Plane.ToString(),
                        PlaneOffsetMm = _polyBeam.Position.PlaneOffset,
                        Depth = _polyBeam.Position.Depth.ToString(),
                        DepthOffsetMm = _polyBeam.Position.DepthOffset,
                        Rotation = _polyBeam.Position.Rotation.ToString(),
                        RotationOffsetDeg = _polyBeam.Position.RotationOffset,
                        ComponentType = componentType,
                        SchemaVersion = schemaVersion,
                        LastOperationId = operationId,
                    };
                });
            }

            public void Restore()
            {
                if (_created)
                {
                    if (_inserted && !_polyBeam.Delete())
                        throw Failure("TEKLA_PLAN_POLYBEAM_ROLLBACK_DELETE_FAILED", "PolyBeam.Delete() returned false.");
                    _inserted = false;
                    _applied = false;
                    return;
                }

                if (_snapshot is null)
                    throw Failure("TEKLA_PLAN_POLYBEAM_ROLLBACK_STATE_MISSING", "Existing PolyBeam snapshot is missing.");
                InPlane(_model, new TransformationPlane(), () =>
                {
                    if (!_polyBeam.Select())
                        throw Failure("TEKLA_PLAN_POLYBEAM_ROLLBACK_SELECT_FAILED", "PolyBeam.Select() returned false during rollback.");
                    _snapshot.Restore(_polyBeam);
                    if (!_polyBeam.Modify())
                        throw Failure("TEKLA_PLAN_POLYBEAM_ROLLBACK_MODIFY_FAILED", "PolyBeam.Modify() returned false during rollback.");
                });
                _applied = false;
            }

            private void ConfigurePolyBeam()
            {
                var contour = new Contour();
                foreach (var node in _orderedNodes)
                {
                    var contourPoint = new ContourPoint(Point(node.Point), new Chamfer { Type = Chamfer.ChamferTypeEnum.CHAMFER_NONE });
                    contour.AddContourPoint(contourPoint);
                }
                _polyBeam.Contour = contour;
                _polyBeam.Profile.ProfileString = _profile;
                _polyBeam.Material.MaterialString = _material;
                _polyBeam.Position.Plane = _position.Plane;
                _polyBeam.Position.PlaneOffset = _position.PlaneOffsetMm;
                _polyBeam.Position.Depth = _position.Depth;
                _polyBeam.Position.DepthOffset = _position.DepthOffsetMm;
                _polyBeam.Position.Rotation = Position.RotationEnum.FRONT;
                _polyBeam.Position.RotationOffset = _position.RotationDeg;
            }

            private void StampOwnership()
            {
                RequireUda(_polyBeam.SetUserProperty(StructuraServiceUdas.ExternalObjectId, _externalObjectId), StructuraServiceUdas.ExternalObjectId);
                RequireUda(_polyBeam.SetUserProperty(StructuraServiceUdas.ComponentType, _componentType), StructuraServiceUdas.ComponentType);
                RequireUda(_polyBeam.SetUserProperty(StructuraServiceUdas.SchemaVersion, _plan.SchemaVersion), StructuraServiceUdas.SchemaVersion);
                RequireUda(_polyBeam.SetUserProperty(StructuraServiceUdas.LastOperationId, _operationId), StructuraServiceUdas.LastOperationId);
            }

            private void RequireUda(bool result, string name)
            {
                if (!result)
                    throw Failure("TEKLA_PLAN_UDA_STAMP_FAILED", $"SetUserProperty('{name}') returned false.");
            }

            private void RestorePartialApply()
            {
                try
                {
                    if (_created)
                    {
                        if (_inserted) _polyBeam.Delete();
                        _inserted = false;
                    }
                    else if (_snapshot is not null)
                    {
                        InPlane(_model, new TransformationPlane(), () =>
                        {
                            _polyBeam.Select();
                            _snapshot.Restore(_polyBeam);
                            _polyBeam.Modify();
                        });
                    }
                }
                finally
                {
                    _applied = false;
                }
            }

            private void VerifyPosition()
            {
                if (_polyBeam.Position.Plane != _position.Plane ||
                    Math.Abs(_polyBeam.Position.PlaneOffset - _position.PlaneOffsetMm) > Tolerance ||
                    _polyBeam.Position.Depth != _position.Depth ||
                    Math.Abs(_polyBeam.Position.DepthOffset - _position.DepthOffsetMm) > Tolerance ||
                    _polyBeam.Position.Rotation != Position.RotationEnum.FRONT ||
                    Math.Abs(_polyBeam.Position.RotationOffset - _position.RotationDeg) > Tolerance)
                {
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native PolyBeam placement does not match the plan.");
                }
            }

            private void VerifyDevelopedPlateSolid()
            {
                var developed = _payload.DevelopedPlate!;
                var stations = developed.StationFrame.Stations;
                var solid = _polyBeam.GetSolid();
                var vertices = ReadSolidVertices(solid);
                if (vertices.Count < 8)
                    throw Failure("TEKLA_PLAN_DEVELOPED_PLATE_SOLID_INVALID", "Native DevelopedPlate solid has too few vertices.");
                if (developed.DevelopedContour is not null)
                {
                    // RAW excludes booleans, including cuts retained by an idempotent upsert.
                    var raw = _polyBeam.GetSolid(global::Tekla.Structures.Model.Solid.SolidCreationTypeEnum.RAW);
                    var rawVertices = ReadSolidVertices(raw);
                    VerifyEndSection("raw start", stations[0], rawVertices, developed);
                    VerifyEndSection("raw end", stations[stations.Count - 1], rawVertices, developed);
                    VerifySegmentSections(raw, stations, developed);
                    VerifyEndSection("final start", stations[0], vertices, developed, processed: true);
                    VerifyEndSection("final end", stations[stations.Count - 1], vertices, developed, processed: true);
                    VerifySegmentSections(solid, stations, developed, processed: true);
                    VerifyContourSections(solid, stations, developed);
                    try
                    {
                        var diagnostics = TeklaDevelopedPlateReadback.ValidateVolumes(_command, developed,
                            TeklaDevelopedPlateReadback.MeasureVolume(ReadSolidFaces(raw)),
                            TeklaDevelopedPlateReadback.MeasureVolume(ReadSolidFaces(solid)));
                        if (diagnostics.Count > 0) throw Failure(diagnostics[0].Code, diagnostics[0].Message);
                    }
                    catch (InvalidOperationException error)
                    {
                        throw Failure("TEKLA_PLAN_DEVELOPED_PLATE_SOLID_INVALID", error.Message);
                    }
                    return;
                }
                VerifyEndSection("start", stations[0], vertices, developed);
                VerifyEndSection("end", stations[stations.Count - 1], vertices, developed);
                VerifySegmentSections(solid, stations, developed);
            }

            private void VerifySegmentSections(
                global::Tekla.Structures.Model.Solid solid,
                IReadOnlyList<TeklaDevelopedStationSpec> stations,
                TeklaDevelopedPlateSpec developed,
                bool processed = false)
            {
                for (var index = 0; index < stations.Count - 1; index++)
                {
                    var start = stations[index];
                    var end = stations[index + 1];
                    var frame = InterpolateSegmentFrame(start, end);
                    if (processed)
                    {
                        VerifyProcessedSection(solid, $"segment[{index}]", frame, (start.StationMm + end.StationMm) / 2, developed);
                        continue;
                    }
                    VerifySolidSpan(
                        solid,
                        $"segment[{index}] width",
                        frame.Origin,
                        frame.AxisX,
                        frame.Origin,
                        developed.TransverseOffsetMm - (developed.StockWidthMm / 2),
                        developed.TransverseOffsetMm + (developed.StockWidthMm / 2),
                        developed);

                    var thicknessProbeOrigin = Offset(frame.Origin, frame.AxisX, developed.TransverseOffsetMm);
                    VerifySolidSpan(
                        solid,
                        $"segment[{index}] thickness",
                        thicknessProbeOrigin,
                        frame.AxisY,
                        frame.Origin,
                        -(developed.ThicknessMm / 2),
                        developed.ThicknessMm / 2,
                        developed);
                }
            }

            private void VerifyContourSections(global::Tekla.Structures.Model.Solid solid,
                IReadOnlyList<TeklaDevelopedStationSpec> stations, TeklaDevelopedPlateSpec developed)
            {
                for (var i = 0; i < stations.Count - 1; i++)
                {
                    var start = stations[i];
                    var end = stations[i + 1];
                    if (!AxesNear(start.Frame, end.Frame)) continue;
                    var breaks = developed.DevelopedContour!.Vertices.Select(vertex => vertex.Point.X)
                        .Where(s => s > start.StationMm && s < end.StationMm)
                        .Concat(new[] { start.StationMm, end.StationMm }).Distinct().OrderBy(s => s).ToArray();
                    var probes = breaks.Skip(1).Take(breaks.Length - 2)
                        .Concat(breaks.Zip(breaks.Skip(1), (a, b) => (a + b) / 2));
                    foreach (var stationMm in probes)
                    {
                        var origin = Offset(Point(start.Frame.Origin), Vector(start.Frame.AxisZ), stationMm - start.StationMm);
                        VerifyProcessedSection(solid, $"contour[{stationMm:0.###}]",
                            new SegmentFrame(origin, Vector(start.Frame.AxisX), Vector(start.Frame.AxisY)), stationMm, developed);
                    }
                }
            }

            private void VerifyProcessedSection(global::Tekla.Structures.Model.Solid solid, string role,
                SegmentFrame frame, double stationMm, TeklaDevelopedPlateSpec developed)
            {
                var width = TeklaDevelopedPlateReadback.WidthAt(developed, stationMm);
                foreach (var depth in new[] { -developed.ThicknessMm / 4, 0, developed.ThicknessMm / 4 })
                    VerifySolidSpan(solid, $"{role} width at depth {depth:0.###}", Offset(frame.Origin, frame.AxisY, depth),
                        frame.AxisX, frame.Origin, width.X, width.Y, developed, requireSingleInterval: true);
                foreach (var fraction in new[] { 0.1, 0.5, 0.9 })
                    VerifySolidSpan(solid, $"{role} thickness at fraction {fraction}",
                        Offset(frame.Origin, frame.AxisX, width.X + (width.Y - width.X) * fraction),
                        frame.AxisY, frame.Origin, -developed.ThicknessMm / 2, developed.ThicknessMm / 2, developed, requireSingleInterval: true);
            }

            private static bool AxesNear(TeklaPlane3 a, TeklaPlane3 b)
                => new[] { (a.AxisX, b.AxisX), (a.AxisY, b.AxisY), (a.AxisZ, b.AxisZ) }.All(pair =>
                    Math.Abs(pair.Item1.X - pair.Item2.X) <= 1e-6 && Math.Abs(pair.Item1.Y - pair.Item2.Y) <= 1e-6 && Math.Abs(pair.Item1.Z - pair.Item2.Z) <= 1e-6);

            private void VerifySolidSpan(
                global::Tekla.Structures.Model.Solid solid,
                string role,
                Point probeOrigin,
                Vector probeAxis,
                Point projectionOrigin,
                double expectedMin,
                double expectedMax,
                TeklaDevelopedPlateSpec developed,
                bool requireSingleInterval = false)
            {
                var probeHalfLength = Math.Max(developed.StockWidthMm, developed.ThicknessMm)
                    + Math.Abs(developed.TransverseOffsetMm)
                    + 100;
                var intersections = solid.Intersect(new LineSegment(
                        Offset(probeOrigin, probeAxis, -probeHalfLength),
                        Offset(probeOrigin, probeAxis, probeHalfLength)))
                    .OfType<Point>()
                    .ToArray();
                if (intersections.Length < 2)
                    throw Failure("TEKLA_PLAN_DEVELOPED_PLATE_SECTION_MISSING", $"Native DevelopedPlate {role} probe did not cross the solid.");

                var projected = intersections
                    .Select(point => Project(point, projectionOrigin, probeAxis))
                    .OrderBy(value => value).ToArray();
                if (requireSingleInterval && projected.Where((value, index) => index == 0 || value - projected[index - 1] > Tolerance).Count() != 2)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native DevelopedPlate {role} has unexpected internal boundaries.");
                VerifySolidNear($"{role} min", projected.Min(), expectedMin);
                VerifySolidNear($"{role} max", projected.Max(), expectedMax);
            }

            private SegmentFrame InterpolateSegmentFrame(
                TeklaDevelopedStationSpec start,
                TeklaDevelopedStationSpec end)
            {
                var startOrigin = Point(start.Frame.Origin);
                var endOrigin = Point(end.Frame.Origin);
                var axisZ = Normalize(Subtract(endOrigin, startOrigin));
                if (axisZ is null)
                    throw Failure("TEKLA_PLAN_DEVELOPED_STATION_FRAME_INVALID", $"DevelopedPlate stations '{start.Id}' and '{end.Id}' have coincident origins.");

                var startAxisX = Vector(start.Frame.AxisX);
                var endAxisX = Vector(end.Frame.AxisX);
                var preferredAxisX = Add(startAxisX, endAxisX);
                var axisX = Normalize(Reject(preferredAxisX, axisZ))
                    ?? Normalize(Reject(startAxisX, axisZ));
                if (axisX is null)
                    throw Failure("TEKLA_PLAN_DEVELOPED_STATION_FRAME_INVALID", $"DevelopedPlate segment '{start.Id}' -> '{end.Id}' has no stable transverse axis.");
                if (Dot(axisX, preferredAxisX) < 0) axisX = Scale(axisX, -1);

                var axisY = Normalize(Cross(axisZ, axisX));
                var preferredAxisY = Add(Vector(start.Frame.AxisY), Vector(end.Frame.AxisY));
                if (axisY is null || Dot(axisY, preferredAxisY) <= 0)
                    throw Failure("TEKLA_PLAN_DEVELOPED_STATION_FRAME_INVALID", $"DevelopedPlate segment '{start.Id}' -> '{end.Id}' changes signed thickness orientation.");

                return new SegmentFrame(
                    Midpoint(startOrigin, endOrigin),
                    axisX,
                    axisY);
            }

            private void VerifyEndSection(
                string role,
                TeklaDevelopedStationSpec station,
                IReadOnlyList<Point> vertices,
                TeklaDevelopedPlateSpec developed,
                bool processed = false)
            {
                const double sectionTolerance = 0.25;
                if (processed)
                {
                    var diagnostics = TeklaDevelopedPlateReadback.ValidateEndSection(_command, developed, station,
                        vertices.Select(point => new TeklaVector3(point.X, point.Y, point.Z)).ToArray());
                    if (diagnostics.Count > 0) throw Failure(diagnostics[0].Code, diagnostics[0].Message);
                    return;
                }
                var origin = Point(station.Frame.Origin);
                var axisX = Vector(station.Frame.AxisX);
                var axisY = Vector(station.Frame.AxisY);
                var axisZ = Vector(station.Frame.AxisZ);
                var section = vertices
                    .Where(point => Math.Abs(Project(point, origin, axisZ)) <= sectionTolerance)
                    .ToArray();
                if (section.Length < 4)
                    throw Failure("TEKLA_PLAN_DEVELOPED_PLATE_SECTION_MISSING", $"Native DevelopedPlate {role} section was not found in the solid.");
                var x = section.Select(point => Project(point, origin, axisX)).ToArray();
                var y = section.Select(point => Project(point, origin, axisY)).ToArray();
                VerifyNear($"{role} width min", x.Min(), developed.TransverseOffsetMm - (developed.StockWidthMm / 2));
                VerifyNear($"{role} width max", x.Max(), developed.TransverseOffsetMm + (developed.StockWidthMm / 2));
                VerifyNear($"{role} thickness min", y.Min(), -(developed.ThicknessMm / 2));
                VerifyNear($"{role} thickness max", y.Max(), developed.ThicknessMm / 2);
            }

            private void VerifyNear(string role, double actual, double expected)
            {
                if (Math.Abs(actual - expected) > Tolerance)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native DevelopedPlate {role} is {actual:0.###} mm, expected {expected:0.###} mm.");
            }

            private void VerifySolidNear(string role, double actual, double expected)
            {
                if (Math.Abs(actual - expected) > SolidSectionToleranceMm)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native DevelopedPlate {role} is {actual:0.###} mm, expected {expected:0.###} mm.");
            }

            private void VerifyPoint(string role, Point actual, TeklaVector3 expected)
            {
                if (Math.Abs(actual.X - expected.X) > Tolerance ||
                    Math.Abs(actual.Y - expected.Y) > Tolerance ||
                    Math.Abs(actual.Z - expected.Z) > Tolerance)
                {
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native PolyBeam {role} point does not match the plan.");
                }
            }

            private void VerifyEqual(string role, string actual, string expected)
            {
                if (!string.Equals(actual?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native PolyBeam {role} does not match the plan.");
            }

            private string ReadStringUda(string name)
            {
                var value = string.Empty;
                if (!_polyBeam.GetUserProperty(name, ref value))
                    throw Failure("TEKLA_PLAN_READBACK_UDA_MISSING", $"Native PolyBeam UDA '{name}' is missing.");
                return value;
            }

            private int ReadIntUda(string name)
            {
                var value = 0;
                if (!_polyBeam.GetUserProperty(name, ref value))
                    throw Failure("TEKLA_PLAN_READBACK_UDA_MISSING", $"Native PolyBeam UDA '{name}' is missing.");
                return value;
            }

            private TeklaNativeExecutionException Failure(string code, string message)
                => new(code, $"Command '{_command.CommandId}': {message}");

            private sealed class SegmentFrame
            {
                public SegmentFrame(Point origin, Vector axisX, Vector axisY)
                {
                    Origin = origin;
                    AxisX = axisX;
                    AxisY = axisY;
                }

                public Point Origin { get; }
                public Vector AxisX { get; }
                public Vector AxisY { get; }
            }
        }

        private sealed class PolyBeamSnapshot
        {
            private readonly IReadOnlyList<ContourPointSnapshot> _points;
            private readonly string _name;
            private readonly string _finish;
            private readonly string _className;
            private readonly string _profile;
            private readonly string _material;
            private readonly Position.PlaneEnum _plane;
            private readonly double _planeOffset;
            private readonly Position.DepthEnum _depth;
            private readonly double _depthOffset;
            private readonly Position.RotationEnum _rotation;
            private readonly double _rotationOffset;
            private readonly double _angle;
            private readonly double _angle2;
            private readonly double _cambering;
            private readonly double _shortening;
            private readonly string _externalObjectId;
            private readonly string _componentType;
            private readonly int _schemaVersion;
            private readonly string _lastOperationId;

            private PolyBeamSnapshot(PolyBeam polyBeam)
            {
                _points = ReadContourPoints(polyBeam).Select(static point => ContourPointSnapshot.Capture(point)).ToArray();
                _name = polyBeam.Name;
                _finish = polyBeam.Finish;
                _className = polyBeam.Class;
                _profile = polyBeam.Profile.ProfileString;
                _material = polyBeam.Material.MaterialString;
                _plane = polyBeam.Position.Plane;
                _planeOffset = polyBeam.Position.PlaneOffset;
                _depth = polyBeam.Position.Depth;
                _depthOffset = polyBeam.Position.DepthOffset;
                _rotation = polyBeam.Position.Rotation;
                _rotationOffset = polyBeam.Position.RotationOffset;
                _angle = polyBeam.DeformingData.Angle;
                _angle2 = polyBeam.DeformingData.Angle2;
                _cambering = polyBeam.DeformingData.Cambering;
                _shortening = polyBeam.DeformingData.Shortening;
                _externalObjectId = ReadString(polyBeam, StructuraServiceUdas.ExternalObjectId);
                _componentType = ReadString(polyBeam, StructuraServiceUdas.ComponentType);
                _schemaVersion = ReadInt(polyBeam, StructuraServiceUdas.SchemaVersion);
                _lastOperationId = ReadString(polyBeam, StructuraServiceUdas.LastOperationId);
            }

            public static PolyBeamSnapshot Capture(PolyBeam polyBeam) => new(polyBeam);

            public void Restore(PolyBeam polyBeam)
            {
                var contour = new Contour();
                foreach (var point in _points)
                {
                    contour.AddContourPoint(point.Create());
                }
                polyBeam.Contour = contour;
                polyBeam.Name = _name;
                polyBeam.Finish = _finish;
                polyBeam.Class = _className;
                polyBeam.Profile.ProfileString = _profile;
                polyBeam.Material.MaterialString = _material;
                polyBeam.Position.Plane = _plane;
                polyBeam.Position.PlaneOffset = _planeOffset;
                polyBeam.Position.Depth = _depth;
                polyBeam.Position.DepthOffset = _depthOffset;
                polyBeam.Position.Rotation = _rotation;
                polyBeam.Position.RotationOffset = _rotationOffset;
                polyBeam.DeformingData.Angle = _angle;
                polyBeam.DeformingData.Angle2 = _angle2;
                polyBeam.DeformingData.Cambering = _cambering;
                polyBeam.DeformingData.Shortening = _shortening;
                polyBeam.SetUserProperty(StructuraServiceUdas.ExternalObjectId, _externalObjectId);
                polyBeam.SetUserProperty(StructuraServiceUdas.ComponentType, _componentType);
                polyBeam.SetUserProperty(StructuraServiceUdas.SchemaVersion, _schemaVersion);
                polyBeam.SetUserProperty(StructuraServiceUdas.LastOperationId, _lastOperationId);
            }

            private static string ReadString(PolyBeam polyBeam, string name)
            {
                var value = string.Empty;
                try { polyBeam.GetUserProperty(name, ref value); } catch { }
                return value;
            }

            private static int ReadInt(PolyBeam polyBeam, string name)
            {
                var value = 0;
                try { polyBeam.GetUserProperty(name, ref value); } catch { }
                return value;
            }
        }

        private sealed class ContourPointSnapshot
        {
            private readonly Point _point;
            private readonly Chamfer.ChamferTypeEnum _type;
            private readonly double _x;
            private readonly double _y;
            private readonly double _dz1;
            private readonly double _dz2;

            private ContourPointSnapshot(ContourPoint point)
            {
                _point = new Point(point.X, point.Y, point.Z);
                _type = point.Chamfer?.Type ?? Chamfer.ChamferTypeEnum.CHAMFER_NONE;
                _x = point.Chamfer?.X ?? 0;
                _y = point.Chamfer?.Y ?? 0;
                _dz1 = point.Chamfer?.DZ1 ?? 0;
                _dz2 = point.Chamfer?.DZ2 ?? 0;
            }

            public static ContourPointSnapshot Capture(ContourPoint point) => new(point);

            public ContourPoint Create()
                => new(
                    new Point(_point.X, _point.Y, _point.Z),
                    new Chamfer { Type = _type, X = _x, Y = _y, DZ1 = _dz1, DZ2 = _dz2 });
        }

        private sealed class PolyBeamPosition
        {
            public Position.PlaneEnum Plane { get; private set; } = Position.PlaneEnum.MIDDLE;
            public double PlaneOffsetMm { get; private set; }
            public Position.DepthEnum Depth { get; private set; } = Position.DepthEnum.MIDDLE;
            public double DepthOffsetMm { get; private set; }
            public double RotationDeg { get; private set; }

            public static PolyBeamPosition From(TeklaSectionPlacementSpec? placement, TeklaDevelopedPlateSpec? developedPlate)
            {
                var result = new PolyBeamPosition();
                result.PlaneOffsetMm = developedPlate?.TransverseOffsetMm ?? 0;
                if (placement is null) return result;
                result.PlaneOffsetMm += placement.OffsetXmm ?? 0;
                result.DepthOffsetMm = placement.OffsetYmm ?? 0;
                result.RotationDeg = placement.RotationDeg ?? 0;
                switch (placement.Anchor)
                {
                    case "left": result.Plane = Position.PlaneEnum.LEFT; break;
                    case "right": result.Plane = Position.PlaneEnum.RIGHT; break;
                    case "top": result.Depth = Position.DepthEnum.FRONT; break;
                    case "bottom": result.Depth = Position.DepthEnum.BEHIND; break;
                }
                return result;
            }
        }

        private sealed class DevelopedPlateWorkPlane
        {
            private readonly Matrix _toLocal;

            private DevelopedPlateWorkPlane(CoordinateSystem coordinateSystem)
            {
                TransformationPlane = new TransformationPlane(coordinateSystem);
                _toLocal = MatrixFactory.ToCoordinateSystem(coordinateSystem);
            }

            public TransformationPlane TransformationPlane { get; }

            public TeklaPathNode ToLocal(TeklaPathNode node)
            {
                var point = _toLocal.Transform(Point(node.Point));
                return new TeklaPathNode
                {
                    Id = node.Id,
                    Point = new TeklaVector3(point.X, point.Y, point.Z),
                };
            }

            public static DevelopedPlateWorkPlane? Create(
                TeklaDevelopedPlateSpec? developedPlate,
                IReadOnlyList<TeklaPathNode> nodes)
            {
                if (developedPlate is null) return null;
                if (developedPlate.StationFrame.Version != 1 || developedPlate.StationFrame.Stations.Count != nodes.Count)
                    throw new TeklaNativeExecutionException("TEKLA_PLAN_DEVELOPED_STATION_FRAME_INVALID", "DevelopedPlate requires station-frame version 1 with one station per path node.");
                var first = developedPlate.StationFrame.Stations[0];
                var axisX = Vector(first.Frame.AxisX);
                var axisY = Vector(first.Frame.AxisY);
                var coordinateSystem = new CoordinateSystem(
                    Point(first.Frame.Origin),
                    new Vector(-axisY.X, -axisY.Y, -axisY.Z),
                    axisX);
                return new DevelopedPlateWorkPlane(coordinateSystem);
            }
        }

        private static IReadOnlyList<TeklaPathNode> OrderedNodes(TeklaPath3 path)
        {
            var byId = path.Nodes.ToDictionary(static node => node.Id, StringComparer.Ordinal);
            var result = new List<TeklaPathNode> { byId[path.Segments[0].StartNodeId] };
            result.AddRange(path.Segments.Select(segment => byId[segment.EndNodeId]));
            return result;
        }

        private static IReadOnlyList<ContourPoint> ReadContourPoints(PolyBeam polyBeam)
            => polyBeam.Contour?.ContourPoints?.OfType<ContourPoint>().ToArray() ?? Array.Empty<ContourPoint>();

        private static IReadOnlyList<Point> ReadSolidVertices(global::Tekla.Structures.Model.Solid solid)
        {
            var result = new List<Point>();
            var faces = solid.GetFaceEnumerator();
            while (faces.MoveNext())
            {
                if (faces.Current is not Face face) continue;
                var loops = face.GetLoopEnumerator();
                while (loops.MoveNext())
                {
                    if (loops.Current is not Loop loop) continue;
                    var vertices = loop.GetVertexEnumerator();
                    while (vertices.MoveNext())
                    {
                        if (vertices.Current is Point point) result.Add(point);
                    }
                }
            }
            return result;
        }

        private static double Project(Point point, Point origin, Vector axis)
            => ((point.X - origin.X) * axis.X)
             + ((point.Y - origin.Y) * axis.Y)
             + ((point.Z - origin.Z) * axis.Z);

        private static IReadOnlyList<TeklaSolidFaceReadback> ReadSolidFaces(global::Tekla.Structures.Model.Solid solid)
        {
            var result = new List<TeklaSolidFaceReadback>();
            var faces = solid.GetFaceEnumerator();
            while (faces.MoveNext())
            {
                if (faces.Current is not Face face) continue;
                var loops = new List<IReadOnlyList<TeklaVector3>>();
                var iterator = face.GetLoopEnumerator();
                while (iterator.MoveNext())
                {
                    if (iterator.Current is not Loop loop) continue;
                    var points = new List<TeklaVector3>();
                    var vertices = loop.GetVertexEnumerator();
                    while (vertices.MoveNext())
                        if (vertices.Current is Point point) points.Add(new TeklaVector3(point.X, point.Y, point.Z));
                    loops.Add(points);
                }
                result.Add(new TeklaSolidFaceReadback
                {
                    Normal = new TeklaVector3(face.Normal.X, face.Normal.Y, face.Normal.Z), Loops = loops,
                });
            }
            return result;
        }

        private static Point Midpoint(Point start, Point end)
            => new(
                (start.X + end.X) / 2,
                (start.Y + end.Y) / 2,
                (start.Z + end.Z) / 2);

        private static Point Offset(Point point, Vector axis, double distance)
            => new(
                point.X + (axis.X * distance),
                point.Y + (axis.Y * distance),
                point.Z + (axis.Z * distance));

        private static Vector Subtract(Point end, Point start)
            => new(end.X - start.X, end.Y - start.Y, end.Z - start.Z);

        private static Vector Add(Vector left, Vector right)
            => new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

        private static Vector Scale(Vector vector, double scale)
            => new(vector.X * scale, vector.Y * scale, vector.Z * scale);

        private static double Dot(Vector left, Vector right)
            => (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);

        private static Vector Cross(Vector left, Vector right)
            => new(
                (left.Y * right.Z) - (left.Z * right.Y),
                (left.Z * right.X) - (left.X * right.Z),
                (left.X * right.Y) - (left.Y * right.X));

        private static Vector Reject(Vector vector, Vector axis)
            => Add(vector, Scale(axis, -Dot(vector, axis)));

        private static Vector? Normalize(Vector vector)
        {
            var length = Math.Sqrt(Dot(vector, vector));
            return length <= 1e-9 ? null : Scale(vector, 1 / length);
        }

        private static void InPlane(Model model, TransformationPlane plane, Action action)
        {
            var handler = model.GetWorkPlaneHandler();
            var previous = handler.GetCurrentTransformationPlane();
            if (!handler.SetCurrentTransformationPlane(plane))
                throw new TeklaNativeExecutionException("TEKLA_PLAN_WORK_PLANE_SET_FAILED", "Tekla rejected the requested PolyBeam work plane.");
            Exception? primary = null;
            try
            {
                action();
            }
            catch (Exception exception)
            {
                primary = exception;
                throw;
            }
            finally
            {
                if (!handler.SetCurrentTransformationPlane(previous) && primary is null)
                    throw new TeklaNativeExecutionException("TEKLA_PLAN_WORK_PLANE_RESTORE_FAILED", "Tekla rejected restoration of the previous work plane.");
            }
        }

        private static T InPlane<T>(Model model, TransformationPlane plane, Func<T> action)
        {
            T? result = default;
            InPlane(model, plane, () => result = action());
            return result!;
        }

        private static string ResolveProfile(TeklaProfileSpec profile)
        {
            if (string.Equals(profile.Kind, "catalog", StringComparison.Ordinal)) return profile.Name!;
            return "PL" + Number(profile.ThicknessMm!.Value) + "*" + Number(profile.WidthMm!.Value);
        }

        private static string ResolveMaterial(TeklaMaterialSpec material)
            => !string.IsNullOrWhiteSpace(material.Grade)
                ? material.Grade!
                : !string.IsNullOrWhiteSpace(material.Name)
                    ? material.Name!
                    : material.Id;

        private static string Number(double value) => value.ToString("0.###############", CultureInfo.InvariantCulture);
        private static Point Point(TeklaVector3 value) => new(value.X, value.Y, value.Z);
        private static Vector Vector(TeklaVector3 value) => new(value.X, value.Y, value.Z);
        private static double[] PointArray(Point value) => new[] { value.X, value.Y, value.Z };
    }
}
