using System.Diagnostics;
using System.Net;
using System.Security.Principal;
using System.Text.Json;
using Connector.Upgrade.Core;
using Connector.Upgrade.WindowsHost;
using Connector.Upgrade.WindowsMsi;

namespace Connector.Upgrade.WindowsEnvironment;

public interface IPlatformAccessSchemaProbe
{
    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken);
}

public interface ILegacyProcessProbe
{
    LegacyProcessProbeResult Inspect(LegacyApplicationKind kind);
}

public sealed record LegacyProcessProbeResult(bool Complete, IReadOnlyList<int> ProcessIds);

/// <summary>
/// Read-only gate. Legacy clients have no migration IPC, so the installer asks the user to exit
/// both applications and refuses MSI removal while either named process is still present.
/// </summary>
public sealed class WindowsHostEnvironmentPort : IWindowsHostEnvironmentPort
{
    private readonly string _initiatingSid;
    private readonly Func<string> _currentSid;
    private readonly IPlatformAccessSchemaProbe _schema;
    private readonly ILegacyProcessProbe _processes;

    public WindowsHostEnvironmentPort(
        string initiatingSid,
        IPlatformAccessSchemaProbe schema,
        ILegacyProcessProbe processes,
        Func<string>? currentSid = null)
    {
        if (string.IsNullOrWhiteSpace(initiatingSid))
            throw new ArgumentException("The initiating SID is required.", nameof(initiatingSid));
        _initiatingSid = initiatingSid;
        _schema = schema ?? throw new ArgumentNullException(nameof(schema));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _currentSid = currentSid ?? CurrentWindowsSid;
    }

    public string GetCurrentUserSid() => _currentSid();

    public async ValueTask<WindowsHostReadiness> InspectReadinessAsync(CancellationToken cancellationToken)
    {
        AssertSameUser();
        var schemaAvailable = await _schema.IsAvailableAsync(cancellationToken).ConfigureAwait(false);
        var structura = _processes.Inspect(LegacyApplicationKind.StructuraConnector);
        var platform = _processes.Inspect(LegacyApplicationKind.PlatformConnector);
        var noLiveWork = IsAbsent(structura) && IsAbsent(platform);
        return new WindowsHostReadiness(
            schemaAvailable,
            noLiveWork,
            $"schema:{schemaAvailable};structura:{Describe(structura)};platform:{Describe(platform)}");
    }

    public ValueTask<LegacyProcessDrainProof> DrainLegacyApplicationAsync(
        LegacyApplicationKind kind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AssertSameUser();
        if (kind is not (LegacyApplicationKind.StructuraConnector or LegacyApplicationKind.PlatformConnector))
            throw new ArgumentOutOfRangeException(nameof(kind));
        var observed = _processes.Inspect(kind);
        if (!IsAbsent(observed))
            throw new InvalidOperationException(
                $"Close the legacy {kind} application and finish its work before continuing the upgrade.");
        return ValueTask.FromResult(new LegacyProcessDrainProof(
            kind,
            Drained: true,
            EvidenceId: $"process-absent:{kind}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"));
    }

    private static bool IsAbsent(LegacyProcessProbeResult result) =>
        result.Complete && result.ProcessIds.Count == 0;

    private static string Describe(LegacyProcessProbeResult result) =>
        result.Complete ? result.ProcessIds.Count.ToString() : "unknown";

    private void AssertSameUser()
    {
        if (!string.Equals(_currentSid(), _initiatingSid, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The Windows user changed during legacy process inspection.");
    }

    private static string CurrentWindowsSid()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows SID is required.");
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new UnauthorizedAccessException("Current Windows SID is missing.");
    }
}

public sealed class SystemLegacyProcessProbe : ILegacyProcessProbe
{
    public LegacyProcessProbeResult Inspect(LegacyApplicationKind kind)
    {
        var names = kind switch
        {
            LegacyApplicationKind.StructuraConnector => new[] { "Connector.Desktop" },
            LegacyApplicationKind.PlatformConnector => new[]
            {
                "Platform.Connector.App", "Platform.Connector.Desktop.Ui"
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var pids = new HashSet<int>();
        try
        {
            foreach (var name in names)
            {
                var found = Process.GetProcessesByName(name);
                try
                {
                    foreach (var process in found)
                        pids.Add(process.Id);
                }
                finally
                {
                    foreach (var process in found) process.Dispose();
                }
            }
            return new LegacyProcessProbeResult(true, pids.Order().ToArray());
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new LegacyProcessProbeResult(false, pids.Order().ToArray());
        }
    }
}

/// <summary>
/// Proves the exact new device route is deployed before a one-use token is consumed. The expected
/// unauthenticated response is the access module's own 401 device_unauthorized JSON, not a generic
/// HTTP health result or a redirected legacy endpoint.
/// </summary>
public sealed class HttpPlatformAccessSchemaProbe : IPlatformAccessSchemaProbe
{
    private const string ProfilePath = "/api/platform/connector/access/v1/profile";
    private readonly HttpClient _client;
    private readonly Uri _profileUri;

    public HttpPlatformAccessSchemaProbe(HttpClient client, Uri profileUri)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _profileUri = profileUri ?? throw new ArgumentNullException(nameof(profileUri));
        if (!_profileUri.IsAbsoluteUri || _profileUri.Scheme != Uri.UriSchemeHttps ||
            _profileUri.AbsolutePath != ProfilePath ||
            !string.IsNullOrEmpty(_profileUri.Query) || !string.IsNullOrEmpty(_profileUri.Fragment))
            throw new ArgumentException("An exact HTTPS Platform access profile URI is required.", nameof(profileUri));
    }

    public static HttpPlatformAccessSchemaProbe Create(Uri profileUri)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        return new HttpPlatformAccessSchemaProbe(new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(10),
        }, profileUri);
    }

    public async ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _profileUri);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized || response.Content.Headers.ContentLength > 4096)
            return false;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[4097];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        if (count > 4096) return false;
        try
        {
            using var json = JsonDocument.Parse(buffer.AsMemory(0, count));
            return json.RootElement.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.String &&
                code.GetString() == "device_unauthorized";
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
