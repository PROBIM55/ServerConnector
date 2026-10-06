using System.Windows;
using Connector.Desktop.Mvvm;
using Connector.Platform;

namespace Connector.Desktop.Features.PlatformTools;

public sealed class PlatformToolsModule : IFeatureModule, IDisposable
{
    private readonly PlatformToolsService _service;
    private readonly PlatformToolsView _view;
    public PlatformToolsModule(PlatformToolsService? service = null)
    {
        _service = service ?? new PlatformToolsService();
        ViewModel = new PlatformToolsViewModel(_service);
        _view = new PlatformToolsView { DataContext = ViewModel };
    }
    public PlatformToolsService NativeApi => _service;
    public PlatformToolsViewModel ViewModel { get; }
    public string Title => "Инструменты Platform";
    public FrameworkElement View => _view;
    public Task OnActivatedAsync() => Task.CompletedTask;
    public void Dispose() { ViewModel.Dispose(); _service.Dispose(); }
}
