// Резервный recovery channel — служебные UDA, которые мы пишем на каждый
// father-component при insert/modify. По ним можно восстановить связь
// externalObjectId ↔ teklaComponentGuid если потерян object-map. См. plan §5.3.

#nullable enable

namespace Platform.Bridge.Desktop.Tekla.ComponentRuntime
{
    public static class StructuraServiceUdas
    {
        public const string ExternalObjectId  = "STRUCTURA_EXTERNAL_OBJECT_ID";
        public const string ComponentType     = "STRUCTURA_COMPONENT_TYPE";
        public const string SchemaVersion     = "STRUCTURA_SCHEMA_VERSION";
        public const string LastOperationId   = "STRUCTURA_LAST_OPERATION_ID";
    }
}
