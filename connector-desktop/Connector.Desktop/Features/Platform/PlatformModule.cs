using System.Windows;
using System.Windows.Threading;
using Connector.Desktop.Mvvm;
using Connector.Platform;

namespace Connector.Desktop.Features.Platform;

/// <summary>Первый модуль Platform: явное подключение и наблюдение за runtime.</summary>
public sealed class PlatformModule : IFeatureModule, IAsyncDisposable
{
    private readonly PlatformRuntimeFacade _runtime;
    private readonly PlatformViewModel _viewModel;
    private readonly PlatformView _view;
    private readonly DispatcherTimer _snapshotTimer;
    private Action<string>? _log;

    public PlatformModule(PlatformRuntimeFacade runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _viewModel = new PlatformViewModel(_runtime);
        _view = new PlatformView { DataContext = _viewModel };
        _viewModel.ClearTokenRequested += _view.ClearToken;
        _snapshotTimer = new DispatcherTimer(DispatcherPriority.Background, _view.Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _snapshotTimer.Tick += (_, _) => _viewModel.RefreshSnapshot();
        _snapshotTimer.Start();
    }

    public string Title => "Platform";
    public FrameworkElement View => _view;
    public IPlatformConnectionApi NativeApi => _runtime;

    public Action<string>? Log
    {
        get => _log;
        set
        {
            _log = value;
            _runtime.Log = message =>
            {
                if (_view.Dispatcher.CheckAccess())
                {
                    _log?.Invoke(message);
                }
                else
                {
                    _view.Dispatcher.BeginInvoke(() => _log?.Invoke(message));
                }
            };
        }
    }

    public Task OnActivatedAsync()
    {
        _viewModel.RefreshSnapshot();
        return Task.CompletedTask;
    }

    public string? GetRestartBlockReason() => _runtime.GetRestartBlockReason();

    public async ValueTask DisposeAsync()
    {
        _snapshotTimer.Stop();
        _viewModel.ClearTokenRequested -= _view.ClearToken;
        await _runtime.DisposeAsync();
    }
}
