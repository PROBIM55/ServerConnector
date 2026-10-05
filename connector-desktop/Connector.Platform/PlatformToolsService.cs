using System.Text.Json;
using Platform.Cad.AutoCad.Adapter.Sessions;
using Platform.Cad.Tekla.Adapter;
using Platform.Connector.Compute.Agr;
using Platform.Connector.Core;

namespace Connector.Platform;

/// <summary>Typed native capability API. It delegates to the existing CAD adapters.</summary>
public sealed class PlatformToolsService : IDisposable
{
    private readonly HttpClient _httpClient = new();
    private readonly AgrManagedRuntime _agr;
    private readonly TeklaProviderAdapter _tekla = new();
    private readonly IBridgeDesktopRestarter _bridgeRestarter;
    private readonly PlatformSettingsPersistence _settingsPersistence;

    public PlatformToolsService(
        PlatformSettingsPersistence? settingsPersistence = null,
        IBridgeDesktopRestarter? bridgeRestarter = null)
    {
        _agr = new AgrManagedRuntime(_httpClient);
        _settingsPersistence = settingsPersistence ?? new PlatformSettingsPersistence();
        _bridgeRestarter = bridgeRestarter ?? new WindowsBridgeDesktopRestarter();
    }

    public Task<AgrRuntimeProbe> ProbeAgrAsync(CancellationToken cancellationToken) => _agr.ProbeAsync(cancellationToken);

    public Task<AgrManagedRuntimeInstallResult> InstallAgrAsync(IProgress<AgrRuntimeInstallProgress>? progress, CancellationToken cancellationToken)
        => _agr.EnsureInstalledAsync(progress, cancellationToken);

    public async Task<PlatformToolResult> ProbeTeklaAsync(CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(new { endpoint = "/health", interactive = false });
        var job = new ConnectorJobEnvelope(1, Guid.NewGuid().ToString("N"), "platform-tools", CadProvider.Tekla,
            JobOperation.Healthcheck, DateTime.UtcNow, payload, ExecutorId: ConnectorExecutorIds.Tekla);
        var result = await _tekla.ExecuteAsync(job, cancellationToken);
        return new(result.IsSuccess,
            result.Message ?? (result.IsSuccess ? "Bridge Tekla доступен." : "Bridge Tekla недоступен."),
            result.ErrorCode);
    }

    public async Task<PlatformToolResult<PlatformTeklaBridgeState>> EnsureTeklaBridgeAsync(
        bool restart,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !_bridgeRestarter.IsAvailable)
        {
            return PlatformToolResult<PlatformTeklaBridgeState>.Failure(
                "TEKLA_BRIDGE_WINDOWS_ONLY", "Управление Bridge Tekla доступно только в Windows.");
        }

        if (!restart && await IsBridgeHealthyAsync(cancellationToken))
        {
            return PlatformToolResult<PlatformTeklaBridgeState>.Success(
                new(true, BridgeRestartOutcome.MechanismNone),
                "Bridge Tekla уже запущен.");
        }

        var outcome = await _bridgeRestarter.RestartAndWaitDetailedAsync(
            TeklaProviderAdapter.DefaultBridgeBaseUrl,
            _ => new HttpClient(),
            TeklaProviderAdapter.RestartTimeout,
            cancellationToken);
        if (!outcome.Healthy)
        {
            return PlatformToolResult<PlatformTeklaBridgeState>.Failure(
                "TEKLA_BRIDGE_START_FAILED",
                "Bridge Tekla не ответил после запуска. Проверьте установку Bridge и открытую модель Tekla.");
        }

