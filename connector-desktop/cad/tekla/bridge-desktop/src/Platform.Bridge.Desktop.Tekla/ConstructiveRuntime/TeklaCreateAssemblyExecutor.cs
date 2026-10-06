#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Platform.Bridge.Desktop.Tekla.ComponentRuntime;
using Platform.Contracts.TeklaPlan;
using Tekla.Structures.Model;

namespace Platform.Bridge.Desktop.Tekla.ConstructiveRuntime
{
    public sealed class TeklaCreateAssemblyExecutor : ITeklaNativeCommandExecutor
    {
        private const string ExecutorId = "tekla-native.create-assembly.v1";
        private const string ComponentType = "ConstructiveAssemblyV1";

        public TeklaPlanExecutorRegistration Registration { get; } = new(
            ExecutorId,
            "create-assembly",
            new[] { "assembly", "udaStamp" },
            applyReady: true);

        public ITeklaPreparedMutation<TeklaNativeCommandReadback> Prepare(
            Model model,
            TeklaPlanDocument plan,
            TeklaPlanCommand command,
            string operationId)
        {
            var parsed = TeklaPlanPayloads.ParseCreateAssembly(command);
            if (!parsed.Success || parsed.Value is null)
            {
                throw Failure(
                    command,
                    "TEKLA_PLAN_ASSEMBLY_PAYLOAD_INVALID",
                    string.Join(" ", parsed.Diagnostics.Select(static item => item.Message)));
            }
            if (parsed.Value.Properties.Count > 0)
            {
                throw Failure(
                    command,
                    "TEKLA_PLAN_ASSEMBLY_PROPERTIES_UNSUPPORTED",
                    "Native Assembly v1 does not map arbitrary constructive properties to Tekla attributes.");
            }

            var mainTarget = TeklaPlanNativeTargetResolver.ResolvePartCommand(
                plan,
                command,
                parsed.Value.MainElementId);
            var secondaryTargets = parsed.Value.SecondaryElementIds
                .Select(elementId => TeklaPlanNativeTargetResolver.ResolvePartCommand(plan, command, elementId))
                .ToArray();
            foreach (var featureId in parsed.Value.FeatureIds)
                TeklaPlanNativeTargetResolver.ResolveFeatureCommands(plan, command, featureId);

            var memberOwnershipIds = new[] { mainTarget.ExternalObjectId }
                .Concat(secondaryTargets.Select(static target => target.ExternalObjectId))
                .ToArray();
            if (memberOwnershipIds.Distinct(StringComparer.Ordinal).Count() != memberOwnershipIds.Length)
            {
                throw Failure(
                    command,
                    "TEKLA_PLAN_ASSEMBLY_MEMBER_IDENTITY_DUPLICATE",
                    "Assembly members do not resolve to distinct native Part ownership ids.");
            }

            var mainPart = TeklaPlanNativeTargetResolver.FindPart(model, command, mainTarget.ExternalObjectId);
            var secondaryParts = secondaryTargets
                .Select(target => TeklaPlanNativeTargetResolver.FindPart(model, command, target.ExternalObjectId))
                .ToArray();
            var mainAssembly = SelectAssembly(mainPart, command, "main Part");
            RequireMainPart(model, command, mainAssembly, mainPart);

            var externalObjectId = TeklaPlanOwnershipIdentity.Create(plan, command);
            var existing = FindExisting(model, command, externalObjectId);
            var created = existing is null;
            if (existing is not null && !SameObject(existing, mainAssembly))
            {
                throw Failure(
                    command,
                    "TEKLA_PLAN_ASSEMBLY_MAIN_OWNERSHIP_CONFLICT",
                    "The owned Assembly does not belong to the requested main Part.");
            }
            if (existing is null)
            {
                var currentOwnershipId = ReadString(mainAssembly, StructuraServiceUdas.ExternalObjectId);
                if (!string.IsNullOrWhiteSpace(currentOwnershipId))
                {
                    throw Failure(
                        command,
                        "TEKLA_PLAN_NATIVE_OWNERSHIP_CONFLICT",
                        $"Main Part Assembly already has ownership id '{currentOwnershipId}'.");
                }
                if (PartSecondaries(mainAssembly).Count > 0)
                {
                    throw Failure(
                        command,
                        "TEKLA_PLAN_ASSEMBLY_UNMANAGED_MEMBERSHIP_CONFLICT",
                        "An unmanaged multi-Part Assembly cannot be adopted implicitly.");
                }
            }

            foreach (var secondaryPart in secondaryParts)
                ValidateSecondaryOwnership(model, command, mainAssembly, secondaryPart);

            return new PreparedAssemblyMutation(
                model,
                plan,
                command,
                parsed.Value,
                operationId,
                externalObjectId,
                mainTarget.ExternalObjectId,
                secondaryTargets.Select(static target => target.ExternalObjectId).ToArray(),
                GuidOf(model, mainAssembly, command, "Assembly"),
                AssemblySnapshot.Capture(model, command, mainAssembly),
                created);
        }

