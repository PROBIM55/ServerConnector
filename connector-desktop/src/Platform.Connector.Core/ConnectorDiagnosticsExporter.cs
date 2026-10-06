using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Platform.Connector.Core;

/// <summary>
/// Сборка redacted-диагностики (опции без секретов + runtime-статус + логи)
/// в zip. Используется console-приложением (--export-diagnostics) и кнопкой
/// «Экспорт диагностики» в Desktop UI.
/// </summary>
public static class ConnectorDiagnosticsExporter
{
    public static string Export(ConnectorRuntimeOptions options, ConnectorRuntimeState runtimeState, string? outputDirectory)
    {
        var diagnosticsRoot = string.IsNullOrWhiteSpace(outputDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Platform", "Connector", "diagnostics")
            : outputDirectory;

        Directory.CreateDirectory(diagnosticsRoot);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var workspace = Path.Combine(diagnosticsRoot, $"connector-diagnostics-{stamp}");
        Directory.CreateDirectory(workspace);

        var safeOptions = new
        {
            options.ServerUrl,
            options.DeviceId,
            options.HeartbeatSeconds,
            options.PollIntervalSeconds,
            options.PollBackoffMaxSeconds,
            options.StatusRefreshSeconds,
            options.EnableJobPolling,
            options.EnableStatusShell,
            options.RunDemoJobWhenIdle,
            options.IdempotencyStatePath,
            options.LogFilePath,
            options.AgentType,
            options.ModuleScope,
            options.Capabilities
        };

        WriteJson(Path.Combine(workspace, "runtime-options.redacted.json"), safeOptions);
        WriteJson(Path.Combine(workspace, "runtime-status.json"), runtimeState.GetSnapshot());
        File.WriteAllText(Path.Combine(workspace, "system-info.txt"), BuildSystemInfo(), System.Text.Encoding.UTF8);

        var tokenPath = SecureTokenStore.ResolvePath(options.SecureTokenPath);
        if (File.Exists(tokenPath))
        {
            File.WriteAllText(
                Path.Combine(workspace, "secure-token-info.txt"),
                $"secure_token_file={tokenPath}{Environment.NewLine}present=true{Environment.NewLine}",
                System.Text.Encoding.UTF8);
        }

        CopyIfExists(options.LogFilePath, Path.Combine(workspace, "connector.jsonl"));
        CopyIfExists(options.IdempotencyStatePath, Path.Combine(workspace, "idempotency-state.json"));

        var zipPath = Path.Combine(diagnosticsRoot, $"connector-diagnostics-{stamp}.zip");
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        ZipFile.CreateFromDirectory(workspace, zipPath);
        Directory.Delete(workspace, recursive: true);
        return zipPath;
    }

    private static void WriteJson(string path, object value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(path, json, System.Text.Encoding.UTF8);
    }

    private static void CopyIfExists(string? source, string destination)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        if (!File.Exists(source))
        {
            return;
        }

        try
        {
            File.Copy(source, destination, overwrite: true);
        }
        catch (IOException)
        {
            // Файл может быть залочен живым runtime — копируем через FileShare.
            using var read = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var write = File.Create(destination);
            read.CopyTo(write);
        }
    }

    private static string BuildSystemInfo()
    {
        var lines = new[]
        {
            $"generated_utc={DateTime.UtcNow:O}",
            $"machine={Environment.MachineName}",
            $"user={Environment.UserName}",
            $"os={Environment.OSVersion}",
            $"framework={Environment.Version}",
            $"process_arch={RuntimeInformation.ProcessArchitecture}",
            $"os_arch={RuntimeInformation.OSArchitecture}"
        };

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
