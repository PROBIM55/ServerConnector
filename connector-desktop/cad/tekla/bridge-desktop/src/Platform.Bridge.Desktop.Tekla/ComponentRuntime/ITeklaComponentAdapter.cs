// Контракт адаптера для одного типа Tekla-компонента.
// См. plan §3.2. Каждый адаптер регистрируется в ComponentAdapterRegistry
// по паре (ComponentType, SchemaVersion).

#nullable enable

using System.Threading;
using Tekla.Structures.Model;

namespace Platform.Bridge.Desktop.Tekla.ComponentRuntime
{
    public interface ITeklaComponentAdapter
    {
        string ComponentType { get; }
        int SchemaVersion { get; }

        /// <summary>Возвращает schema (поля, типы, defaults) для этого адаптера.</summary>
        ComponentSchema GetSchema();

        /// <summary>Сообщает поддерживаемые операции и установлен ли plugin/component в Tekla.</summary>
        ComponentCapabilities GetCapabilities(Model? model);

        /// <summary>Schema-based валидация input'а до того как мы трогаем Tekla.</summary>
        ValidationResult Validate(ComponentOperationRequest request);

        /// <summary>
        /// Создать (если не существует) или обновить (если найден) компонент.
        /// Lookup приоритет: target.GUID → STRUCTURA_EXTERNAL_OBJECT_ID UDA scan.
        /// </summary>
        TeklaOperationResult Upsert(Model model, ComponentOperationRequest request, CancellationToken ct);

        /// <summary>Только update; ошибка TEKLA_OBJECT_NOT_FOUND если объект не существует.</summary>
        TeklaOperationResult Modify(Model model, ComponentOperationRequest request, CancellationToken ct);

        /// <summary>Удалить компонент + commit; идемпотентно (если уже удалён — Ok=true).</summary>
        TeklaOperationResult Delete(Model model, ComponentOperationRequest request, CancellationToken ct);

        /// <summary>Прочитать актуальные значения schema-задекларированных полей.</summary>
        TeklaReadResult Read(Model model, TeklaObjectRef objectRef, CancellationToken ct);
    }
}
