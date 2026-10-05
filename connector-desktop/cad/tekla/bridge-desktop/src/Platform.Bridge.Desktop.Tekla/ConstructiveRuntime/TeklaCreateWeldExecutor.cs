#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Platform.Bridge.Desktop.Tekla.ComponentRuntime;
using Platform.Contracts.TeklaPlan;
using Tekla.Structures.Model;

namespace Platform.Bridge.Desktop.Tekla.ConstructiveRuntime
{
    public sealed class TeklaCreateWeldExecutor : ITeklaNativeCommandExecutor
    {
        private const string ExecutorId = "tekla-native.create-weld.v1";
        private const string ComponentType = "ConstructiveWeldV1";

        public TeklaPlanExecutorRegistration Registration { get; } = new(
            ExecutorId,
            "create-weld",
            new[] { "weld", "udaStamp" },
            applyReady: true);

        public ITeklaPreparedMutation<TeklaNativeCommandReadback> Prepare(
            Model model,
            TeklaPlanDocument plan,
            TeklaPlanCommand command,
            string operationId)
        {
            var parsed = TeklaPlanPayloads.ParseCreateWeld(command);
            if (!parsed.Success || parsed.Value is null)
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_WELD_PAYLOAD_INVALID",
                    string.Join(" ", parsed.Diagnostics.Select(static item => item.Message)));
            }

            TeklaPlanNativeTargetResolver.ValidateWeldPreparation(plan, command, parsed.Value);
            var target = TeklaPlanNativeTargetResolver.ResolvePartCommand(plan, command, parsed.Value.Target);
            var participant = TeklaPlanNativeTargetResolver.ResolvePartCommand(plan, command, parsed.Value.Participant);
            if (string.Equals(target.ExternalObjectId, participant.ExternalObjectId, StringComparison.Ordinal))
            {
                throw Failure(
                    command,
                    "TEKLA_PLAN_WELD_PARTICIPANTS_SAME",
                    "Target and participant resolve to the same native Part ownership id.");
            }

