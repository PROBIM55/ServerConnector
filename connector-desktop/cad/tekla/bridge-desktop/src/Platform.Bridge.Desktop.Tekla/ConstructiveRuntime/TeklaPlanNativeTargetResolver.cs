#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Platform.Bridge.Desktop.Tekla.ComponentRuntime;
using Platform.Contracts.TeklaPlan;
using Tekla.Structures.Model;

namespace Platform.Bridge.Desktop.Tekla.ConstructiveRuntime
{
    internal sealed class TeklaPlanNativeTarget
    {
        public TeklaPlanNativeTarget(TeklaPlanCommand command, string externalObjectId)
        {
            Command = command;
            ExternalObjectId = externalObjectId;
        }

        public TeklaPlanCommand Command { get; }
        public string ExternalObjectId { get; }
    }

    /// <summary>
    /// Resolves an operation target through the plan's stable constructive
    /// identity. Native operations never infer their owner from command order,
    /// geometry proximity, or Tekla selection state.
    /// </summary>
    internal static class TeklaPlanNativeTargetResolver
    {
        private static readonly HashSet<string> PartCommandKinds = new(StringComparer.Ordinal)
        {
            "create-beam",
            "create-poly-beam",
            "create-contour-plate",
            "create-lofted-plate",
        };

        public static TeklaPlanNativeTarget ResolvePartCommand(
            TeklaPlanDocument plan,
            TeklaPlanCommand operation,
            TeklaTopologyRef target)
            => ResolvePartCommand(plan, operation, target.ElementId);

        public static TeklaPlanNativeTarget ResolvePartCommand(
            TeklaPlanDocument plan,
            TeklaPlanCommand operation,
            string elementId)
        {
            var matches = plan.Commands
                .Where(candidate =>
                    string.Equals(candidate.Phase, "base-parts", StringComparison.Ordinal) &&
                    PartCommandKinds.Contains(candidate.Kind) &&
                    string.Equals(candidate.Source.Kind, "element", StringComparison.Ordinal) &&
                    string.Equals(candidate.Source.Id, elementId, StringComparison.Ordinal))
                .ToArray();

            if (matches.Length == 0)
            {
                throw Failure(
                    operation,
                    "TEKLA_PLAN_NATIVE_TARGET_COMMAND_MISSING",
                    $"No base-part command materializes target element '{elementId}'.");
            }
            if (matches.Length > 1)
            {
                throw Failure(
                    operation,
                    "TEKLA_PLAN_NATIVE_TARGET_COMMAND_AMBIGUOUS",
                    $"Target element '{elementId}' is materialized by {matches.Length} base-part commands.");
            }

            var match = matches[0];
            if (!(operation.DependsOn ?? Array.Empty<string>()).Contains(match.CommandId, StringComparer.Ordinal))
            {
                throw Failure(
                    operation,
                    "TEKLA_PLAN_NATIVE_TARGET_DEPENDENCY_MISSING",
                    $"Operation does not depend on target command '{match.CommandId}'.");
            }

            return new TeklaPlanNativeTarget(match, TeklaPlanOwnershipIdentity.Create(plan, match));
        }

        public static IReadOnlyList<TeklaPlanCommand> ResolveFeatureCommands(
            TeklaPlanDocument plan,
            TeklaPlanCommand operation,
            string featureId)
        {
            var resolved = TeklaPlanFeatureResolver.ResolveFeatureCommands(plan, operation, featureId);
            if (!resolved.Success || resolved.Value is null)
                throw Failure(operation, resolved.Diagnostics[0].Code, resolved.Diagnostics[0].Message);
            return resolved.Value;
        }

        public static void ValidateWeldPreparation(
            TeklaPlanDocument plan,
            TeklaPlanCommand operation,
            TeklaCreateWeldPayload weld)
        {
            var resolved = TeklaPlanFeatureResolver.ResolveWeldPreparation(plan, operation, weld);
            if (!resolved.Success || resolved.Value is null)
                throw Failure(operation, resolved.Diagnostics[0].Code, resolved.Diagnostics[0].Message);
        }

        public static Part FindPart(Model model, TeklaPlanCommand operation, string externalObjectId)
        {
            var matches = new List<Part>();
            var selector = model.GetModelObjectSelector();
            foreach (var objectType in SupportedPartObjectTypes)
            {
                var enumerator = selector.GetAllObjectsWithType(objectType);
                while (enumerator.MoveNext())
                {
                    if (enumerator.Current is not Part part) continue;
                    var candidate = string.Empty;
                    if (!part.GetUserProperty(StructuraServiceUdas.ExternalObjectId, ref candidate)) continue;
                    if (string.Equals(candidate, externalObjectId, StringComparison.Ordinal)) matches.Add(part);
                }
            }

            if (matches.Count == 0)
            {
                throw Failure(
                    operation,
                    "TEKLA_PLAN_NATIVE_TARGET_NOT_FOUND",
                    $"No native Part has ownership id '{externalObjectId}'.");
            }
            if (matches.Count > 1)
            {
                throw Failure(
                    operation,
                    "TEKLA_PLAN_NATIVE_TARGET_DUPLICATE",
                    $"Native target ownership id '{externalObjectId}' belongs to {matches.Count} Parts.");
            }
            return matches[0];
        }

        private static readonly ModelObject.ModelObjectEnum[] SupportedPartObjectTypes =
        {
            ModelObject.ModelObjectEnum.BEAM,
            ModelObject.ModelObjectEnum.POLYBEAM,
            ModelObject.ModelObjectEnum.CONTOURPLATE,
            ModelObject.ModelObjectEnum.LOFTED_PLATE,
        };

        private static TeklaNativeExecutionException Failure(
            TeklaPlanCommand operation,
            string code,
            string message)
            => new(code, $"Command '{operation.CommandId}': {message}");
    }
}
