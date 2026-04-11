using System.Windows;

namespace Connector.Desktop;

public partial class OperationProgressWindow : Window
{
    private bool _finished;

    public OperationProgressWindow(string title, string firstStep)
    {
        InitializeComponent();
        TitleTextBlock.Text = string.IsNullOrWhiteSpace(title) ? "Операция выполняется" : title.Trim();
        StepTextBlock.Text = string.IsNullOrWhiteSpace(firstStep) ? "Подготовка" : firstStep.Trim();
    }

    public void UpdateStep(string step, string details, int currentStep, int totalSteps, TimeSpan eta)
    {
        Dispatcher.Invoke(() =>
        {
            if (_finished)
            {
                return;
            }

            var safeTotal = Math.Max(1, totalSteps);
            var safeCurrent = Math.Max(0, Math.Min(currentStep, safeTotal));
            ProgressBar.Value = safeCurrent * 100d / safeTotal;
            StepTextBlock.Text = $"Шаг {safeCurrent} из {safeTotal}: {step}";
            DetailTextBlock.Text = string.IsNullOrWhiteSpace(details) ? "Идет выполнение операции" : details.Trim();
            EtaTextBlock.Text = eta <= TimeSpan.Zero
                ? "Оценка времени: завершаем"
                : "Оценка времени: примерно " + FormatEta(eta);
        });
    }

    public void UpdateDetail(string details)
    {
        Dispatcher.Invoke(() =>
        {
            if (_finished)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(details))
            {
                DetailTextBlock.Text = details.Trim();
            }
        });
    }

    public void MarkSucceeded(string details)
    {
        Dispatcher.Invoke(() =>
        {
            _finished = true;
            ProgressBar.Value = 100;
            StepTextBlock.Text = "Операция завершена";
            DetailTextBlock.Text = string.IsNullOrWhiteSpace(details)
                ? "Все шаги выполнены успешно"
                : details.Trim();
            EtaTextBlock.Text = "Оценка времени: завершено";
            CloseButton.Content = "Закрыть";
        });
    }

    public void MarkFailed(string details)
    {
        Dispatcher.Invoke(() =>
        {
            _finished = true;
            StepTextBlock.Text = "Операция остановлена с ошибкой";
            DetailTextBlock.Text = string.IsNullOrWhiteSpace(details)
                ? "Не удалось завершить операцию"
                : details.Trim();
            EtaTextBlock.Text = "Оценка времени: не завершено";
            CloseButton.Content = "Закрыть";
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalSeconds < 60)
        {
            return Math.Max(1, (int)Math.Round(eta.TotalSeconds)) + " сек";
        }

        if (eta.TotalMinutes < 60)
        {
            return Math.Max(1, (int)Math.Round(eta.TotalMinutes)) + " мин";
        }

        return Math.Max(1, (int)Math.Round(eta.TotalHours)) + " ч";
    }
}
