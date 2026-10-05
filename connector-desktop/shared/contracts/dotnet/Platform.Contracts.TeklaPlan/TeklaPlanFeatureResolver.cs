#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Platform.Contracts.TeklaPlan
{
    /// <summary>Resolves source features as complete native mappings, without Tekla SDK or solved geometry.</summary>
    public static class TeklaPlanFeatureResolver
    {
        public static TeklaPayloadParseResult<IReadOnlyList<TeklaPlanCommand>> ResolveFeatureCommands(
            TeklaPlanDocument plan,
            TeklaPlanCommand operation,
            string featureId)
        {
            var commands = (plan.Commands ?? Array.Empty<TeklaPlanCommand>()).Where(candidate => candidate.Source.Kind == "feature" &&
                string.Equals(candidate.Source.Id, featureId, StringComparison.Ordinal)).ToArray();
            if (commands.Length == 0)
                return Failure(operation, "TEKLA_PLAN_NATIVE_FEATURE_COMMAND_MISSING",
                    $"No command materializes feature '{featureId}'.");

            var mappings = (plan.Mappings ?? Array.Empty<TeklaPlanMapping>()).Where(mapping => mapping.Source.Kind == "feature" &&
                string.Equals(mapping.Source.Id, featureId, StringComparison.Ordinal)).ToArray();
            if (mappings.Length != 1)
                return Failure(operation, "TEKLA_PLAN_NATIVE_FEATURE_MAPPING_INVALID",
                    $"Feature '{featureId}' must have exactly one native mapping.");

            var mapping = mappings[0];
            var mappedIds = mapping.CommandIds ?? Array.Empty<string>();
            if (mapping.Strategy != (commands.Length == 1 ? "native-single" : "native-composite") ||
                mappedIds.Length != commands.Length ||
                mappedIds.Distinct(StringComparer.Ordinal).Count() != commands.Length ||
                commands.Select(item => item.CommandId).Distinct(StringComparer.Ordinal).Count() != commands.Length ||
                mapping.OwnershipStableKey != mapping.Source.StableKey ||
                commands.Any(item => !mappedIds.Contains(item.CommandId, StringComparer.Ordinal) ||
                    item.Source.StableKey != mapping.Source.StableKey))
                return Failure(operation, "TEKLA_PLAN_NATIVE_FEATURE_MAPPING_INVALID",
                    $"Feature '{featureId}' mapping must identify every native command exactly once with the same stable source.");

            var dependencies = operation.DependsOn ?? Array.Empty<string>();
            foreach (var command in commands)
            {
                if (!dependencies.Contains(command.CommandId, StringComparer.Ordinal))
                    return Failure(operation, "TEKLA_PLAN_NATIVE_FEATURE_DEPENDENCY_MISSING",
                        $"Operation does not depend on feature command '{command.CommandId}'.");
            }
            return new TeklaPayloadParseResult<IReadOnlyList<TeklaPlanCommand>>(commands,
                Array.Empty<TeklaPlanValidationDiagnostic>());
        }

        public static TeklaPayloadParseResult<IReadOnlyList<TeklaPlanCommand>> ResolveWeldPreparation(
            TeklaPlanDocument plan,
            TeklaPlanCommand operation,
            TeklaCreateWeldPayload weld)
        {
            if (weld.EdgePreparationCommandId is null)
                return new TeklaPayloadParseResult<IReadOnlyList<TeklaPlanCommand>>(
                    Array.Empty<TeklaPlanCommand>(), Array.Empty<TeklaPlanValidationDiagnostic>());

            var anchors = (plan.Commands ?? Array.Empty<TeklaPlanCommand>()).Where(item => item.CommandId == weld.EdgePreparationCommandId).ToArray();
            if (anchors.Length != 1 || anchors[0].Source.Kind != "feature")
                return Failure(operation, "TEKLA_PLAN_WELD_PREPARATION_REFERENCE_INVALID",
                    "Linked edge preparation must identify a command of one mapped feature.");

            var resolved = ResolveFeatureCommands(plan, operation, anchors[0].Source.Id);
            if (!resolved.Success || resolved.Value is null) return resolved;
            string? targetElementId = null;
            foreach (var command in resolved.Value)
            {
                if (command.Kind != "apply-boolean-cut" || command.Phase != "boolean-cuts")
                    return Failure(operation, "TEKLA_PLAN_WELD_PREPARATION_COMMAND_UNSUPPORTED",
                        "Linked edge preparation must be materialized entirely by BooleanPart cuts.");
                var cut = TeklaPlanPayloads.ParseApplyBooleanCut(command);
                if (!cut.Success || cut.Value is null)
                    return new TeklaPayloadParseResult<IReadOnlyList<TeklaPlanCommand>>(null, cut.Diagnostics);
                if (cut.Value.Cutter.Kind != "through-contour")
                    return Failure(operation, "TEKLA_PLAN_WELD_PREPARATION_COMMAND_UNSUPPORTED",
                        "Linked edge preparation requires explicit through-contour prisms.");
                var cutTarget = cut.Value.Target.ElementId;
                if ((cutTarget != weld.Target.ElementId && cutTarget != weld.Participant.ElementId) ||
                    (targetElementId is not null && targetElementId != cutTarget))
                    return Failure(operation, "TEKLA_PLAN_WELD_PREPARATION_TARGET_MISMATCH",
                        "Every cut of a linked preparation must target the same weld participant.");
                targetElementId = cutTarget;
            }
            return resolved;
        }

        public static IReadOnlyList<TeklaPlanValidationDiagnostic> ValidateReferences(
            TeklaPlanDocument plan,
            TeklaPlanCommand command)
        {
            var diagnostics = new List<TeklaPlanValidationDiagnostic>();
            if (command.Kind == "create-assembly")
            {
                var assembly = TeklaPlanPayloads.ParseCreateAssembly(command);
                if (!assembly.Success || assembly.Value is null) return assembly.Diagnostics;
                foreach (var featureId in assembly.Value.FeatureIds)
                    diagnostics.AddRange(ResolveFeatureCommands(plan, command, featureId).Diagnostics);
            }
            else if (command.Kind == "create-weld")
            {
                var weld = TeklaPlanPayloads.ParseCreateWeld(command);
                if (!weld.Success || weld.Value is null) return weld.Diagnostics;
                diagnostics.AddRange(ResolveWeldPreparation(plan, command, weld.Value).Diagnostics);
            }
            return diagnostics;
        }

        private static TeklaPayloadParseResult<IReadOnlyList<TeklaPlanCommand>> Failure(
            TeklaPlanCommand operation, string code, string message)
            => new(null, new[] { new TeklaPlanValidationDiagnostic(code, message, operation.CommandId) });
    }
}
