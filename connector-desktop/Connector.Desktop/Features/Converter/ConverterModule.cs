using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Connector.AgrConversion.Service;
using Connector.Desktop.Mvvm;

namespace Connector.Desktop.Features.Converter;

// Домен «Конвертер» (C2b): модели АГР (FBX) → .glb.zip для Студии, локально, без сети (Р9). Модуль владеет службой
// конвертера, вид-моделью и таймером; «История» — второй подвкладкой над той же вид-моделью. Оболочка задаёт Log
// (AppendLog), как у VPN, и освобождает модуль при закрытии окна (отмена идущих частей).
public sealed class ConverterModule : IFeatureModule, IDisposable
{
    private readonly ConverterView _view;
    private readonly DispatcherTimer _timer;

    // Создаётся в потоке окна (ShellViewModel в конструкторе MainWindow): диспетчер — текущий, владелец диалогов и
    // окна «Отчёт» — главное окно на момент показа.
    public ConverterModule(Dispatcher? dispatcher = null, Func<Window?>? owner = null, IAgrPartConverter? partConverter = null)
    {
        dispatcher ??= Dispatcher.CurrentDispatcher;
        var ui = new WpfConverterUi(owner ?? (() => System.Windows.Application.Current?.MainWindow));
        ViewModel = new ConverterViewModel(
            log => new AgrConverterService(partConverter ?? new AgrGltfpackPartConverter(AgrGltfpackPartConverter.DefaultGltfpackPath),
                                           options: new AgrConverterServiceOptions { Log = log }),
            ui,
            ConverterDispatch.Post(dispatcher));
        _view = new ConverterView { DataContext = ViewModel };
        History = new ConverterHistoryModule(ViewModel);
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => ViewModel.Tick();
        _timer.Start();
    }

    public ConverterViewModel ViewModel { get; }
    public ConverterHistoryModule History { get; }

    public string Title => "Конвертация";
    public FrameworkElement View => _view;

    public Action<string>? Log
    {
        get => ViewModel.Log;
        set => ViewModel.Log = value;
    }

    public Task OnActivatedAsync()
    {
        ViewModel.Refresh();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer.Stop();
        ViewModel.Dispose();
    }
}

// Подвкладка «История» (кадр 5).
public sealed class ConverterHistoryModule : IFeatureModule
{
    private readonly ConverterViewModel _viewModel;
    private readonly ConverterHistoryView _view;

    public ConverterHistoryModule(ConverterViewModel viewModel)
    {
        _viewModel = viewModel;
        _view = new ConverterHistoryView { DataContext = viewModel };
    }

    public string Title => "История";
    public FrameworkElement View => _view;

    public Task OnActivatedAsync()
    {
        _viewModel.RefreshHistory();
        return Task.CompletedTask;
    }
}

// Диалоги, Проводник, браузер, буфер обмена и окно «Отчёт» — то, что вид-модель просит у окна.
internal sealed class WpfConverterUi : IConverterUi
{
    private readonly Func<Window?> _owner;

    public WpfConverterUi(Func<Window?> owner) => _owner = owner;

    private bool? Show(Microsoft.Win32.CommonDialog dialog) => _owner() is { } w ? dialog.ShowDialog(w) : dialog.ShowDialog();

    public IReadOnlyList<string>? PickZips()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Добавить zip частей АГР",
            Filter = "Архивы частей (*.zip)|*.zip",
            Multiselect = true,
        };
        return Show(dialog) == true ? dialog.FileNames : null;
    }

    public string? PickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Папка с zip частей или с распакованными частями" };
        return Show(dialog) == true ? dialog.FolderName : null;
    }

    public string? PickOutputRoot(string current)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Папка результатов конвертера",
            InitialDirectory = Directory.Exists(current) ? current : "",
        };
        return Show(dialog) == true ? dialog.FolderName : null;
    }

    public void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    public void ShowInFolder(string file)
    {
        if (File.Exists(file))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
        }
        else
        {
            OpenFolder(Path.GetDirectoryName(file) ?? "");
        }
    }

    public void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public void CopyText(string text)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Буфер обмена занят другим приложением — не падаем, пользователь повторит.
        }
    }

    public void ShowReport(ConverterReportViewModel report)
    {
        var window = new ConverterReportWindow(report) { Owner = _owner() };
        window.Show();
    }
}