        private static Assembly? FindExisting(Model model, TeklaPlanCommand command, string externalObjectId)
        {
            var matches = new List<Assembly>();
            var enumerator = model.GetModelObjectSelector()
                .GetAllObjectsWithType(ModelObject.ModelObjectEnum.ASSEMBLY);
            while (enumerator.MoveNext())
            {
                if (enumerator.Current is not Assembly assembly) continue;
                var candidate = ReadString(assembly, StructuraServiceUdas.ExternalObjectId);
                if (string.Equals(candidate, externalObjectId, StringComparison.Ordinal)) matches.Add(assembly);
            }

            if (matches.Count > 1)
            {
                throw Failure(
                    command,
                    "TEKLA_PLAN_NATIVE_IDENTITY_DUPLICATE",
                    $"Assembly ownership id '{externalObjectId}' belongs to {matches.Count} objects.");
            }
            var existing = matches.SingleOrDefault();
            if (existing is null) return null;
            var componentType = ReadString(existing, StructuraServiceUdas.ComponentType);
            if (!string.Equals(componentType, ComponentType, StringComparison.Ordinal))
            {
                throw Failure(
                    command,
                    "TEKLA_PLAN_NATIVE_OWNERSHIP_CONFLICT",
                    $"Ownership id '{externalObjectId}' belongs to component type '{componentType}', not '{ComponentType}'.");
            }
            return existing;
        }

        private sealed class PreparedAssemblyMutation : ITeklaPreparedMutation<TeklaNativeCommandReadback>
        {
            private readonly Model _model;
            private readonly TeklaPlanDocument _plan;
            private readonly TeklaPlanCommand _command;
            private readonly TeklaCreateAssemblyPayload _payload;
            private readonly string _operationId;
            private readonly string _externalObjectId;
            private readonly string _mainExternalObjectId;
            private readonly string[] _secondaryExternalObjectIds;
            private readonly string _assemblyGuid;
            private readonly AssemblySnapshot _snapshot;
            private readonly bool _created;
            private bool _applied;

            public PreparedAssemblyMutation(
                Model model,
                TeklaPlanDocument plan,
                TeklaPlanCommand command,
                TeklaCreateAssemblyPayload payload,
                string operationId,
                string externalObjectId,
                string mainExternalObjectId,
                string[] secondaryExternalObjectIds,
                string assemblyGuid,
                AssemblySnapshot snapshot,
                bool created)
            {
                _model = model;
                _plan = plan;
                _command = command;
                _payload = payload;
                _operationId = operationId;
                _externalObjectId = externalObjectId;
                _mainExternalObjectId = mainExternalObjectId;
                _secondaryExternalObjectIds = secondaryExternalObjectIds;
                _assemblyGuid = assemblyGuid;
                _snapshot = snapshot;
                _created = created;
            }

            public string CommandId => _command.CommandId;

