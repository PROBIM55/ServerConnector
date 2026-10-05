// Результат /component/read — текущее состояние компонента в Tekla,
// фильтрованное через schema адаптера (никаких "всех UDA").

#nullable enable

using System.Collections.Generic;

namespace Platform.Bridge.Desktop.Tekla.ComponentRuntime
{
    public sealed class TeklaReadResult
    {
        public bool Ok { get; set; }
        public string ComponentType { get; set; } = "";
        public int SchemaVersion { get; set; }

        public int? TeklaComponentId { get; set; }
        public string? TeklaComponentGuid { get; set; }
        public string? ExternalObjectId { get; set; }

        /// <summary>Schema-задекларированные параметры, прочитанные из Tekla UDA. </summary>
        public Dictionary<string, object?>? Parameters { get; set; }

        public long DurationMs { get; set; }
        public string? ErrorCode { get; set; }
        public string? Message { get; set; }

        public static TeklaReadResult Failure(string errorCode, string message, long durationMs = 0)
            => new() { Ok = false, ErrorCode = errorCode, Message = message, DurationMs = durationMs };
    }
}
