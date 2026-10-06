using System.IO;

namespace TeklaBridge.Infrastructure;

/// <summary>
/// Файловое хранилище command.txt → result.txt в директории TeklaBridge.exe.
/// Контракт прежнего плагина: client пишет команду в command.txt, запускает
/// TeklaBridge.exe, читает результат из result.txt. Mirrors decomp
/// CommandFileStore :128-158.
/// </summary>
internal sealed class CommandFileStore
{
    private readonly string _baseDir;

    public string CommandPath => Path.Combine(_baseDir, "command.txt");
    public string ResultPath => Path.Combine(_baseDir, "result.txt");

    public CommandFileStore(string baseDir)
    {
        _baseDir = baseDir;
    }

    public bool TryReadCommand(out string command)
    {
        command = string.Empty;
        if (!File.Exists(CommandPath)) return false;
        command = File.ReadAllText(CommandPath).Trim();
        return true;
    }

    public void WriteResult(string text)
        => File.WriteAllText(ResultPath, text);
}
