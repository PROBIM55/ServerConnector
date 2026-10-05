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
            var result = await _service.InstallAgrAsync(null, ct);
            AgrStatus = result.Probe.Ready ? "Blender установлен и готов к работе." : "Установка завершилась, но проверка Blender не пройдена.";
        }, text => AgrStatus = text), CanRun);
        ProbeTeklaCommand = new AsyncRelayCommand(() => RunAsync(async ct =>
        {
            var result = await _service.ProbeTeklaAsync(ct);
            TeklaStatus = result.IsReady ? "Связь с Tekla установлена." : "Tekla недоступна. Откройте модель в Tekla и повторите проверку.";
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
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); RaiseCommands();
    }
}
