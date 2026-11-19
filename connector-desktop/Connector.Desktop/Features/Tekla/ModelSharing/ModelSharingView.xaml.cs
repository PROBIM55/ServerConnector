using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Connector.Desktop.Features.Tekla.ModelSharing;

// View is passive: its ViewModel is supplied via DataContext by ModelSharingModule (the shell owns the VM/service).
// The only logic here is the WinForms folder picker (UI-only); progress is an entry of the page journal.
public partial class ModelSharingView : System.Windows.Controls.UserControl
{
    public ModelSharingView()
    {
        InitializeComponent();
        Loaded += (_, _) => Vm?.RefreshStatus();
    }

    private ModelSharingViewModel? Vm => DataContext as ModelSharingViewModel;

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Выберите папку bin Tekla Structures",
            SelectedPath = (vm.TeklaBin ?? string.Empty).Trim()
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            // Setting TeklaBin re-runs RefreshStatus through the VM setter.
            vm.TeklaBin = dialog.SelectedPath;
        }
    }
}
