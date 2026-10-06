// Результат insert/modify/delete операции — то что bridge отдаёт connector'у
// в response body. Read имеет свой shape (TeklaReadResult).

#nullable enable

using System.Collections.Generic;

namespace Platform.Bridge.Desktop.Tekla.ComponentRuntime
{
    public sealed class TeklaOperationResult
    {
        public bool Ok { get; set; }
        public string Operation { get; set; } = "";
        public string ComponentType { get; set; } = "";
        public int SchemaVersion { get; set; }
        public string ExternalObjectId { get; set; } = "";

        public int? TeklaComponentId { get; set; }
        public string? TeklaComponentGuid { get; set; }

        public List<int>? CreatedChildIds { get; set; }
        public List<string>? CreatedChildGuids { get; set; }

        /// <summary>Имена web-полей реально применённых к Tekla.</summary>
        public List<string>? AppliedParameters { get; set; }

        /// <summary>Поля прибывшие в payload, но проигнорированные (не в schema).</summary>
        public List<string>? SkippedParameters { get; set; }

        public long DurationMs { get; set; }

        /// <summary>Если Ok=false: код из §2.3 плана (TEKLA_COMPONENT_INSERT_FAILED, …).</summary>
        public string? ErrorCode { get; set; }

        /// <summary>Если Ok=false: human-readable message.</summary>
        public string? Message { get; set; }

        public static TeklaOperationResult Failure(
            string errorCode,
            string message,
            ComponentOperationRequest request,
            long durationMs = 0)
        {
            return new TeklaOperationResult
            {
                Ok = false,
                ErrorCode = errorCode,
                Message = message,
                Operation = request.Operation,
                ComponentType = request.ComponentType,
                SchemaVersion = request.SchemaVersion,
                ExternalObjectId = request.ExternalObjectId,
                DurationMs = durationMs,
            };
        }
    }
}
