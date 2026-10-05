using System;
using Tekla.Structures.Model;

namespace TeklaBridge.TeklaShim;

/// <summary>
/// Wrapper над Tekla BooleanPart API. Используется в Phase E4 для
/// trapezoidal/bevel-cuts на стыках поясов разной ширины/толщины.
/// Mirrors decomp <c>BooleanCut</c> (TeklaBridge.exe :1985).
/// </summary>
internal static class BooleanOperations
{
    /// <summary>
    /// Создать BooleanPart с типом BOOLEAN_CUT: cutter удаляет геометрию
    /// из father. После успешного BooleanPart.Insert() сам cutter-part
    /// удаляется из модели — BooleanPart хранит cutter shape internally,
    /// видимая plate больше не нужна. Mirrors decomp TeklaBridge.exe:1985.
    /// </summary>
    public static bool Cut(Part father, Part cutter)
    {
        if (father is null || cutter is null) return false;
        try
        {
            var boolean = new BooleanPart { Father = father };
            boolean.SetOperativePart(cutter);
            boolean.Type = BooleanPart.BooleanTypeEnum.BOOLEAN_CUT;
            var ok = boolean.Insert();
            if (ok)
            {
                // BooleanPart забрал shape cutter'а внутрь себя; видимая
                // ContourPlate больше не нужна. Без Delete cutters остаются
                // в модели как «висящие» плиты с Class="BlOpCl".
                try { cutter.Delete(); } catch { /* best-effort cleanup */ }
            }
            return ok;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
