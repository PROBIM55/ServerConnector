using System.Text.Json;
using System.Diagnostics;
using Connector.JobModules;
using Platform.Connector.Core;

namespace Connector.JobModules.Tests;

public sealed class IfcOptimizeJobExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "connector-job-modules", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExecuteAsync_WithFakeNdjsonWorker_PromotesValidatedArtifactsAndMapsProgress()
    {
        Directory.CreateDirectory(_root);
        var input = Path.Combine(_root, "source.ifc"); await File.WriteAllTextAsync(input, "ISO-10303-21;");
        var script = Path.Combine(_root, "fake-worker.ps1");
        await File.WriteAllTextAsync(script, "param(); $r = [Console]::In.ReadLine() | ConvertFrom-Json; $p = $r.payload; [Console]::Error.Write(('x' * 20000)); Set-Content -LiteralPath $p.output_path -Value 'optimized'; Set-Content -LiteralPath $p.report_path -Value ('{\"output\":{\"path\":\"' + $p.output_path + '\"}}'); @{protocol='structura-ifc-optimizer/1';request_id=$r.request_id;event='progress';stage='write';progress=0.5;message='half'} | ConvertTo-Json -Compress; @{protocol='structura-ifc-optimizer/1';request_id=$r.request_id;event='completed';stage='done';progress=1.0;message='done'} | ConvertTo-Json -Compress");
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var executor = new IfcOptimizeJobExecutor(new IfcOptimizeOptions { ExecutablePath = shell, ExecutableArguments = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script] });
        var reported = new List<int>();
        var output = Path.Combine(_root, "out");
        var finalProgressObservedAfterCommit = false;
        var oldOutput = Path.Combine(output, "source.optimized.ifc"); Directory.CreateDirectory(Path.GetDirectoryName(oldOutput)!); await File.WriteAllTextAsync(oldOutput, "must remain");
        var result = await executor.ExecuteAsync(CreateJob(input, output, "exact"), (p, _) =>
        {
            reported.Add(p.Progress);
            if (p.Progress == 100) finalProgressObservedAfterCommit = Directory.EnumerateDirectories(output, "ifc-result-*").Any();
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Contains(reported, value => value is > 0 and < 100);
        Assert.Equal(100, reported[^1]);
        Assert.Equal(1, reported.Count(value => value == 100));
        Assert.True(finalProgressObservedAfterCommit);
        var published = Assert.Single(Directory.EnumerateDirectories(output, "ifc-result-*"));
        Assert.True(File.Exists(Path.Combine(published, "source.optimized.ifc")));
        Assert.True(File.Exists(Path.Combine(published, "source.optimized.report.json")));
        Assert.Equal("must remain", await File.ReadAllTextAsync(oldOutput));
    }

    [Fact]
    public async Task ExecuteAsync_WhenWorkerIsNotConfigured_ReturnsExplicitUnavailableCode()
    {
        Directory.CreateDirectory(_root); var input = Path.Combine(_root, "source.ifc"); await File.WriteAllTextAsync(input, "x");
        var result = await new IfcOptimizeJobExecutor(new IfcOptimizeOptions()).ExecuteAsync(CreateJob(input, Path.Combine(_root, "out"), "exact"), null, CancellationToken.None);
        Assert.False(result.IsSuccess); Assert.Equal("IFC_WORKER_UNAVAILABLE", result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_Analyze_PublishesWorkerAnalysisWithoutAnOptimizedIfc()
    {
        Directory.CreateDirectory(_root);
        var input = Path.Combine(_root, "source.ifc"); await File.WriteAllTextAsync(input, "ISO-10303-21;");
        var script = Path.Combine(_root, "analyze-worker.ps1");
        await File.WriteAllTextAsync(script,
            "$r = [Console]::In.ReadLine() | ConvertFrom-Json; " +
            "if ($r.command -ne 'analyze') { exit 2 }; " +
            "Set-Content -LiteralPath (Join-Path $r.payload.job_dir 'analysis.json') -Value '{\"schema\":\"IFC4\"}'; " +
            "@{protocol='structura-ifc-optimizer/1';request_id=$r.request_id;event='analysis';stage='analysis-complete';progress=1.0;message='done'} | ConvertTo-Json -Compress");
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var executor = new IfcOptimizeJobExecutor(
            new IfcOptimizeOptions { ExecutablePath = shell, ExecutableArguments = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script] },
            IfcOptimizeJobExecutor.AnalyzeId,
            "analyze");

        var output = Path.Combine(_root, "out");
        var result = await executor.ExecuteAsync(CreateJob(input, output, "default", IfcOptimizeJobExecutor.AnalyzeId), null, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Message);
        var published = Assert.Single(Directory.EnumerateDirectories(output, "ifc-result-*"));
        Assert.True(File.Exists(Path.Combine(published, "analysis.json")));
        Assert.Null(result.Result?.GetProperty("outputPath").GetString());
        Assert.EndsWith("analysis.json", result.Result?.GetProperty("reportPath").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WhenWorkerHangs_CancellationKillsItsOwnProcess()
    {
        Directory.CreateDirectory(_root); var input = Path.Combine(_root, "source.ifc"); await File.WriteAllTextAsync(input, "x");
        var script = Path.Combine(_root, "hanging-worker.ps1"); await File.WriteAllTextAsync(script, "[Console]::In.ReadLine() | Out-Null; Start-Sleep -Seconds 30");
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var executor = new IfcOptimizeJobExecutor(new IfcOptimizeOptions { ExecutablePath = shell, ExecutableArguments = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script] });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(CreateJob(input, Path.Combine(_root, "out"), "exact"), null, cancellation.Token));
    }

    [Fact]
    public async Task ExecuteAsync_WhenProtocolIsMalformed_StopsFakeChildAndDoesNotPublish()
    {
        Directory.CreateDirectory(_root); var input = Path.Combine(_root, "source.ifc"); await File.WriteAllTextAsync(input, "x");
        var pidFile = Path.Combine(_root, "pid.txt"); var script = Path.Combine(_root, "bad-worker.ps1");
        await File.WriteAllTextAsync(script, "$PID | Set-Content -LiteralPath '" + pidFile.Replace("'", "''") + "'; [Console]::In.ReadLine() | Out-Null; '{\"protocol\":\"bad\"}'; Start-Sleep -Seconds 30");
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var executor = new IfcOptimizeJobExecutor(new IfcOptimizeOptions { ExecutablePath = shell, ExecutableArguments = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script] });
        var reported = new List<int>();
        var output = Path.Combine(_root, "out"); var result = await executor.ExecuteAsync(CreateJob(input, output, "exact"), (p, _) => { reported.Add(p.Progress); return Task.CompletedTask; }, CancellationToken.None);
        Assert.False(result.IsSuccess); Assert.Equal("IFC_PROTOCOL_INVALID", result.ErrorCode); Assert.Empty(Directory.EnumerateDirectories(output, "ifc-result-*"));
        Assert.DoesNotContain(100, reported);
        var pid = int.Parse(await File.ReadAllTextAsync(pidFile)); Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    [Fact]
    public async Task ExecuteAsync_WhenWorkerSpawnsChildImmediately_OwnsAndStopsEscapedChildBeforeReturning()
    {
        Directory.CreateDirectory(_root);
        var input = Path.Combine(_root, "source.ifc");
        await File.WriteAllTextAsync(input, "x");
        var pidFile = Path.Combine(_root, "immediate-child.pid");
        var script = Path.Combine(_root, "immediate child worker.ps1");
        var escapedPidFile = pidFile.Replace("'", "''");
        await File.WriteAllTextAsync(script,
            "$child = Start-Process -PassThru -WindowStyle Hidden -FilePath (Join-Path $PSHOME 'powershell.exe') " +
            "-ArgumentList '-NoProfile -Command \"Start-Sleep -Seconds 30\"'; " +
            "$child.Id | Set-Content -LiteralPath '" + escapedPidFile + "'; " +
            "[Console]::In.ReadLine() | Out-Null; " +
            "[Console]::Out.WriteLine('{\"protocol\":\"bad\"}')");
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var output = Path.Combine(_root, "out");
        var executor = new IfcOptimizeJobExecutor(new IfcOptimizeOptions
        {
            ExecutablePath = shell,
            ExecutableArguments = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script]
        });

        var result = await executor.ExecuteAsync(CreateJob(input, output, "exact"), null, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("IFC_PROTOCOL_INVALID", result.ErrorCode);
        var childPid = int.Parse(await File.ReadAllTextAsync(pidFile));
        Assert.False(IsProcessAlive(childPid));
        Assert.Empty(Directory.EnumerateDirectories(output, "ifc-result-*"));
        Assert.Empty(Directory.EnumerateDirectories(output, ".ifc-attempt-*"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenTerminalEventIsMissing_DoesNotPublishArtifacts()
    {
        Directory.CreateDirectory(_root); var input = Path.Combine(_root, "source.ifc"); await File.WriteAllTextAsync(input, "x");
        var script = Path.Combine(_root, "no-terminal.ps1"); await File.WriteAllTextAsync(script, "$r = [Console]::In.ReadLine() | ConvertFrom-Json; Set-Content -LiteralPath $r.payload.output_path -Value 'optimized'; Set-Content -LiteralPath $r.payload.report_path -Value '{}'");
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"); var output = Path.Combine(_root, "out");
        var executor = new IfcOptimizeJobExecutor(new IfcOptimizeOptions { ExecutablePath = shell, ExecutableArguments = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script] });
        var reported = new List<int>();
        var result = await executor.ExecuteAsync(CreateJob(input, output, "exact"), (p, _) => { reported.Add(p.Progress); return Task.CompletedTask; }, CancellationToken.None);
        Assert.False(result.IsSuccess); Assert.Equal("IFC_PROTOCOL_INCOMPLETE", result.ErrorCode); Assert.Empty(Directory.EnumerateDirectories(output, "ifc-result-*"));
        Assert.DoesNotContain(100, reported);
    }

    private static ConnectorJobEnvelope CreateJob(string input, string output, string profile, string? executorId = null) => new(1, Guid.NewGuid().ToString("N"), "converter", CadProvider.AutoCad, JobOperation.Export, DateTime.UtcNow, JsonSerializer.SerializeToElement(new { inputPath = input, outputDirectory = output, profile }), ExecutorId: executorId ?? IfcOptimizeJobExecutor.Id);

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