        return PlatformToolResult<PlatformTeklaBridgeState>.Success(
            new(true, outcome.Mechanism),
            restart ? "Bridge Tekla перезапущен." : "Bridge Tekla запущен.");
    }

    public async Task<PlatformToolResult<IReadOnlyList<PlatformAutoCadSession>>> ListAutoCadSessionsAsync(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return PlatformToolResult<IReadOnlyList<PlatformAutoCadSession>>.Failure(
                AutoCadSessionErrorCodes.NotSupportedOnPlatform,
                "Проверка AutoCAD доступна только в Windows.",
                Array.Empty<PlatformAutoCadSession>());
        }

        var sessions = await AutoCadSessionService.Shared.ListSessionsAsync(cancellationToken);
        var selectedKey = AutoCadSessionSelection.SelectedSessionKey;
        if (string.IsNullOrWhiteSpace(selectedKey))
        {
            var savedKey = _settingsPersistence.Load().AutoCadSessionKey;
            if (sessions.Any(item => string.Equals(item.SessionKey, savedKey, StringComparison.Ordinal)))
            {
                selectedKey = savedKey;
                AutoCadSessionSelection.SelectedSessionKey = savedKey;
            }
            else if (sessions.Count == 1)
            {
                selectedKey = sessions[0].SessionKey;
                AutoCadSessionSelection.SelectedSessionKey = selectedKey;
            }
        }

        var safeSessions = sessions
            .Select(item => new PlatformAutoCadSession(
                item.SessionKey,
                item.DisplayName,
                item.Version,
                item.DocumentName,
                string.Equals(item.SessionKey, selectedKey, StringComparison.Ordinal)))
            .ToArray();
        return safeSessions.Length == 0
            ? PlatformToolResult<IReadOnlyList<PlatformAutoCadSession>>.Failure(
                AutoCadSessionErrorCodes.NoRunningSessions,
                "Запущенные сеансы AutoCAD не найдены.",
                safeSessions)
            : PlatformToolResult<IReadOnlyList<PlatformAutoCadSession>>.Success(
                safeSessions,
                $"Найдено сеансов AutoCAD: {safeSessions.Length}.");
    }

    public async Task<PlatformToolResult<PlatformAutoCadPing>> SelectAndPingAutoCadAsync(
        string? sessionKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionKey))
        {
            return PlatformToolResult<PlatformAutoCadPing>.Failure(
                AutoCadSessionErrorCodes.SessionNotSelected,
                "Выберите сеанс AutoCAD.");
        }

        var previousKey = AutoCadSessionSelection.SelectedSessionKey;
        AutoCadSessionSelection.SelectedSessionKey = sessionKey.Trim();
        var result = await AutoCadSessionService.Shared.ExecuteAsync(
            new AutoCadSessionRequest(AutoCadSessionAction.Ping, sessionKey.Trim(), [], []),
            cancellationToken);
        if (!result.IsSuccess || result.Session is null || result.Ping is null)
        {
            AutoCadSessionSelection.SelectedSessionKey = previousKey;
            return PlatformToolResult<PlatformAutoCadPing>.Failure(
                result.ErrorCode ?? AutoCadSessionErrorCodes.ComError,
                result.Message ?? "AutoCAD не ответил на проверку.");
        }

        var settings = _settingsPersistence.Load();
        settings.AutoCadSessionKey = result.Session.SessionKey;
        _settingsPersistence.SaveSettings(settings);
        var safePing = new PlatformAutoCadPing(
            result.Session.SessionKey,
            result.Ping.Version,
            result.Ping.DocumentName,
            result.Ping.DocumentsCount,
            result.Ping.ModelSpaceCount);
        return PlatformToolResult<PlatformAutoCadPing>.Success(safePing, "Связь с AutoCAD установлена.");
    }

    public async Task<PlatformToolResult> ProbeAutoCadAsync(CancellationToken cancellationToken)
    {
        var result = await ListAutoCadSessionsAsync(cancellationToken);
        return new(result.IsReady, result.Message, result.ErrorCode);
    }

    public void Dispose() => _httpClient.Dispose();

    private async Task<bool> IsBridgeHealthyAsync(CancellationToken cancellationToken)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await _httpClient.GetAsync(
                TeklaProviderAdapter.DefaultBridgeBaseUrl + "/health",
                probeCts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }
}

public sealed record PlatformToolResult(bool IsReady, string Message, string? ErrorCode);

public sealed record PlatformToolResult<T>(bool IsReady, string Message, string? ErrorCode, T? Value)
{
    public static PlatformToolResult<T> Success(T value, string message) => new(true, message, null, value);

    public static PlatformToolResult<T> Failure(string errorCode, string message, T? value = default) =>
        new(false, message, errorCode, value);
}

public sealed record PlatformAutoCadSession(
    string SessionKey,
    string DisplayName,
    string Version,
    string? DocumentName,
    bool IsSelected);

public sealed record PlatformAutoCadPing(
    string SessionKey,
    string Version,
    string? DocumentName,
    int DocumentsCount,
    int? ModelSpaceCount);

public sealed record PlatformTeklaBridgeState(bool Healthy, string Mechanism);
