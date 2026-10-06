using System;
using Tekla.Structures.Model;

namespace TeklaBridge.TeklaShim;

/// <summary>
/// Утилиты удаления Part'ов перед re-генерацией компонента (modify-режим).
/// Mirrors decomp <c>DeletePartsByFatherComponent</c> + <c>DeletePartsByNamePrefix</c>
/// (TeklaBridge.exe :1595-1662).
///
/// Lookup-приоритет в Step4:
///   1. <see cref="DeleteByFatherComponent"/> — если payload содержит cmpid.
///   2. <see cref="DeleteByNamePrefix"/> — legacy fallback по префиксу имени
///      когда componentId не передан.
/// </summary>
internal static class PartCleanup
{
    /// <summary>
    /// Удалить все Part'ы у которых GetFatherComponent().Identifier.ID == componentId.
    /// Делает CommitChanges если что-то удалено. Возвращает количество удалённых.
    /// При исключении (например объект в lock-state) — возвращает то, что
    /// успело удалиться. Поведение mirror-decomp: silent на errors.
    /// </summary>
    public static int DeleteByFatherComponent(Model model, int componentId)
    {
        if (componentId <= 0) return 0;
        var count = 0;
        try
        {
            var selector = model.GetModelObjectSelector();
            var en = selector.GetAllObjects();
            while (en.MoveNext())
            {
                if (en.Current is not Part part) continue;
                BaseComponent? father = null;
                try { father = part.GetFatherComponent(); }
                catch { }
                if (father?.Identifier?.ID == componentId && part.Delete())
                    count++;
            }
            if (count > 0) model.CommitChanges();
        }
        catch
        {
        }
        return count;
    }

    /// <summary>
    /// Удалить все Part'ы у которых Name начинается с prefix (case-insensitive).
    /// Legacy путь когда componentId неизвестен; нужен чтобы Modify не
    /// дублировал плиты.
    /// </summary>
    public static int DeleteByNamePrefix(Model model, string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return 0;
        var count = 0;
        try
        {
            var selector = model.GetModelObjectSelector();
            var en = selector.GetAllObjects();
            while (en.MoveNext())
            {
                if (en.Current is not Part part) continue;
                if (part.Name is { } name
                    && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && part.Delete())
                    count++;
            }
            if (count > 0) model.CommitChanges();
        }
        catch
        {
        }
        return count;
    }
}