            public void Apply()
            {
                try
                {
                    var assembly = FindAssemblyByGuid(_model, _command, _assemblyGuid);
                    var mainPart = TeklaPlanNativeTargetResolver.FindPart(_model, _command, _mainExternalObjectId);
                    var secondaryParts = _secondaryExternalObjectIds
                        .Select(externalId => TeklaPlanNativeTargetResolver.FindPart(_model, _command, externalId))
                        .ToArray();
                    RequireMainPart(_model, _command, assembly, mainPart);
                    foreach (var secondaryPart in secondaryParts)
                        ValidateSecondaryOwnership(_model, _command, assembly, secondaryPart);

                    ApplyMembership(assembly, mainPart, secondaryParts);
                    assembly.Name = _payload.Name ?? string.Empty;
                    StampOwnership(
                        assembly,
                        _externalObjectId,
                        ComponentType,
                        _plan.SchemaVersion,
                        _operationId);
                    if (!assembly.Modify())
                        throw Failure("TEKLA_PLAN_ASSEMBLY_MODIFY_FAILED", "Assembly.Modify() returned false.");
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
                    throw Failure("TEKLA_PLAN_ASSEMBLY_NOT_APPLIED", "Readback was requested before apply completed.");

                var assembly = FindAssemblyByGuid(_model, _command, _assemblyGuid);
                var mainPart = assembly.GetMainPart() as Part ??
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native Assembly has no main Part.");
                var mainExternalObjectId = ReadRequiredString(
                    mainPart,
                    StructuraServiceUdas.ExternalObjectId,
                    "main Part");
                VerifyEqual("main Part ownership id", mainExternalObjectId, _mainExternalObjectId);

                var secondariesByExternalId = PartSecondaries(assembly)
                    .Select(part => new { Part = part, ExternalId = ReadString(part, StructuraServiceUdas.ExternalObjectId) })
                    .Where(static item => !string.IsNullOrWhiteSpace(item.ExternalId))
                    .ToDictionary(static item => item.ExternalId, static item => item.Part, StringComparer.Ordinal);
                var expectedIds = new HashSet<string>(_secondaryExternalObjectIds, StringComparer.Ordinal);
                var unexpectedGenerated = secondariesByExternalId.Keys
                    .Where(id => !expectedIds.Contains(id))
                    .OrderBy(static id => id, StringComparer.Ordinal)
                    .ToArray();
                if (unexpectedGenerated.Length > 0)
                {
                    throw Failure(
                        "TEKLA_PLAN_READBACK_MISMATCH",
                        $"Native Assembly contains unexpected generated secondary Parts: {string.Join(", ", unexpectedGenerated)}.");
                }

                var secondaryGuids = new List<string>();
                foreach (var expectedId in _secondaryExternalObjectIds)
                {
                    if (!secondariesByExternalId.TryGetValue(expectedId, out var part))
                        throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native Assembly misses secondary Part '{expectedId}'.");
                    secondaryGuids.Add(GuidOf(_model, part, _command, "secondary Part"));
                }

                var externalObjectId = ReadRequiredString(
                    assembly,
                    StructuraServiceUdas.ExternalObjectId,
                    "Assembly");
                var componentType = ReadRequiredString(
                    assembly,
                    StructuraServiceUdas.ComponentType,
                    "Assembly");
                var lastOperationId = ReadRequiredString(
                    assembly,
                    StructuraServiceUdas.LastOperationId,
                    "Assembly");
                var schemaVersion = ReadRequiredInt(assembly, StructuraServiceUdas.SchemaVersion, "Assembly");
                VerifyEqual("ownership id", externalObjectId, _externalObjectId);
                VerifyEqual("component type", componentType, ComponentType);
                VerifyEqual("last operation id", lastOperationId, _operationId);
                VerifyEqual("name", assembly.Name ?? string.Empty, _payload.Name ?? string.Empty);
                if (schemaVersion != _plan.SchemaVersion)
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", "Native Assembly schema version does not match the plan.");

                return new TeklaNativeCommandReadback
                {
                    CommandId = _command.CommandId,
                    CommandKind = _command.Kind,
                    Action = _created ? "created" : "modified",
                    ExternalObjectId = externalObjectId,
                    TeklaGuid = GuidOf(_model, assembly, _command, "Assembly"),
                    TeklaId = assembly.Identifier.ID,
                    MainElementExternalObjectId = mainExternalObjectId,
                    MainElementTeklaGuid = GuidOf(_model, mainPart, _command, "main Part"),
                    SecondaryElementExternalObjectIds = _secondaryExternalObjectIds.ToArray(),
                    SecondaryElementTeklaGuids = secondaryGuids.ToArray(),
                    FeatureIds = _payload.FeatureIds.ToArray(),
                    AssemblyName = assembly.Name ?? string.Empty,
                    ComponentType = componentType,
                    SchemaVersion = schemaVersion,
                    LastOperationId = lastOperationId,
                };
            }

            public void Restore()
            {
                _snapshot.Restore(_model, _command, _assemblyGuid);
                _applied = false;
            }

            private void ApplyMembership(Assembly assembly, Part mainPart, IReadOnlyCollection<Part> desiredSecondaries)
            {
                var desiredGuids = new HashSet<string>(
                    desiredSecondaries.Select(part => GuidOf(_model, part, _command, "secondary Part")),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var existingPart in PartSecondaries(assembly))
                {
                    var existingGuid = GuidOf(_model, existingPart, _command, "secondary Part");
                    var ownershipId = ReadString(existingPart, StructuraServiceUdas.ExternalObjectId);
                    if (!string.IsNullOrWhiteSpace(ownershipId) && !desiredGuids.Contains(existingGuid))
                    {
                        if (!assembly.Remove(existingPart))
                            throw Failure("TEKLA_PLAN_ASSEMBLY_REMOVE_FAILED", $"Assembly.Remove() failed for generated Part '{ownershipId}'.");
                    }
                }

                var currentGuids = new HashSet<string>(
                    PartSecondaries(assembly).Select(part => GuidOf(_model, part, _command, "secondary Part")),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var desiredPart in desiredSecondaries)
                {
                    var guid = GuidOf(_model, desiredPart, _command, "secondary Part");
                    if (currentGuids.Contains(guid)) continue;
                    if (!assembly.Add(desiredPart))
                        throw Failure("TEKLA_PLAN_ASSEMBLY_ADD_FAILED", $"Assembly.Add() failed for Part '{guid}'.");
                    currentGuids.Add(guid);
                }
                if (!assembly.SetMainPart(mainPart))
                    throw Failure("TEKLA_PLAN_ASSEMBLY_MAIN_PART_FAILED", "Assembly.SetMainPart() returned false.");
            }

            private void RestorePartialApply()
            {
                try { _snapshot.Restore(_model, _command, _assemblyGuid); }
                catch { }
                finally { _applied = false; }
            }

            private string ReadRequiredString(ModelObject target, string name, string role)
            {
                var value = string.Empty;
                if (!target.GetUserProperty(name, ref value))
                    throw Failure("TEKLA_PLAN_READBACK_UDA_MISSING", $"Native {role} UDA '{name}' is missing.");
                return value;
            }

            private int ReadRequiredInt(ModelObject target, string name, string role)
            {
                var value = 0;
                if (!target.GetUserProperty(name, ref value))
                    throw Failure("TEKLA_PLAN_READBACK_UDA_MISSING", $"Native {role} UDA '{name}' is missing.");
                return value;
            }

            private void VerifyEqual(string role, string actual, string expected)
            {
                if (!string.Equals(actual?.Trim(), expected.Trim(), StringComparison.Ordinal))
                    throw Failure("TEKLA_PLAN_READBACK_MISMATCH", $"Native Assembly {role} does not match the plan.");
            }

            private TeklaNativeExecutionException Failure(string code, string message)
                => TeklaCreateAssemblyExecutor.Failure(_command, code, message);
        }

