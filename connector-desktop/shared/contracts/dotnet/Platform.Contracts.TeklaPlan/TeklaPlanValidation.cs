#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Platform.Contracts.TeklaPlan
{
    public sealed class TeklaPlanValidationDiagnostic
    {
        public TeklaPlanValidationDiagnostic(string code, string message, string? commandId = null)
        {
            Code = code;
            Message = message;
            CommandId = commandId;
        }

        public string Severity { get; } = "error";
        public string Code { get; }
        public string Message { get; }
        public string? CommandId { get; }
    }

    public sealed class TeklaPlanValidationResult
    {
        public TeklaPlanValidationResult(IReadOnlyList<TeklaPlanValidationDiagnostic> diagnostics)
        {
            Diagnostics = diagnostics;
        }

        public bool Valid => Diagnostics.Count == 0;
        public IReadOnlyList<TeklaPlanValidationDiagnostic> Diagnostics { get; }
    }

    public static class TeklaPlanValidator
    {
        private const int MaxCommands = 20_000;

        private static readonly IReadOnlyDictionary<string, int> PhaseOrder = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["base-parts"] = 0,
            ["fittings"] = 10,
            ["boolean-cuts"] = 20,
            ["edge-treatments"] = 30,
            ["holes-and-bolts"] = 40,
            ["welds"] = 50,
            ["assemblies"] = 60,
        };

        public static TeklaPlanValidationResult Validate(TeklaPlanDocument? plan, TeklaRuntimeCapabilities runtime)
        {
            var diagnostics = new List<TeklaPlanValidationDiagnostic>();
            if (plan is null)
            {
                diagnostics.Add(Error("TEKLA_PLAN_MISSING", "TeklaPlan is required."));
                return new TeklaPlanValidationResult(diagnostics);
            }

            if (plan.SchemaVersion != TeklaPlanContract.SchemaVersion)
                diagnostics.Add(Error("TEKLA_PLAN_SCHEMA_UNSUPPORTED", $"Schema v{plan.SchemaVersion} is not supported."));
            if (plan.Source is null || plan.Source.Address is null)
            {
                diagnostics.Add(Error("TEKLA_PLAN_SOURCE_MISSING", "source and source.address are required."));
                return new TeklaPlanValidationResult(diagnostics);
            }
            Required(plan.Source.GenerationId, "source.generationId", diagnostics);
            Required(plan.Source.SourceHash, "source.sourceHash", diagnostics);
            Required(plan.Source.ConstructiveContentHash, "source.constructiveContentHash", diagnostics);
            if (plan.Source.ConstructiveSchemaVersion < 1)
                diagnostics.Add(Error("TEKLA_PLAN_CONSTRUCTIVE_SCHEMA_INVALID", "source.constructiveSchemaVersion must be at least 1."));
            Required(plan.Source.Address.ProjectId, "source.address.projectId", diagnostics);
            Required(plan.Source.Address.ModuleId, "source.address.moduleId", diagnostics);
            Required(plan.Source.Address.ModuleVariantId, "source.address.moduleVariantId", diagnostics);
            Required(plan.Source.Address.RevisionId, "source.address.revisionId", diagnostics);
            Required(plan.Target?.AdapterId, "target.adapterId", diagnostics);
            Required(plan.Target?.AdapterVersion, "target.adapterVersion", diagnostics);

            if (!plan.Executable)
                diagnostics.Add(Error("TEKLA_PLAN_NOT_EXECUTABLE", "Compiler marked the plan as non-executable."));
            if (plan.Diagnostics?.Any(static item => string.Equals(item.Severity, "error", StringComparison.Ordinal)) == true)
                diagnostics.Add(Error("TEKLA_PLAN_HAS_ERRORS", "Plan contains compiler errors."));
            if (!string.Equals(plan.Target?.AdapterId, runtime.AdapterId, StringComparison.Ordinal))
                diagnostics.Add(Error("TEKLA_PLAN_ADAPTER_MISMATCH", $"Plan targets '{plan.Target?.AdapterId}', runtime is '{runtime.AdapterId}'."));
            if (!string.Equals(plan.Target?.AdapterVersion, runtime.AdapterVersion, StringComparison.Ordinal))
                diagnostics.Add(Error("TEKLA_PLAN_ADAPTER_VERSION_MISMATCH", $"Plan targets adapter version '{plan.Target?.AdapterVersion}', runtime is '{runtime.AdapterVersion}'."));
            if (!SameMajor(plan.Target?.TeklaVersion, runtime.TeklaVersion))
                diagnostics.Add(Error("TEKLA_PLAN_TEKLA_VERSION_MISMATCH", $"Plan targets Tekla '{plan.Target?.TeklaVersion}', runtime is '{runtime.TeklaVersion}'."));

            var commands = plan.Commands ?? Array.Empty<TeklaPlanCommand>();
            if (commands.Length > MaxCommands)
                diagnostics.Add(Error("TEKLA_PLAN_TOO_LARGE", $"At most {MaxCommands} commands are accepted."));
            foreach (var capability in TeklaPlanContract.CapabilityNames)
            {
                if (plan.Capabilities is null || !plan.Capabilities.ContainsKey(capability))
                    diagnostics.Add(Error("TEKLA_PLAN_CAPABILITY_MISSING", $"Plan capability snapshot is missing '{capability}'."));
            }
            if (plan.Capabilities is not null)
            {
                var knownCapabilities = new HashSet<string>(TeklaPlanContract.CapabilityNames, StringComparer.Ordinal);
                foreach (var capability in plan.Capabilities.Keys.Where(capability => !knownCapabilities.Contains(capability)))
                    diagnostics.Add(Error("TEKLA_PLAN_CAPABILITY_UNKNOWN", $"Plan capability snapshot contains unknown '{capability}'."));
            }
            if (!CapabilityEnabled(plan.Capabilities, "udaStamp") || !CapabilityEnabled(runtime.Flags, "udaStamp"))
                diagnostics.Add(Error("TEKLA_PLAN_UDA_STAMP_REQUIRED", "Both plan and runtime must support ownership UDA stamps."));

            var byId = new Dictionary<string, TeklaPlanCommand>(StringComparer.Ordinal);
            for (var index = 0; index < commands.Length; index++)
            {
                var command = commands[index];
                if (command is null)
                {
                    diagnostics.Add(Error("TEKLA_PLAN_COMMAND_NULL", $"commands[{index}] is null."));
                    continue;
                }
                Required(command.CommandId, $"commands[{index}].commandId", diagnostics);
                if (!string.IsNullOrWhiteSpace(command.CommandId))
                {
                    if (byId.ContainsKey(command.CommandId))
                        diagnostics.Add(Error("TEKLA_PLAN_COMMAND_DUPLICATE", $"Command '{command.CommandId}' is duplicated.", command.CommandId));
                    else
                        byId.Add(command.CommandId, command);
                }
                ValidateCommand(plan, command, runtime, diagnostics);
            }

            ValidateDependencies(commands, byId, diagnostics);
            ValidateMappings(plan.Mappings ?? Array.Empty<TeklaPlanMapping>(), commands, byId, diagnostics);
            return new TeklaPlanValidationResult(diagnostics);
        }

        public static bool ContainsForbiddenGeometry(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Array)
                return value.EnumerateArray().Any(ContainsForbiddenGeometry);
            if (value.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in value.EnumerateObject())
            {
                var normalized = property.Name.Replace("-", string.Empty).Replace("_", string.Empty).ToLowerInvariant();
                if (normalized.Contains("mesh") || normalized.Contains("brep") || normalized.Contains("inlinegeometry") || normalized.Contains("triangle"))
                    return true;
                if (ContainsForbiddenGeometry(property.Value)) return true;
            }
            return false;
        }

        private static void ValidateCommand(
            TeklaPlanDocument plan,
            TeklaPlanCommand command,
            TeklaRuntimeCapabilities runtime,
            List<TeklaPlanValidationDiagnostic> diagnostics)
        {
            if (!TeklaPlanCommandCatalog.TryGet(command.Kind ?? string.Empty, out var rule))
            {
                diagnostics.Add(Error("TEKLA_PLAN_COMMAND_UNSUPPORTED", $"Command kind '{command.Kind}' is unsupported.", command.CommandId));
                return;
            }
            if (!string.Equals(command.Phase, rule.Phase, StringComparison.Ordinal))
                diagnostics.Add(Error("TEKLA_PLAN_PHASE_INVALID", $"Command '{command.CommandId}' must be in phase '{rule.Phase}'.", command.CommandId));
            if (rule.Capability is not null)
                RequireCapability(plan, runtime, rule.Capability, command.CommandId, diagnostics);
            foreach (var property in rule.RequiredPayload)
            {
                if (command.Payload is null || !command.Payload.ContainsKey(property))
                    diagnostics.Add(Error("TEKLA_PLAN_PAYLOAD_INVALID", $"Command '{command.CommandId}' requires '{property}'.", command.CommandId));
            }

            if (string.Equals(command.Kind, "create-hole", StringComparison.Ordinal))
            {
                var holeType = PayloadString(command, "holeType");
                if (string.Equals(holeType, "round", StringComparison.Ordinal))
                    RequireCapability(plan, runtime, "roundHole", command.CommandId, diagnostics);
                else if (string.Equals(holeType, "slotted", StringComparison.Ordinal))
                    RequireCapability(plan, runtime, "slottedHole", command.CommandId, diagnostics);
                else
                    diagnostics.Add(Error("TEKLA_PLAN_HOLE_TYPE_INVALID", $"Command '{command.CommandId}' has unsupported holeType '{holeType}'.", command.CommandId));
            }
            if (string.Equals(command.Kind, "create-poly-beam", StringComparison.Ordinal) && PayloadContainsArc(command, "path"))
                RequireCapability(plan, runtime, "polyBeamArcSegments", command.CommandId, diagnostics);
            if (string.Equals(command.Kind, "create-poly-beam", StringComparison.Ordinal) &&
                command.Payload?.TryGetValue("bends", out var bends) == true &&
                bends.ValueKind == JsonValueKind.Array && bends.GetArrayLength() > 0)
            {
                RequireCapability(plan, runtime, "bend", command.CommandId, diagnostics);
            }
            if (string.Equals(command.Kind, "create-poly-beam", StringComparison.Ordinal) && command.Payload?.ContainsKey("developedPlate") == true)
                RequireCapability(plan, runtime, "developedPlatePolyBeamStationFrameV1", command.CommandId, diagnostics);
            if ((string.Equals(command.Kind, "create-contour-plate", StringComparison.Ordinal) ||
                 string.Equals(command.Kind, "create-lofted-plate", StringComparison.Ordinal)) &&
                command.Payload is not null && command.Payload.Values.Any(ContainsArc))
            {
                RequireCapability(plan, runtime, "contourArcEdges", command.CommandId, diagnostics);
            }
            if (string.Equals(command.Kind, "create-weld", StringComparison.Ordinal) && command.Payload?.ContainsKey("path") == true)
                RequireCapability(plan, runtime, "polygonWeld", command.CommandId, diagnostics);

            ValidateSource(command.Source, command.CommandId, diagnostics);
            Required(command.Owner?.ModuleId, $"commands[{command.CommandId}].owner.moduleId", diagnostics, command.CommandId);
            Required(command.Owner?.EntityId, $"commands[{command.CommandId}].owner.entityId", diagnostics, command.CommandId);
            Required(command.RevisionProvenance?.RevisionId, $"commands[{command.CommandId}].revisionProvenance.revisionId", diagnostics, command.CommandId);
            Required(command.RevisionProvenance?.SourceHash, $"commands[{command.CommandId}].revisionProvenance.sourceHash", diagnostics, command.CommandId);
            Required(command.RevisionProvenance?.AdapterId, $"commands[{command.CommandId}].revisionProvenance.adapterId", diagnostics, command.CommandId);
            Required(command.RevisionProvenance?.AdapterVersion, $"commands[{command.CommandId}].revisionProvenance.adapterVersion", diagnostics, command.CommandId);
            ValidateOwnership(plan, command, diagnostics);
        }

        private static void ValidateSource(TeklaPlanSourceRef? source, string commandId, List<TeklaPlanValidationDiagnostic> diagnostics)
        {
            if (source is null)
            {
                diagnostics.Add(Error("TEKLA_PLAN_SOURCE_MISSING", $"Command '{commandId}' has no source.", commandId));
                return;
            }
            Required(source.Kind, $"commands[{commandId}].source.kind", diagnostics, commandId);
            Required(source.Id, $"commands[{commandId}].source.id", diagnostics, commandId);
            Required(source.StableKey, $"commands[{commandId}].source.stableKey", diagnostics, commandId);
            Required(source.Role, $"commands[{commandId}].source.role", diagnostics, commandId);
            if (source.SourceLayer != "generated" && source.SourceLayer != "manual")
                diagnostics.Add(Error("TEKLA_PLAN_SOURCE_LAYER_INVALID", $"Command '{commandId}' has invalid sourceLayer.", commandId));
        }

        private static void ValidateOwnership(TeklaPlanDocument plan, TeklaPlanCommand command, List<TeklaPlanValidationDiagnostic> diagnostics)
        {
            var stamp = command.Ownership;
            var source = command.Source;
            var address = plan.Source.Address;
            var valid = stamp is not null && source is not null &&
                stamp.Namespace == TeklaPlanContract.OwnershipNamespace &&
                stamp.DocumentSourceHash == plan.Source.SourceHash &&
                stamp.GenerationId == plan.Source.GenerationId &&
                stamp.SourceKind == source.Kind &&
                stamp.SourceId == source.Id &&
                stamp.StableKey == source.StableKey &&
                stamp.ModuleId == address.ModuleId &&
                stamp.ModuleVariantId == address.ModuleVariantId &&
                stamp.RevisionId == address.RevisionId;
            if (!valid)
                diagnostics.Add(Error("TEKLA_PLAN_OWNERSHIP_INVALID", $"Command '{command.CommandId}' ownership stamp does not match its source document.", command.CommandId));
        }

        private static void ValidateDependencies(
            IReadOnlyList<TeklaPlanCommand> commands,
            IReadOnlyDictionary<string, TeklaPlanCommand> byId,
            List<TeklaPlanValidationDiagnostic> diagnostics)
        {
            var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < commands.Count; index++)
            {
                var command = commands[index];
                if (command is not null && !string.IsNullOrWhiteSpace(command.CommandId) && !positions.ContainsKey(command.CommandId))
                    positions.Add(command.CommandId, index);
            }
            var previousPhase = -1;
            foreach (var command in commands.Where(static item => item is not null))
            {
                if (PhaseOrder.TryGetValue(command.Phase ?? string.Empty, out var phase))
                {
                    if (phase < previousPhase)
                        diagnostics.Add(Error("TEKLA_PLAN_PHASE_ORDER_INVALID", $"Command '{command.CommandId}' is out of phase order.", command.CommandId));
                    previousPhase = Math.Max(previousPhase, phase);
                }
                foreach (var dependencyId in command.DependsOn ?? Array.Empty<string>())
                {
                    if (!byId.TryGetValue(dependencyId, out var dependency))
                    {
                        diagnostics.Add(Error("TEKLA_PLAN_DEPENDENCY_MISSING", $"Command '{command.CommandId}' depends on missing '{dependencyId}'.", command.CommandId));
                        continue;
                    }
                    if (positions.TryGetValue(dependencyId, out var dependencyPosition) &&
                        positions.TryGetValue(command.CommandId, out var commandPosition) &&
                        dependencyPosition >= commandPosition)
                    {
                        diagnostics.Add(Error("TEKLA_PLAN_DEPENDENCY_ORDER_INVALID", $"Dependency '{dependencyId}' must precede '{command.CommandId}'.", command.CommandId));
                    }
                    if (PhaseOrder.TryGetValue(dependency.Phase ?? string.Empty, out var dependencyPhase) &&
                        PhaseOrder.TryGetValue(command.Phase ?? string.Empty, out var commandPhase) && dependencyPhase > commandPhase)
                    {
                        diagnostics.Add(Error("TEKLA_PLAN_DEPENDENCY_PHASE_INVALID", $"Dependency '{dependencyId}' is in a later phase.", command.CommandId));
                    }
                }
            }
        }

        private static void ValidateMappings(
            IReadOnlyList<TeklaPlanMapping> mappings,
            IReadOnlyList<TeklaPlanCommand> commands,
            IReadOnlyDictionary<string, TeklaPlanCommand> byId,
            List<TeklaPlanValidationDiagnostic> diagnostics)
        {
            var mapped = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mapping in mappings.Where(static item => item is not null))
            {
                if (mapping.Strategy == "blocked")
                    diagnostics.Add(Error("TEKLA_PLAN_MAPPING_BLOCKED", $"Source '{mapping.Source?.Id}' is blocked."));
                if (mapping.Source is null || mapping.OwnershipStableKey != mapping.Source.StableKey)
                    diagnostics.Add(Error("TEKLA_PLAN_MAPPING_OWNERSHIP_INVALID", "Mapping ownershipStableKey does not match source stableKey."));
                foreach (var commandId in mapping.CommandIds ?? Array.Empty<string>())
                {
                    if (!byId.TryGetValue(commandId, out var command))
                    {
                        diagnostics.Add(Error("TEKLA_PLAN_MAPPING_COMMAND_MISSING", $"Mapping references missing command '{commandId}'."));
                        continue;
                    }
                    if (!mapped.Add(commandId))
                        diagnostics.Add(Error("TEKLA_PLAN_MAPPING_COMMAND_DUPLICATE", $"Command '{commandId}' is mapped more than once.", commandId));
                    if (mapping.Source is not null && (command.Source.Id != mapping.Source.Id || command.Source.Kind != mapping.Source.Kind))
                        diagnostics.Add(Error("TEKLA_PLAN_MAPPING_SOURCE_MISMATCH", $"Command '{commandId}' source does not match its mapping.", commandId));
                }
            }
            foreach (var command in commands.Where(static item => item is not null))
            {
                if (!mapped.Contains(command.CommandId))
                    diagnostics.Add(Error("TEKLA_PLAN_COMMAND_UNMAPPED", $"Command '{command.CommandId}' has no design mapping.", command.CommandId));
            }
        }

        private static void RequireCapability(
            TeklaPlanDocument plan,
            TeklaRuntimeCapabilities runtime,
            string capability,
            string commandId,
            List<TeklaPlanValidationDiagnostic> diagnostics)
        {
            if (!CapabilityEnabled(plan.Capabilities, capability))
                diagnostics.Add(Error("TEKLA_PLAN_CAPABILITY_NOT_DECLARED", $"Plan did not declare capability '{capability}'.", commandId));
            if (!CapabilityEnabled(runtime.Flags, capability))
                diagnostics.Add(Error("TEKLA_RUNTIME_CAPABILITY_UNAVAILABLE", $"Runtime cannot execute capability '{capability}'.", commandId));
        }

        private static bool CapabilityEnabled(IReadOnlyDictionary<string, bool>? flags, string name)
            => flags is not null && flags.TryGetValue(name, out var enabled) && enabled;

        private static string? PayloadString(TeklaPlanCommand command, string property)
            => command.Payload is not null && command.Payload.TryGetValue(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static bool PayloadContainsArc(TeklaPlanCommand command, string property)
            => command.Payload is not null && command.Payload.TryGetValue(property, out var value) && ContainsArc(value);

        private static bool ContainsArc(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(ContainsArc);
            if (value.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in value.EnumerateObject())
            {
                if (property.NameEquals("kind") && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == "arc")
                    return true;
                if (ContainsArc(property.Value)) return true;
            }
            return false;
        }

        private static bool SameMajor(string? expected, string? actual)
        {
            if (string.IsNullOrWhiteSpace(expected)) return true;
            return !string.IsNullOrWhiteSpace(actual) && Major(expected!) == Major(actual!);
        }

        private static string Major(string value)
        {
            var separator = value.IndexOf('.');
            return separator < 0 ? value.Trim() : value.Substring(0, separator).Trim();
        }

        private static void Required(
            string? value,
            string path,
            List<TeklaPlanValidationDiagnostic> diagnostics,
            string? commandId = null)
        {
            if (string.IsNullOrWhiteSpace(value)) diagnostics.Add(Error("TEKLA_PLAN_REQUIRED_VALUE_MISSING", $"'{path}' is required.", commandId));
        }

        private static TeklaPlanValidationDiagnostic Error(string code, string message, string? commandId = null)
            => new(code, message, commandId);
    }
}
