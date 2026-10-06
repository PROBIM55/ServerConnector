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
    public sealed class TeklaCreateBoltGroupExecutor : ITeklaNativeCommandExecutor
    {
        private const string ExecutorId = "tekla-native.create-bolt-group.v1";
        private const string ComponentType = "ConstructiveBoltGroupV1";
        private const double ThroughCutLengthMm = 100000;

        public TeklaPlanExecutorRegistration Registration { get; } = new(
            ExecutorId,
            "create-bolt-group",
            new[] { "boltGroup", "udaStamp" },
            applyReady: true);

        public ITeklaPreparedMutation<TeklaNativeCommandReadback> Prepare(
            Model model,
            TeklaPlanDocument plan,
            TeklaPlanCommand command,
            string operationId)
        {
            var parsed = TeklaPlanPayloads.ParseCreateBoltGroup(command);
            if (!parsed.Success || parsed.Value is null)
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_BOLT_PAYLOAD_INVALID",
                    string.Join(" ", parsed.Diagnostics.Select(static item => item.Message)));
            }
            if (parsed.Value.LengthMm.HasValue)
            {
                throw Failure(
                    command,
                    "TEKLA_PLAN_BOLT_LENGTH_UNSUPPORTED",
                    "Tekla 2025 normalizes an assigned catalog BoltGroup.Length back to zero; explicit lengthMm is not a round-trip-safe native capability.");
            }

            var target = TeklaPlanNativeTargetResolver.ResolvePartCommand(plan, command, parsed.Value.Target);
            var participant = TeklaPlanNativeTargetResolver.ResolvePartCommand(plan, command, parsed.Value.Participant);
            var externalObjectId = TeklaPlanOwnershipIdentity.Create(plan, command);
            var existing = FindExisting(model, externalObjectId);
            RequireNativePatternType(command, parsed.Value.Pattern.Kind, existing);
            return new PreparedBoltMutation(
                model,
                plan,
                command,
                parsed.Value,
                operationId,
                externalObjectId,
                target.ExternalObjectId,
                participant.ExternalObjectId,
                existing);
        }

        private static BoltGroup? FindExisting(Model model, string externalObjectId)
        {
            var matches = new List<ModelObject>();
            foreach (var objectType in new[]
            {
                ModelObject.ModelObjectEnum.BOLT_ARRAY,
                ModelObject.ModelObjectEnum.BOLT_XYLIST,
            })
            {
                var enumerator = model.GetModelObjectSelector().GetAllObjectsWithType(objectType);
                while (enumerator.MoveNext())
                {
                    if (enumerator.Current is not ModelObject current) continue;
                    var candidate = string.Empty;
                    if (!current.GetUserProperty(StructuraServiceUdas.ExternalObjectId, ref candidate)) continue;
                    if (string.Equals(candidate, externalObjectId, StringComparison.Ordinal)) matches.Add(current);
                }
            }

            if (matches.Count > 1)
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_NATIVE_IDENTITY_DUPLICATE",
                    $"Bolt group has {matches.Count} instances with ownership id '{externalObjectId}'.");
            }

            var existing = matches.SingleOrDefault();
            if (existing is null) return null;
            if (existing is not BoltGroup bolts)
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_NATIVE_OWNERSHIP_CONFLICT",
                    $"Ownership id '{externalObjectId}' belongs to '{existing.GetType().Name}', not a BoltGroup.");
            }

            var componentType = string.Empty;
            bolts.GetUserProperty(StructuraServiceUdas.ComponentType, ref componentType);
            if (!string.Equals(componentType, ComponentType, StringComparison.Ordinal))
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_NATIVE_OWNERSHIP_CONFLICT",
                    $"Ownership id '{externalObjectId}' belongs to component type '{componentType}', not a constructive bolt group.");
            }
            return bolts;
        }

        private static void RequireNativePatternType(TeklaPlanCommand command, string patternKind, BoltGroup? existing)
        {
            if (existing is null) return;
            var expectedPoints = string.Equals(patternKind, "points", StringComparison.Ordinal);
            if ((expectedPoints && existing is BoltXYList) || (!expectedPoints && existing is BoltArray)) return;
            throw Failure(
                command,
                "TEKLA_PLAN_BOLT_PATTERN_TYPE_CHANGE_UNSUPPORTED",
                $"Pattern '{patternKind}' requires {(expectedPoints ? nameof(BoltXYList) : nameof(BoltArray))}, but the owned native object is {existing.GetType().Name}.");
        }

        private sealed class PreparedBoltMutation : ITeklaPreparedMutation<TeklaNativeCommandReadback>
        {
            private const double Tolerance = 1e-3;
            private readonly Model _model;
            private readonly TeklaPlanDocument _plan;
            private readonly TeklaPlanCommand _command;
            private readonly TeklaCreateBoltGroupPayload _payload;
            private readonly string _operationId;
            private readonly string _externalObjectId;
            private readonly string _targetExternalObjectId;
            private readonly string _participantExternalObjectId;
            private readonly BoltFrame _frame;
            private readonly BoltGroup _bolts;
            private readonly BoltSnapshot? _snapshot;
            private readonly bool _created;
            private bool _inserted;
            private bool _applied;

            public PreparedBoltMutation(
                Model model,
                TeklaPlanDocument plan,
                TeklaPlanCommand command,
                TeklaCreateBoltGroupPayload payload,
                string operationId,
                string externalObjectId,
                string targetExternalObjectId,
                string participantExternalObjectId,
                BoltGroup? existing)
            {
                _model = model;
                _plan = plan;
                _command = command;
                _payload = payload;
                _operationId = operationId;
                _externalObjectId = externalObjectId;
                _targetExternalObjectId = targetExternalObjectId;
                _participantExternalObjectId = participantExternalObjectId;
                _frame = BoltFrame.Create(payload.Frame);
                _created = existing is null;
                _bolts = existing ?? CreateNativePattern(payload.Pattern.Kind);
                _snapshot = existing is null ? null : BoltSnapshot.Capture(model, existing);
            }

            public string CommandId => _command.CommandId;

            public void Apply()
            {
                try
                {
                    var target = TeklaPlanNativeTargetResolver.FindPart(_model, _command, _targetExternalObjectId);
                    var participant = TeklaPlanNativeTargetResolver.FindPart(_model, _command, _participantExternalObjectId);
                    InPlane(_model, _frame.TransformationPlane, () =>
                    {
                        if (!_created && !Select(_bolts))
                            throw Failure("TEKLA_PLAN_BOLT_EXISTING_SELECT_FAILED", $"Existing {_bolts.GetType().Name}.Select() returned false.");
                        ConfigurePattern(_bolts, _payload.Pattern, _created);
                        ConfigureCommon(_bolts, target, participant, _payload);
                        if (_created)
                        {
                            if (!Insert(_bolts))
                                throw Failure("TEKLA_PLAN_BOLT_INSERT_FAILED", $"{_bolts.GetType().Name}.Insert() returned false.");
                            _inserted = true;
                        }

                        StampOwnership(_bolts, _externalObjectId, ComponentType, _plan.SchemaVersion, _operationId);
                        if (!Modify(_bolts))
                            throw Failure("TEKLA_PLAN_BOLT_MODIFY_FAILED", $"{_bolts.GetType().Name}.Modify() returned false.");
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
                    throw Failure("TEKLA_PLAN_BOLT_NOT_APPLIED", "Readback was requested before apply completed.");

                return InPlane(_model, new TransformationPlane(), () =>
                {
                    if (!Select(_bolts))
                        throw Failure("TEKLA_PLAN_BOLT_READBACK_SELECT_FAILED", $"{_bolts.GetType().Name}.Select() returned false during readback.");
                    VerifyPayload();

                    var target = _bolts.PartToBeBolted as Part ??
                        throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native bolt group has no target Part.");
                    var participant = _bolts.PartToBoltTo as Part ??
                        throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native bolt group has no participant Part.");
                    if (SameObject(target, participant))
                        throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native bolt group target and participant unexpectedly reference the same Part.");

                    var targetExternalObjectId = ReadStringUda(target, StructuraServiceUdas.ExternalObjectId, "target");
                    var participantExternalObjectId = ReadStringUda(participant, StructuraServiceUdas.ExternalObjectId, "participant");
                    VerifyEqual("target ownership id", targetExternalObjectId, _targetExternalObjectId);
                    VerifyEqual("participant ownership id", participantExternalObjectId, _participantExternalObjectId);

                    var externalObjectId = ReadStringUda(_bolts, StructuraServiceUdas.ExternalObjectId, "bolt group");
                    var componentType = ReadStringUda(_bolts, StructuraServiceUdas.ComponentType, "bolt group");
                    var lastOperationId = ReadStringUda(_bolts, StructuraServiceUdas.LastOperationId, "bolt group");
                    var schemaVersion = ReadIntUda(_bolts, StructuraServiceUdas.SchemaVersion);
                    VerifyEqual("ownership id", externalObjectId, _externalObjectId);
                    VerifyEqual("component type", componentType, ComponentType);
                    VerifyEqual("last operation id", lastOperationId, _operationId);
                    if (schemaVersion != _plan.SchemaVersion)
                        throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native schema version does not match the plan.");

                    var coordinateSystem = _bolts.GetCoordinateSystem();
                    var axisX = Normalize(coordinateSystem.AxisX);
                    var axisZ = Normalize(Cross(coordinateSystem.AxisX, coordinateSystem.AxisY));
                    VerifyPoint("frame origin", coordinateSystem.Origin, _frame.Origin);
                    if (Dot(axisX, _frame.AxisX) < 1 - 1e-6 || Dot(axisZ, _frame.AxisZ) < 1 - 1e-6)
                        throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native bolt-group frame does not match the plan.");

                    var positions = _bolts.BoltPositions.Cast<Point>().ToArray();
                    var expectedPositions = ExpectedWorldPositions(_payload.Pattern, _frame).ToArray();
                    if (positions.Length != expectedPositions.Length)
                        throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native bolt group exposes {positions.Length} positions, expected {expectedPositions.Length}.");
                    foreach (var expected in expectedPositions)
                    {
                        if (!positions.Any(actual => Distance(actual, expected) <= Tolerance))
                            throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native bolt-group positions do not match the plan.");
                    }

                    var guid = _model.GetGUIDByIdentifier(_bolts.Identifier) ?? string.Empty;
                    var targetGuid = _model.GetGUIDByIdentifier(target.Identifier) ?? string.Empty;
                    var participantGuid = _model.GetGUIDByIdentifier(participant.Identifier) ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(guid) || _bolts.Identifier.ID <= 0)
                        throw Failure("TEKLA_PLAN_READBACK_IDENTITY_MISSING", "Tekla did not return a persistent bolt-group identity.");
                    if (string.IsNullOrWhiteSpace(targetGuid) || string.IsNullOrWhiteSpace(participantGuid))
                        throw Failure("TEKLA_PLAN_READBACK_IDENTITY_MISSING", "Tekla did not return persistent participant identities.");

                    return new TeklaNativeCommandReadback
                    {
                        CommandId = _command.CommandId,
                        CommandKind = _command.Kind,
                        Action = _created ? "created" : "modified",
                        ExternalObjectId = externalObjectId,
                        TeklaGuid = guid,
                        TeklaId = _bolts.Identifier.ID,
                        TargetExternalObjectId = targetExternalObjectId,
                        TargetTeklaGuid = targetGuid,
                        ParticipantExternalObjectIds = new[] { participantExternalObjectId },
                        ParticipantTeklaGuids = new[] { participantGuid },
                        PlaneOrigin = PointArray(coordinateSystem.Origin),
                        PlaneAxisX = VectorArray(axisX),
                        PlaneAxisY = VectorArray(Normalize(coordinateSystem.AxisY)),
                        BoltStandard = _bolts.BoltStandard,
                        BoltDiameterMm = _bolts.BoltSize,
                        BoltLengthMm = null,
                        BoltToleranceMm = _bolts.Tolerance,
                        BoltType = _payload.BoltType,
                        BoltCreatesHoles = _payload.CreateHoles,
                        BoltPatternKind = _payload.Pattern.Kind,
                        BoltPositions = positions.Select(PointArray).ToArray(),
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
                    if (_inserted && !Delete(_bolts))
                        throw Failure("TEKLA_PLAN_BOLT_ROLLBACK_DELETE_FAILED", $"{_bolts.GetType().Name}.Delete() returned false.");
                    _inserted = false;
                    _applied = false;
                    return;
                }
                if (_snapshot is null)
                    throw Failure("TEKLA_PLAN_BOLT_ROLLBACK_STATE_MISSING", "Existing bolt-group snapshot is missing.");
                _snapshot.Restore(_model, _bolts);
                _applied = false;
            }

            private void RestorePartialApply()
            {
                try
                {
                    if (_created)
                    {
                        if (_inserted) Delete(_bolts);
                        _inserted = false;
                    }
                    else if (_snapshot is not null)
                    {
                        _snapshot.Restore(_model, _bolts);
                    }
                }
                finally
                {
                    _applied = false;
                }
            }

            private void VerifyPayload()
            {
                if (!_bolts.Bolt || Math.Abs(_bolts.BoltSize - _payload.DiameterMm) > Tolerance ||
                    Math.Abs(_bolts.Tolerance - _payload.ToleranceMm) > Tolerance ||
                    !string.Equals(_bolts.BoltStandard, _payload.BoltStandard, StringComparison.Ordinal) ||
                    _bolts.BoltType != BoltType(_payload.BoltType) || Math.Abs(_bolts.Length) > Tolerance)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native bolt catalog parameters do not match the plan.");
                if (_bolts.Hole1 != _payload.CreateHoles || _bolts.Hole2 != _payload.CreateHoles ||
                    _bolts.Hole3 || _bolts.Hole4 || _bolts.Hole5)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native bolt-group hole flags do not match the plan.");
                if (_bolts.HoleType != BoltGroup.BoltHoleTypeEnum.HOLE_TYPE_SLOTTED ||
                    Math.Abs(_bolts.SlottedHoleX) > Tolerance || Math.Abs(_bolts.SlottedHoleY) > Tolerance ||
                    Math.Abs(_bolts.SlotOffsetX) > Tolerance || Math.Abs(_bolts.SlotOffsetY) > Tolerance)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native bolt group unexpectedly contains slotted-hole dimensions.");
                VerifyNativePattern(_bolts, _payload.Pattern);
            }

            private void VerifyEqual(string role, string actual, string expected)
            {
                if (!string.Equals(actual?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native bolt-group {role} does not match the plan.");
            }

            private void VerifyPoint(string role, Point actual, Point expected)
            {
                if (Distance(actual, expected) > Tolerance)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native bolt-group {role} does not match the plan.");
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
                    throw Failure("TEKLA_PLAN_READBACK_UDA_MISSING", $"Native bolt-group UDA '{name}' is missing.");
                return value;
            }

            private TeklaNativeExecutionException Failure(string code, string message)
                => new(code, $"Command '{_command.CommandId}': {message}");
        }

        private sealed class BoltSnapshot
        {
            private readonly TransformationPlane _capturePlane;
            private readonly Part _target;
            private readonly Part _participant;
            private readonly Point _firstPosition;
            private readonly Point _secondPosition;
            private readonly double[] _xDistances;
            private readonly double[] _yDistances;
            private readonly BoltProperties _properties;
            private readonly string _externalObjectId;
            private readonly string _componentType;
            private readonly int _schemaVersion;
            private readonly string _lastOperationId;

            private BoltSnapshot(Model model, BoltGroup bolts)
            {
                var system = bolts.GetCoordinateSystem();
                _capturePlane = new TransformationPlane(new CoordinateSystem(system.Origin, system.AxisX, system.AxisY));
                Part? target = null;
                Part? participant = null;
                Point? firstPosition = null;
                Point? secondPosition = null;
                double[] xDistances = Array.Empty<double>();
                double[] yDistances = Array.Empty<double>();
                BoltProperties? properties = null;
                InPlane(model, _capturePlane, () =>
                {
                    if (!Select(bolts))
                        throw new TeklaNativeExecutionException("TEKLA_PLAN_BOLT_EXISTING_SELECT_FAILED", $"Existing {bolts.GetType().Name}.Select() returned false during snapshot.");
                    target = bolts.PartToBeBolted as Part;
                    participant = bolts.PartToBoltTo as Part;
                    firstPosition = Clone(bolts.FirstPosition);
                    secondPosition = Clone(bolts.SecondPosition);
                    xDistances = ReadXDistances(bolts);
                    yDistances = ReadYDistances(bolts);
                    properties = BoltProperties.Capture(bolts);
                });
                _target = target ?? throw new TeklaNativeExecutionException("TEKLA_PLAN_BOLT_EXISTING_TARGET_MISSING", "Existing bolt group has no target Part.");
                _participant = participant ?? throw new TeklaNativeExecutionException("TEKLA_PLAN_BOLT_EXISTING_PARTICIPANT_MISSING", "Existing bolt group has no participant Part.");
                _firstPosition = firstPosition!;
                _secondPosition = secondPosition!;
                _xDistances = xDistances;
                _yDistances = yDistances;
                _properties = properties!;
                _externalObjectId = ReadString(bolts, StructuraServiceUdas.ExternalObjectId);
                _componentType = ReadString(bolts, StructuraServiceUdas.ComponentType);
                _schemaVersion = ReadInt(bolts, StructuraServiceUdas.SchemaVersion);
                _lastOperationId = ReadString(bolts, StructuraServiceUdas.LastOperationId);
            }

            public static BoltSnapshot Capture(Model model, BoltGroup bolts) => new(model, bolts);

            public void Restore(Model model, BoltGroup bolts)
            {
                InPlane(model, _capturePlane, () =>
                {
                    if (!Select(bolts))
                        throw new TeklaNativeExecutionException("TEKLA_PLAN_BOLT_ROLLBACK_SELECT_FAILED", $"Existing {bolts.GetType().Name}.Select() returned false during rollback.");
                    if (bolts is BoltArray array)
                    {
                        ClearDistances(array);
                        foreach (var value in _xDistances) Require(array.AddBoltDistX(value), "restore X distance");
                        foreach (var value in _yDistances) Require(array.AddBoltDistY(value), "restore Y distance");
                    }
                    else if (!DistancesEqual(ReadXDistances(bolts), _xDistances) || !DistancesEqual(ReadYDistances(bolts), _yDistances))
                    {
                        throw new TeklaNativeExecutionException(
                            "TEKLA_PLAN_BOLT_POINTS_ROLLBACK_UNSUPPORTED",
                            "Tekla 2025 cannot replace BoltXYList point arrays in place.");
                    }
                    bolts.PartToBeBolted = _target;
                    bolts.PartToBoltTo = _participant;
                    bolts.FirstPosition = Clone(_firstPosition);
                    bolts.SecondPosition = Clone(_secondPosition);
                    _properties.Restore(bolts);
                    StampOwnership(bolts, _externalObjectId, _componentType, _schemaVersion, _lastOperationId);
                    if (!Modify(bolts))
                        throw new TeklaNativeExecutionException("TEKLA_PLAN_BOLT_ROLLBACK_MODIFY_FAILED", $"{bolts.GetType().Name}.Modify() returned false during rollback.");
                });
            }
        }

        private sealed class BoltProperties
        {
            private double _boltSize;
            private string _boltStandard = string.Empty;
            private BoltGroup.BoltTypeEnum _boltType;
            private BoltGroup.BoltThreadInMaterialEnum _threadInMaterial;
            private double _length;
            private double _cutLength;
            private double _extraLength;
            private double _tolerance;
            private BoltGroup.BoltHoleTypeEnum _holeType;
            private BoltGroup.BoltPlainHoleTypeEnum _plainHoleType;
            private double _slottedHoleX;
            private double _slottedHoleY;
            private double _slotOffsetX;
            private double _slotOffsetY;
            private BoltGroup.BoltRotateSlotsEnum _rotateSlots;
            private bool[] _flags = Array.Empty<bool>();
            private Position.PlaneEnum _plane;
            private double _planeOffset;
            private Position.DepthEnum _depth;
            private double _depthOffset;
            private Position.RotationEnum _rotation;
            private double _rotationOffset;

            public static BoltProperties Capture(BoltGroup bolts) => new()
            {
                _boltSize = bolts.BoltSize,
                _boltStandard = bolts.BoltStandard,
                _boltType = bolts.BoltType,
                _threadInMaterial = bolts.ThreadInMaterial,
                _length = bolts.Length,
                _cutLength = bolts.CutLength,
                _extraLength = bolts.ExtraLength,
                _tolerance = bolts.Tolerance,
                _holeType = bolts.HoleType,
                _plainHoleType = bolts.PlainHoleType,
                _slottedHoleX = bolts.SlottedHoleX,
                _slottedHoleY = bolts.SlottedHoleY,
                _slotOffsetX = bolts.SlotOffsetX,
                _slotOffsetY = bolts.SlotOffsetY,
                _rotateSlots = bolts.RotateSlots,
                _flags = new[]
                {
                    bolts.Washer1, bolts.Washer2, bolts.Washer3, bolts.Nut1, bolts.Nut2,
                    bolts.Bolt, bolts.Hole1, bolts.Hole2, bolts.Hole3, bolts.Hole4, bolts.Hole5,
                    bolts.ConnectAssemblies,
                },
                _plane = bolts.Position.Plane,
                _planeOffset = bolts.Position.PlaneOffset,
                _depth = bolts.Position.Depth,
                _depthOffset = bolts.Position.DepthOffset,
                _rotation = bolts.Position.Rotation,
                _rotationOffset = bolts.Position.RotationOffset,
            };

            public void Restore(BoltGroup bolts)
            {
                bolts.BoltSize = _boltSize;
                bolts.BoltStandard = _boltStandard;
                bolts.BoltType = _boltType;
                bolts.ThreadInMaterial = _threadInMaterial;
                bolts.Length = _length;
                bolts.CutLength = _cutLength;
                bolts.ExtraLength = _extraLength;
                bolts.Tolerance = _tolerance;
                bolts.HoleType = _holeType;
                bolts.PlainHoleType = _plainHoleType;
                bolts.SlottedHoleX = _slottedHoleX;
                bolts.SlottedHoleY = _slottedHoleY;
                bolts.SlotOffsetX = _slotOffsetX;
                bolts.SlotOffsetY = _slotOffsetY;
                bolts.RotateSlots = _rotateSlots;
                bolts.Washer1 = _flags[0];
                bolts.Washer2 = _flags[1];
                bolts.Washer3 = _flags[2];
                bolts.Nut1 = _flags[3];
                bolts.Nut2 = _flags[4];
                bolts.Bolt = _flags[5];
                bolts.Hole1 = _flags[6];
                bolts.Hole2 = _flags[7];
                bolts.Hole3 = _flags[8];
                bolts.Hole4 = _flags[9];
                bolts.Hole5 = _flags[10];
                bolts.ConnectAssemblies = _flags[11];
                bolts.Position.Plane = _plane;
                bolts.Position.PlaneOffset = _planeOffset;
                bolts.Position.Depth = _depth;
                bolts.Position.DepthOffset = _depthOffset;
                bolts.Position.Rotation = _rotation;
                bolts.Position.RotationOffset = _rotationOffset;
            }
        }

        private sealed class BoltFrame
        {
            private BoltFrame(Point origin, Vector axisX, Vector axisY, Vector axisZ)
            {
                Origin = origin;
                AxisX = axisX;
                AxisY = axisY;
                AxisZ = axisZ;
                TransformationPlane = new TransformationPlane(new CoordinateSystem(origin, axisX, axisY));
            }

            public Point Origin { get; }
            public Vector AxisX { get; }
            public Vector AxisY { get; }
            public Vector AxisZ { get; }
            public TransformationPlane TransformationPlane { get; }

            public static BoltFrame Create(TeklaPlane3 frame)
            {
                var axisX = Normalize(Vector(frame.AxisX));
                var axisY = Normalize(Vector(frame.AxisY));
                var axisZ = Normalize(Vector(frame.AxisZ));
                if (Math.Abs(Dot(axisX, axisY)) > 1e-7 || Dot(Normalize(Cross(axisX, axisY)), axisZ) < 1 - 1e-7)
                {
                    throw new TeklaNativeExecutionException(
                        "TEKLA_PLAN_BOLT_FRAME_INVALID",
                        "Bolt-group frame must be a right-handed orthonormal frame.");
                }
                return new BoltFrame(Point(frame.Origin), axisX, axisY, axisZ);
            }
        }

        private static BoltGroup CreateNativePattern(string patternKind)
            => string.Equals(patternKind, "points", StringComparison.Ordinal)
                ? new BoltXYList()
                : new BoltArray();

        private static void ConfigurePattern(BoltGroup bolts, TeklaBoltPatternSpec pattern, bool created)
        {
            var expectedX = ExpectedXDistances(pattern);
            var expectedY = ExpectedYDistances(pattern);
            if (bolts is BoltArray array)
            {
                ClearDistances(array);
                AddArrayDistances(array, expectedX, expectedY, created);
                return;
            }
            if (bolts is not BoltXYList list)
                throw new TeklaNativeExecutionException("TEKLA_PLAN_BOLT_PATTERN_NATIVE_INVALID", $"Unsupported native pattern type '{bolts.GetType().Name}'.");
            if (created)
            {
                foreach (var point in pattern.Points)
                {
                    Require(list.AddBoltDistX(point.X), "add point X");
                    Require(list.AddBoltDistY(point.Y), "add point Y");
                }
                return;
            }
            if (!DistancesEqual(ReadXDistances(list), expectedX) || !DistancesEqual(ReadYDistances(list), expectedY))
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_BOLT_POINTS_MODIFY_UNSUPPORTED",
                    "Tekla 2025 exposes no in-place remove/set API for BoltXYList points; changing an existing points pattern is blocked to preserve native identity.");
            }
        }

        private static void ConfigureCommon(BoltGroup bolts, Part target, Part participant, TeklaCreateBoltGroupPayload payload)
        {
            bolts.PartToBeBolted = target;
            bolts.PartToBoltTo = participant;
            bolts.FirstPosition = new Point(0, 0, 0);
            bolts.SecondPosition = new Point(100, 0, 0);
            bolts.BoltSize = payload.DiameterMm;
            bolts.BoltStandard = payload.BoltStandard;
            bolts.BoltType = BoltType(payload.BoltType);
            bolts.ThreadInMaterial = BoltGroup.BoltThreadInMaterialEnum.THREAD_IN_MATERIAL_YES;
            bolts.Length = 0;
            bolts.CutLength = ThroughCutLengthMm;
            bolts.ExtraLength = 0;
            bolts.Tolerance = payload.ToleranceMm;
            bolts.HoleType = BoltGroup.BoltHoleTypeEnum.HOLE_TYPE_SLOTTED;
            bolts.PlainHoleType = BoltGroup.BoltPlainHoleTypeEnum.HOLE_TYPE_THROUGH;
            bolts.SlottedHoleX = 0;
            bolts.SlottedHoleY = 0;
            bolts.SlotOffsetX = 0;
            bolts.SlotOffsetY = 0;
            bolts.RotateSlots = BoltGroup.BoltRotateSlotsEnum.ROTATE_SLOTS_PARALLEL;
            bolts.Washer1 = true;
            bolts.Washer2 = false;
            bolts.Washer3 = false;
            bolts.Nut1 = true;
            bolts.Nut2 = false;
            bolts.Bolt = true;
            bolts.Hole1 = payload.CreateHoles;
            bolts.Hole2 = payload.CreateHoles;
            bolts.Hole3 = false;
            bolts.Hole4 = false;
            bolts.Hole5 = false;
            bolts.ConnectAssemblies = false;
            bolts.Position.Plane = Position.PlaneEnum.MIDDLE;
            bolts.Position.PlaneOffset = 0;
            bolts.Position.Depth = Position.DepthEnum.MIDDLE;
            bolts.Position.DepthOffset = 0;
            bolts.Position.Rotation = Position.RotationEnum.FRONT;
            bolts.Position.RotationOffset = 0;
        }

        private static void VerifyNativePattern(BoltGroup bolts, TeklaBoltPatternSpec pattern)
        {
            var expectedX = ExpectedXDistances(pattern);
            var expectedY = ExpectedYDistances(pattern);
            var actualX = ReadXDistances(bolts);
            var actualY = ReadYDistances(bolts);
            if (!DistancesEqual(actualX, expectedX) || !DistancesEqual(actualY, expectedY))
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_READBACK_MISMATCH",
                    $"Native bolt-group pattern distances do not match the plan: " +
                    $"X actual=[{string.Join(",", actualX.Select(FormatDistance))}] expected=[{string.Join(",", expectedX.Select(FormatDistance))}]; " +
                    $"Y actual=[{string.Join(",", actualY.Select(FormatDistance))}] expected=[{string.Join(",", expectedY.Select(FormatDistance))}].");
            }
        }

        private static string FormatDistance(double value)
            => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        private static double[] ExpectedXDistances(TeklaBoltPatternSpec pattern)
        {
            if (pattern.Kind == "single") return Array.Empty<double>();
            if (pattern.Kind == "linear") return RepeatedGaps(pattern.Count!.Value, pattern.SpacingMm!.Value);
            if (pattern.Kind == "grid") return RepeatedGaps(pattern.CountX!.Value, pattern.SpacingXmm!.Value);
            return pattern.Points.Select(static point => point.X).ToArray();
        }

        private static double[] ExpectedYDistances(TeklaBoltPatternSpec pattern)
        {
            if (pattern.Kind is "single" or "linear") return Array.Empty<double>();
            if (pattern.Kind == "grid") return RepeatedGaps(pattern.CountY!.Value, pattern.SpacingYmm!.Value);
            return pattern.Points.Select(static point => point.Y).ToArray();
        }

        private static double[] RepeatedGaps(int count, double spacing)
        {
            var result = new double[Math.Max(0, count - 1)];
            for (var index = 0; index < result.Length; index++) result[index] = spacing;
            return result;
        }

        private static void AddArrayDistances(
            BoltArray array,
            IReadOnlyCollection<double> distancesX,
            IReadOnlyCollection<double> distancesY,
            bool created)
        {
            foreach (var value in distancesX) Require(array.AddBoltDistX(value), "add X distance");
            foreach (var value in distancesY) Require(array.AddBoltDistY(value), "add Y distance");
            if (!created) return;
            if (distancesX.Count == 0) Require(array.AddBoltDistX(0), "add bootstrap X distance");
            if (distancesY.Count == 0) Require(array.AddBoltDistY(0), "add bootstrap Y distance");
        }

        private static IEnumerable<Point> ExpectedWorldPositions(TeklaBoltPatternSpec pattern, BoltFrame frame)
        {
            if (pattern.Kind == "points")
            {
                foreach (var point in pattern.Points)
                    yield return World(frame, point.X, point.Y);
                yield break;
            }
            if (pattern.Kind == "single")
            {
                yield return frame.Origin;
                yield break;
            }
            if (pattern.Kind == "linear")
            {
                for (var x = 0; x < pattern.Count!.Value; x++)
                    yield return World(frame, x * pattern.SpacingMm!.Value, 0);
                yield break;
            }
            var centerY = (pattern.CountY!.Value - 1) * pattern.SpacingYmm!.Value / 2;
            for (var y = 0; y < pattern.CountY.Value; y++)
            for (var x = 0; x < pattern.CountX!.Value; x++)
                yield return World(frame, x * pattern.SpacingXmm!.Value, (y * pattern.SpacingYmm.Value) - centerY);
        }

        private static Point World(BoltFrame frame, double x, double y)
            => new(
                frame.Origin.X + (frame.AxisX.X * x) + (frame.AxisY.X * y),
                frame.Origin.Y + (frame.AxisX.Y * x) + (frame.AxisY.Y * y),
                frame.Origin.Z + (frame.AxisX.Z * x) + (frame.AxisY.Z * y));

        private static double[] ReadXDistances(BoltGroup bolts)
            => bolts switch
            {
                BoltArray array => Enumerable.Range(0, array.GetBoltDistXCount()).Select(array.GetBoltDistX).ToArray(),
                BoltXYList list => Enumerable.Range(0, list.GetBoltDistXCount()).Select(list.GetBoltDistX).ToArray(),
                _ => Array.Empty<double>(),
            };

        private static double[] ReadYDistances(BoltGroup bolts)
            => bolts switch
            {
                BoltArray array => Enumerable.Range(0, array.GetBoltDistYCount()).Select(array.GetBoltDistY).ToArray(),
                BoltXYList list => Enumerable.Range(0, list.GetBoltDistYCount()).Select(list.GetBoltDistY).ToArray(),
                _ => Array.Empty<double>(),
            };

        private static bool DistancesEqual(IReadOnlyList<double> actual, IReadOnlyList<double> expected)
        {
            if (actual.Count != expected.Count) return false;
            for (var index = 0; index < actual.Count; index++)
                if (Math.Abs(actual[index] - expected[index]) > 1e-3) return false;
            return true;
        }

        private static void ClearDistances(BoltArray bolts)
        {
            for (var index = bolts.GetBoltDistXCount() - 1; index >= 0; index--) Require(bolts.RemoveBoltDistX(index), "remove X distance");
            for (var index = bolts.GetBoltDistYCount() - 1; index >= 0; index--) Require(bolts.RemoveBoltDistY(index), "remove Y distance");
        }

        private static BoltGroup.BoltTypeEnum BoltType(string value)
            => value == "shop" ? BoltGroup.BoltTypeEnum.BOLT_TYPE_WORKSHOP : BoltGroup.BoltTypeEnum.BOLT_TYPE_SITE;

        private static bool Insert(BoltGroup bolts)
            => bolts switch { BoltArray array => array.Insert(), BoltXYList list => list.Insert(), _ => false };
        private static bool Modify(BoltGroup bolts)
            => bolts switch { BoltArray array => array.Modify(), BoltXYList list => list.Modify(), _ => false };
        private static bool Select(BoltGroup bolts)
            => bolts switch { BoltArray array => array.Select(), BoltXYList list => list.Select(), _ => false };
        private static bool Delete(BoltGroup bolts)
            => bolts switch { BoltArray array => array.Delete(), BoltXYList list => list.Delete(), _ => false };

        private static void StampOwnership(ModelObject target, string externalObjectId, string componentType, int schemaVersion, string operationId)
        {
            RequireUda(target.SetUserProperty(StructuraServiceUdas.ExternalObjectId, externalObjectId), StructuraServiceUdas.ExternalObjectId);
            RequireUda(target.SetUserProperty(StructuraServiceUdas.ComponentType, componentType), StructuraServiceUdas.ComponentType);
            RequireUda(target.SetUserProperty(StructuraServiceUdas.SchemaVersion, schemaVersion), StructuraServiceUdas.SchemaVersion);
            RequireUda(target.SetUserProperty(StructuraServiceUdas.LastOperationId, operationId), StructuraServiceUdas.LastOperationId);
        }

        private static void RequireUda(bool result, string name)
        {
            if (!result) throw new TeklaNativeExecutionException("TEKLA_PLAN_UDA_WRITE_FAILED", $"Failed to write UDA '{name}'.");
        }

        private static void Require(bool result, string role)
        {
            if (!result) throw new TeklaNativeExecutionException("TEKLA_PLAN_BOLT_API_FAILED", $"Tekla bolt-group API operation '{role}' returned false.");
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
                throw new TeklaNativeExecutionException("TEKLA_PLAN_WORK_PLANE_SET_FAILED", "Tekla rejected the requested bolt-group work plane.");
            Exception? primary = null;
            try { action(); }
            catch (Exception exception) { primary = exception; throw; }
            finally
            {
                if (!handler.SetCurrentTransformationPlane(previous) && primary is null)
                    throw new TeklaNativeExecutionException("TEKLA_PLAN_WORK_PLANE_RESTORE_FAILED", "Tekla rejected restoration of the previous work plane.");
            }
        }

        private static T InPlane<T>(Model model, TransformationPlane plane, Func<T> action)
        {
            T? result = default;
            InPlane(model, plane, (Action)(() => result = action()));
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
                throw new TeklaNativeExecutionException("TEKLA_PLAN_BOLT_FRAME_INVALID", "Bolt-group frame axis cannot be zero.");
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
