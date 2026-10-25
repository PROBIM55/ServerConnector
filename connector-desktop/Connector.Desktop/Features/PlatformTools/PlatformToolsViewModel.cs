using Connector.Desktop.Mvvm;
using Connector.Platform;

namespace Connector.Desktop.Features.PlatformTools;

public sealed class PlatformToolsViewModel : ObservableObject, IDisposable
{
    private readonly PlatformToolsService _service;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private string _agrStatus = "Не проверено", _teklaStatus = "Не проверено", _autoCadStatus = "Не проверено";
    private bool _isBusy, _disposed;
    private PlatformTeklaStatus? _teklaInfo;
    private DateTimeOffset? _teklaCheckedAtUtc;
    private AgrInstallProgressView? _agrInstallProgress;

    public PlatformToolsViewModel(PlatformToolsService service)
    {
        _service = service;
        ProbeAgrCommand = new AsyncRelayCommand(() => RunAsync(async ct =>
        {
            var probe = await _service.ProbeAgrAsync(ct);
            AgrStatus = probe.Ready ? "Blender готов к работе." : "Blender ещё не готов. Установите его или повторите проверку.";
        }, text => AgrStatus = text), CanRun);
        InstallAgrCommand = new AsyncRelayCommand(() => RunAsync(async ct =>
        {
            AgrStatus = "Скачивание и установка Blender…";
            AgrInstallProgress = new AgrInstallProgressView("download", 0, null);
            try
            {
                var result = await _service.InstallAgrAsync(new InstallProgress(this), ct);
                AgrStatus = result.Probe.Ready ? "Blender установлен и готов к работе." : "Установка завершилась, но проверка Blender не пройдена.";
            }
            finally { AgrInstallProgress = null; }
        }, text => AgrStatus = text), CanRun);
        ProbeTeklaCommand = new AsyncRelayCommand(() => RunAsync(async ct =>
        {
            var result = await _service.ProbeTeklaAsync(ct);
            TeklaInfo = result.Value;
            TeklaCheckedAtUtc = DateTimeOffset.UtcNow;
            TeklaStatus = result.IsReady && result.Value?.Connected == true
                ? "Связь с Tekla установлена."
                : "Tekla недоступна. Откройте модель в Tekla и повторите проверку.";
        }, text => TeklaStatus = text), CanRun);
        ProbeAutoCadCommand = new AsyncRelayCommand(() => RunAsync(async ct =>
        {
            var result = await _service.ProbeAutoCadAsync(ct);
            AutoCadStatus = result.IsReady ? result.Message : "Открытые сеансы AutoCAD не найдены.";
        }, text => AutoCadStatus = text), CanRun);
        CancelCommand = new RelayCommand(() => _operation?.Cancel(), () => IsBusy);
    }

    public AsyncRelayCommand ProbeAgrCommand { get; }
    public AsyncRelayCommand InstallAgrCommand { get; }
    public AsyncRelayCommand ProbeTeklaCommand { get; }
    public AsyncRelayCommand ProbeAutoCadCommand { get; }
    public RelayCommand CancelCommand { get; }
    public PlatformToolsService NativeApi => _service;
    public string AgrStatus { get => _agrStatus; private set => SetProperty(ref _agrStatus, value); }
    public string TeklaStatus { get => _teklaStatus; private set => SetProperty(ref _teklaStatus, value); }
    public string AutoCadStatus { get => _autoCadStatus; private set => SetProperty(ref _autoCadStatus, value); }
    /// <summary>Facts of the last bridge check (Tekla version, open model); null until checked or after a failure.</summary>
    public PlatformTeklaStatus? TeklaInfo { get => _teklaInfo; private set => SetProperty(ref _teklaInfo, value); }
    public DateTimeOffset? TeklaCheckedAtUtc { get => _teklaCheckedAtUtc; private set => SetProperty(ref _teklaCheckedAtUtc, value); }
    /// <summary>Downloaded and total bytes of the Blender archive while it is installed; null otherwise.</summary>
    public AgrInstallProgressView? AgrInstallProgress { get => _agrInstallProgress; private set => SetProperty(ref _agrInstallProgress, value); }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommands(); } }
    private bool CanRun() => !_disposed && !IsBusy;

    public async Task<PlatformToolResult<IReadOnlyList<PlatformAutoCadSession>>> RefreshAutoCadSessionsAsync(
        CancellationToken cancellationToken)
    {
        var result = await _service.ListAutoCadSessionsAsync(cancellationToken);
        AutoCadStatus = result.Message;
        return result;
    }

    public async Task<PlatformToolResult<PlatformAutoCadPing>> SelectAndPingAutoCadAsync(
        string? sessionKey,
        CancellationToken cancellationToken)
    {
        var result = await _service.SelectAndPingAutoCadAsync(sessionKey, cancellationToken);
        AutoCadStatus = result.Message;
        return result;
    }

    public async Task<PlatformToolResult<PlatformTeklaBridgeState>> EnsureTeklaBridgeAsync(
        bool restart,
        CancellationToken cancellationToken)
    {
        var result = await _service.EnsureTeklaBridgeAsync(restart, cancellationToken);
        TeklaStatus = result.Message;
        TeklaCheckedAtUtc = DateTimeOffset.UtcNow;
        if (!result.IsReady) TeklaInfo = null;
        return result;
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, Action<string> setStatus)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = cancellation; IsBusy = true;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { setStatus("Действие отменено."); }
        catch (Exception) { setStatus("Не удалось выполнить действие. Подробности доступны в журнале."); }
        finally { _operation = null; IsBusy = false; }
    }

    private void RaiseCommands()
    {
        ProbeAgrCommand.RaiseCanExecuteChanged(); InstallAgrCommand.RaiseCanExecuteChanged();
        ProbeTeklaCommand.RaiseCanExecuteChanged(); ProbeAutoCadCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged();
    }
    /// <summary>Publishes download progress in whole megabytes so the page is not redrawn for every buffer.</summary>
    private sealed class InstallProgress(PlatformToolsViewModel owner) : IProgress<global::Platform.Connector.Compute.Agr.AgrRuntimeInstallProgress>
    {
        public void Report(global::Platform.Connector.Compute.Agr.AgrRuntimeInstallProgress value)
        {
            var next = new AgrInstallProgressView(value.Phase, Math.Max(0, value.CompletedBytes), value.TotalBytes);
            var previous = owner.AgrInstallProgress;
            if (previous is not null && previous.Phase == next.Phase &&
                previous.CompletedBytes / (1024 * 1024) == next.CompletedBytes / (1024 * 1024)) return;
            owner.AgrInstallProgress = next;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); RaiseCommands();
    }
}

public sealed record AgrInstallProgressView(string Phase, long CompletedBytes, long? TotalBytes);