        private sealed class AssemblySnapshot
        {
            private readonly string _mainPartGuid;
            private readonly string[] _secondaryPartGuids;
            private readonly string _name;
            private readonly string _externalObjectId;
            private readonly string _componentType;
            private readonly int _schemaVersion;
            private readonly string _lastOperationId;

            private AssemblySnapshot(
                string mainPartGuid,
                string[] secondaryPartGuids,
                string name,
                string externalObjectId,
                string componentType,
                int schemaVersion,
                string lastOperationId)
            {
                _mainPartGuid = mainPartGuid;
                _secondaryPartGuids = secondaryPartGuids;
                _name = name;
                _externalObjectId = externalObjectId;
                _componentType = componentType;
                _schemaVersion = schemaVersion;
                _lastOperationId = lastOperationId;
            }

            public static AssemblySnapshot Capture(Model model, TeklaPlanCommand command, Assembly assembly)
            {
                if (!assembly.Select())
                    throw Failure(command, "TEKLA_PLAN_ASSEMBLY_SELECT_FAILED", "Cannot select Assembly for snapshot.");
                var mainPart = assembly.GetMainPart() as Part ??
                    throw Failure(command, "TEKLA_PLAN_ASSEMBLY_MAIN_PART_MISSING", "Assembly has no main Part.");
                return new AssemblySnapshot(
                    GuidOf(model, mainPart, command, "main Part"),
                    PartSecondaries(assembly)
                        .Select(part => GuidOf(model, part, command, "secondary Part"))
                        .ToArray(),
                    assembly.Name ?? string.Empty,
                    ReadString(assembly, StructuraServiceUdas.ExternalObjectId),
                    ReadString(assembly, StructuraServiceUdas.ComponentType),
                    ReadInt(assembly, StructuraServiceUdas.SchemaVersion),
                    ReadString(assembly, StructuraServiceUdas.LastOperationId));
            }