            var externalObjectId = TeklaPlanOwnershipIdentity.Create(plan, command);
            return new PreparedWeldMutation(
                model,
                plan,
                command,
                parsed.Value,
                operationId,
                externalObjectId,
                target.ExternalObjectId,
                participant.ExternalObjectId,
                FindExisting(model, externalObjectId));
        }

        private static Weld? FindExisting(Model model, string externalObjectId)
        {
            var matches = new List<BaseWeld>();
            var enumerator = model.GetModelObjectSelector().GetAllObjects();
            while (enumerator.MoveNext())
            {
                if (enumerator.Current is not BaseWeld weld) continue;
                var candidate = string.Empty;
                if (!weld.GetUserProperty(StructuraServiceUdas.ExternalObjectId, ref candidate)) continue;
                if (string.Equals(candidate, externalObjectId, StringComparison.Ordinal)) matches.Add(weld);
            }

            if (matches.Count > 1)
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_NATIVE_IDENTITY_DUPLICATE",
                    $"Weld has {matches.Count} instances with ownership id '{externalObjectId}'.");
            }

            var existing = matches.SingleOrDefault();
            if (existing is null) return null;
            if (existing is not Weld simpleWeld)
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_NATIVE_OWNERSHIP_CONFLICT",
                    $"Ownership id '{externalObjectId}' belongs to '{existing.GetType().Name}', not a simple Weld.");
            }

            var componentType = string.Empty;
            simpleWeld.GetUserProperty(StructuraServiceUdas.ComponentType, ref componentType);
            if (!string.Equals(componentType, ComponentType, StringComparison.Ordinal))
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_NATIVE_OWNERSHIP_CONFLICT",
                    $"Ownership id '{externalObjectId}' belongs to component type '{componentType}', not '{ComponentType}'.");
            }
            return simpleWeld;
        }

        private sealed class PreparedWeldMutation : ITeklaPreparedMutation<TeklaNativeCommandReadback>
        {
            private const double Tolerance = 1e-3;
            private readonly Model _model;
            private readonly TeklaPlanDocument _plan;
            private readonly TeklaPlanCommand _command;
            private readonly TeklaCreateWeldPayload _payload;
            private readonly string _operationId;
            private readonly string _externalObjectId;
            private readonly string _targetExternalObjectId;
            private readonly string _participantExternalObjectId;
            private readonly Weld _weld;
            private readonly WeldSnapshot? _snapshot;
            private readonly bool _created;
            private bool _inserted;
            private bool _applied;

            public PreparedWeldMutation(
                Model model,
                TeklaPlanDocument plan,
                TeklaPlanCommand command,
                TeklaCreateWeldPayload payload,
                string operationId,
                string externalObjectId,
                string targetExternalObjectId,
                string participantExternalObjectId,
                Weld? existing)
            {
                _model = model;
                _plan = plan;
                _command = command;
                _payload = payload;
                _operationId = operationId;
                _externalObjectId = externalObjectId;
                _targetExternalObjectId = targetExternalObjectId;
                _participantExternalObjectId = participantExternalObjectId;
                _created = existing is null;
                _weld = existing ?? new Weld();
                _snapshot = existing is null ? null : WeldSnapshot.Capture(existing);
            }

            public string CommandId => _command.CommandId;

            public void Apply()
            {
                try
                {
                    var target = TeklaPlanNativeTargetResolver.FindPart(_model, _command, _targetExternalObjectId);
                    var participant = TeklaPlanNativeTargetResolver.FindPart(_model, _command, _participantExternalObjectId);
                    if (SameObject(target, participant))
                        throw Failure("TEKLA_PLAN_WELD_PARTICIPANTS_SAME", "Target and participant resolve to the same native Part.");

                    ApplyPayload(_weld, target, participant, _payload);
                    if (_created)
                    {
                        if (!_weld.Insert())
                            throw Failure("TEKLA_PLAN_WELD_INSERT_FAILED", "Weld.Insert() returned false.");
                        _inserted = true;
                    }

                    StampOwnership(_weld, _externalObjectId, ComponentType, _plan.SchemaVersion, _operationId);
                    if (!_weld.Modify())
                        throw Failure("TEKLA_PLAN_WELD_MODIFY_FAILED", "Weld.Modify() returned false.");
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
                    throw Failure("TEKLA_PLAN_WELD_NOT_APPLIED", "Readback was requested before apply completed.");

                _weld.Select();
                var target = _weld.MainObject as Part ??
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native Weld has no main Part.");
                var participant = _weld.SecondaryObject as Part ??
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native Weld has no secondary Part.");
                var targetExternalObjectId = ReadStringUda(target, StructuraServiceUdas.ExternalObjectId, "target");
                var participantExternalObjectId = ReadStringUda(participant, StructuraServiceUdas.ExternalObjectId, "participant");
                VerifyEqual("target ownership id", targetExternalObjectId, _targetExternalObjectId);
                VerifyEqual("participant ownership id", participantExternalObjectId, _participantExternalObjectId);
                VerifyPayload();

                var externalObjectId = ReadStringUda(_weld, StructuraServiceUdas.ExternalObjectId, "weld");
                var componentType = ReadStringUda(_weld, StructuraServiceUdas.ComponentType, "weld");
                var lastOperationId = ReadStringUda(_weld, StructuraServiceUdas.LastOperationId, "weld");
                var schemaVersion = ReadIntUda(_weld, StructuraServiceUdas.SchemaVersion);
                VerifyEqual("ownership id", externalObjectId, _externalObjectId);
                VerifyEqual("component type", componentType, ComponentType);
                VerifyEqual("last operation id", lastOperationId, _operationId);
                if (schemaVersion != _plan.SchemaVersion)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native schema version does not match the plan.");

                var guid = _model.GetGUIDByIdentifier(_weld.Identifier) ?? string.Empty;
                var targetGuid = _model.GetGUIDByIdentifier(target.Identifier) ?? string.Empty;
                var participantGuid = _model.GetGUIDByIdentifier(participant.Identifier) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(guid) || _weld.Identifier.ID <= 0)
                    throw Failure("TEKLA_PLAN_READBACK_IDENTITY_MISSING", "Tekla did not return a persistent Weld identity.");
                if (string.IsNullOrWhiteSpace(targetGuid) || target.Identifier.ID <= 0 ||
                    string.IsNullOrWhiteSpace(participantGuid) || participant.Identifier.ID <= 0)
                    throw Failure("TEKLA_PLAN_READBACK_IDENTITY_MISSING", "Tekla did not return persistent participant identities.");

                return new TeklaNativeCommandReadback
                {
                    CommandId = _command.CommandId,
                    CommandKind = _command.Kind,
                    Action = _created ? "created" : "modified",
                    ExternalObjectId = externalObjectId,
                    TeklaGuid = guid,
                    TeklaId = _weld.Identifier.ID,
                    TargetExternalObjectId = targetExternalObjectId,
                    TargetTeklaGuid = targetGuid,
                    ParticipantExternalObjectIds = new[] { participantExternalObjectId },
                    ParticipantTeklaGuids = new[] { participantGuid },
                    WeldType = _payload.WeldType,
                    WeldSizeMm = _payload.SizeMm,
                    WeldSide = _payload.Side,
                    ShopSite = _payload.ShopSite,
                    ComponentType = componentType,
                    SchemaVersion = schemaVersion,
                    LastOperationId = lastOperationId,
                };
            }

            public void Restore()
            {
                if (_created)
                {
                    if (_inserted && !_weld.Delete())
                        throw Failure("TEKLA_PLAN_WELD_ROLLBACK_DELETE_FAILED", "Weld.Delete() returned false.");
                    _inserted = false;
                    _applied = false;
                    return;
                }

                if (_snapshot is null)
                    throw Failure("TEKLA_PLAN_WELD_ROLLBACK_STATE_MISSING", "Existing Weld snapshot is missing.");
                _snapshot.Restore(_weld);
                if (!_weld.Modify())
                    throw Failure("TEKLA_PLAN_WELD_ROLLBACK_MODIFY_FAILED", "Weld.Modify() returned false during rollback.");
                _applied = false;
            }

            private void RestorePartialApply()
            {
                try
                {
                    if (_created)
                    {
                        if (_inserted) _weld.Delete();
                        _inserted = false;
                    }
                    else if (_snapshot is not null)
                    {
                        _snapshot.Restore(_weld);
                        _weld.Modify();
                    }
                }
                finally
                {
                    _applied = false;
                }
            }

            private void VerifyPayload()
            {
                var above = _payload.Side is "other" or "both";
                var below = _payload.Side is "arrow" or "both";
                VerifyWeldSide("above", _weld.TypeAbove, _weld.SizeAbove, above);
                VerifyWeldSide("below", _weld.TypeBelow, _weld.SizeBelow, below);
                if (_weld.ShopWeld != (_payload.ShopSite == "shop"))
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native Weld shop/site flag does not match the plan.");
                if (_weld.IntermittentType != BaseWeld.WeldIntermittentTypeEnum.CONTINUOUS)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native Weld is not continuous.");
                if (_weld.ConnectAssemblies)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native Weld unexpectedly connects assemblies.");
            }

            private void VerifyWeldSide(string role, BaseWeld.WeldTypeEnum type, double size, bool enabled)
            {
                var expectedType = enabled
                    ? BaseWeld.WeldTypeEnum.WELD_TYPE_FILLET
                    : BaseWeld.WeldTypeEnum.WELD_TYPE_NONE;
                var expectedSize = enabled ? _payload.SizeMm : 0d;
                if (type != expectedType || Math.Abs(size - expectedSize) > Tolerance)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native Weld {role} type or size does not match the plan.");
            }

            private void VerifyEqual(string role, string actual, string expected)
            {
                if (!string.Equals(actual?.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase))
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native Weld {role} does not match the plan.");
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
                    throw Failure("TEKLA_PLAN_READBACK_UDA_MISSING", $"Native Weld UDA '{name}' is missing.");
                return value;
            }

            private TeklaNativeExecutionException Failure(string code, string message)
                => new(code, $"Command '{_command.CommandId}': {message}");
        }

        private sealed class WeldSnapshot
        {
            private readonly ModelObject _main;
            private readonly ModelObject _secondary;
            private readonly BaseWeld.WeldTypeEnum _typeAbove;
            private readonly BaseWeld.WeldTypeEnum _typeBelow;
            private readonly double _sizeAbove;
            private readonly double _sizeBelow;
            private readonly bool _shopWeld;
            private readonly bool _connectAssemblies;
            private readonly bool _aroundWeld;
            private readonly BaseWeld.WeldPreparationTypeEnum _preparation;
            private readonly BaseWeld.WeldIntermittentTypeEnum _intermittentType;
            private readonly string _externalObjectId;
            private readonly string _componentType;
            private readonly int _schemaVersion;
            private readonly string _lastOperationId;

            private WeldSnapshot(Weld weld)
            {
                weld.Select();
                _main = weld.MainObject ?? throw new TeklaNativeExecutionException("TEKLA_PLAN_WELD_EXISTING_MAIN_MISSING", "Existing Constructive Weld has no main object.");
                _secondary = weld.SecondaryObject ?? throw new TeklaNativeExecutionException("TEKLA_PLAN_WELD_EXISTING_SECONDARY_MISSING", "Existing Constructive Weld has no secondary object.");
                _typeAbove = weld.TypeAbove;
                _typeBelow = weld.TypeBelow;
                _sizeAbove = weld.SizeAbove;
                _sizeBelow = weld.SizeBelow;
                _shopWeld = weld.ShopWeld;
                _connectAssemblies = weld.ConnectAssemblies;
                _aroundWeld = weld.AroundWeld;
                _preparation = weld.Preparation;
                _intermittentType = weld.IntermittentType;
                _externalObjectId = ReadString(weld, StructuraServiceUdas.ExternalObjectId);
                _componentType = ReadString(weld, StructuraServiceUdas.ComponentType);
                _schemaVersion = ReadInt(weld, StructuraServiceUdas.SchemaVersion);
                _lastOperationId = ReadString(weld, StructuraServiceUdas.LastOperationId);
            }

            public static WeldSnapshot Capture(Weld weld) => new(weld);

            public void Restore(Weld weld)
            {
                weld.MainObject = _main;
                weld.SecondaryObject = _secondary;
                weld.TypeAbove = _typeAbove;
                weld.TypeBelow = _typeBelow;
                weld.SizeAbove = _sizeAbove;
                weld.SizeBelow = _sizeBelow;
                weld.ShopWeld = _shopWeld;
                weld.ConnectAssemblies = _connectAssemblies;
                weld.AroundWeld = _aroundWeld;
                weld.Preparation = _preparation;
                weld.IntermittentType = _intermittentType;
                StampOwnership(weld, _externalObjectId, _componentType, _schemaVersion, _lastOperationId);
            }
        }

        private static void ApplyPayload(Weld weld, Part target, Part participant, TeklaCreateWeldPayload payload)
        {
            var above = payload.Side is "other" or "both";
            var below = payload.Side is "arrow" or "both";
            weld.MainObject = target;
            weld.SecondaryObject = participant;
            weld.TypeAbove = above ? BaseWeld.WeldTypeEnum.WELD_TYPE_FILLET : BaseWeld.WeldTypeEnum.WELD_TYPE_NONE;
            weld.TypeBelow = below ? BaseWeld.WeldTypeEnum.WELD_TYPE_FILLET : BaseWeld.WeldTypeEnum.WELD_TYPE_NONE;
            weld.SizeAbove = above ? payload.SizeMm : 0d;
            weld.SizeBelow = below ? payload.SizeMm : 0d;
            weld.Preparation = BaseWeld.WeldPreparationTypeEnum.PREPARATION_NONE;
            weld.ShopWeld = payload.ShopSite == "shop";
            weld.AroundWeld = false;
            weld.IntermittentType = BaseWeld.WeldIntermittentTypeEnum.CONTINUOUS;
            weld.ConnectAssemblies = false;
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
                throw new TeklaNativeExecutionException("TEKLA_PLAN_UDA_STAMP_FAILED", $"SetUserProperty('{name}') returned false.");
        }

        private static bool SameObject(ModelObject first, ModelObject second)
        {
            if (first.Identifier.ID != 0 && second.Identifier.ID != 0)
                return first.Identifier.ID == second.Identifier.ID;
            return first.Identifier.GUID != Guid.Empty && first.Identifier.GUID == second.Identifier.GUID;
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

        private static TeklaNativeExecutionException Failure(TeklaPlanCommand command, string code, string message)
            => new(code, $"Command '{command.CommandId}': {message}");
    }
}
