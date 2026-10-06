using System.Windows.Input;
using Connector.Desktop.Mvvm;
using Connector.Platform;
using Platform.Connector.Core;

namespace Connector.Desktop.Features.Platform;

public sealed class PlatformViewModel : ObservableObject
{
    private readonly PlatformRuntimeFacade _runtime;
    private string _token = string.Empty;
    private string _runtimeStatus = "Остановлен";
    private string _jobStatus = "Задач ещё не было.";
    private string _restartBlockReason = "Обновление не блокируется подключением Platform.";
    private string? _operationError;

    public PlatformViewModel(PlatformRuntimeFacade runtime)
    {
        _runtime = runtime;
        _runtime.SnapshotChanged += _ => RefreshSnapshot();
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !_runtime.RuntimeEnabled);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => _runtime.RuntimeEnabled);
        ImportSettingsCommand = new RelayCommand(ImportLegacySettings, () => !_runtime.RuntimeEnabled);
        RefreshSnapshot();
    }

    public string ServerUrl
    {
        get => _runtime.AutomaticServerUrl;
        set
        {
            // The endpoint is assigned by PlatformConnectionPolicy. A two-way
            // legacy binding may attempt to write, but cannot change it.
            if (!string.Equals(value, _runtime.AutomaticServerUrl, StringComparison.Ordinal))
            {
                OnPropertyChanged();
            }
        }
    }
    public string Token { get => _token; set => SetProperty(ref _token, value); }
    public string RuntimeStatus { get => _runtimeStatus; private set => SetProperty(ref _runtimeStatus, value); }
    public string JobStatus { get => _jobStatus; private set => SetProperty(ref _jobStatus, value); }
    public string RestartBlockReason { get => _restartBlockReason; private set => SetProperty(ref _restartBlockReason, value); }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ImportSettingsCommand { get; }
    public event Action? ClearTokenRequested;

    public void RefreshSnapshot()
    {
        var snapshot = _runtime.Snapshot;
        var connection = _runtime.Connection;
        RuntimeStatus = _operationError ?? $"{ConnectorPhasePresentation.GetRussianLabel(connection.Phase)}; связь с сервером: {(connection.IsConnected ? "доступна" : "нет данных")}";
        JobStatus = string.IsNullOrWhiteSpace(snapshot.LastJobStatus)
            ? "Задач ещё не было."
            : $"{snapshot.LastJobStatus}: {snapshot.LastJobMessage ?? snapshot.LastRequestId ?? "без сообщения"}";
        RestartBlockReason = _runtime.GetRestartBlockReason() ?? "Обновление не блокируется подключением Platform.";
        ((AsyncRelayCommand)ConnectCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)DisconnectCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ImportSettingsCommand).RaiseCanExecuteChanged();
    }

    private async Task ConnectAsync()
    {
        _operationError = null;
        var completed = false;
        try
        {
            var result = await _runtime.ConnectAsync(Token);
            completed = result.Accepted;
            if (!result.Accepted) RuntimeStatus = _operationError = result.Message;
        }
        catch (Exception ex)
        {
            RuntimeStatus = _operationError = "Ошибка подключения: " + ex.Message;
        }
        finally
        {
            Token = string.Empty;
            ClearTokenRequested?.Invoke();
            if (completed) RefreshSnapshot();
        }
    }

    private async Task DisconnectAsync()
    {
        _operationError = null;
        var completed = false;
        try
        {
            var result = await _runtime.DisconnectAsync();
            completed = result.Accepted;
            if (!result.Accepted) RuntimeStatus = _operationError = result.Message;
        }
        catch (Exception ex)
        {
            RuntimeStatus = _operationError = "Ошибка отключения: " + ex.Message;
        }
        finally
        {
            if (completed) RefreshSnapshot();
        }
    }

    private void ImportLegacySettings()
    {
        _operationError = null;
        var completed = false;
        try
        {
            var result = _runtime.ImportLegacyCredential();
            completed = result.Accepted;
            if (!result.Accepted) RuntimeStatus = _operationError = result.Message;
        }
        catch (Exception ex)
        {
            RuntimeStatus = _operationError = "Ошибка импорта: " + ex.Message;
        }
        if (completed) RefreshSnapshot();
    }
}