            public void Restore(Model model, TeklaPlanCommand command, string assemblyGuid)
            {
                var assembly = FindAssemblyByGuid(model, command, assemblyGuid);
                var mainPart = FindPartByGuid(model, command, _mainPartGuid, "snapshot main Part");
                var desiredParts = _secondaryPartGuids
                    .Select(guid => FindPartByGuid(model, command, guid, "snapshot secondary Part"))
                    .ToArray();
                var desiredGuids = new HashSet<string>(_secondaryPartGuids, StringComparer.OrdinalIgnoreCase);

                foreach (var existingPart in PartSecondaries(assembly))
                {
                    var guid = GuidOf(model, existingPart, command, "secondary Part");
                    if (desiredGuids.Contains(guid)) continue;
                    if (!assembly.Remove(existingPart))
                        throw Failure(command, "TEKLA_PLAN_ASSEMBLY_ROLLBACK_REMOVE_FAILED", $"Cannot remove Part '{guid}' during rollback.");
                }

                var currentGuids = new HashSet<string>(
                    PartSecondaries(assembly).Select(part => GuidOf(model, part, command, "secondary Part")),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var desiredPart in desiredParts)
                {
                    var guid = GuidOf(model, desiredPart, command, "secondary Part");
                    if (currentGuids.Contains(guid)) continue;
                    if (!assembly.Add(desiredPart))
                        throw Failure(command, "TEKLA_PLAN_ASSEMBLY_ROLLBACK_ADD_FAILED", $"Cannot restore Part '{guid}' during rollback.");
                }
                if (!assembly.SetMainPart(mainPart))
                    throw Failure(command, "TEKLA_PLAN_ASSEMBLY_ROLLBACK_MAIN_FAILED", "Cannot restore Assembly main Part.");
                assembly.Name = _name;
                StampOwnership(assembly, _externalObjectId, _componentType, _schemaVersion, _lastOperationId);
                if (!assembly.Modify())
                    throw Failure(command, "TEKLA_PLAN_ASSEMBLY_ROLLBACK_MODIFY_FAILED", "Assembly.Modify() returned false during rollback.");
            }
        }

