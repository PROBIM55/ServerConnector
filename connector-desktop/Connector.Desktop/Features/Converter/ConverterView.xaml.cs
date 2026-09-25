using System.Windows;

namespace Connector.Desktop.Features.Converter;

public partial class ConverterView : System.Windows.Controls.UserControl
{
    public ConverterView()
    {
        InitializeComponent();
        // Подписка здесь, а не в XAML: в пространстве Features имя «Connector» закрыто Features.Connector.
        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    private void OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    // Zip частей и папки, перетащенные из Проводника, — сразу в очередь (кадр 1: «Перетащите сюда…»).
    private void OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (DataContext is ConverterViewModel vm && e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            vm.AddPaths(paths);
        }
        e.Handled = true;
    }
}
