using System.Windows;
using Connector.Desktop.Mvvm;

namespace Connector.Desktop.Features.ConverterWorkspace;

public sealed class ConverterWorkspaceModule : IFeatureModule, IDisposable
{
    private readonly ConverterWorkspaceView _view;
    public ConverterWorkspaceModule(IConverterJobClient jobClient, Action<Action>? post = null)
    {
        ViewModel = new ConverterWorkspaceViewModel(jobClient, post);
        _view = new ConverterWorkspaceView { DataContext = ViewModel };
    }
    public ConverterWorkspaceViewModel ViewModel { get; }
    public string Title => "Рабочие задания";
    public FrameworkElement View => _view;
    public Task OnActivatedAsync() => Task.CompletedTask;
    public void Dispose() => ViewModel.Dispose();
}
