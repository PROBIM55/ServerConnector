// Ссылка на объект в модели Tekla — приоритет GUID (persistent), Identifier.ID
// (runtime hint). См. plan §5.3.

#nullable enable

namespace Platform.Bridge.Desktop.Tekla.ComponentRuntime
{
    public sealed class TeklaObjectRef
    {
        public string? Guid { get; set; }
        public int? Id { get; set; }

        public bool HasGuid => !string.IsNullOrWhiteSpace(Guid);

        public static TeklaObjectRef FromGuid(string guid) => new() { Guid = guid };
        public static TeklaObjectRef FromId(int id) => new() { Id = id };
    }
}
