using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.IO;
using Connector.Desktop.Mvvm;
using Platform.Connector.Core;

namespace Connector.Desktop.Features.ConverterWorkspace;

public sealed class ConverterWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly IConverterJobClient _client;
    private readonly Action<Action> _post;
    private CancellationTokenSource? _batchCancellation;
    private readonly Dictionary<string, CancellationTokenSource> _jobCancellations = new(StringComparer.OrdinalIgnoreCase);
    private string _fbxInputPath = "", _ifcInputPath = "", _outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Structura Connector", "Результаты"), _ifcProfile = "exact", _message = "Выберите исходные данные для конвертации.";
    private bool _isSubmitting;
    private ConverterWorkspaceJob? _selectedJob;

    public ConverterWorkspaceViewModel(IConverterJobClient client, Action<Action>? post = null)
    {
        _client = client;
        _post = post ?? (action => action());
        _client.StatusChanged += OnStatusChanged;
        SubmitFbxCommand = new AsyncRelayCommand(() => SubmitAsync(FbxGlbExecutor, FbxInputPath, "default"), CanSubmitFbx);
        SubmitIfcCommand = new AsyncRelayCommand(() => SubmitIfcBatchAsync(ConverterJobOperation.Optimize), CanSubmitIfc);
        AnalyzeIfcCommand = new AsyncRelayCommand(() => SubmitIfcBatchAsync(ConverterJobOperation.Analyze), CanSubmitIfc);
        CancelCommand = new RelayCommand(CancelActive, () => IsSubmitting);
        OpenResultCommand = new RelayCommand(OpenResult, () => SelectedJob?.OutputPath is { Length: > 0 });
        OpenReportCommand = new RelayCommand(OpenReport, () => SelectedJob?.ReportPath is { Length: > 0 });
        OpenValidationReportCommand = new RelayCommand(OpenValidationReport, () => SelectedJob is { Operation: ConverterJobOperation.Optimize, ReportPath.Length: > 0 });
        PickFbxInputCommand = new RelayCommand(PickFbxInput);
        PickFbxFolderCommand = new RelayCommand(PickFbxFolder);
        PickIfcInputCommand = new RelayCommand(PickIfcInput);
        PickOutputCommand = new RelayCommand(PickOutputDirectory);
    }

    public const string FbxGlbExecutor = "converter.fbx-glb";
    public const string IfcOptimizeExecutor = "converter.ifc-optimize";
    public const string IfcAnalyzeExecutor = "converter.ifc-analyze";
    public ObservableCollection<ConverterWorkspaceJob> Jobs { get; } = new();
    public ObservableCollection<string> IfcInputPaths { get; } = new();
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenResultCommand { get; }
    public RelayCommand OpenReportCommand { get; }
    public RelayCommand OpenValidationReportCommand { get; }
    public RelayCommand PickFbxInputCommand { get; }
    public RelayCommand PickFbxFolderCommand { get; }
    public RelayCommand PickIfcInputCommand { get; }
    public RelayCommand PickOutputCommand { get; }
    public AsyncRelayCommand SubmitFbxCommand { get; }
    public AsyncRelayCommand SubmitIfcCommand { get; }
    public AsyncRelayCommand AnalyzeIfcCommand { get; }
    public string FbxInputPath { get => _fbxInputPath; set { if (SetProperty(ref _fbxInputPath, value)) RefreshCommands(); } }
    public string IfcInputPath { get => _ifcInputPath; set { if (SetProperty(ref _ifcInputPath, value)) RefreshCommands(); } }
    public string OutputDirectory { get => _outputDirectory; set { if (SetProperty(ref _outputDirectory, value)) RefreshCommands(); } }
    public string IfcProfile { get => _ifcProfile; set => SetProperty(ref _ifcProfile, value); }
    // The UI selects the product before invoking a command. Each submission copies
    // this value into its immutable request, so a later tab change cannot retag it.
    public ConnectorProductId Product { get; set; } = ConnectorProductId.Structura;
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public bool IsSubmitting { get => _isSubmitting; private set { if (SetProperty(ref _isSubmitting, value)) RefreshCommands(); } }
    public ConverterWorkspaceJob? SelectedJob { get => _selectedJob; set { if (SetProperty(ref _selectedJob, value)) { OpenResultCommand.RaiseCanExecuteChanged(); OpenReportCommand.RaiseCanExecuteChanged(); OpenValidationReportCommand.RaiseCanExecuteChanged(); } } }

    private bool CanSubmitFbx() => !IsSubmitting && Valid(FbxInputPath);
    private bool CanSubmitIfc() => !IsSubmitting && (IfcInputPaths.Count > 0 || Valid(IfcInputPath));
    private bool Valid(string path) => !string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(OutputDirectory);

    private async Task SubmitAsync(string executorId, string inputPath, string profile)
    {
        ConnectorProductId product = Product;
        var requestId = Guid.NewGuid().ToString("N");
        var job = new ConverterWorkspaceJob(requestId, executorId, inputPath.Trim(), OutputDirectory.Trim(), profile, product: product);
        Jobs.Insert(0, job); SelectedJob = job;
        IsSubmitting = true; Message = "Задание передано Connector Agent.";
        using var cancellation = _batchCancellation = new CancellationTokenSource();
        try { Apply(await _client.SubmitAsync(new ConverterJobRequest(requestId, executorId, job.InputPath, job.OutputDirectory, profile, ConverterJobOperation.Optimize, product), cancellation.Token)); }
        catch (OperationCanceledException) { job.Set("Отменено", null, "Задание отменено.", null); Message = job.Message; }
        catch (Exception ex) { job.Set("Ошибка", null, "Не удалось выполнить задание: " + ex.Message, null); Message = job.Message; }
        finally { _batchCancellation = null; IsSubmitting = false; }
    }
    public void SetIfcInputPaths(IEnumerable<string> paths)
    {
        IfcInputPaths.Clear();
        foreach (var path in paths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            IfcInputPaths.Add(path);
        IfcInputPath = IfcInputPaths.FirstOrDefault() ?? "";
        RefreshCommands();
    }

    public Task SubmitIfcBatchAsync() => SubmitIfcBatchAsync(ConverterJobOperation.Optimize);
    public Task AnalyzeIfcBatchAsync() => SubmitIfcBatchAsync(ConverterJobOperation.Analyze);

    private async Task SubmitIfcBatchAsync(ConverterJobOperation operation)
    {
        ConnectorProductId product = Product;
        var inputs = IfcInputPaths.Count > 0 ? IfcInputPaths.ToArray() : [IfcInputPath.Trim()];
        if (inputs.Any(path => !Valid(path))) return;
        IsSubmitting = true;
        _batchCancellation = new CancellationTokenSource();
        Message = "Задания переданы в общую очередь Connector Agent.";
        var executorId = operation == ConverterJobOperation.Optimize ? IfcOptimizeExecutor : IfcAnalyzeExecutor;
        var profile = operation == ConverterJobOperation.Optimize ? IfcProfile : "default";
        try
        {
            await Task.WhenAll(inputs.Select(input => SubmitOneAsync(executorId, input, profile, operation, product, _batchCancellation.Token)));
        }
        finally
        {
            _batchCancellation.Dispose();
            _batchCancellation = null;
            IsSubmitting = false;
        }
    }

    private async Task SubmitOneAsync(string executorId, string inputPath, string profile, ConverterJobOperation operation, ConnectorProductId product, CancellationToken batchCancellation)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var job = new ConverterWorkspaceJob(requestId, executorId, inputPath.Trim(), OutputDirectory.Trim(), profile, operation, product);
        Jobs.Insert(0, job); SelectedJob = job;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(batchCancellation);
        _jobCancellations.Add(requestId, cancellation);
        try
        {
            Apply(await _client.SubmitAsync(new ConverterJobRequest(requestId, executorId, job.InputPath, job.OutputDirectory, profile, operation, product), cancellation.Token));
        }
        catch (OperationCanceledException) { job.Set("Отменено", null, "Задание отменено.", null); Message = job.Message; }
        catch (Exception ex) { job.Set("Ошибка", null, "Не удалось выполнить задание: " + ex.Message, null); Message = job.Message; }
        finally { _jobCancellations.Remove(requestId); cancellation.Dispose(); }
    }
    private void OnStatusChanged(ConnectorJobStatusEnvelope status) => _post(() => Apply(status));
    public void RestoreSavedJobs(IReadOnlyList<LocalJobSnapshot> snapshots)
    {
        foreach (var saved in snapshots.OrderBy(item => item.Job.CreatedAtUtc))
        {
            var envelope = saved.Job;
            if (envelope.ModuleId != "converters" || envelope.Scope?.ScopeKind != ConnectorScopeKind.DeviceLocal ||
                envelope.EffectiveExecutorId is not (FbxGlbExecutor or IfcOptimizeExecutor or IfcAnalyzeExecutor) ||
                Jobs.Any(item => item.RequestId == envelope.RequestId)) continue;
            var payload = envelope.Payload;
            if (payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty("inputPath", out var input) || input.ValueKind != JsonValueKind.String ||
                !payload.TryGetProperty("outputDirectory", out var output) || output.ValueKind != JsonValueKind.String) continue;
            var profile = payload.TryGetProperty("profile", out var selected) && selected.ValueKind == JsonValueKind.String
                ? selected.GetString() ?? "default" : "default";
            var job = new ConverterWorkspaceJob(envelope.RequestId, envelope.EffectiveExecutorId,
                input.GetString() ?? "", output.GetString() ?? "", profile,
                envelope.EffectiveExecutorId == IfcAnalyzeExecutor ? ConverterJobOperation.Analyze : ConverterJobOperation.Optimize,
                envelope.Scope.ProductId);
            Jobs.Insert(0, job);
            Apply(saved.LastStatus);
        }
        SelectedJob ??= Jobs.FirstOrDefault();
        RefreshCommands();
    }
    private void Apply(ConnectorJobStatusEnvelope status)
    {
        var job = Jobs.FirstOrDefault(x => x.RequestId == status.RequestId); if (job is null) return;
        job.Set(RussianStatus(status.Status), status.Progress, status.Message ?? status.ErrorCode ?? "Нет сообщения от Agent.", status.Result);
        Message = job.Message; OpenResultCommand.RaiseCanExecuteChanged(); OpenReportCommand.RaiseCanExecuteChanged(); OpenValidationReportCommand.RaiseCanExecuteChanged();
    }
    private void CancelActive()
    {
        _batchCancellation?.Cancel();
        foreach (var cancellation in _jobCancellations.Values.ToArray()) cancellation.Cancel();
        Message = "Запрошена отмена только заданий текущего пакета.";
    }
    private void OpenResult()
    {
        var path = SelectedJob?.OutputPath; if (string.IsNullOrWhiteSpace(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }
    private void OpenReport() { var path = SelectedJob?.ReportPath; if (!string.IsNullOrWhiteSpace(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    private void OpenValidationReport()
    {
        if (SelectedJob?.Operation == ConverterJobOperation.Optimize) OpenReport();
    }
    private void PickFbxInput() { var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Архив части АГР|*.zip" }; if (dialog.ShowDialog() == true) FbxInputPath = dialog.FileName; }
    private void PickFbxFolder() { var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Папка части АГР с FBX и текстурами" }; if (dialog.ShowDialog() == true) FbxInputPath = dialog.FolderName; }
    private void PickIfcInput() { var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "IFC модель|*.ifc" }; if (dialog.ShowDialog() == true) IfcInputPath = dialog.FileName; }
    private void PickOutputDirectory() { var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(OutputDirectory) ? OutputDirectory : "" }; if (dialog.ShowDialog() == true) OutputDirectory = dialog.FolderName; }
    private static string RussianStatus(JobStatus status) => status switch { JobStatus.Queued => "В очереди", JobStatus.Picked => "Принято исполнителем", JobStatus.Running => "Выполняется", JobStatus.Success => "Готово", JobStatus.Error => "Ошибка", JobStatus.Cancelled => "Отменено", JobStatus.Timeout => "Превышено время", JobStatus.Interrupted => "Прервано", _ => "Неизвестное состояние" };
    private void RefreshCommands() { SubmitFbxCommand.RaiseCanExecuteChanged(); SubmitIfcCommand.RaiseCanExecuteChanged(); AnalyzeIfcCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged(); }
    public void Dispose()
    {
        _client.StatusChanged -= OnStatusChanged;
        _batchCancellation?.Cancel();
        foreach (var cancellation in _jobCancellations.Values) cancellation.Cancel();
    }
}

public sealed class ConverterWorkspaceJob : ObservableObject
{
    private string _state = "В очереди", _message = "Ожидает исполнителя.", _outputPath = "", _reportPath = ""; private int? _progress;
    public ConverterWorkspaceJob(string requestId, string executorId, string inputPath, string outputDirectory, string profile, ConverterJobOperation operation = ConverterJobOperation.Optimize, ConnectorProductId product = ConnectorProductId.Structura) { RequestId=requestId; ExecutorId=executorId; InputPath=inputPath; OutputDirectory=outputDirectory; Profile=profile; Operation=operation; Product=product; }
    public string RequestId { get; } public string ExecutorId { get; } public string InputPath { get; } public string OutputDirectory { get; } public string Profile { get; } public ConverterJobOperation Operation { get; } public ConnectorProductId Product { get; }
    public string DisplayName => ExecutorId == ConverterWorkspaceViewModel.IfcOptimizeExecutor ? "Оптимизация IFC" : ExecutorId == ConverterWorkspaceViewModel.IfcAnalyzeExecutor ? "Анализ IFC" : "АГР → GLB";
    public string State { get => _state; private set => SetProperty(ref _state, value); } public int? Progress { get => _progress; private set => SetProperty(ref _progress, value); } public string Message { get => _message; private set => SetProperty(ref _message, value); } public string OutputPath { get => _outputPath; private set => SetProperty(ref _outputPath, value); } public string ReportPath { get => _reportPath; private set => SetProperty(ref _reportPath, value); }
    public void Set(string state, int? progress, string message, JsonElement? result) { State=state; Progress=progress; Message=message; if (result is { } data) { if (data.TryGetProperty("outputPath", out var output)) OutputPath=output.GetString() ?? ""; if (data.TryGetProperty("reportPath", out var report)) ReportPath=report.GetString() ?? ""; else if (data.TryGetProperty("manifestPath", out var manifest)) ReportPath=manifest.GetString() ?? ""; } }
}
