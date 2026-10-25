using System.Windows;
using Connector.Desktop.Mvvm;
using Connector.Desktop.Services;

namespace Connector.Desktop.Features.Tekla.Patching;

// Tekla domain module: IFC-export patching. Owns the ViewModel + View (composition root for the feature).
public sealed class PatchingModule : IFeatureModule
{
    private readonly PatchingView _view;
    private readonly PatchingViewModel _viewModel;

    public PatchingModule(IfcExportPatchService? service = null)
    {
        // Any-user self-patch guardrail: the shell supplies the confirmation (a dialog of the page) before
        // licensed Tekla binaries are touched; without it the patch is not applied.
        _viewModel = new PatchingViewModel(service);
        _view = new PatchingView { DataContext = _viewModel };
    }

    public string Title => "Патчинг";
    public FrameworkElement View => _view;

    public Func<PageConfirmation, Task<bool>>? ConfirmAsync { get => _viewModel.ConfirmAsync; set => _viewModel.ConfirmAsync = value; }

    public Task OnActivatedAsync()
    {
        _viewModel.RefreshStatus();
        return Task.CompletedTask;
    }
}
