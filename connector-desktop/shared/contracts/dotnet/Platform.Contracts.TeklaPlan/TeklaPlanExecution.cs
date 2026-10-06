#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Platform.Contracts.TeklaPlan
{
    public sealed class TeklaPlanCommandDefinition
    {
        public TeklaPlanCommandDefinition(
            string kind,
            string phase,
            string? capability,
            params string[] requiredPayload)
        {
            Kind = kind;
            Phase = phase;
            Capability = capability;
            RequiredPayload = requiredPayload;
        }

        public string Kind { get; }
        public string Phase { get; }
        public string? Capability { get; }
        public IReadOnlyList<string> RequiredPayload { get; }
    }

    public static class TeklaPlanCommandCatalog
    {
        private static readonly IReadOnlyDictionary<string, TeklaPlanCommandDefinition> ByKind =
            new Dictionary<string, TeklaPlanCommandDefinition>(StringComparer.Ordinal)
            {
                ["create-beam"] = Definition("create-beam", "base-parts", "beam", "start", "end", "profile", "material"),
                ["create-poly-beam"] = Definition("create-poly-beam", "base-parts", "polyBeam", "path", "profile", "material"),
                ["create-contour-plate"] = Definition("create-contour-plate", "base-parts", "contourPlate", "plane", "contour", "thicknessMm", "extrusionSide", "material"),
                ["create-lofted-plate"] = Definition("create-lofted-plate", "base-parts", "loftedPlate", "sections", "thicknessMm", "material"),
                ["create-reinforcement"] = Definition("create-reinforcement", "base-parts", "reinforcement", "path", "diameterMm"),
                ["apply-fitting"] = Definition("apply-fitting", "fittings", "fitting", "target", "plane", "keepSide"),
                ["apply-boolean-cut"] = Definition("apply-boolean-cut", "boolean-cuts", "booleanPart", "target", "cutter"),
                ["create-hole"] = Definition("create-hole", "holes-and-bolts", null, "target", "center", "axis", "diameterMm", "holeType"),
                ["create-bolt-group"] = Definition("create-bolt-group", "holes-and-bolts", "boltGroup", "target", "participants", "frame", "boltStandard", "diameterMm", "toleranceMm", "boltType", "pattern", "createHoles"),
                ["create-weld"] = Definition("create-weld", "welds", "weld", "target", "participants", "weldType", "sizeMm", "shopSite", "side"),
                ["apply-contour-corner"] = Definition("apply-contour-corner", "edge-treatments", "contourChamfer", "target", "targetTopology", "cornerType", "sizeXmm"),
                ["apply-edge-treatment"] = Definition("apply-edge-treatment", "edge-treatments", "edgeChamfer", "target", "targetTopology", "treatmentType", "sizeMm"),
                ["apply-bend"] = Definition("apply-bend", "edge-treatments", "bend", "target", "radiusMm"),
                ["create-assembly"] = Definition("create-assembly", "assemblies", "assembly", "mainElementId", "secondaryElementIds", "featureIds"),
            };

        public static IReadOnlyCollection<TeklaPlanCommandDefinition> All => ByKind.Values.ToArray();

        public static bool TryGet(string kind, out TeklaPlanCommandDefinition definition)
            => ByKind.TryGetValue(kind, out definition!);

        private static TeklaPlanCommandDefinition Definition(
            string kind,
            string phase,
            string? capability,
            params string[] requiredPayload)
            => new(kind, phase, capability, requiredPayload);
    }

    public sealed class TeklaPlanExecutorRegistration
    {
        public TeklaPlanExecutorRegistration(
            string executorId,
            string commandKind,
            IEnumerable<string> capabilities,
            bool applyReady)
        {
            ExecutorId = executorId;
            CommandKind = commandKind;
            Capabilities = capabilities?.Distinct(StringComparer.Ordinal).ToArray()
                ?? Array.Empty<string>();
            ApplyReady = applyReady;
        }

        public string ExecutorId { get; }
        public string CommandKind { get; }
        public IReadOnlyList<string> Capabilities { get; }
        public bool ApplyReady { get; }
    }

    /// <summary>
    /// Exact command-to-native-executor registry. Runtime capabilities are
    /// derived from apply-ready registrations and cannot be enabled separately.
    /// </summary>
    public sealed class TeklaPlanExecutorRegistry
    {
        private readonly IReadOnlyDictionary<string, TeklaPlanExecutorRegistration> _byCommandKind;
        private readonly IReadOnlyDictionary<string, bool> _capabilityFlags;

        public TeklaPlanExecutorRegistry(IEnumerable<TeklaPlanExecutorRegistration>? registrations = null)
        {
            var byKind = new Dictionary<string, TeklaPlanExecutorRegistration>(StringComparer.Ordinal);
            var knownCapabilities = new HashSet<string>(TeklaPlanContract.CapabilityNames, StringComparer.Ordinal);
            var enabledCapabilities = new HashSet<string>(StringComparer.Ordinal);

            foreach (var registration in registrations ?? Array.Empty<TeklaPlanExecutorRegistration>())
            {
                if (string.IsNullOrWhiteSpace(registration.ExecutorId))
                    throw new ArgumentException("Executor id is required.", nameof(registrations));
                if (!TeklaPlanCommandCatalog.TryGet(registration.CommandKind, out var definition))
                    throw new ArgumentException($"Unknown TeklaPlan command kind '{registration.CommandKind}'.", nameof(registrations));
                if (byKind.ContainsKey(registration.CommandKind))
                    throw new ArgumentException($"Command kind '{registration.CommandKind}' has more than one executor.", nameof(registrations));
                byKind.Add(registration.CommandKind, registration);

                foreach (var capability in registration.Capabilities)
                {
                    if (!knownCapabilities.Contains(capability))
                        throw new ArgumentException($"Unknown Tekla capability '{capability}'.", nameof(registrations));
                }

                if (!registration.ApplyReady) continue;
                if (!registration.Capabilities.Contains("udaStamp", StringComparer.Ordinal))
                    throw new ArgumentException($"Apply-ready executor '{registration.ExecutorId}' must own UDA stamping.", nameof(registrations));
                if (definition.Capability is not null &&
                    !registration.Capabilities.Contains(definition.Capability, StringComparer.Ordinal))
                {
                    throw new ArgumentException(
                        $"Executor '{registration.ExecutorId}' must provide capability '{definition.Capability}'.",
                        nameof(registrations));
                }
                enabledCapabilities.UnionWith(registration.Capabilities);
            }

            _byCommandKind = byKind;
            _capabilityFlags = TeklaPlanContract.CapabilityNames.ToDictionary(
                static capability => capability,
                capability => enabledCapabilities.Contains(capability),
                StringComparer.Ordinal);
        }

        public static TeklaPlanExecutorRegistry Empty { get; } = new();

        public IReadOnlyCollection<TeklaPlanExecutorRegistration> All => _byCommandKind.Values.ToArray();
        public IReadOnlyDictionary<string, bool> CapabilityFlags => _capabilityFlags;
        public bool HasApplyReadyExecutors => _byCommandKind.Values.Any(static item => item.ApplyReady);

        public bool TryResolve(string commandKind, out TeklaPlanExecutorRegistration registration)
        {
            if (_byCommandKind.TryGetValue(commandKind, out var candidate) && candidate.ApplyReady)
            {
                registration = candidate;
                return true;
            }
            registration = null!;
            return false;
        }

        public TeklaRuntimeCapabilities CreateRuntimeCapabilities(
            string adapterId,
            string adapterVersion,
            string teklaVersion)
            => new()
            {
                AdapterId = adapterId,
                AdapterVersion = adapterVersion,
                TeklaVersion = teklaVersion,
                Flags = _capabilityFlags,
            };
    }

    public sealed class TeklaPlanExecutorBinding
    {
        public TeklaPlanExecutorBinding(string commandId, string commandKind, string executorId)
        {
            CommandId = commandId;
            CommandKind = commandKind;
            ExecutorId = executorId;
        }

        public string CommandId { get; }
        public string CommandKind { get; }
        public string ExecutorId { get; }
    }

    public sealed class TeklaPlanPreparationResult
    {
        public TeklaPlanPreparationResult(
            IReadOnlyList<TeklaPlanValidationDiagnostic> diagnostics,
            IReadOnlyList<TeklaPlanExecutorBinding> bindings)
        {
            Diagnostics = diagnostics;
            Bindings = bindings;
        }

        public bool Prepared => Diagnostics.Count == 0;
        public IReadOnlyList<TeklaPlanValidationDiagnostic> Diagnostics { get; }
        public IReadOnlyList<TeklaPlanExecutorBinding> Bindings { get; }
    }

    public static class TeklaPlanPreparer
    {
        public static TeklaPlanPreparationResult Prepare(
            TeklaPlanDocument plan,
            TeklaRuntimeCapabilities runtime,
            TeklaPlanExecutorRegistry registry)
        {
            var validation = TeklaPlanValidator.Validate(plan, runtime);
            var diagnostics = validation.Diagnostics.ToList();
            var bindings = new List<TeklaPlanExecutorBinding>();

            foreach (var command in plan.Commands ?? Array.Empty<TeklaPlanCommand>())
            {
                if (registry.TryResolve(command.Kind, out var registration))
                {
                    var payloadDiagnostics = TeklaPlanPayloads.Validate(command);
                    if (payloadDiagnostics.Count > 0)
                    {
                        diagnostics.AddRange(payloadDiagnostics);
                        continue;
                    }
                    var referenceDiagnostics = TeklaPlanFeatureResolver.ValidateReferences(plan, command);
                    if (referenceDiagnostics.Count > 0)
                    {
                        diagnostics.AddRange(referenceDiagnostics);
                        continue;
                    }
                    var developedDiagnostics = TeklaDevelopedPlateReadback.ValidateReferences(plan, command);
                    if (developedDiagnostics.Count > 0)
                    {
                        diagnostics.AddRange(developedDiagnostics);
                        continue;
                    }
                    bindings.Add(new TeklaPlanExecutorBinding(
                        command.CommandId,
                        command.Kind,
                        registration.ExecutorId));
                    continue;
                }

                diagnostics.Add(new TeklaPlanValidationDiagnostic(
                    "TEKLA_PLAN_EXECUTOR_UNAVAILABLE",
                    $"No apply-ready native executor is registered for command kind '{command.Kind}'.",
                    command.CommandId));
            }

            return new TeklaPlanPreparationResult(diagnostics, bindings);
        }
    }
}
