using System.IO;
using Microsoft.Win32;

namespace Connector.Desktop.Services;

public interface IAutoStartService
{
    void SetEnabled(bool enabled);
    bool IsEnabled();
}

public sealed class AutoStartService : IAutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "StructuraUnifiedConnector";
    private readonly string _runKeyPath;
    private readonly Func<string> _launcher;

    public AutoStartService() : this(RunKeyPath, ResolveLauncherPath) { }

    internal AutoStartService(string runKeyPath, Func<string> launcher)
    {
        _runKeyPath = runKeyPath;
        _launcher = launcher;
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(_runKeyPath, writable: true)
                        ?? throw new InvalidOperationException("Cannot open HKCU Run key.");

        if (enabled)
        {
            var exe = _launcher();
            key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    private static string ResolveLauncherPath()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve process path.");
        var directory = Path.GetDirectoryName(exe);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return exe;
        }

        // A managed Velopack install runs the app below current/. Autostart must target
        // the stable root launcher so it follows the version switched by Update.exe.
        var currentDirectory = new DirectoryInfo(directory);
        if (!string.Equals(currentDirectory.Name, "current", StringComparison.OrdinalIgnoreCase) ||
            currentDirectory.Parent is null)
        {
            return exe;
        }

        var stableLauncher = Path.Combine(currentDirectory.Parent.FullName, Path.GetFileName(exe));
        return File.Exists(stableLauncher) ? stableLauncher : exe;
    }
}
