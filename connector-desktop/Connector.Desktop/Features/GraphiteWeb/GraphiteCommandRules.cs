using System.IO;
using System.Windows;
using Connector.Desktop.Models;

namespace Connector.Desktop.Features.GraphiteWeb;

/// <summary>A refusal whose message is a fixed user text of the native boundary (never an exception detail).</summary>
public sealed class GraphiteUserException(string message) : InvalidOperationException(message);

/// <summary>
/// Pure rules of the Graphite command boundary. Kept apart from GraphiteDesktopController so that they are
/// unit-tested without the desktop composition. None of them reads a path or URL from the web document.
/// </summary>
public static class GraphiteCommandRules
{
    public static string? NormalizeTheme(string? theme) => theme is "dark" or "light" ? theme : null;

    public static bool IsWindowCommand(string command) => command is "window-minimize" or "window-maximize" or "window-close";

    /// <summary>Target state for a title-bar button; null means «close», which the window turns into hiding to the tray.</summary>
    public static WindowState? NextWindowState(string command, WindowState current) => command switch
    {
        "window-minimize" => WindowState.Minimized,
        "window-maximize" => current == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized,
        "window-close" => null,
        _ => throw new GraphiteUserException("Неизвестная команда окна.")
    };

    /// <summary>
    /// Local folder of a Tekla standard section: the folder picked in this session, otherwise the saved setting.
    /// The command name selects the section; the document never supplies the path.
    /// </summary>
    public static string ResolveTeklaFolder(string command, IReadOnlyDictionary<string, string> picked, AppSettings settings)
    {
        var (field, saved) = command switch
        {
            "xs-open" => ("xs-path", settings.TeklaStandardLocalPath),
            "ext-open" => ("ext-path", settings.TeklaExtensionsLocalPath),
            "lib-open" => ("lib-path", settings.TeklaLibrariesLocalPath),
            _ => throw new GraphiteUserException("Неизвестный раздел стандарта Tekla.")
        };
        var path = picked.TryGetValue(field, out var chosen) && !string.IsNullOrWhiteSpace(chosen) ? chosen : saved;
        if (string.IsNullOrWhiteSpace(path)) throw new GraphiteUserException("Папка ещё не выбрана. Нажмите «Изменить папку».");
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new GraphiteUserException("Папка не найдена на этом компьютере. Обновите раздел или выберите другую папку.");
        return path;
    }

    /// <summary>Converter job states that «Очистить завершённые» may remove; queued and running jobs stay.</summary>
    public static bool IsFinishedJobState(string state) => state is "Готово" or "Отменено" or "Ошибка" or "Превышено время" or "Прервано";

    /// <summary>Runs a command; a GraphiteUserException becomes a refusal that carries its own message.</summary>
    public static Task<object?> RefuseUserErrors(Func<Task<object?>> handle, Func<string, Task<object?>> refuse)
    {
        try { return handle(); }
        catch (GraphiteUserException ex) { return refuse(ex.Message); }
    }
}