        private static void ValidateSecondaryOwnership(
            Model model,
            TeklaPlanCommand command,
            Assembly mainAssembly,
            Part secondaryPart)
        {
            var secondaryAssembly = SelectAssembly(secondaryPart, command, "secondary Part");
            if (SameObject(mainAssembly, secondaryAssembly)) return;

            var ownershipId = ReadString(secondaryAssembly, StructuraServiceUdas.ExternalObjectId);
            if (!string.IsNullOrWhiteSpace(ownershipId))
            {
                throw Failure(
                    command,
                    "TEKLA_PLAN_ASSEMBLY_SECONDARY_OWNERSHIP_CONFLICT",
                    $"Secondary Part already belongs to managed Assembly '{ownershipId}'.");
            }
            RequireMainPart(model, command, secondaryAssembly, secondaryPart);
            if (PartSecondaries(secondaryAssembly).Count > 0)
            {
                throw Failure(
                    command,
                    "TEKLA_PLAN_ASSEMBLY_SECONDARY_MEMBERSHIP_CONFLICT",
                    "Secondary Part is the main Part of an unmanaged multi-Part Assembly.");
            }
        }

        private static Assembly SelectAssembly(Part part, TeklaPlanCommand command, string role)
        {
            var assembly = part.GetAssembly();
            if (assembly is null || !assembly.Select())
                throw Failure(command, "TEKLA_PLAN_ASSEMBLY_SELECT_FAILED", $"Cannot select Assembly for {role}.");
            return assembly;
        }

        private static Assembly FindAssemblyByGuid(Model model, TeklaPlanCommand command, string guid)
        {
            var modelObject = model.SelectModelObject(model.GetIdentifierByGUID(guid));
            if (modelObject is not Assembly assembly || !assembly.Select())
                throw Failure(command, "TEKLA_PLAN_ASSEMBLY_NOT_FOUND", $"Cannot select Assembly '{guid}'.");
            return assembly;
        }

        private static Part FindPartByGuid(Model model, TeklaPlanCommand command, string guid, string role)
        {
            var modelObject = model.SelectModelObject(model.GetIdentifierByGUID(guid));
            if (modelObject is not Part part || !part.Select())
                throw Failure(command, "TEKLA_PLAN_ASSEMBLY_PART_NOT_FOUND", $"Cannot select {role} '{guid}'.");
            return part;
        }

        private static List<Part> PartSecondaries(Assembly assembly)
            => assembly.GetSecondaries().Cast<object>().OfType<Part>().ToList();

        private static void RequireMainPart(
            Model model,
            TeklaPlanCommand command,
            Assembly assembly,
            Part expectedMainPart)
        {
            var actualMainPart = assembly.GetMainPart() as Part ??
                throw Failure(command, "TEKLA_PLAN_ASSEMBLY_MAIN_PART_MISSING", "Assembly has no main Part.");
            if (!SameObject(actualMainPart, expectedMainPart))
            {
                throw Failure(
                    command,
                    "TEKLA_PLAN_ASSEMBLY_MAIN_PART_CONFLICT",
                    $"Assembly main Part '{GuidOf(model, actualMainPart, command, "main Part")}' does not match the requested Part.");
            }
        }

        private static string GuidOf(Model model, ModelObject modelObject, TeklaPlanCommand command, string role)
        {
            var guid = model.GetGUIDByIdentifier(modelObject.Identifier) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(guid) || modelObject.Identifier.ID <= 0)
                throw Failure(command, "TEKLA_PLAN_READBACK_IDENTITY_MISSING", $"Native {role} has no persistent identity.");
            return guid;
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
            try { target.GetUserProperty(name, ref value); } catch { }
            return value;
        }

        private static int ReadInt(ModelObject target, string name)
        {
            var value = 0;
            try { target.GetUserProperty(name, ref value); } catch { }
            return value;
        }

        private static TeklaNativeExecutionException Failure(
            TeklaPlanCommand command,
            string code,
            string message)
            => new(code, $"Command '{command.CommandId}': {message}");
    }
}
