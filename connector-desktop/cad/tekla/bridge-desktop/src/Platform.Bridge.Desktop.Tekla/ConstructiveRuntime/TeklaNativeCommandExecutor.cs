#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Platform.Contracts.TeklaPlan;
using Tekla.Structures.Model;

namespace Platform.Bridge.Desktop.Tekla.ConstructiveRuntime
{
    public interface ITeklaNativeCommandExecutor
    {
        TeklaPlanExecutorRegistration Registration { get; }

        ITeklaPreparedMutation<TeklaNativeCommandReadback> Prepare(
            Model model,
            TeklaPlanDocument plan,
            TeklaPlanCommand command,
            string operationId);
    }

    public sealed class TeklaNativeCommandRegistry
    {
        private readonly IReadOnlyDictionary<string, ITeklaNativeCommandExecutor> _byCommandKind;

        public TeklaNativeCommandRegistry(IEnumerable<ITeklaNativeCommandExecutor> executors)
        {
            if (executors is null) throw new ArgumentNullException(nameof(executors));
            var byKind = new Dictionary<string, ITeklaNativeCommandExecutor>(StringComparer.Ordinal);
            foreach (var executor in executors)
            {
                if (executor is null)
                    throw new ArgumentException("Native executor list cannot contain null values.", nameof(executors));
                if (!executor.Registration.ApplyReady)
                    throw new ArgumentException(
                        $"Native executor '{executor.Registration.ExecutorId}' must be apply-ready.",
                        nameof(executors));
                if (byKind.ContainsKey(executor.Registration.CommandKind))
                    throw new ArgumentException(
                        $"Command kind '{executor.Registration.CommandKind}' has more than one native executor.",
                        nameof(executors));
                byKind.Add(executor.Registration.CommandKind, executor);
            }

            _byCommandKind = byKind;
            SharedRegistry = new TeklaPlanExecutorRegistry(
                byKind.Values.Select(static executor => executor.Registration));
        }

        public TeklaPlanExecutorRegistry SharedRegistry { get; }
        public bool HasApplyReadyExecutors => _byCommandKind.Count > 0;

        public ITeklaPreparedMutation<TeklaNativeCommandReadback> Prepare(
            Model model,
            TeklaPlanDocument plan,
            TeklaPlanCommand command,
            string operationId)
        {
            if (!_byCommandKind.TryGetValue(command.Kind, out var executor))
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_EXECUTOR_UNAVAILABLE",
                    $"No native executor is registered for command kind '{command.Kind}'.");
            }
            return executor.Prepare(model, plan, command, operationId);
        }
    }

    public sealed class TeklaNativeCommandReadback
    {
        public string CommandId { get; set; } = string.Empty;
        public string CommandKind { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string ExternalObjectId { get; set; } = string.Empty;
        public string TeklaGuid { get; set; } = string.Empty;
        public int TeklaId { get; set; }
        public double[] Start { get; set; } = Array.Empty<double>();
        public double[] End { get; set; } = Array.Empty<double>();
        public double[][] Path { get; set; } = Array.Empty<double[]>();
        public double[][] Contour { get; set; } = Array.Empty<double[]>();
        public double ThicknessMm { get; set; }
        public string ExtrusionSide { get; set; } = string.Empty;
        public string Profile { get; set; } = string.Empty;
        public string Material { get; set; } = string.Empty;
        public string Plane { get; set; } = string.Empty;
        public double PlaneOffsetMm { get; set; }
        public string Depth { get; set; } = string.Empty;
        public double DepthOffsetMm { get; set; }
        public string Rotation { get; set; } = string.Empty;
        public double RotationOffsetDeg { get; set; }
        public string TargetExternalObjectId { get; set; } = string.Empty;
        public string TargetTeklaGuid { get; set; } = string.Empty;
        public string TargetContourId { get; set; } = string.Empty;
        public string TargetVertexId { get; set; } = string.Empty;
        public string TargetEdgeId { get; set; } = string.Empty;
        public string TargetEdgeSide { get; set; } = string.Empty;
        public string CornerType { get; set; } = string.Empty;
        public double CornerSizeXmm { get; set; }
        public double CornerSizeYmm { get; set; }
        public string EdgeTreatmentType { get; set; } = string.Empty;
        public double EdgeTreatmentSizeMm { get; set; }
        public double? EdgeTreatmentSecondarySizeMm { get; set; }
        public double[] PlaneOrigin { get; set; } = Array.Empty<double>();
        public double[] PlaneAxisX { get; set; } = Array.Empty<double>();
        public double[] PlaneAxisY { get; set; } = Array.Empty<double>();
        public string KeepSide { get; set; } = string.Empty;
        public string CutterKind { get; set; } = string.Empty;
        public string[] ParticipantExternalObjectIds { get; set; } = Array.Empty<string>();
        public string[] ParticipantTeklaGuids { get; set; } = Array.Empty<string>();
        public string MainElementExternalObjectId { get; set; } = string.Empty;
        public string MainElementTeklaGuid { get; set; } = string.Empty;
        public string[] SecondaryElementExternalObjectIds { get; set; } = Array.Empty<string>();
        public string[] SecondaryElementTeklaGuids { get; set; } = Array.Empty<string>();
        public string[] FeatureIds { get; set; } = Array.Empty<string>();
        public string AssemblyName { get; set; } = string.Empty;
        public string WeldType { get; set; } = string.Empty;
        public double WeldSizeMm { get; set; }
        public string WeldSide { get; set; } = string.Empty;
        public string ShopSite { get; set; } = string.Empty;
        public double[] HoleCenter { get; set; } = Array.Empty<double>();
        public double[] HoleAxis { get; set; } = Array.Empty<double>();
        public double HoleDiameterMm { get; set; }
        public double? HoleDepthMm { get; set; }
        public string HoleType { get; set; } = string.Empty;
        public double? SlotLengthMm { get; set; }
        public double[] SlotDirection { get; set; } = Array.Empty<double>();
        public string BoltStandard { get; set; } = string.Empty;
        public double BoltDiameterMm { get; set; }
        public double? BoltLengthMm { get; set; }
        public double BoltToleranceMm { get; set; }
        public string BoltType { get; set; } = string.Empty;
        public bool BoltCreatesHoles { get; set; }
        public string BoltPatternKind { get; set; } = string.Empty;
        public double[][] BoltPositions { get; set; } = Array.Empty<double[]>();
        public string ComponentType { get; set; } = string.Empty;
        public int SchemaVersion { get; set; }
        public string LastOperationId { get; set; } = string.Empty;
    }

    public sealed class TeklaNativeExecutionException : Exception
    {
        public TeklaNativeExecutionException(string errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public string ErrorCode { get; }
    }
}
