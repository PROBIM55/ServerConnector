// POST /selection — возвращает текущий выбранный component в Tekla
// (если выбран ровно один наш плагин-компонент). Не interactive: моментальный
// snapshot без блокировки на ввод пользователя.
//
// Lookup: ModelObjectSelector.GetSelectedObjects() → перебираем; ищем
// первый BaseComponent с известным STRUCTURA_COMPONENT_TYPE UDA → адаптер
// читает параметры через свою schema.
//
// Возможные исходы:
//  - ok=true, component=<TeklaReadResult-shape>      ровно один наш компонент
//  - ok=false, errorCode=COMPONENT_NOT_SELECTED      пусто
//  - ok=false, errorCode=COMPONENT_SELECTION_INVALID несколько / не наш / не component

#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Platform.Bridge.Desktop.Tekla.ComponentRuntime;
using Platform.Bridge.Desktop.Tekla.Tekla;
using Tekla.Structures.Model;
using Tekla.Structures.Model.UI;

namespace Platform.Bridge.Desktop.Tekla.Http
{
    public sealed class SelectionHandler
    {
        private readonly TeklaWorker _worker;
        private readonly ComponentAdapterRegistry _registry;

        public SelectionHandler(TeklaWorker worker, ComponentAdapterRegistry registry)
        {
            _worker = worker;
            _registry = registry;
        }

