namespace Platform.Connector.Core;

/// <summary>
/// Динамические capabilities коннектора. Базовый список живёт в настройках
/// («autocad.build», «tekla.apply»); часть способностей зависит от среды и
/// меняется на лету — например, «autocad.session» есть только пока на машине
/// запущен хотя бы один AutoCAD.
///
/// Heartbeat сериализует options.Capabilities при каждой отправке, поэтому
/// атомарная подмена ССЫЛКИ на список (не мутация in-place) даёт следующему
/// heartbeat'у актуальный набор без гонок с сериализацией.
/// </summary>
public static class ConnectorCapabilities
{
    /// <summary>Есть запущенная сессия AutoCAD — устройство умеет session-задачи (mode=session).</summary>
    public const string AutoCadSession = "autocad.session";

    /// <summary>
    /// Pure: новый список с добавленной/убранной capability (без дубликатов,
    /// порядок исходных сохраняется). Исходный список не мутируется.
    /// </summary>
    public static List<string> Apply(IEnumerable<string>? current, string capability, bool present)
    {
        var result = (current ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(x => !string.Equals(x, capability, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (present)
        {
            result.Add(capability);
        }

        return result;
    }

    /// <summary>
    /// Применяет динамическую capability к live-опциям runtime'а (options
    /// разделяется с worker'ом — см. ConnectorRuntimeHost.CurrentOptions).
    /// Возвращает true, если набор фактически изменился (для логирования в UI).
    /// </summary>
    public static bool ApplyDynamic(ConnectorRuntimeOptions? options, string capability, bool present)
    {
        if (options is null)
        {
            return false;
        }

        var current = options.Capabilities;
        var hasNow = current?.Any(x => string.Equals(x, capability, StringComparison.OrdinalIgnoreCase)) == true;
        if (hasNow == present)
        {
            return false;
        }

        options.Capabilities = Apply(current, capability, present);
        return true;
    }
}
