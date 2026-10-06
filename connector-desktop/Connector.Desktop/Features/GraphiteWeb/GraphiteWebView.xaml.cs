using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace Connector.Desktop.Features.GraphiteWeb;

/// <summary>
/// Hosts the approved local Graphite UI and exposes one validated JSON command boundary.
/// Initialization is explicit so constructing the control never starts WebView2 or performs I/O.
/// </summary>
public partial class GraphiteWebView : System.Windows.Controls.UserControl, IAsyncDisposable
{
    private const string CommandFailedError = "Команда не выполнена.";
    private const string CommandCancelledError = "Операция отменена.";
    private const int SeenRequestLimit = 512;

    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _pendingOperations = [];
    private readonly HashSet<Guid> _seenRequestIds = [];
    private readonly Queue<Guid> _seenRequestOrder = new();

    private Func<GraphiteRpcRequest, CancellationToken, Task<object?>>? _commandHandler;
    private Action<string>? _log;
    private Func<object>? _documentSnapshot;
    private Task? _initializationTask;
    private Task? _disposalTask;
    private string? _userDataDirectory;
    private string? _pendingSnapshotJson;
    private bool _documentReady;
    private bool _eventsAttached;
    private bool _disposed;

    public GraphiteWebView()
    {
        InitializeComponent();
    }