        public async Task<HttpResult> HandleAsync(RequestContext request, CancellationToken ct)
        {
            try
            {
                var result = await _worker.RunAsync(model =>
                {
                    var selector = new global::Tekla.Structures.Model.UI.ModelObjectSelector();
                    var enumerator = selector.GetSelectedObjects();

                    // Когда user кликает на балку в Tekla, обычно выделяется не сам
                    // BaseComponent, а его child (ContourPlate / Part). Поднимаемся
                    // через ModelObject.GetFatherComponent() пока не найдём наш
                    // marked component (у которого STRUCTURA_COMPONENT_TYPE UDA).
                    var ourComponents = new System.Collections.Generic.HashSet<int>();
                    BaseComponent? ourComponent = null;
                    int totalSelected = 0;
                    var debugItems = new System.Collections.Generic.List<string>();
                    while (enumerator.MoveNext())
                    {
                        totalSelected++;
                        var current = enumerator.Current as ModelObject;
                        if (current is null)
                        {
                            debugItems.Add($"#{totalSelected}: NULL");
                            continue;
                        }

                        var (resolved, debugChain) = ResolveOurComponentVerbose(current);
                        debugItems.Add($"#{totalSelected}: {debugChain}");
                        if (resolved is null) continue;

                        // де-дуп: если user выделил несколько частей одной балки —
                        // считаем за один компонент.
                        if (ourComponents.Add(resolved.Identifier.ID))
                        {
                            if (ourComponent is null) ourComponent = resolved;
                            else return SelectionOutcome.Invalid("multiple our components selected", debugItems);
                        }
                    }

                    if (ourComponent is null)
                    {
                        return totalSelected == 0
                            ? SelectionOutcome.NotSelected("no objects selected in Tekla", debugItems)
                            : SelectionOutcome.Invalid($"selection contains {totalSelected} object(s), none are our components", debugItems);
                    }

                    string compType = "";
                    ourComponent.GetUserProperty(StructuraServiceUdas.ComponentType, ref compType);
                    int schemaVer = 0;
                    ourComponent.GetUserProperty(StructuraServiceUdas.SchemaVersion, ref schemaVer);
                    if (schemaVer <= 0) schemaVer = 1;

                    if (!_registry.TryResolve(compType, schemaVer, out var adapter))
                    {
                        return SelectionOutcome.Unsupported($"({compType} v{schemaVer}) not registered in adapter registry");
                    }

                    var guid = model.GetGUIDByIdentifier(ourComponent.Identifier);
                    var read = adapter.Read(model, TeklaObjectRef.FromGuid(guid), ct);
                    return SelectionOutcome.Ok(read);
                }, ct);

                return result.Status switch
                {
                    SelectionStatus.Ok => HttpResult.Ok(new
                    {
                        ok = true,
                        component = result.ReadResult,
                    }),
                    SelectionStatus.NotSelected => new HttpResult(404, new
                    {
                        ok = false, errorCode = "COMPONENT_NOT_SELECTED",
                        message = result.Message ?? "No component selected.",
                        debug = result.Debug,
                    }),
                    SelectionStatus.Invalid => new HttpResult(400, new
                    {
                        ok = false, errorCode = "COMPONENT_SELECTION_INVALID",
                        message = result.Message ?? "Invalid selection.",
                        debug = result.Debug,
                    }),
                    SelectionStatus.Unsupported => HttpResult.BadRequest("TEKLA_COMPONENT_NOT_SUPPORTED", result.Message ?? "Component type not supported."),
                    _ => HttpResult.ServerError("INTERNAL_ERROR", "Unhandled selection status."),
                };
            }
            catch (TeklaDisconnectedException ex)
            {
                return HttpResult.ServerError("TEKLA_DISCONNECTED", ex.Message);
            }
            catch (TeklaStaleHandleException)
            {
                return HttpResult.ServiceUnavailable("BRIDGE_STALE_TEKLA",
                    "Tekla remote references became stale after Tekla restart; in-process reconnect failed. Connector should hard-restart Bridge.Desktop.");
            }
            catch (Exception ex)
            {
                return HttpResult.ServerError("INTERNAL_ERROR", $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Verbose-версия для диагностики: возвращает не только resolved-компонент
        /// но и читаемую строку с обходом цепочки родителей. Это позволяет user'у
        /// видеть в response /selection почему его выбор не был принят.
        /// </summary>
        private static (BaseComponent? Resolved, string DebugChain) ResolveOurComponentVerbose(ModelObject current)
        {
            var typeName = current.GetType().Name;
            var id = current.Identifier?.ID ?? 0;

            // Step 1: текущий — наш BaseComponent?
            if (current is BaseComponent self)
            {
                string ud = "";
                self.GetUserProperty(StructuraServiceUdas.ComponentType, ref ud);
                if (!string.IsNullOrEmpty(ud))
                    return (self, $"{typeName}#{id} OUR (UDA={ud})");
            }

            var chain = new System.Text.StringBuilder($"{typeName}#{id}");
            var visited = new System.Collections.Generic.HashSet<int> { id };
            var pointer = current;
            for (int depth = 0; depth < 16; depth++)
            {
                BaseComponent? father;
                try { father = pointer.GetFatherComponent(); }
                catch (Exception ex)
                {
                    chain.Append(" → father:THROWS(" + ex.GetType().Name + ")");
                    return (null, chain.ToString());
                }
                if (father is null)
                {
                    chain.Append(" → null");
                    return (null, chain.ToString());
                }
                var fId = father.Identifier?.ID ?? 0;
                if (!visited.Add(fId))
                {
                    chain.Append(" → cycle@" + fId);
                    return (null, chain.ToString());
                }
                string ud = "";
                father.GetUserProperty(StructuraServiceUdas.ComponentType, ref ud);
                chain.Append($" → {father.GetType().Name}#{fId}");
                if (!string.IsNullOrEmpty(ud))
                {
                    chain.Append($" OUR (UDA={ud})");
                    return (father, chain.ToString());
                }
                chain.Append(" no-UDA");
                pointer = father;
            }
            chain.Append(" → max-depth");
            return (null, chain.ToString());
        }

        private enum SelectionStatus { Ok, NotSelected, Invalid, Unsupported }

        private readonly record struct SelectionOutcome(
            SelectionStatus Status, TeklaReadResult? ReadResult, string? Message, System.Collections.Generic.List<string>? Debug)
        {
            public static SelectionOutcome Ok(TeklaReadResult read)
                => new(SelectionStatus.Ok, read, null, null);
            public static SelectionOutcome NotSelected(string msg, System.Collections.Generic.List<string>? debug = null)
                => new(SelectionStatus.NotSelected, null, msg, debug);
            public static SelectionOutcome Invalid(string msg, System.Collections.Generic.List<string>? debug = null)
                => new(SelectionStatus.Invalid, null, msg, debug);
            public static SelectionOutcome Unsupported(string msg)
                => new(SelectionStatus.Unsupported, null, msg, null);
        }
    }
}
