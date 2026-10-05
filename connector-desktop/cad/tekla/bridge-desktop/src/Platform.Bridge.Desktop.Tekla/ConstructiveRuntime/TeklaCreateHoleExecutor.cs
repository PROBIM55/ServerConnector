#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Platform.Bridge.Desktop.Tekla.ComponentRuntime;
using Platform.Contracts.TeklaPlan;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace Platform.Bridge.Desktop.Tekla.ConstructiveRuntime
{
    public sealed class TeklaCreateHoleExecutor : ITeklaNativeCommandExecutor
    {
        private const string ExecutorId = "tekla-native.create-hole.v1";
        private const string RoundComponentType = "ConstructiveRoundHoleV1";
        private const string SlottedComponentType = "ConstructiveSlottedHoleV1";

        public TeklaPlanExecutorRegistration Registration { get; } = new(
            ExecutorId,
            "create-hole",
            new[] { "roundHole", "slottedHole", "udaStamp" },
            applyReady: true);

        public ITeklaPreparedMutation<TeklaNativeCommandReadback> Prepare(
            Model model,
            TeklaPlanDocument plan,
            TeklaPlanCommand command,
            string operationId)
        {
            var parsed = TeklaPlanPayloads.ParseCreateHole(command);
            if (!parsed.Success || parsed.Value is null)
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_HOLE_PAYLOAD_INVALID",
                    string.Join(" ", parsed.Diagnostics.Select(static item => item.Message)));
            }

            var target = TeklaPlanNativeTargetResolver.ResolvePartCommand(plan, command, parsed.Value.Target);
            var externalObjectId = TeklaPlanOwnershipIdentity.Create(plan, command);
            var existing = FindExisting(model, externalObjectId);
            return new PreparedHoleMutation(
                model,
                plan,
                command,
                parsed.Value,
                operationId,
                externalObjectId,
                target.ExternalObjectId,
                existing);
        }

        private static BoltArray? FindExisting(Model model, string externalObjectId)
        {
            var matches = new List<ModelObject>();
            var enumerator = model.GetModelObjectSelector()
                .GetAllObjectsWithType(ModelObject.ModelObjectEnum.BOLT_ARRAY);
            while (enumerator.MoveNext())
            {
                if (enumerator.Current is not ModelObject current) continue;
                var candidate = string.Empty;
                if (!current.GetUserProperty(StructuraServiceUdas.ExternalObjectId, ref candidate)) continue;
                if (string.Equals(candidate, externalObjectId, StringComparison.Ordinal)) matches.Add(current);
            }

            if (matches.Count > 1)
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_NATIVE_IDENTITY_DUPLICATE",
                    $"Hole has {matches.Count} instances with ownership id '{externalObjectId}'.");
            }

            var existing = matches.SingleOrDefault();
            if (existing is null) return null;
            if (existing is not BoltArray holes)
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_NATIVE_OWNERSHIP_CONFLICT",
                    $"Ownership id '{externalObjectId}' belongs to '{existing.GetType().Name}', not a BoltArray hole.");
            }

            var componentType = string.Empty;
            holes.GetUserProperty(StructuraServiceUdas.ComponentType, ref componentType);
            if (!string.Equals(componentType, RoundComponentType, StringComparison.Ordinal) &&
                !string.Equals(componentType, SlottedComponentType, StringComparison.Ordinal))
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_NATIVE_OWNERSHIP_CONFLICT",
                    $"Ownership id '{externalObjectId}' belongs to component type '{componentType}', not a constructive hole.");
            }
            return holes;
        }

        private sealed class PreparedHoleMutation : ITeklaPreparedMutation<TeklaNativeCommandReadback>
        {
            private const double Tolerance = 1e-3;
            private const double ThroughCutLengthMm = 100000;
            private readonly Model _model;
            private readonly TeklaPlanDocument _plan;
            private readonly TeklaPlanCommand _command;
            private readonly TeklaCreateHolePayload _payload;
            private readonly string _operationId;
            private readonly string _externalObjectId;
            private readonly string _targetExternalObjectId;
            private readonly string _componentType;
            private readonly HoleFrame _frame;
            private readonly BoltArray _holes;
            private readonly HoleSnapshot? _snapshot;
            private readonly bool _created;
            private bool _inserted;
            private bool _applied;

            public PreparedHoleMutation(
                Model model,
                TeklaPlanDocument plan,
                TeklaPlanCommand command,
                TeklaCreateHolePayload payload,
                string operationId,
                string externalObjectId,
                string targetExternalObjectId,
                BoltArray? existing)
            {
                _model = model;
                _plan = plan;
                _command = command;
                _payload = payload;
                _operationId = operationId;
                _externalObjectId = externalObjectId;
                _targetExternalObjectId = targetExternalObjectId;
                _componentType = string.Equals(payload.HoleType, "slotted", StringComparison.Ordinal)
                    ? SlottedComponentType
                    : RoundComponentType;
                _frame = HoleFrame.Create(payload.Center, payload.Axis, payload.SlotDirection);
                _created = existing is null;
                _holes = existing ?? new BoltArray();
                _snapshot = existing is null ? null : HoleSnapshot.Capture(model, existing);
            }

            public string CommandId => _command.CommandId;

            public void Apply()
            {
                try
                {
                    var target = TeklaPlanNativeTargetResolver.FindPart(_model, _command, _targetExternalObjectId);
                    InPlane(_model, _frame.TransformationPlane, () =>
                    {
                        if (!_created && !_holes.Select())
                            throw Failure("TEKLA_PLAN_HOLE_EXISTING_SELECT_FAILED", "Existing BoltArray.Select() returned false.");
                        Configure(_holes, target, _payload);
                        if (_created)
                        {
                            if (!_holes.Insert())
                                throw Failure("TEKLA_PLAN_HOLE_INSERT_FAILED", "BoltArray.Insert() returned false.");
                            _inserted = true;
                        }

                        StampOwnership(_holes, _externalObjectId, _componentType, _plan.SchemaVersion, _operationId);
                        if (!_holes.Modify())
                            throw Failure("TEKLA_PLAN_HOLE_MODIFY_FAILED", "BoltArray.Modify() returned false.");
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
                    throw Failure("TEKLA_PLAN_HOLE_NOT_APPLIED", "Readback was requested before apply completed.");

                return InPlane(_model, new TransformationPlane(), () =>
                {
                    if (!_holes.Select())
                        throw Failure("TEKLA_PLAN_HOLE_READBACK_SELECT_FAILED", "BoltArray.Select() returned false during readback.");
                    VerifyPayload();

                    var target = _holes.PartToBeBolted as Part ??
                        throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native hole has no target Part.");
                    var secondTarget = _holes.PartToBoltTo as Part ??
                        throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native hole has no secondary target Part.");
                    if (!SameObject(target, secondTarget))
                        throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native hole target references differ.");
                    var targetExternalObjectId = ReadStringUda(target, StructuraServiceUdas.ExternalObjectId, "target");
                    VerifyEqual("target ownership id", targetExternalObjectId, _targetExternalObjectId);

                    var externalObjectId = ReadStringUda(_holes, StructuraServiceUdas.ExternalObjectId, "hole");
                    var componentType = ReadStringUda(_holes, StructuraServiceUdas.ComponentType, "hole");
                    var lastOperationId = ReadStringUda(_holes, StructuraServiceUdas.LastOperationId, "hole");
                    var schemaVersion = ReadIntUda(_holes, StructuraServiceUdas.SchemaVersion);
                    VerifyEqual("ownership id", externalObjectId, _externalObjectId);
                    VerifyEqual("component type", componentType, _componentType);
                    VerifyEqual("last operation id", lastOperationId, _operationId);
                    if (schemaVersion != _plan.SchemaVersion)
                        throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native schema version does not match the plan.");

                    var coordinateSystem = _holes.GetCoordinateSystem();
                    var center = coordinateSystem.Origin;
                    var slotDirection = Normalize(coordinateSystem.AxisX);
                    var axis = Normalize(Cross(coordinateSystem.AxisX, coordinateSystem.AxisY));
                    VerifyPoint("center", center, _frame.Center);
                    if (Dot(axis, _frame.AxisZ) < 1 - 1e-6)
                        throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native hole axis does not match the plan.");
                    if (_payload.SlotDirection is not null && Dot(slotDirection, _frame.AxisX) < 1 - 1e-6)
                        throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native slotted-hole direction does not match the plan.");
                    var positions = _holes.BoltPositions.Cast<Point>().ToArray();
                    if (positions.Length != 1)
                        throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native hole must expose exactly one position.");
                    VerifyPoint("position", positions[0], _frame.Center);

                    var guid = _model.GetGUIDByIdentifier(_holes.Identifier) ?? string.Empty;
                    var targetGuid = _model.GetGUIDByIdentifier(target.Identifier) ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(guid) || _holes.Identifier.ID <= 0)
                        throw Failure("TEKLA_PLAN_READBACK_IDENTITY_MISSING", "Tekla did not return a persistent hole identity.");
                    if (string.IsNullOrWhiteSpace(targetGuid) || target.Identifier.ID <= 0)
                        throw Failure("TEKLA_PLAN_READBACK_IDENTITY_MISSING", "Tekla did not return a persistent target Part identity.");

                    return new TeklaNativeCommandReadback
                    {
                        CommandId = _command.CommandId,
                        CommandKind = _command.Kind,
                        Action = _created ? "created" : "modified",
                        ExternalObjectId = externalObjectId,
                        TeklaGuid = guid,
                        TeklaId = _holes.Identifier.ID,
                        TargetExternalObjectId = targetExternalObjectId,
                        TargetTeklaGuid = targetGuid,
                        HoleCenter = PointArray(center),
                        HoleAxis = VectorArray(axis),
                        HoleDiameterMm = _holes.BoltSize,
                        HoleDepthMm = _payload.DepthMm,
                        HoleType = _payload.HoleType,
                        SlotLengthMm = _payload.SlotLengthMm,
                        SlotDirection = _payload.SlotDirection is null ? Array.Empty<double>() : VectorArray(slotDirection),
                        ComponentType = componentType,
                        SchemaVersion = schemaVersion,
                        LastOperationId = lastOperationId,
                    };
                });
            }

            public void Restore()
            {
                if (_created)
                {
                    if (_inserted && !_holes.Delete())
                        throw Failure("TEKLA_PLAN_HOLE_ROLLBACK_DELETE_FAILED", "BoltArray.Delete() returned false.");
                    _inserted = false;
                    _applied = false;
                    return;
                }

                if (_snapshot is null)
                    throw Failure("TEKLA_PLAN_HOLE_ROLLBACK_STATE_MISSING", "Existing BoltArray snapshot is missing.");
                _snapshot.Restore(_model, _holes);
                _applied = false;
            }

            private void RestorePartialApply()
            {
                try
                {
                    if (_created)
                    {
                        if (_inserted) _holes.Delete();
                        _inserted = false;
                    }
                    else if (_snapshot is not null)
                    {
                        _snapshot.Restore(_model, _holes);
                    }
                }
                finally
                {
                    _applied = false;
                }
            }

            private void VerifyPayload()
            {
                if (_holes.Bolt || Math.Abs(_holes.BoltSize - _payload.DiameterMm) > Tolerance ||
                    Math.Abs(_holes.Tolerance) > Tolerance)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native hole-only mode or diameter does not match the plan.");
                var expectedSlottedX = _payload.SlotLengthMm.HasValue
                    ? _payload.SlotLengthMm.Value - _payload.DiameterMm
                    : 0;
                if (_holes.HoleType != BoltGroup.BoltHoleTypeEnum.HOLE_TYPE_SLOTTED ||
                    Math.Abs(_holes.SlottedHoleX - expectedSlottedX) > Tolerance || Math.Abs(_holes.SlottedHoleY) > Tolerance ||
                    Math.Abs(_holes.SlotOffsetX) > Tolerance || Math.Abs(_holes.SlotOffsetY) > Tolerance)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native BoltArray slot dimensions do not match the plan.");
                var expectedPlainType = _payload.DepthMm.HasValue
                    ? BoltGroup.BoltPlainHoleTypeEnum.HOLE_TYPE_BLIND
                    : BoltGroup.BoltPlainHoleTypeEnum.HOLE_TYPE_THROUGH;
                if (_holes.PlainHoleType != expectedPlainType ||
                    Math.Abs(_holes.BlindHoleDepth - (_payload.DepthMm ?? 0)) > Tolerance)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native through/blind hole parameters do not match the plan.");
                if (_holes.GetBoltDistXCount() != 1 || _holes.GetBoltDistYCount() != 1 ||
                    Math.Abs(_holes.GetBoltDistX(0)) > Tolerance || Math.Abs(_holes.GetBoltDistY(0)) > Tolerance)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native hole does not contain exactly one pattern point.");
                if (!_holes.Hole1 || _holes.Hole2 || _holes.Hole3 || _holes.Hole4 || _holes.Hole5)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native hole is not enabled exclusively on the target part.");
            }

            private void VerifyPoint(string role, Point actual, Point expected)
            {
                if (Distance(actual, expected) > Tolerance)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native hole {role} does not match the plan.");
            }

            private void VerifyEqual(string role, string actual, string expected)
            {
                if (!string.Equals(actual?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native hole {role} does not match the plan.");
            }

            private string ReadStringUda(ModelObject target, string name, string role)
            {
                var value = string.Empty;
                if (!target.GetUserProperty(name, ref value))
                    throw Failure("TEKLA_PLAN_READBACK_UDA_MISSING", $"Native {role} UDA '{name}' is missing.");
                return value;
            }

            private int ReadIntUda(ModelObject target, string name)
            {
                var value = 0;
                if (!target.GetUserProperty(name, ref value))
                    throw Failure("TEKLA_PLAN_READBACK_UDA_MISSING", $"Native hole UDA '{name}' is missing.");
                return value;
            }

            private TeklaNativeExecutionException Failure(string code, string message)
                => new(code, $"Command '{_command.CommandId}': {message}");

            private static void Configure(BoltArray holes, Part target, TeklaCreateHolePayload payload)
            {
                ClearBoltDistances(holes);
                holes.PartToBeBolted = target;
                holes.PartToBoltTo = target;
                holes.FirstPosition = new Point(0, 0, 0);
                holes.SecondPosition = new Point(100, 0, 0);
                holes.BoltSize = payload.DiameterMm;
                holes.BoltStandard = "7798";
                holes.BoltType = BoltGroup.BoltTypeEnum.BOLT_TYPE_SITE;
                holes.ThreadInMaterial = BoltGroup.BoltThreadInMaterialEnum.THREAD_IN_MATERIAL_YES;
                holes.Length = 0;
                holes.CutLength = payload.DepthMm ?? ThroughCutLengthMm;
                holes.ExtraLength = 0;
                holes.Tolerance = 0;
                holes.HoleType = BoltGroup.BoltHoleTypeEnum.HOLE_TYPE_SLOTTED;
                holes.PlainHoleType = payload.DepthMm.HasValue
                    ? BoltGroup.BoltPlainHoleTypeEnum.HOLE_TYPE_BLIND
                    : BoltGroup.BoltPlainHoleTypeEnum.HOLE_TYPE_THROUGH;
                holes.BlindHoleDepth = payload.DepthMm ?? 0;
                holes.SlottedHoleX = payload.SlotLengthMm.HasValue
                    ? payload.SlotLengthMm.Value - payload.DiameterMm
                    : 0;
                holes.SlottedHoleY = 0;
                holes.SlotOffsetX = 0;
                holes.SlotOffsetY = 0;
                holes.RotateSlots = BoltGroup.BoltRotateSlotsEnum.ROTATE_SLOTS_PARALLEL;
                holes.Washer1 = false;
                holes.Washer2 = false;
                holes.Washer3 = false;
                holes.Nut1 = false;
                holes.Nut2 = false;
                holes.Bolt = false;
                holes.Hole1 = true;
                holes.Hole2 = false;
                holes.Hole3 = false;
                holes.Hole4 = false;
                holes.Hole5 = false;
                holes.ConnectAssemblies = false;
                holes.Position.Plane = Position.PlaneEnum.MIDDLE;
                holes.Position.PlaneOffset = 0;
                holes.Position.Depth = Position.DepthEnum.MIDDLE;
                holes.Position.DepthOffset = 0;
                holes.Position.Rotation = Position.RotationEnum.FRONT;
                holes.Position.RotationOffset = 0;
                holes.AddBoltDistX(0);
                holes.AddBoltDistY(0);
            }
        }

        private sealed class HoleSnapshot
        {
            private readonly TransformationPlane _capturePlane;
            private readonly Part _partToBeBolted;
            private readonly Part _partToBoltTo;
            private readonly Point _firstPosition;
            private readonly Point _secondPosition;
            private readonly double[] _xDistances;
            private readonly double[] _yDistances;
            private readonly double _boltSize;
            private readonly string _boltStandard;
            private readonly BoltGroup.BoltTypeEnum _boltType;
            private readonly BoltGroup.BoltThreadInMaterialEnum _threadInMaterial;
            private readonly double _length;
            private readonly double _cutLength;
            private readonly double _extraLength;
            private readonly double _tolerance;
            private readonly BoltGroup.BoltHoleTypeEnum _holeType;
            private readonly BoltGroup.BoltPlainHoleTypeEnum _plainHoleType;
            private readonly double _blindHoleDepth;
            private readonly double _slottedHoleX;
            private readonly double _slottedHoleY;
            private readonly double _slotOffsetX;
            private readonly double _slotOffsetY;
            private readonly BoltGroup.BoltRotateSlotsEnum _rotateSlots;
            private readonly bool[] _flags;
            private readonly Position.PlaneEnum _plane;
            private readonly double _planeOffset;
            private readonly Position.DepthEnum _depth;
            private readonly double _depthOffset;
            private readonly Position.RotationEnum _rotation;
            private readonly double _rotationOffset;
            private readonly string _externalObjectId;
            private readonly string _componentType;
            private readonly int _schemaVersion;
            private readonly string _lastOperationId;

            private HoleSnapshot(Model model, BoltArray holes)
            {
                _capturePlane = model.GetWorkPlaneHandler().GetCurrentTransformationPlane();
                if (!holes.Select())
                    throw new TeklaNativeExecutionException("TEKLA_PLAN_HOLE_EXISTING_SELECT_FAILED", "Existing BoltArray.Select() returned false during snapshot.");
                _partToBeBolted = holes.PartToBeBolted as Part ??
                    throw new TeklaNativeExecutionException("TEKLA_PLAN_HOLE_EXISTING_TARGET_MISSING", "Existing round hole has no target Part.");
                _partToBoltTo = holes.PartToBoltTo as Part ??
                    throw new TeklaNativeExecutionException("TEKLA_PLAN_HOLE_EXISTING_TARGET_MISSING", "Existing round hole has no secondary target Part.");
                _firstPosition = Clone(holes.FirstPosition);
                _secondPosition = Clone(holes.SecondPosition);
                _xDistances = Enumerable.Range(0, holes.GetBoltDistXCount()).Select(holes.GetBoltDistX).ToArray();
                _yDistances = Enumerable.Range(0, holes.GetBoltDistYCount()).Select(holes.GetBoltDistY).ToArray();
                _boltSize = holes.BoltSize;
                _boltStandard = holes.BoltStandard;
                _boltType = holes.BoltType;
                _threadInMaterial = holes.ThreadInMaterial;
                _length = holes.Length;
                _cutLength = holes.CutLength;
                _extraLength = holes.ExtraLength;
                _tolerance = holes.Tolerance;
                _holeType = holes.HoleType;
                _plainHoleType = holes.PlainHoleType;
                _blindHoleDepth = holes.BlindHoleDepth;
                _slottedHoleX = holes.SlottedHoleX;
                _slottedHoleY = holes.SlottedHoleY;
                _slotOffsetX = holes.SlotOffsetX;
                _slotOffsetY = holes.SlotOffsetY;
                _rotateSlots = holes.RotateSlots;
                _flags = new[]
                {
                    holes.Washer1, holes.Washer2, holes.Washer3, holes.Nut1, holes.Nut2,
                    holes.Bolt, holes.Hole1, holes.Hole2, holes.Hole3, holes.Hole4, holes.Hole5,
                    holes.ConnectAssemblies,
                };
                _plane = holes.Position.Plane;
                _planeOffset = holes.Position.PlaneOffset;
                _depth = holes.Position.Depth;
                _depthOffset = holes.Position.DepthOffset;
                _rotation = holes.Position.Rotation;
                _rotationOffset = holes.Position.RotationOffset;
                _externalObjectId = ReadString(holes, StructuraServiceUdas.ExternalObjectId);
                _componentType = ReadString(holes, StructuraServiceUdas.ComponentType);
                _schemaVersion = ReadInt(holes, StructuraServiceUdas.SchemaVersion);
                _lastOperationId = ReadString(holes, StructuraServiceUdas.LastOperationId);
            }

            public static HoleSnapshot Capture(Model model, BoltArray holes) => new(model, holes);

            public void Restore(Model model, BoltArray holes)
            {
                InPlane(model, _capturePlane, () =>
                {
                    if (!holes.Select())
                        throw new TeklaNativeExecutionException("TEKLA_PLAN_HOLE_ROLLBACK_SELECT_FAILED", "Existing BoltArray.Select() returned false during rollback.");
                    ClearBoltDistances(holes);
                    holes.PartToBeBolted = _partToBeBolted;
                    holes.PartToBoltTo = _partToBoltTo;
                    holes.FirstPosition = Clone(_firstPosition);
                    holes.SecondPosition = Clone(_secondPosition);
                    foreach (var value in _xDistances) holes.AddBoltDistX(value);
                    foreach (var value in _yDistances) holes.AddBoltDistY(value);
                    holes.BoltSize = _boltSize;
                    holes.BoltStandard = _boltStandard;
                    holes.BoltType = _boltType;
                    holes.ThreadInMaterial = _threadInMaterial;
                    holes.Length = _length;
                    holes.CutLength = _cutLength;
                    holes.ExtraLength = _extraLength;
                    holes.Tolerance = _tolerance;
                    holes.HoleType = _holeType;
                    holes.PlainHoleType = _plainHoleType;
                    holes.BlindHoleDepth = _blindHoleDepth;
                    holes.SlottedHoleX = _slottedHoleX;
                    holes.SlottedHoleY = _slottedHoleY;
                    holes.SlotOffsetX = _slotOffsetX;
                    holes.SlotOffsetY = _slotOffsetY;
                    holes.RotateSlots = _rotateSlots;
                    holes.Washer1 = _flags[0];
                    holes.Washer2 = _flags[1];
                    holes.Washer3 = _flags[2];
                    holes.Nut1 = _flags[3];
                    holes.Nut2 = _flags[4];
                    holes.Bolt = _flags[5];
                    holes.Hole1 = _flags[6];
                    holes.Hole2 = _flags[7];
                    holes.Hole3 = _flags[8];
                    holes.Hole4 = _flags[9];
                    holes.Hole5 = _flags[10];
                    holes.ConnectAssemblies = _flags[11];
                    holes.Position.Plane = _plane;
                    holes.Position.PlaneOffset = _planeOffset;
                    holes.Position.Depth = _depth;
                    holes.Position.DepthOffset = _depthOffset;
                    holes.Position.Rotation = _rotation;
                    holes.Position.RotationOffset = _rotationOffset;
                    StampOwnership(holes, _externalObjectId, _componentType, _schemaVersion, _lastOperationId);
                    if (!holes.Modify())
                        throw new TeklaNativeExecutionException("TEKLA_PLAN_HOLE_ROLLBACK_MODIFY_FAILED", "BoltArray.Modify() returned false during rollback.");
                });
            }
        }

        private sealed class HoleFrame
        {
            private HoleFrame(Point center, Vector axisX, Vector axisY, Vector axisZ)
            {
                Center = center;
                AxisX = axisX;
                AxisY = axisY;
                AxisZ = axisZ;
                TransformationPlane = new TransformationPlane(new CoordinateSystem(center, axisX, axisY));
            }

            public Point Center { get; }
            public Vector AxisX { get; }
            public Vector AxisY { get; }
            public Vector AxisZ { get; }
            public TransformationPlane TransformationPlane { get; }

            public static HoleFrame Create(TeklaVector3 center, TeklaVector3 axis, TeklaVector3? slotDirection)
            {
                var axisZ = Normalize(Vector(axis));
                var axisX = slotDirection is null
                    ? DeterministicAxisX(axisZ)
                    : Normalize(Vector(slotDirection));
                var axisY = Normalize(Cross(axisZ, axisX));
                return new HoleFrame(Point(center), axisX, axisY, axisZ);
            }

            private static Vector DeterministicAxisX(Vector axisZ)
            {
                var seed = Math.Abs(axisZ.Z) < 0.9 ? new Vector(0, 0, 1) : new Vector(0, 1, 0);
                return Normalize(Cross(seed, axisZ));
            }
        }

        private static void ClearBoltDistances(BoltArray holes)
        {
            for (var index = holes.GetBoltDistXCount() - 1; index >= 0; index--) holes.RemoveBoltDistX(index);
            for (var index = holes.GetBoltDistYCount() - 1; index >= 0; index--) holes.RemoveBoltDistY(index);
        }

        private static void StampOwnership(
            ModelObject target,
            string externalObjectId,
            string componentType,
            int schemaVersion,
            string operationId)
        {
            RequireUda(target.SetUserProperty(StructuraServiceUdas.ExternalObjectId, externalObjectId), StructuraServiceUdas.ExternalObjectId);
            RequireUda(target.SetUserProperty(StructuraServiceUdas.ComponentType, componentType), StructuraServiceUdas.ComponentType);
            RequireUda(target.SetUserProperty(StructuraServiceUdas.SchemaVersion, schemaVersion), StructuraServiceUdas.SchemaVersion);
            RequireUda(target.SetUserProperty(StructuraServiceUdas.LastOperationId, operationId), StructuraServiceUdas.LastOperationId);
        }

        private static void RequireUda(bool result, string name)
        {
            if (!result)
                throw new TeklaNativeExecutionException("TEKLA_PLAN_UDA_WRITE_FAILED", $"Failed to write UDA '{name}'.");
        }

        private static string ReadString(ModelObject target, string name)
        {
            var value = string.Empty;
            target.GetUserProperty(name, ref value);
            return value;
        }

        private static int ReadInt(ModelObject target, string name)
        {
            var value = 0;
            target.GetUserProperty(name, ref value);
            return value;
        }

        private static void InPlane(Model model, TransformationPlane plane, Action action)
        {
            var handler = model.GetWorkPlaneHandler();
            var previous = handler.GetCurrentTransformationPlane();
            if (!handler.SetCurrentTransformationPlane(plane))
                throw new TeklaNativeExecutionException("TEKLA_PLAN_WORK_PLANE_SET_FAILED", "Tekla rejected the requested hole work plane.");
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
            InPlane(model, plane, () =>
            {
                result = action();
            });
            return result!;
        }

        private static bool SameObject(ModelObject left, ModelObject right)
            => left.Identifier.ID != 0 && left.Identifier.ID == right.Identifier.ID;

        private static Point Clone(Point value) => new(value.X, value.Y, value.Z);
        private static Point Point(TeklaVector3 value) => new(value.X, value.Y, value.Z);
        private static Vector Vector(TeklaVector3 value) => new(value.X, value.Y, value.Z);
        private static double[] PointArray(Point value) => new[] { value.X, value.Y, value.Z };
        private static double[] VectorArray(Vector value) => new[] { value.X, value.Y, value.Z };
        private static double Dot(Vector left, Vector right) => (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);
        private static Vector Cross(Vector left, Vector right) => new(
            (left.Y * right.Z) - (left.Z * right.Y),
            (left.Z * right.X) - (left.X * right.Z),
            (left.X * right.Y) - (left.Y * right.X));
        private static Vector Normalize(Vector value)
        {
            var length = Math.Sqrt(Dot(value, value));
            if (length <= 1e-12)
                throw new TeklaNativeExecutionException("TEKLA_PLAN_HOLE_AXIS_INVALID", "Round-hole axis cannot be zero.");
            return new Vector(value.X / length, value.Y / length, value.Z / length);
        }
        private static double Distance(Point left, Point right)
        {
            var x = left.X - right.X;
            var y = left.Y - right.Y;
            var z = left.Z - right.Z;
            return Math.Sqrt((x * x) + (y * y) + (z * z));
        }

        private static TeklaNativeExecutionException Failure(TeklaPlanCommand command, string code, string message)
            => new(code, $"Command '{command.CommandId}': {message}");
    }
}
