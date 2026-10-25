using System.Windows;
using Connector.Desktop.Mvvm;
using Connector.Desktop.Services;

namespace Connector.Desktop.Features.Tekla.ModelSharing;

// Tekla domain module: self-hosted Model Sharing provisioning. Owns the ViewModel + View (composition root
// for the feature). The shell wires identity / server / persistence into the VM via Initialize(...) at
// composition time — the VM never reaches into AppSettings directly.
public sealed class ModelSharingModule : IFeatureModule
{
    private readonly ModelSharingView _view;
    private readonly ModelSharingViewModel _viewModel;

    public ModelSharingModule(ModelSharingProvisioningService? service = null)
    {
        // The shell supplies the confirmation (a dialog of the page) and the progress (an entry of «Задания»);
        // the result reaches the page as the outcome of the command.
        _viewModel = new ModelSharingViewModel(service);
        _view = new ModelSharingView { DataContext = _viewModel };
    }

    public string Title => "Model Sharing";
    public FrameworkElement View => _view;

    // Shell wires these after construction: Log -> AppendLog; OnProvisioned -> persist ModelSharing* keys + Save();
    // Initialize -> push current device identity / server endpoint / persisted bin / Extensions fallback into the VM.
    // ConfirmAsync and BeginProgress are supplied by the shell; without a confirmation nothing is changed.
    public Action<string>? Log { get => _viewModel.Log; set => _viewModel.Log = value; }
    public Action<ModelSharingProvisionedInfo>? OnProvisioned { get => _viewModel.OnProvisioned; set => _viewModel.OnProvisioned = value; }
    public Func<PageConfirmation, Task<bool>>? ConfirmAsync { get => _viewModel.ConfirmAsync; set => _viewModel.ConfirmAsync = value; }
    public Func<ModelSharingProgressHandle?>? BeginProgress { get => _viewModel.BeginProgress; set => _viewModel.BeginProgress = value; }

    public void Initialize(ModelSharingIdentity identity, string serverHost, int serverPort,
        string persistedTeklaBin, string extensionsLocalPath)
        => _viewModel.Initialize(identity, serverHost, serverPort, persistedTeklaBin, extensionsLocalPath);

    public Task OnActivatedAsync()
    {
        _viewModel.RefreshStatus();
        return Task.CompletedTask;
    }
}
