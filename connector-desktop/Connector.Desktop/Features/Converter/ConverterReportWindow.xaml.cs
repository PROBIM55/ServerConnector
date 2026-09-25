using System.Windows;

namespace Connector.Desktop.Features.Converter;

public partial class ConverterReportWindow : Window
{
    public ConverterReportWindow(ConverterReportViewModel report)
    {
        InitializeComponent();
        DataContext = report;
        CloseButton.Click += (_, _) => Close();
    }
}
