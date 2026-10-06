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
    private const string ValueName = "ConnectorAgentDesktop";

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                        ?? throw new InvalidOperationException("Cannot open HKCU Run key.");

        if (enabled)
        {
            var exe = ResolveLauncherPath();
            key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
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