    public void Configure(
        Func<GraphiteRpcRequest, CancellationToken, Task<object?>> commandHandler,
        Action<string>? log = null,
        Func<object>? documentSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(commandHandler);

        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _commandHandler = commandHandler;
            _log = log;
            _documentSnapshot = documentSnapshot;
        }
    }

    /// <summary>
    /// Starts WebView2 after the containing window has loaded.
    /// </summary>
    public Task InitializeAsync(string? userDataDirectory = null)
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _userDataDirectory ??= userDataDirectory;
            return _initializationTask ??= InitializeOnDispatcherAsync();
        }
    }

    public async Task PublishSnapshotAsync(object snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string json;
        try
        {
            json = GraphiteRpcProtocol.SerializeSnapshot(snapshot);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            LogFixed("Graphite UI snapshot serialization failed.");
            throw new InvalidOperationException("Не удалось опубликовать состояние интерфейса.");
        }

        await InvokeOnDispatcherAsync(() =>
        {
            ThrowIfDisposed();
            if (!_documentReady || Browser.CoreWebView2 is null)
            {
                // Keep only the newest state. Snapshot publication must never grow an offline queue.
                _pendingSnapshotJson = json;
                return;
            }

            Browser.CoreWebView2.PostWebMessageAsJson(json);
        });
    }

    public ValueTask DisposeAsync()
    {
        lock (_stateLock)
            return new ValueTask(_disposalTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await InvokeOnDispatcherAsync(() =>
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _lifetime.Cancel();
                DetachCoreEvents();
            }
        });

        Task[] pending;
        lock (_stateLock)
        {
            pending = _initializationTask is { } initialization
                ? [.. _pendingOperations, initialization]
                : _pendingOperations.ToArray();
        }

        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the expected shutdown path for command operations.
            }
            catch (Exception)
            {
                LogFixed("Graphite UI shutdown completed with a failed pending operation.");
            }
        }

        await InvokeOnDispatcherAsync(() => Browser.Dispose());
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task InitializeOnDispatcherAsync()
    {
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(InitializeCoreAsync).Task.Unwrap();
            return;
        }

        await InitializeCoreAsync();
    }

    private async Task InitializeCoreAsync()
    {
        ThrowIfDisposed();

        if (!IsLoaded)
        {
            throw new InvalidOperationException("GraphiteWebView.InitializeAsync должен вызываться после Loaded.");
        }

        string uiDirectory = Path.Combine(AppContext.BaseDirectory, "ui");
        string documentPath = Path.Combine(uiDirectory, "desktop.html");
        if (!File.Exists(documentPath))
        {
            ShowInitializationError("Локальные файлы интерфейса не найдены. Переустановите Connector.");
            LogFixed("Graphite UI assets are unavailable.");
            return;
        }

        try
        {
            Browser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 18, 23, 29);
            string userDataDirectory = _userDataDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Structura Connector", "UI", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataDirectory);
            await Browser.EnsureCoreWebView2Async(environment);
            ThrowIfDisposed();

            CoreWebView2 core = Browser.CoreWebView2 ??
                throw new InvalidOperationException("WebView2 initialization did not produce a browser instance.");
            ApplyRestrictedSettings(core.Settings);
            AttachCoreEvents(core);
            core.SetVirtualHostNameToFolderMapping(
                GraphiteWebSecurity.VirtualHostName,
                uiDirectory,
                CoreWebView2HostResourceAccessKind.DenyCors);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);

            Browser.ZoomFactor = 1d;
            core.Navigate(GraphiteWebSecurity.DocumentUri);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Normal shutdown while the browser environment is being created.
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowInitializationError("Для интерфейса требуется Microsoft Edge WebView2 Runtime.");
            LogFixed("Graphite UI initialization failed: WebView2 Runtime is unavailable.");
        }
        catch (Exception)
        {
            ShowInitializationError("Не удалось запустить локальный интерфейс. Перезапустите Connector.");
            LogFixed("Graphite UI initialization failed.");
        }
    }

    private static void ApplyRestrictedSettings(CoreWebView2Settings settings)
    {
        settings.IsScriptEnabled = true;
        settings.IsWebMessageEnabled = true;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
    }

    private void AttachCoreEvents(CoreWebView2 core)
    {
        if (_eventsAttached)
        {
            return;
        }

        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.PermissionRequested += OnPermissionRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.WebResourceRequested += OnWebResourceRequested;
        core.WebMessageReceived += OnWebMessageReceived;
        _eventsAttached = true;
    }

    private void DetachCoreEvents()
    {
        CoreWebView2? core = Browser.CoreWebView2;
        if (!_eventsAttached || core is null)
        {
            return;
        }

        core.NavigationStarting -= OnNavigationStarting;
        core.NavigationCompleted -= OnNavigationCompleted;
        core.NewWindowRequested -= OnNewWindowRequested;
        core.PermissionRequested -= OnPermissionRequested;
        core.DownloadStarting -= OnDownloadStarting;
        core.WebResourceRequested -= OnWebResourceRequested;
        core.WebMessageReceived -= OnWebMessageReceived;
        core.RemoveWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        _eventsAttached = false;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs eventArgs)
    {
        if (!GraphiteWebSecurity.IsTrustedDocument(eventArgs.Uri))
        {
            eventArgs.Cancel = true;
            LogFixed("Graphite UI blocked an untrusted navigation.");
            return;
        }
        _documentReady = false;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        if (!eventArgs.IsSuccess && eventArgs.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled &&
            GraphiteWebSecurity.IsTrustedDocument(Browser.Source?.AbsoluteUri)) return;
        if (!eventArgs.IsSuccess || !GraphiteWebSecurity.IsTrustedDocument(Browser.Source?.AbsoluteUri))
        {
            ShowInitializationError("Не удалось загрузить локальный интерфейс. Перезапустите Connector.");
            LogFixed("Graphite UI local navigation failed.");
            return;
        }

        _documentReady = true;
        InitializationError.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Visible;

        // Incremental updates may have replaced the pre-load snapshot. Hydrate the
        // document from native state on every successful load, including reloads.
        if (_documentSnapshot is not null && Browser.CoreWebView2 is { } readyCore)
        {
            _pendingSnapshotJson = null;
            readyCore.PostWebMessageAsJson(GraphiteRpcProtocol.SerializeSnapshot(_documentSnapshot()));
            return;
        }

        if (_pendingSnapshotJson is { } json && Browser.CoreWebView2 is { } core)
        {
            _pendingSnapshotJson = null;
            core.PostWebMessageAsJson(json);
        }
    }

    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs eventArgs)
        => eventArgs.Handled = true;

    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs eventArgs)
    {
        eventArgs.State = CoreWebView2PermissionState.Deny;
        eventArgs.SavesInProfile = false;
        eventArgs.Handled = true;
    }

    private static void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs eventArgs)
        => eventArgs.Cancel = true;

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs eventArgs)
    {
        if (GraphiteWebSecurity.IsTrustedResource(eventArgs.Request.Uri))
        {
            return;
        }

        eventArgs.Response = Browser.CoreWebView2.Environment.CreateWebResourceResponse(
            Stream.Null,
            403,
            "Forbidden",
            "Content-Type: text/plain\r\nCache-Control: no-store");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        if (_disposed ||
            !GraphiteWebSecurity.IsTrustedDocument(eventArgs.Source) ||
            !GraphiteWebSecurity.IsTrustedDocument(Browser.Source?.AbsoluteUri))
        {
            LogFixed("Graphite UI rejected a message from an untrusted source.");
            return;
        }

        string json = eventArgs.WebMessageAsJson;
        if (!GraphiteRpcProtocol.TryParseRequest(json, out GraphiteRpcRequest? request, out _) || request is null)
        {
            // Invalid envelopes have no trustworthy request id, so do not reflect any input into a response.
            LogFixed("Graphite UI rejected an invalid command envelope.");
            return;
        }

        if (!RememberRequestId(request.Id))
        {
            PostResponse(request.Id, false, null, InvalidRequestError());
            return;
        }

        Func<GraphiteRpcRequest, CancellationToken, Task<object?>>? handler;
        lock (_stateLock)
        {
            handler = _commandHandler;
        }

        if (handler is null)
        {
            PostResponse(request.Id, false, null, "Действие пока недоступно.");
            return;
        }

        Task operation = ExecuteCommandAsync(handler, request);
        lock (_stateLock)
        {
            if (!_disposed)
            {
                _pendingOperations.Add(operation);
            }
        }

        _ = operation.ContinueWith(
            completed =>
            {
                lock (_stateLock)
                {
                    _pendingOperations.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ExecuteCommandAsync(
        Func<GraphiteRpcRequest, CancellationToken, Task<object?>> handler,
        GraphiteRpcRequest request)
    {
        try
        {
            object? result = await handler(request, _lifetime.Token).ConfigureAwait(false);
            await InvokeOnDispatcherAsync(() => PostResponse(request.Id, true, result, null)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The view is closing; the web document is no longer waiting for a response.
        }
        catch (OperationCanceledException)
        {
            await InvokeOnDispatcherAsync(() => PostResponse(request.Id, false, null, CommandCancelledError)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            LogFixed("Graphite UI command failed.");
            await InvokeOnDispatcherAsync(() => PostResponse(request.Id, false, null, CommandFailedError)).ConfigureAwait(false);
        }
    }

    private void PostResponse(Guid id, bool ok, object? result, string? error)
    {
        if (_disposed || !_documentReady || Browser.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            Browser.CoreWebView2.PostWebMessageAsJson(
                GraphiteRpcProtocol.SerializeResponse(id, ok, result, error));
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            if (ok)
            {
                Browser.CoreWebView2.PostWebMessageAsJson(
                    GraphiteRpcProtocol.SerializeResponse(id, false, null, CommandFailedError));
            }

            LogFixed("Graphite UI response serialization failed.");
        }
    }

    private bool RememberRequestId(Guid id)
    {
        if (!_seenRequestIds.Add(id))
        {
            return false;
        }

        _seenRequestOrder.Enqueue(id);
        while (_seenRequestOrder.Count > SeenRequestLimit)
        {
            _seenRequestIds.Remove(_seenRequestOrder.Dequeue());
        }

        return true;
    }

    private static string InvalidRequestError() => "Повторный запрос отклонён.";

    private void ShowInitializationError(string message)
    {
        _documentReady = false;
        Browser.Visibility = Visibility.Collapsed;
        InitializationErrorText.Text = message;
        InitializationError.Visibility = Visibility.Visible;
    }

    private Task InvokeOnDispatcherAsync(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return Task.CompletedTask;
        }

        return Dispatcher.InvokeAsync(action).Task;
    }

    private void LogFixed(string message)
    {
        Action<string>? logger;
        lock (_stateLock)
        {
            logger = _log;
        }

        try
        {
            logger?.Invoke(message);
        }
        catch
        {
            // Logging cannot alter command processing or expose the original exception.
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}
