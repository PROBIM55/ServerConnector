using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;

namespace Platform.Cad.AutoCad.Adapter.Sessions;

/// <summary>
/// Перечисление запущенных AutoCAD через Running Object Table (ROT).
/// .NET 8 не имеет Marshal.GetActiveObject — делаем стандартный ROT-walk:
/// GetRunningObjectTable → EnumRunning → IMoniker.GetDisplayName → фильтр →
/// IRunningObjectTable.GetObject (late-bound IDispatch).
///
/// Форматы моникеров (проверено живым дампом ROT, AutoCAD 2022/R24.1):
/// современный AutoCAD регистрируется через RegisterActiveObject — в ROT
/// item-моникеры вида «!{CLSID}» (например «!{AA46BA8A-9825-40FD-8493-0BA3C4D5CEB5}»
/// = AutoCAD.Application.24.1, плюс дубль под version-independent CLSID).
/// Такие GUID'ы резолвим в ProgID через HKCR\CLSID\{...}\ProgID и матчим
/// «AutoCAD.Application». Старые версии регистрировали текстовый ProgID
/// («!AutoCAD.Application.18:PID») — substring-фильтр оставлен для них.
/// PID: суффикс «:PID» если есть, иначе Application.HWND → GetWindowThreadProcessId.
/// Вызывать только на STA-потоке (см. <see cref="StaExecutor"/>).
/// </summary>
[SupportedOSPlatform("windows")]
public static class AutoCadRunningObjectTable
{
    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable rot);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx bindCtx);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    /// <summary>COM-объект Application + display name его моникера.</summary>
    public sealed record RotEntry(string DisplayName, object Application);

    /// <summary>
    /// Фильтр display name моникеров: текстовый ProgID-паттерн
    /// «AutoCAD.Application[.RXX[.X]][:PID]» ИЛИ «!{CLSID}», чей ProgID
    /// (clsidProgIdResolver; по умолчанию — реестр HKCR) содержит «AutoCAD.Application».
    /// </summary>
    public static bool IsAutoCadApplicationDisplayName(string? displayName, Func<string, string?>? clsidProgIdResolver = null)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return false;
        }

        if (displayName.Contains("AutoCAD.Application", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var clsid = TryExtractClsid(displayName);
        if (clsid is null)
        {
            return false;
        }

        var progId = (clsidProgIdResolver ?? ResolveClsidProgIdFromRegistry)(clsid.Value.ToString("D"));
        return progId?.Contains("AutoCAD.Application", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>«!{AA46BA8A-…}» → GUID (RegisterActiveObject-моникер); null для прочих имён.</summary>
    public static Guid? TryExtractClsid(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var candidate = displayName.Trim();
        if (candidate.StartsWith('!'))
        {
            candidate = candidate[1..];
        }

        return Guid.TryParseExact(candidate, "B", out var clsid) ? clsid : null;
    }

    /// <summary>HKCR\CLSID\{clsid}\ProgID (fallback: VersionIndependentProgID); null если нет.</summary>
    private static string? ResolveClsidProgIdFromRegistry(string clsid)
    {
        try
        {
            using var progIdKey = Registry.ClassesRoot.OpenSubKey($"CLSID\\{{{clsid}}}\\ProgID");
            if (progIdKey?.GetValue(null) is string progId && !string.IsNullOrWhiteSpace(progId))
            {
                return progId;
            }

            using var versionIndependentKey = Registry.ClassesRoot.OpenSubKey($"CLSID\\{{{clsid}}}\\VersionIndependentProgID");
            return versionIndependentKey?.GetValue(null) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>PID из суффикса display name («…:12345»); 0 если суффикса нет.</summary>
    public static int TryParseProcessIdFromDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return 0;
        }

        var colonIndex = displayName.LastIndexOf(':');
        if (colonIndex < 0 || colonIndex == displayName.Length - 1)
        {
            return 0;
        }

        return int.TryParse(displayName[(colonIndex + 1)..], out var pid) && pid > 0 ? pid : 0;
    }

    /// <summary>
    /// Все зарегистрированные в ROT объекты AutoCAD.Application.
    /// Caller обязан освободить каждый Application через Marshal.FinalReleaseComObject.
    /// </summary>
    public static List<RotEntry> GetAutoCadApplications()
    {
        var result = new List<RotEntry>();

        Marshal.ThrowExceptionForHR(GetRunningObjectTable(0, out var rot));
        try
        {
            rot.EnumRunning(out var enumMoniker);
            try
            {
                enumMoniker.Reset();
                var monikers = new IMoniker[1];
                while (enumMoniker.Next(1, monikers, IntPtr.Zero) == 0)
                {
                    var moniker = monikers[0];
                    try
                    {
                        string? displayName = null;
                        try
                        {
                            Marshal.ThrowExceptionForHR(CreateBindCtx(0, out var bindCtx));
                            try
                            {
                                moniker.GetDisplayName(bindCtx, null, out displayName);
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(bindCtx);
                            }
                        }
                        catch
                        {
                            continue; // моникер без display name — не наш
                        }

                        if (!IsAutoCadApplicationDisplayName(displayName))
                        {
                            continue;
                        }

                        try
                        {
                            rot.GetObject(moniker, out var application);
                            if (application is null)
                            {
                                continue;
                            }

                            // Один инстанс AutoCAD регистрируется под двумя CLSID
                            // (версионный + version-independent), а runtime кэширует
                            // RCW по COM identity — GetObject возвращает ТОТ ЖЕ объект.
                            // Дубликаты отбрасываем по reference identity, иначе release
                            // «лишней» записи убил бы RCW и живой сессии.
                            if (result.Any(existing => ReferenceEquals(existing.Application, application)))
                            {
                                continue;
                            }

                            result.Add(new RotEntry(displayName!, application));
                        }
                        catch
                        {
                            // Объект мог уже умереть между EnumRunning и GetObject.
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(moniker);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumMoniker);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(rot);
        }

        return result;
    }

    /// <summary>PID процесса по HWND главного окна Application (fallback-путь).</summary>
    public static int GetProcessIdFromHwnd(long hwnd)
    {
        if (hwnd == 0)
        {
            return 0;
        }

        _ = GetWindowThreadProcessId(new IntPtr(hwnd), out var pid);
        return (int)pid;
    }
}
