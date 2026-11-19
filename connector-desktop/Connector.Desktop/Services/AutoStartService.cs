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

    // The machine installation runs the application from Program Files\Platform Connector directly.
    private static string ResolveLauncherPath() =>
        Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve process path.");
}
