#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Platform.Fachwerk.Geometry;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace Platform.Bridge.Desktop.Tekla.Components.Fachwerk
{
    internal sealed class FachwerkNodeCommand
    {
        public string NodeId { get; init; } = string.Empty;
        public string ColumnExternalObjectId { get; init; } = string.Empty;
        public double TopElevationMm { get; init; }
        public double HeightMm { get; init; }
        public double ProjectionFromBottomInnerFlangeMm { get; init; } = 220;
        public string Material { get; init; } = "C355-5";
        public string ClassName { get; init; } = "3";
    }

    internal sealed class FachwerkNodeAdapterException : Exception
    {
        public FachwerkNodeAdapterException(string errorCode, string message) : base(message)
        {
            ErrorCode = errorCode;
        }

        public string ErrorCode { get; }
    }

    internal sealed class FachwerkNodeTeklaChange
    {
        private const string ExternalIdUda = "STRUCTURA_EXTERNAL_OBJECT_ID";
        private const string ComponentTypeUda = "STRUCTURA_COMPONENT_TYPE";
        private const string SchemaVersionUda = "STRUCTURA_SCHEMA_VERSION";
        private const string NodePartTypePrefix = "FachwerkNodePart:";
        private const string NodeHoleTypePrefix = "FachwerkNodeHoles:";
        private const string NodeFitTypePrefix = "FachwerkNodeFit:";
        private const string StablePartNamePrefix = "FKN";
        private static readonly string[] ContourRoles =
        {
            "main-web", "side-plate-left", "side-plate-right", "closure-left", "closure-right",
        };
        private static readonly string[] BeamRoles =
        {
            "top-plate", "bottom-plate", "diagonal-left", "diagonal-right",
        };
        private static readonly string[] FitRoles =
        {
            "upper-inner-flange", "lower-inner-flange",
            "upper-inner-web", "lower-inner-web",
            "upper-outer-web", "lower-outer-web",
        };
        private static readonly IReadOnlyDictionary<string, string> PartRoleCodes =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["main-web"] = "MW",
                ["side-plate-left"] = "SL",
                ["side-plate-right"] = "SR",
                ["closure-left"] = "CL",
                ["closure-right"] = "CR",
                ["top-plate"] = "TP",
                ["bottom-plate"] = "BP",
                ["diagonal-left"] = "DL",
                ["diagonal-right"] = "DR",
            };
        private static readonly IReadOnlyDictionary<string, string> PartRolesByCode =
            PartRoleCodes.ToDictionary(static pair => pair.Value, static pair => pair.Key, StringComparer.Ordinal);

        private readonly Model _model;
        private readonly Component _column;
        private readonly FachwerkNodeCommand _command;
        private readonly FachwerkNodeSpec _spec;
        private readonly ResolvedColumnContext _columnContext;
        private readonly ExistingNodeState _existing;
        private readonly List<ModelObject> _created = new();
        private readonly List<NodePartSnapshot> _partSnapshots = new();
        private readonly List<FittingSnapshot> _fitSnapshots = new();
        private BoltArraySnapshot? _boltSnapshot;
        private Part? _resultPart;
        private bool _noOp;

        private FachwerkNodeTeklaChange(
            Model model,
            Component column,
            FachwerkNodeCommand command,
            FachwerkNodeSpec spec,
            ResolvedColumnContext columnContext,
            ExistingNodeState existing)
        {
            _model = model;
            _column = column;
            _command = command;
            _spec = spec;
            _columnContext = columnContext;
            _existing = existing;
        }

        public string? ResultObjectGuid => ReadGuid(_resultPart);
        public int TeklaId => _resultPart?.Identifier?.ID ?? 0;

        public static FachwerkNodeTeklaChange Prepare(
            Model model,
            Component column,
            FachwerkNodeCommand command)
        {
            if (model is null) throw new ArgumentNullException(nameof(model));
            if (column is null) throw new ArgumentNullException(nameof(column));
            if (command is null) throw new ArgumentNullException(nameof(command));

            var ownerId = column.Identifier?.ID ?? 0;
            if (ownerId == 0)
                throw Conflict("FACHWERK_NODE_COLUMN_INVALID", "The Fachwerk column has no persistent Tekla identifier.");

            var columnParts = ReadColumnParts(model, ownerId);
            var resolved = ResolveColumnContext(columnParts, command.TopElevationMm, command.HeightMm);
            var bottomElevation = command.TopElevationMm - command.HeightMm;
            var frame = ResolveFrame(resolved, bottomElevation);
            var topInnerX = ProjectX(
                InterpolateCenterLineAtElevation(resolved.UpperInnerFlange.Part, command.TopElevationMm),
                frame);
            var bottomInnerX = ProjectX(
                InterpolateCenterLineAtElevation(resolved.LowerInnerFlange.Part, bottomElevation),
                frame);
            var outerSamples = BuildOuterFlangeSamples(
                resolved.OuterFlange.Part,
                frame,
                bottomElevation,
                command.TopElevationMm);

            FachwerkNodeSpec spec;
            try
            {
                spec = FachwerkNodeGeometry.Build(new FachwerkNodeInput
                {
                    NodeId = command.NodeId,
                    TopElevationMm = command.TopElevationMm,
                    HeightMm = command.HeightMm,
                    Frame = frame,
                    TopInnerFlangeCenterXmm = topInnerX,
                    BottomInnerFlangeCenterXmm = bottomInnerX,
                    OuterFlangeSamples = outerSamples,
                    ProjectionFromBottomInnerFlangeMm = command.ProjectionFromBottomInnerFlangeMm,
                    Material = command.Material,
                    ClassName = command.ClassName,
                });
            }
            catch (ArgumentException ex)
            {
                throw Conflict("FACHWERK_NODE_GEOMETRY_INVALID", ex.Message);
            }

            var existing = ReadExistingNode(model, command.NodeId, spec, resolved);
            existing.ValidateTopology(resolved);
            return new FachwerkNodeTeklaChange(
                model,
                column,
                command,
                spec,
                resolved,
                existing);
        }

        public void Apply()
        {
            if (_existing.IsComplete && _existing.Matches(_spec, _columnContext))
            {
                _resultPart = _existing.Parts["main-web"];
                _noOp = true;
                return;
            }

            try
            {
                if (_existing.IsComplete)
                    UpdateExisting();
                else
                    CreateNew();
            }
            catch
            {
                RestoreCore();
                throw;
            }
        }

        public void Restore()
        {
            if (_noOp) return;
            RestoreCore();
        }

        private void CreateNew()
        {
            var createdParts = new Dictionary<string, Part>(StringComparer.Ordinal);
            foreach (var partSpec in _spec.ContourParts)
            {
                var plate = CreateContourPlate(partSpec);
                createdParts.Add(partSpec.Role, plate);
                _created.Add(plate);
            }
            foreach (var partSpec in _spec.BeamParts)
            {
                var beam = CreateBeam(partSpec);
                createdParts.Add(partSpec.Role, beam);
                _created.Add(beam);
            }

            var mainWeb = createdParts["main-web"];
            var holes = CreateHoleGroup(mainWeb);
            _created.Add(holes);
            foreach (var fitSpec in _spec.ColumnFits)
            {
                var fitting = CreateColumnFit(fitSpec);
                _created.Add(fitting);
            }
            AddToColumnAssembly(createdParts.Values);
            _resultPart = mainWeb;
        }

        private void UpdateExisting()
        {
            foreach (var partSpec in _spec.ContourParts)
            {
                var plate = (ContourPlate)_existing.Parts[partSpec.Role];
                _partSnapshots.Add(NodePartSnapshot.Capture(plate));
                ConfigureContourPlate(plate, partSpec);
                if (!plate.Modify())
                    throw new InvalidOperationException($"ContourPlate.Modify() returned false for node role '{partSpec.Role}'.");
            }
            foreach (var partSpec in _spec.BeamParts)
            {
                var beam = (Beam)_existing.Parts[partSpec.Role];
                _partSnapshots.Add(NodePartSnapshot.Capture(beam));
                ConfigureBeam(beam, partSpec);
                if (!beam.Modify())
                    throw new InvalidOperationException($"Beam.Modify() returned false for node role '{partSpec.Role}'.");
            }

            var mainWeb = _existing.Parts["main-web"];
            _boltSnapshot = BoltArraySnapshot.Capture(_existing.Holes!);
            ConfigureHoleGroup(_existing.Holes!, mainWeb);
            if (!_existing.Holes!.Modify())
                throw new InvalidOperationException("BoltArray.Modify() returned false for Fachwerk node holes.");
            foreach (var fitSpec in _spec.ColumnFits)
            {
                var fitting = _existing.Fittings[fitSpec.Role];
                _fitSnapshots.Add(FittingSnapshot.Capture(fitting));
                ConfigureColumnFit(fitting, fitSpec);
                if (!fitting.Modify())
                    throw new InvalidOperationException($"Fitting.Modify() returned false for node fit '{fitSpec.Role}'.");
            }
            _resultPart = mainWeb;
        }

        private Fitting CreateColumnFit(FachwerkColumnFitSpec spec)
        {
            var fitting = new Fitting();
            ConfigureColumnFit(fitting, spec);
            if (!fitting.Insert())
                throw new InvalidOperationException($"Fitting.Insert() returned false for node fit '{spec.Role}'.");
            ApplyIdentity(fitting, FitObjectId(_command.NodeId, spec.Role), NodeFitTypePrefix);
            if (!fitting.Modify())
                throw new InvalidOperationException($"Fitting.Modify() could not persist identity for '{spec.Role}'.");
            return fitting;
        }

        private void ConfigureColumnFit(Fitting fitting, FachwerkColumnFitSpec spec)
        {
            var localZ = spec.ElevationMm - _spec.Frame.Origin.Z;
            var origin = _spec.Frame.ToGlobal(new FachwerkLocalPoint(0, 0, localZ));
            var axisY = spec.KeepAbove ? _spec.Frame.AxisN : _spec.Frame.AxisN * -1;
            fitting.Father = _columnContext.ResolveFitFather(spec.Role);
            fitting.Plane = new Plane
            {
                Origin = ToTeklaPoint(origin),
                AxisX = ToTeklaVector(_spec.Frame.AxisX),
                AxisY = ToTeklaVector(axisY),
            };
            ApplyIdentity(fitting, FitObjectId(_command.NodeId, spec.Role), NodeFitTypePrefix);
        }

        private ContourPlate CreateContourPlate(FachwerkContourPartSpec spec)
        {
            var plate = new ContourPlate();
            ConfigureContourPlate(plate, spec);
            if (!plate.Insert())
                throw new InvalidOperationException($"ContourPlate.Insert() returned false for node role '{spec.Role}'.");
            ApplyIdentity(plate, PartObjectId(_command.NodeId, spec.Role), NodePartTypePrefix);
            if (!plate.Modify())
                throw new InvalidOperationException($"ContourPlate.Modify() could not persist identity for '{spec.Role}'.");
            return plate;
        }

        private void ConfigureContourPlate(ContourPlate plate, FachwerkContourPartSpec spec)
        {
            var contour = new Contour();
            foreach (var localPoint in spec.Points)
            {
                var point = ToTeklaPoint(_spec.Frame.ToGlobal(localPoint));
                contour.AddContourPoint(new ContourPoint(point, new Chamfer()));
            }
            plate.Contour = contour;
            plate.Profile.ProfileString = spec.Profile;
            plate.Material.MaterialString = spec.Material;
            plate.Class = spec.ClassName;
            plate.Name = StablePartName(_command.NodeId, spec.Role, _spec);
            plate.Position.Depth = Position.DepthEnum.MIDDLE;
            ApplyIdentity(plate, PartObjectId(_command.NodeId, spec.Role), NodePartTypePrefix);
        }

        private Beam CreateBeam(FachwerkBeamPartSpec spec)
        {
            var beam = new Beam();
            ConfigureBeam(beam, spec);
            if (!beam.Insert())
                throw new InvalidOperationException($"Beam.Insert() returned false for node role '{spec.Role}'.");
            ApplyIdentity(beam, PartObjectId(_command.NodeId, spec.Role), NodePartTypePrefix);
            if (!beam.Modify())
                throw new InvalidOperationException($"Beam.Modify() could not persist identity for '{spec.Role}'.");
            return beam;
        }

        private void ConfigureBeam(Beam beam, FachwerkBeamPartSpec spec)
        {
            beam.StartPoint = ToTeklaPoint(_spec.Frame.ToGlobal(spec.Start));
            beam.EndPoint = ToTeklaPoint(_spec.Frame.ToGlobal(spec.End));
            beam.Profile.ProfileString = spec.Profile;
            beam.Material.MaterialString = spec.Material;
            beam.Class = spec.ClassName;
            beam.Name = StablePartName(_command.NodeId, spec.Role, _spec);
            beam.Position.Plane = Position.PlaneEnum.MIDDLE;
            beam.Position.PlaneOffset = 0;
            beam.Position.Depth = Position.DepthEnum.FRONT;
            beam.Position.DepthOffset = 0;
            beam.Position.Rotation = Position.RotationEnum.BACK;
            beam.Position.RotationOffset = 0;
            ApplyIdentity(beam, PartObjectId(_command.NodeId, spec.Role), NodePartTypePrefix);
        }

        private BoltArray CreateHoleGroup(Part mainWeb)
        {
            var holes = new BoltArray();
            ConfigureHoleGroup(holes, mainWeb);
            if (!holes.Insert())
                throw new InvalidOperationException("BoltArray.Insert() returned false for Fachwerk node holes.");
            ApplyIdentity(holes, HoleObjectId(_command.NodeId), NodeHoleTypePrefix);
            if (!holes.Modify())
                throw new InvalidOperationException("BoltArray.Modify() could not persist Fachwerk node identity.");
            return holes;
        }

        private void ConfigureHoleGroup(BoltArray holes, Part mainWeb)
        {
            ClearBoltDistances(holes);
            var centers = _spec.Holes
                .Select(item => _spec.Frame.ToGlobal(item.Center))
                .OrderBy(static point => point.Z)
                .ToArray();
            if (centers.Length == 0)
                throw new InvalidOperationException("The Fachwerk node has no hole rows.");

            var first = centers[0];
            var top = centers[centers.Length - 1];
            holes.PartToBeBolted = mainWeb;
            holes.PartToBoltTo = mainWeb;
            holes.FirstPosition = ToTeklaPoint(new FachwerkPoint3(first.X, first.Y, _spec.BottomElevationMm));
            holes.SecondPosition = ToTeklaPoint(new FachwerkPoint3(top.X, top.Y, _spec.TopElevationMm));
            holes.BoltSize = _spec.Holes[0].DiameterMm;
            holes.BoltStandard = "7798";
            holes.BoltType = BoltGroup.BoltTypeEnum.BOLT_TYPE_SITE;
            holes.ThreadInMaterial = BoltGroup.BoltThreadInMaterialEnum.THREAD_IN_MATERIAL_YES;
            holes.Length = 0;
            holes.CutLength = 100;
            holes.ExtraLength = 0;
            holes.Tolerance = 4;
            holes.HoleType = BoltGroup.BoltHoleTypeEnum.HOLE_TYPE_SLOTTED;
            holes.SlottedHoleX = 0;
            holes.SlottedHoleY = 0;
            holes.RotateSlots = BoltGroup.BoltRotateSlotsEnum.ROTATE_SLOTS_PARALLEL;
            holes.Washer1 = false;
            holes.Washer2 = false;
            holes.Washer3 = false;
            holes.Nut1 = false;
            holes.Nut2 = false;
            holes.Bolt = false;
            holes.Hole1 = false;
            holes.Hole2 = false;
            holes.Hole3 = false;
            holes.Hole4 = false;
            holes.Hole5 = false;
            holes.ConnectAssemblies = false;
            holes.Position.Plane = Position.PlaneEnum.MIDDLE;
            holes.Position.Depth = Position.DepthEnum.MIDDLE;
            holes.Position.Rotation = Position.RotationEnum.FRONT;

            var baselineZ = _spec.BottomElevationMm;
            foreach (var center in centers)
                holes.AddBoltDistX(center.Z - baselineZ);
            holes.AddBoltDistY(0);
            ApplyIdentity(holes, HoleObjectId(_command.NodeId), NodeHoleTypePrefix);
        }

        private void AddToColumnAssembly(IEnumerable<Part> parts)
        {
            var assembly = _columnContext.OuterFlange.Part.GetAssembly();
            if (assembly is null)
                throw new InvalidOperationException("Tekla did not return the Fachwerk column assembly.");
            foreach (var part in parts)
            {
                if (!assembly.Add(part))
                    throw new InvalidOperationException($"Assembly.Add() returned false for node part '{ReadExternalId(part)}'.");
            }
            if (!assembly.Modify())
                throw new InvalidOperationException("Assembly.Modify() returned false after adding Fachwerk node parts.");
        }

        private void ApplyIdentity(ModelObject modelObject, string externalId, string typePrefix)
        {
            modelObject.SetUserProperty(ExternalIdUda, externalId);
            modelObject.SetUserProperty(ComponentTypeUda, typePrefix + _spec.Signature);
            modelObject.SetUserProperty(SchemaVersionUda, 1);
        }

        private void RestoreCore()
        {
            for (var index = _created.Count - 1; index >= 0; index--)
            {
                try { _created[index].Delete(); }
                catch { }
            }
            _created.Clear();

            foreach (var snapshot in _fitSnapshots.AsEnumerable().Reverse())
            {
                try { snapshot.Restore(); }
                catch { }
            }
            _fitSnapshots.Clear();

            foreach (var snapshot in _partSnapshots.AsEnumerable().Reverse())
            {
                try { snapshot.Restore(); }
                catch { }
            }
            _partSnapshots.Clear();
            if (_boltSnapshot is not null && _existing.Holes is not null)
            {
                try { _boltSnapshot.Restore(_existing.Holes); }
                catch { }
                _boltSnapshot = null;
            }
            _resultPart = null;
        }

        private static ResolvedColumnContext ResolveColumnContext(
            IReadOnlyList<ColumnPartInfo> parts,
            double topElevation,
            double height)
        {
            var bottomElevation = topElevation - height;
            var outer = RequireSpanningPart(parts, "outer-flange", bottomElevation, topElevation);
            var upperInnerFlange = RequireAdjacentPart(parts, "inner-flange", topElevation, true);
            var lowerInnerFlange = RequireAdjacentPart(parts, "inner-flange", topElevation, false);
            var upperInnerWeb = RequireAdjacentPart(parts, "inner-web", topElevation, true);
            var lowerInnerWeb = RequireAdjacentPart(parts, "inner-web", topElevation, false);
            var upperOuterWeb = RequireAdjacentPart(parts, "outer-web", topElevation, true);
            var lowerOuterWeb = RequireAdjacentPart(parts, "outer-web", topElevation, false);

            try
            {
                _ = new FachwerkColumnContext(
                    parts[0].OwnerId,
                    parts[0].Mark,
                    outer.ToReference(),
                    upperInnerFlange.ToReference(),
                    lowerInnerFlange.ToReference(),
                    upperInnerWeb.ToReference(),
                    lowerInnerWeb.ToReference(),
                    upperOuterWeb.ToReference(),
                    lowerOuterWeb.ToReference());
            }
            catch (ArgumentException ex)
            {
                throw Conflict("FACHWERK_NODE_COLUMN_TOPOLOGY_INVALID", ex.Message);
            }

            return new ResolvedColumnContext(
                outer,
                upperInnerFlange,
                lowerInnerFlange,
                upperInnerWeb,
                lowerInnerWeb,
                upperOuterWeb,
                lowerOuterWeb);
        }

        private static FachwerkNodeFrame ResolveFrame(
            ResolvedColumnContext context,
            double bottomElevation)
        {
            var lowerInner = InterpolateCenterLineAtElevation(context.LowerInnerWeb.Part, bottomElevation);
            var lowerOuter = InterpolateCenterLineAtElevation(context.LowerOuterWeb.Part, bottomElevation);
            var nx = lowerOuter.X - lowerInner.X;
            var ny = lowerOuter.Y - lowerInner.Y;
            var normalLength = Math.Sqrt(nx * nx + ny * ny);
            if (normalLength < 100)
            {
                throw Conflict(
                    "FACHWERK_NODE_COLUMN_FRAME_INVALID",
                    "The resolved Fachwerk web pair does not define a reliable facade normal.");
            }
            nx /= normalLength;
            ny /= normalLength;
            var axisX = new FachwerkVector3(ny, -nx, 0);
            var originPoint = InterpolateCenterLineAtElevation(context.OuterFlange.Part, bottomElevation);
            return new FachwerkNodeFrame(
                new FachwerkPoint3(originPoint.X, originPoint.Y, originPoint.Z),
                axisX);
        }

        private static IReadOnlyList<FachwerkSectionSample> BuildOuterFlangeSamples(
            Part outerFlange,
            FachwerkNodeFrame frame,
            double bottomElevation,
            double topElevation)
        {
            var result = new List<FachwerkSectionSample>
            {
                SampleOuterAt(outerFlange, frame, bottomElevation),
            };
            result.AddRange(ReadCenterLine(outerFlange)
                .Where(point => point.Z > bottomElevation + 0.1 && point.Z < topElevation - 0.1)
                .GroupBy(point => Math.Round(point.Z, 3))
                .Select(group => group.First())
                .OrderBy(point => point.Z)
                .Select(point => new FachwerkSectionSample(point.Z, ProjectX(point, frame))));
            result.Add(SampleOuterAt(outerFlange, frame, topElevation));
            return result;
        }

        private static FachwerkSectionSample SampleOuterAt(
            Part part,
            FachwerkNodeFrame frame,
            double elevation)
        {
            var point = InterpolateCenterLineAtElevation(part, elevation);
            return new FachwerkSectionSample(elevation, ProjectX(point, frame));
        }

        private static IReadOnlyList<ColumnPartInfo> ReadColumnParts(Model model, int ownerId)
        {
            var owner = ownerId.ToString(CultureInfo.InvariantCulture);
            var result = new List<ColumnPartInfo>();
            var enumerator = model.GetModelObjectSelector().GetAllObjects();
            while (enumerator.MoveNext())
            {
                if (enumerator.Current is not Part part) continue;
                var candidateOwner = string.Empty;
                try { part.GetUserProperty("FK_OWNER", ref candidateOwner); }
                catch { continue; }
                if (!string.Equals(candidateOwner, owner, StringComparison.Ordinal)) continue;

                var role = string.Empty;
                var mark = string.Empty;
                var geometry = string.Empty;
                var breakIndex = 0;
                part.GetUserProperty("FK_ROLE", ref role);
                part.GetUserProperty("FK_MARK", ref mark);
                part.GetUserProperty("FK_GEOMETRY", ref geometry);
                part.GetUserProperty("FK_BREAK", ref breakIndex);
                var solid = part.GetSolid();
                var centerLine = part.GetCenterLine(false).OfType<Point>().ToArray();
                var pathMinZ = centerLine.Length == 0 ? solid.MinimumPoint.Z : centerLine.Min(static point => point.Z);
                var pathMaxZ = centerLine.Length == 0 ? solid.MaximumPoint.Z : centerLine.Max(static point => point.Z);
                result.Add(new ColumnPartInfo(
                    part,
                    owner,
                    mark,
                    role.Trim(),
                    geometry.Trim(),
                    breakIndex,
                    solid.MinimumPoint.Z,
                    solid.MaximumPoint.Z,
                    pathMinZ,
                    pathMaxZ));
            }
            if (result.Count == 0)
            {
                throw Conflict(
                    "FACHWERK_NODE_COLUMN_PARTS_NOT_FOUND",
                    $"No Fachwerk parts with FK_OWNER='{owner}' were found.");
            }
            return result;
        }

        private static ColumnPartInfo RequireSpanningPart(
            IReadOnlyList<ColumnPartInfo> parts,
            string role,
            double bottom,
            double top)
        {
            const double tolerance = 5;
            var matches = parts
                .Where(item => string.Equals(item.Role, role, StringComparison.OrdinalIgnoreCase))
                .Where(item => item.PathMinZ <= bottom + tolerance && item.PathMaxZ >= top - tolerance)
                .ToArray();
            if (matches.Length != 1)
            {
                throw Conflict(
                    "FACHWERK_NODE_COLUMN_ROLE_AMBIGUOUS",
                    $"Role '{role}' must resolve to one part spanning elevations {bottom:0.###}..{top:0.###}; found {matches.Length}.");
            }
            return matches[0];
        }

        private static ColumnPartInfo RequireAdjacentPart(
            IReadOnlyList<ColumnPartInfo> parts,
            string role,
            double elevation,
            bool upper)
        {
            const double maximumGap = 80;
            var candidates = parts
                .Where(item => string.Equals(item.Role, role, StringComparison.OrdinalIgnoreCase))
                .Select(item => new
                {
                    Item = item,
                    Gap = upper ? item.PathMinZ - elevation : elevation - item.PathMaxZ,
                })
                .Where(item => item.Gap >= -5 && item.Gap <= maximumGap)
                .OrderBy(item => Math.Abs(item.Gap))
                .ToArray();
            if (candidates.Length == 0)
            {
                throw Conflict(
                    "FACHWERK_NODE_COLUMN_ROLE_MISSING",
                    $"Role '{role}' has no {(upper ? "upper" : "lower")} part adjacent to elevation {elevation:0.###}.");
            }
            if (candidates.Length > 1 && Math.Abs(candidates[0].Gap - candidates[1].Gap) < 0.1)
            {
                throw Conflict(
                    "FACHWERK_NODE_COLUMN_ROLE_AMBIGUOUS",
                    $"Role '{role}' has ambiguous {(upper ? "upper" : "lower")} parts at elevation {elevation:0.###}.");
            }
            return candidates[0].Item;
        }

        private static ExistingNodeState ReadExistingNode(
            Model model,
            string nodeId,
            FachwerkNodeSpec spec,
            ResolvedColumnContext columnContext)
        {
            var prefix = nodeId.Trim() + "/";
            var parts = new Dictionary<string, Part>(StringComparer.Ordinal);
            var partIdentities = new Dictionary<string, StablePartIdentity>(StringComparer.Ordinal);
            var fittings = new Dictionary<string, Fitting>(StringComparer.Ordinal);
            BoltArray? holes = null;
            var allFittings = new List<Fitting>();
            var allBolts = new List<BoltArray>();
            var enumerator = model.GetModelObjectSelector().GetAllObjects();
            while (enumerator.MoveNext())
            {
                var current = enumerator.Current as ModelObject;
                if (current is null) continue;
                var externalId = ReadExternalId(current);
                if (current is Part part)
                {
                    var role = externalId.StartsWith(prefix, StringComparison.Ordinal)
                        ? ParseRole(nodeId, externalId, "part")
                        : null;
                    StablePartIdentity? stableIdentity = null;
                    if (role is null && TryParseStablePartName(part.Name, nodeId, out var parsedIdentity))
                    {
                        role = parsedIdentity.Role;
                        stableIdentity = parsedIdentity;
                    }
                    if (role is null) continue;
                    if (parts.ContainsKey(role))
                        throw Conflict("FACHWERK_NODE_STATE_INVALID", $"Duplicate existing node role '{role}'.");
                    parts.Add(role, part);
                    if (stableIdentity is not null)
                        partIdentities.Add(role, stableIdentity);
                }
                else if (current is Fitting fitting)
                {
                    allFittings.Add(fitting);
                    if (!externalId.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var role = ParseRole(nodeId, externalId, "fit");
                    if (role is null) continue;
                    if (fittings.ContainsKey(role))
                        throw Conflict("FACHWERK_NODE_STATE_INVALID", $"Duplicate existing node fit '{role}'.");
                    fittings.Add(role, fitting);
                }
                else if (current is BoltArray bolt)
                {
                    allBolts.Add(bolt);
                    if (string.Equals(externalId, HoleObjectId(nodeId), StringComparison.Ordinal))
                    {
                        if (holes is not null)
                            throw Conflict("FACHWERK_NODE_STATE_INVALID", "Duplicate existing Fachwerk node hole groups.");
                        holes = bolt;
                    }
                }
            }

            if (parts.TryGetValue("main-web", out var mainWeb) && holes is null)
            {
                var candidates = allBolts
                    .Where(bolt =>
                        bolt.PartToBeBolted is Part first && SameObject(first, mainWeb) &&
                        bolt.PartToBoltTo is Part second && SameObject(second, mainWeb))
                    .ToArray();
                if (candidates.Length > 1)
                    throw Conflict("FACHWERK_NODE_STATE_INVALID", "Duplicate existing Fachwerk node hole groups.");
                holes = candidates.SingleOrDefault();
            }

            if (parts.Count > 0)
            {
                var priorBounds = ResolvePersistedNodeBounds(partIdentities, spec);
                foreach (var fitSpec in spec.ColumnFits)
                {
                    if (fittings.ContainsKey(fitSpec.Role)) continue;
                    var priorElevation = fitSpec.KeepAbove ? priorBounds.TopElevationMm : priorBounds.BottomElevationMm;
                    var father = columnContext.ResolveFitFather(fitSpec.Role);
                    var candidates = allFittings
                        .Where(fitting => MatchesFitting(fitting, father, priorElevation, fitSpec.KeepAbove))
                        .ToArray();
                    if (candidates.Length > 1)
                    {
                        throw Conflict(
                            "FACHWERK_NODE_STATE_INVALID",
                            $"Duplicate existing Fachwerk node fits for role '{fitSpec.Role}'.");
                    }
                    if (candidates.Length == 1)
                        fittings.Add(fitSpec.Role, candidates[0]);
                }
            }

            return new ExistingNodeState(nodeId, parts, partIdentities, fittings, holes);
        }

        private static string? ParseRole(string nodeId, string externalId, string group)
        {
            var prefix = nodeId.Trim() + "/" + group + "/";
            return externalId.StartsWith(prefix, StringComparison.Ordinal)
                ? externalId.Substring(prefix.Length)
                : null;
        }

        private static string PartObjectId(string nodeId, string role) =>
            nodeId.Trim() + "/part/" + role;

        private static string FitObjectId(string nodeId, string role) =>
            nodeId.Trim() + "/fit/" + role;

        private static string HoleObjectId(string nodeId) => nodeId.Trim() + "/holes";

        private static string StablePartName(string nodeId, string role, FachwerkNodeSpec spec)
        {
            if (!PartRoleCodes.TryGetValue(role, out var roleCode))
                throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown Fachwerk node part role.");
            return string.Join(
                "_",
                StablePartNamePrefix,
                StableNodeKey(nodeId),
                roleCode,
                SignatureToken(spec.Signature),
                ElevationToken(spec.TopElevationMm),
                ElevationToken(spec.BottomElevationMm));
        }

        private static bool TryParseStablePartName(
            string? name,
            string nodeId,
            out StablePartIdentity identity)
        {
            identity = null!;
            var tokens = (name ?? string.Empty).Split('_');
            if (tokens.Length != 6 ||
                !string.Equals(tokens[0], StablePartNamePrefix, StringComparison.Ordinal) ||
                !string.Equals(tokens[1], StableNodeKey(nodeId), StringComparison.Ordinal) ||
                !PartRolesByCode.TryGetValue(tokens[2], out var role) ||
                string.IsNullOrWhiteSpace(tokens[3]) ||
                !TryParseElevationToken(tokens[4], out var topElevation) ||
                !TryParseElevationToken(tokens[5], out var bottomElevation))
            {
                return false;
            }
            identity = new StablePartIdentity(role, tokens[3], topElevation, bottomElevation);
            return true;
        }

        private static string StableNodeKey(string nodeId)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(nodeId.Trim()));
            return string.Concat(bytes.Take(6).Select(static value =>
                value.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private static string SignatureToken(string signature) =>
            signature.Length <= 16 ? signature : signature.Substring(0, 16);

        private static string ElevationToken(double elevation) =>
            elevation.ToString("0.###", CultureInfo.InvariantCulture)
                .Replace("-", "m")
                .Replace(".", "p");

        private static bool TryParseElevationToken(string token, out double elevation) =>
            double.TryParse(
                token.Replace("m", "-").Replace("p", "."),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out elevation);

        private static PersistedNodeBounds ResolvePersistedNodeBounds(
            IReadOnlyDictionary<string, StablePartIdentity> identities,
            FachwerkNodeSpec fallback)
        {
            if (identities.Count == 0)
                return new PersistedNodeBounds(fallback.TopElevationMm, fallback.BottomElevationMm);
            var first = identities.Values.First();
            if (identities.Values.Any(item =>
                    Math.Abs(item.TopElevationMm - first.TopElevationMm) > 0.1 ||
                    Math.Abs(item.BottomElevationMm - first.BottomElevationMm) > 0.1))
            {
                throw Conflict(
                    "FACHWERK_NODE_STATE_INVALID",
                    "Existing Fachwerk node parts contain inconsistent persisted elevations.");
            }
            return new PersistedNodeBounds(first.TopElevationMm, first.BottomElevationMm);
        }

        private static bool MatchesFitting(
            Fitting fitting,
            Part expectedFather,
            double elevation,
            bool keepAbove)
        {
            if (fitting.Father is not Part father || !SameObject(father, expectedFather))
                return false;
            var plane = fitting.Plane;
            if (plane?.Origin is null || plane.AxisX is null || plane.AxisY is null ||
                Math.Abs(plane.Origin.Z - elevation) > 0.1)
            {
                return false;
            }
            var normalZ = plane.AxisX.X * plane.AxisY.Y - plane.AxisX.Y * plane.AxisY.X;
            return keepAbove ? normalZ > 1e-6 : normalZ < -1e-6;
        }

        private static Point InterpolateCenterLineAtElevation(Part part, double elevation)
        {
            var points = ReadCenterLine(part);
            for (var index = 1; index < points.Count; index++)
            {
                var first = points[index - 1];
                var second = points[index];
                var min = Math.Min(first.Z, second.Z) - 0.1;
                var max = Math.Max(first.Z, second.Z) + 0.1;
                if (elevation < min || elevation > max || Math.Abs(second.Z - first.Z) < 1e-9) continue;
                var factor = (elevation - first.Z) / (second.Z - first.Z);
                return new Point(
                    first.X + (second.X - first.X) * factor,
                    first.Y + (second.Y - first.Y) * factor,
                    elevation);
            }
            var nearest = points.OrderBy(point => Math.Abs(point.Z - elevation)).First();
            if (Math.Abs(nearest.Z - elevation) > 80)
            {
                throw Conflict(
                    "FACHWERK_NODE_COLUMN_PATH_MISSING",
                    $"Part '{ReadGuid(part)}' does not cover elevation {elevation:0.###}.");
            }
            return new Point(nearest.X, nearest.Y, elevation);
        }

        private static IReadOnlyList<Point> ReadCenterLine(Part part)
        {
            var points = part.GetCenterLine(false)
                .OfType<Point>()
                .Select(point => new Point(point.X, point.Y, point.Z))
                .ToArray();
            if (points.Length < 2)
                throw Conflict("FACHWERK_NODE_COLUMN_PATH_MISSING", $"Part '{ReadGuid(part)}' has no usable center line.");
            return points;
        }

        private static double ProjectX(Point point, FachwerkNodeFrame frame)
        {
            var delta = new FachwerkVector3(
                point.X - frame.Origin.X,
                point.Y - frame.Origin.Y,
                point.Z - frame.Origin.Z);
            return FachwerkVector3.Dot(delta, frame.AxisX);
        }

        private static Point ToTeklaPoint(FachwerkPoint3 point) => new(point.X, point.Y, point.Z);

        private static Vector ToTeklaVector(FachwerkVector3 vector) => new(vector.X, vector.Y, vector.Z);

        private static string ReadExternalId(ModelObject value)
        {
            var result = string.Empty;
            try { value.GetUserProperty(ExternalIdUda, ref result); }
            catch { }
            return result ?? string.Empty;
        }

        private static string ReadComponentType(ModelObject value)
        {
            var result = string.Empty;
            try { value.GetUserProperty(ComponentTypeUda, ref result); }
            catch { }
            return result ?? string.Empty;
        }

        private static string? ReadGuid(ModelObject? value)
        {
            try
            {
                var guid = value?.Identifier?.GUID ?? Guid.Empty;
                return guid == Guid.Empty ? null : guid.ToString();
            }
            catch { return null; }
        }

        private static void ClearBoltDistances(BoltArray bolt)
        {
            for (var index = bolt.GetBoltDistXCount() - 1; index >= 0; index--)
                bolt.RemoveBoltDistX(index);
            for (var index = bolt.GetBoltDistYCount() - 1; index >= 0; index--)
                bolt.RemoveBoltDistY(index);
        }

        private static bool SameObject(ModelObject first, ModelObject second)
        {
            var firstId = first.Identifier?.ID ?? 0;
            var secondId = second.Identifier?.ID ?? 0;
            if (firstId != 0 && secondId != 0) return firstId == secondId;
            return string.Equals(ReadGuid(first), ReadGuid(second), StringComparison.OrdinalIgnoreCase);
        }

        private static FachwerkNodeAdapterException Conflict(string code, string message) => new(code, message);

        private sealed record ColumnPartInfo(
            Part Part,
            string OwnerId,
            string Mark,
            string Role,
            string Geometry,
            int BreakIndex,
            double MinZ,
            double MaxZ,
            double PathMinZ,
            double PathMaxZ)
        {
            public FachwerkColumnPartRef ToReference() => new(
                ReadGuid(Part) ?? Part.Identifier.ID.ToString(CultureInfo.InvariantCulture),
                Role,
                Geometry,
                BreakIndex);
        }

        private sealed record StablePartIdentity(
            string Role,
            string Signature,
            double TopElevationMm,
            double BottomElevationMm);

        private sealed record PersistedNodeBounds(double TopElevationMm, double BottomElevationMm);

        private sealed record ResolvedColumnContext(
            ColumnPartInfo OuterFlange,
            ColumnPartInfo UpperInnerFlange,
            ColumnPartInfo LowerInnerFlange,
            ColumnPartInfo UpperInnerWeb,
            ColumnPartInfo LowerInnerWeb,
            ColumnPartInfo UpperOuterWeb,
            ColumnPartInfo LowerOuterWeb)
        {
            public Part ResolveFitFather(string role) => role switch
            {
                "upper-inner-flange" => UpperInnerFlange.Part,
                "lower-inner-flange" => LowerInnerFlange.Part,
                "upper-inner-web" => UpperInnerWeb.Part,
                "lower-inner-web" => LowerInnerWeb.Part,
                "upper-outer-web" => UpperOuterWeb.Part,
                "lower-outer-web" => LowerOuterWeb.Part,
                _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown Fachwerk column fit role."),
            };
        }

        private sealed class ExistingNodeState
        {
            public ExistingNodeState(
                string nodeId,
                Dictionary<string, Part> parts,
                Dictionary<string, StablePartIdentity> partIdentities,
                Dictionary<string, Fitting> fittings,
                BoltArray? holes)
            {
                NodeId = nodeId;
                Parts = parts;
                PartIdentities = partIdentities;
                Fittings = fittings;
                Holes = holes;
            }

            public string NodeId { get; }
            public Dictionary<string, Part> Parts { get; }
            public Dictionary<string, StablePartIdentity> PartIdentities { get; }
            public Dictionary<string, Fitting> Fittings { get; }
            public BoltArray? Holes { get; }
            public bool IsComplete =>
                Parts.Count == ContourRoles.Length + BeamRoles.Length &&
                Fittings.Count == FitRoles.Length &&
                Holes is not null;

            public bool Matches(FachwerkNodeSpec spec, ResolvedColumnContext columnContext)
            {
                if (!IsComplete) return false;
                foreach (var role in spec.Roles)
                {
                    if (!string.Equals(Parts[role].Name, StablePartName(NodeId, role, spec), StringComparison.Ordinal))
                        return false;
                }
                var mainWeb = Parts["main-web"];
                if (Holes!.PartToBeBolted is not Part first || !SameObject(first, mainWeb) ||
                    Holes.PartToBoltTo is not Part second || !SameObject(second, mainWeb))
                {
                    return false;
                }
                var centers = spec.Holes.OrderBy(static hole => hole.Center.Z).ToArray();
                if (Holes.GetBoltDistXCount() != centers.Length || Holes.GetBoltDistYCount() != 1 ||
                    Math.Abs(Holes.GetBoltDistY(0)) > 0.1 || Math.Abs(Holes.BoltSize - centers[0].DiameterMm) > 0.1)
                {
                    return false;
                }
                for (var index = 0; index < centers.Length; index++)
                {
                    var expected = centers[index].Center.Z + spec.Frame.Origin.Z - spec.BottomElevationMm;
                    if (Math.Abs(Holes.GetBoltDistX(index) - expected) > 0.1)
                        return false;
                }
                foreach (var fitSpec in spec.ColumnFits)
                {
                    if (!MatchesFitting(
                            Fittings[fitSpec.Role],
                            columnContext.ResolveFitFather(fitSpec.Role),
                            fitSpec.ElevationMm,
                            fitSpec.KeepAbove))
                    {
                        return false;
                    }
                }
                return true;
            }

            public void ValidateTopology(ResolvedColumnContext columnContext)
            {
                if (Parts.Count == 0 && Fittings.Count == 0 && Holes is null) return;
                var expectedParts = new HashSet<string>(ContourRoles.Concat(BeamRoles), StringComparer.Ordinal);
                var expectedFits = new HashSet<string>(FitRoles, StringComparer.Ordinal);
                if (!IsComplete || !expectedParts.SetEquals(Parts.Keys) || !expectedFits.SetEquals(Fittings.Keys))
                {
                    throw Conflict(
                        "FACHWERK_NODE_STATE_INVALID",
                        $"Existing node '{NodeId}' is incomplete. Expected nine part roles, six column fits and one hole group.");
                }
                foreach (var role in ContourRoles)
                {
                    if (Parts[role] is not ContourPlate)
                        throw Conflict("FACHWERK_NODE_STATE_INVALID", $"Existing role '{role}' is not a ContourPlate.");
                }
                foreach (var role in BeamRoles)
                {
                    if (Parts[role] is not Beam || Parts[role] is PolyBeam)
                        throw Conflict("FACHWERK_NODE_STATE_INVALID", $"Existing role '{role}' is not a straight Beam.");
                }
                foreach (var role in FitRoles)
                {
                    var fitting = Fittings[role];
                    if (fitting.Father is not Part father || !SameObject(father, columnContext.ResolveFitFather(role)))
                    {
                        throw Conflict(
                            "FACHWERK_NODE_STATE_INVALID",
                            $"Existing node fit '{role}' is attached to the wrong Fachwerk column part.");
                    }
                }
            }
        }

        private abstract class NodePartSnapshot
        {
            protected NodePartSnapshot(Part part)
            {
                Part = part;
                Profile = part.Profile.ProfileString;
                Material = part.Material.MaterialString;
                ClassName = part.Class;
                Name = part.Name;
                ExternalId = ReadExternalId(part);
                ComponentType = ReadComponentType(part);
                var schemaVersion = 0;
                try { part.GetUserProperty(SchemaVersionUda, ref schemaVersion); }
                catch { }
                SchemaVersion = schemaVersion;
            }

            protected Part Part { get; }
            protected string Profile { get; }
            protected string Material { get; }
            protected string ClassName { get; }
            protected string Name { get; }
            protected string ExternalId { get; }
            protected string ComponentType { get; }
            protected int SchemaVersion { get; }

            public static NodePartSnapshot Capture(Part part) => part switch
            {
                ContourPlate plate => new ContourPlateSnapshot(plate),
                Beam beam => new BeamSnapshot(beam),
                _ => throw new InvalidOperationException("Unsupported Fachwerk node part type."),
            };

            public abstract void Restore();

            protected void RestoreCommon()
            {
                Part.Profile.ProfileString = Profile;
                Part.Material.MaterialString = Material;
                Part.Class = ClassName;
                Part.Name = Name;
                Part.SetUserProperty(ExternalIdUda, ExternalId);
                Part.SetUserProperty(ComponentTypeUda, ComponentType);
                Part.SetUserProperty(SchemaVersionUda, SchemaVersion);
            }
        }

        private sealed class ContourPlateSnapshot : NodePartSnapshot
        {
            private readonly Contour _contour;
            private readonly Position.DepthEnum _depth;
            private readonly double _depthOffset;

            public ContourPlateSnapshot(ContourPlate plate) : base(plate)
            {
                _contour = CloneContour(plate.Contour);
                _depth = plate.Position.Depth;
                _depthOffset = plate.Position.DepthOffset;
            }

            public override void Restore()
            {
                var plate = (ContourPlate)Part;
                RestoreCommon();
                plate.Contour = CloneContour(_contour);
                plate.Position.Depth = _depth;
                plate.Position.DepthOffset = _depthOffset;
                plate.Modify();
            }

            private static Contour CloneContour(Contour source)
            {
                var contour = new Contour();
                foreach (var point in source.ContourPoints.OfType<ContourPoint>())
                {
                    contour.AddContourPoint(new ContourPoint(
                        new Point(point.X, point.Y, point.Z),
                        new Chamfer
                        {
                            Type = point.Chamfer.Type,
                            X = point.Chamfer.X,
                            Y = point.Chamfer.Y,
                            DZ1 = point.Chamfer.DZ1,
                            DZ2 = point.Chamfer.DZ2,
                        }));
                }
                return contour;
            }
        }

        private sealed class BeamSnapshot : NodePartSnapshot
        {
            private readonly Point _start;
            private readonly Point _end;
            private readonly Position.PlaneEnum _plane;
            private readonly double _planeOffset;
            private readonly Position.DepthEnum _depth;
            private readonly double _depthOffset;
            private readonly Position.RotationEnum _rotation;
            private readonly double _rotationOffset;

            public BeamSnapshot(Beam beam) : base(beam)
            {
                _start = new Point(beam.StartPoint.X, beam.StartPoint.Y, beam.StartPoint.Z);
                _end = new Point(beam.EndPoint.X, beam.EndPoint.Y, beam.EndPoint.Z);
                _plane = beam.Position.Plane;
                _planeOffset = beam.Position.PlaneOffset;
                _depth = beam.Position.Depth;
                _depthOffset = beam.Position.DepthOffset;
                _rotation = beam.Position.Rotation;
                _rotationOffset = beam.Position.RotationOffset;
            }

            public override void Restore()
            {
                var beam = (Beam)Part;
                RestoreCommon();
                beam.StartPoint = new Point(_start.X, _start.Y, _start.Z);
                beam.EndPoint = new Point(_end.X, _end.Y, _end.Z);
                beam.Position.Plane = _plane;
                beam.Position.PlaneOffset = _planeOffset;
                beam.Position.Depth = _depth;
                beam.Position.DepthOffset = _depthOffset;
                beam.Position.Rotation = _rotation;
                beam.Position.RotationOffset = _rotationOffset;
                beam.Modify();
            }
        }

        private sealed class FittingSnapshot
        {
            private readonly Fitting _fitting;
            private readonly Part _father;
            private readonly Plane _plane;
            private readonly string _externalId;
            private readonly string _componentType;
            private readonly int _schemaVersion;

            private FittingSnapshot(Fitting fitting)
            {
                _fitting = fitting;
                _father = fitting.Father as Part ??
                    throw new InvalidOperationException("The existing Fachwerk fitting has no Part father.");
                _plane = ClonePlane(fitting.Plane);
                _externalId = ReadExternalId(fitting);
                _componentType = ReadComponentType(fitting);
                var schema = 0;
                try { fitting.GetUserProperty(SchemaVersionUda, ref schema); }
                catch { }
                _schemaVersion = schema;
            }

            public static FittingSnapshot Capture(Fitting fitting) => new(fitting);

            public void Restore()
            {
                _fitting.Father = _father;
                _fitting.Plane = ClonePlane(_plane);
                _fitting.SetUserProperty(ExternalIdUda, _externalId);
                _fitting.SetUserProperty(ComponentTypeUda, _componentType);
                _fitting.SetUserProperty(SchemaVersionUda, _schemaVersion);
                _fitting.Modify();
            }

            private static Plane ClonePlane(Plane source) => new()
            {
                Origin = new Point(source.Origin.X, source.Origin.Y, source.Origin.Z),
                AxisX = new Vector(source.AxisX.X, source.AxisX.Y, source.AxisX.Z),
                AxisY = new Vector(source.AxisY.X, source.AxisY.Y, source.AxisY.Z),
            };
        }

        private sealed class BoltArraySnapshot
        {
            private readonly Point _first;
            private readonly Point _second;
            private readonly double[] _xDistances;
            private readonly double[] _yDistances;
            private readonly string _externalId;
            private readonly string _componentType;
            private readonly int _schemaVersion;

            private BoltArraySnapshot(BoltArray bolt)
            {
                _first = new Point(bolt.FirstPosition.X, bolt.FirstPosition.Y, bolt.FirstPosition.Z);
                _second = new Point(bolt.SecondPosition.X, bolt.SecondPosition.Y, bolt.SecondPosition.Z);
                _xDistances = Enumerable.Range(0, bolt.GetBoltDistXCount()).Select(bolt.GetBoltDistX).ToArray();
                _yDistances = Enumerable.Range(0, bolt.GetBoltDistYCount()).Select(bolt.GetBoltDistY).ToArray();
                _externalId = ReadExternalId(bolt);
                _componentType = ReadComponentType(bolt);
                var schema = 0;
                try { bolt.GetUserProperty(SchemaVersionUda, ref schema); }
                catch { }
                _schemaVersion = schema;
            }

            public static BoltArraySnapshot Capture(BoltArray bolt) => new(bolt);

            public void Restore(BoltArray bolt)
            {
                ClearBoltDistances(bolt);
                bolt.FirstPosition = new Point(_first.X, _first.Y, _first.Z);
                bolt.SecondPosition = new Point(_second.X, _second.Y, _second.Z);
                foreach (var value in _xDistances) bolt.AddBoltDistX(value);
                foreach (var value in _yDistances) bolt.AddBoltDistY(value);
                bolt.SetUserProperty(ExternalIdUda, _externalId);
                bolt.SetUserProperty(ComponentTypeUda, _componentType);
                bolt.SetUserProperty(SchemaVersionUda, _schemaVersion);
                bolt.Modify();
            }
        }
    }
}
