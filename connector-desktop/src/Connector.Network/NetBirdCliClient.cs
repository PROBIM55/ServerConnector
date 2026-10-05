using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace Connector.Network;

public sealed class NetBirdCliOptions
{
    public required string ExecutablePath { get; init; }
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public WindowsNetBirdInstallationOwnershipOptions? WindowsInstallationOwnership { get; init; }

    internal ValidatedNetBirdCliOptions Validate()
    {
        if (string.IsNullOrWhiteSpace(ExecutablePath) || !Path.IsPathFullyQualified(ExecutablePath))
            throw new ArgumentException("NetBird executable path must be absolute.");
        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
        return new ValidatedNetBirdCliOptions(Path.GetFullPath(ExecutablePath), CommandTimeout);
    }
}

public sealed class NetBirdCliClient : INetworkOverlayClient
{
    private readonly ValidatedNetBirdCliOptions _options;
    private readonly INetBirdCommandRunner _runner;
    private readonly INetBirdInstallationOwnershipGate _ownershipGate;
    private readonly TimeProvider _timeProvider;

    public NetBirdCliClient(NetBirdCliOptions options, TimeProvider? timeProvider = null)
        : this(options, new NetBirdCommandRunner(), CreateOwnershipGate(options), timeProvider) { }

    internal NetBirdCliClient(NetBirdCliOptions options, INetBirdCommandRunner runner, TimeProvider? timeProvider = null)
        : this(options, runner, new UntrustedNetBirdInstallationOwnershipGate(), timeProvider) { }

    internal NetBirdCliClient(
        NetBirdCliOptions options,
        INetBirdCommandRunner runner,
        INetBirdInstallationOwnershipGate ownershipGate,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Validate();
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _ownershipGate = ownershipGate ?? throw new ArgumentNullException(nameof(ownershipGate));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private static INetBirdInstallationOwnershipGate CreateOwnershipGate(NetBirdCliOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.WindowsInstallationOwnership is null
            ? new UntrustedNetBirdInstallationOwnershipGate()
            : new WindowsNetBirdInstallationOwnershipGate(options.WindowsInstallationOwnership, new NetBirdCommandRunner());
    }

    public async ValueTask<NetworkOverlaySnapshot> ConnectAsync(
        NetworkOverlayBootstrap bootstrap,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        if (!bootstrap.ManagementUri.IsAbsoluteUri || bootstrap.ManagementUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(bootstrap.ManagementUri.UserInfo) || string.IsNullOrWhiteSpace(bootstrap.SetupKey))
            throw new ArgumentException("NetBird bootstrap must contain an HTTPS management URI and setup key.", nameof(bootstrap));

        await RequireTrustedInstallationAsync(cancellationToken).ConfigureAwait(false);
        await RequireSafeDaemonStateBeforeUpAsync(bootstrap.ManagementUri, cancellationToken).ConfigureAwait(false);
        var result = await _runner.RunAsync(
            _options.ExecutablePath,
            ["up", "--management-url", bootstrap.ManagementUri.AbsoluteUri.TrimEnd('/')],
            new Dictionary<string, string?> { ["NB_SETUP_KEY"] = bootstrap.SetupKey },
            _options.CommandTimeout,
            cancellationToken);
        if (result.ExitCode != 0)
            throw new NetworkGateException("netbird_up_failed", "NetBird rejected the bootstrap: " + Sanitize(result.StandardError, bootstrap.SetupKey));

        var snapshot = await GetStatusAsync(bootstrap.ManagementUri, cancellationToken).ConfigureAwait(false);
        if (snapshot.Status != NetworkServiceStatus.Ready)
            throw new NetworkGateException("netbird_not_ready", "NetBird did not become ready after bootstrap.");
        return snapshot;
    }

    public async ValueTask<NetworkOverlaySnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
        => await GetStatusAsync(null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<NetBirdLocalIdentityReadResult> GetLocalIdentityAsync(
        Uri expectedManagementUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedManagementUri);
        if (!expectedManagementUri.IsAbsoluteUri || expectedManagementUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(expectedManagementUri.UserInfo))
            throw new ArgumentException("Expected management URI must be an HTTPS URI without user info.", nameof(expectedManagementUri));

        var read = await ReadStatusAsync(expectedManagementUri, cancellationToken).ConfigureAwait(false);
        if (read.Snapshot.Status != NetworkServiceStatus.Ready)
            return NetBirdLocalIdentityReadResult.Unavailable(read.Snapshot.DiagnosticCode);
        if (read.CanonicalWireGuardPublicKey is null || read.Snapshot.ManagementUri is null)
            return NetBirdLocalIdentityReadResult.Unavailable("netbird_identity_invalid");

        return NetBirdLocalIdentityReadResult.Available(new NetBirdLocalIdentity(
            read.CanonicalWireGuardPublicKey,
            read.Snapshot.ManagementUri,
            read.Snapshot.AssignedInternalAddresses,
            read.Snapshot.ObservedAtUtc));
    }

    private async ValueTask<NetworkOverlaySnapshot> GetStatusAsync(Uri? expectedManagementUri, CancellationToken cancellationToken)
        => (await ReadStatusAsync(expectedManagementUri, cancellationToken).ConfigureAwait(false)).Snapshot;

    private async ValueTask<NetBirdStatusRead> ReadStatusAsync(Uri? expectedManagementUri, CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.ExecutablePath))
            return new(Unknown(NetworkServiceStatus.NotInstalled, "netbird_not_installed"), null);

        var ownership = await _ownershipGate.CheckAsync(_options.ExecutablePath, cancellationToken).ConfigureAwait(false);
        if (!ownership.IsTrusted)
            return new(Unknown(NetworkServiceStatus.Unknown, ownership.DiagnosticCode), null);

        var startup = await _runner.RunAsync(
            _options.ExecutablePath,
            ["status", "--check", "startup"],
            null,
            _options.CommandTimeout,
            cancellationToken);
        var status = await _runner.RunAsync(
            _options.ExecutablePath,
            ["status", "--json"],
            null,
            _options.CommandTimeout,
            cancellationToken);
        if (status.ExitCode != 0 || string.IsNullOrWhiteSpace(status.StandardOutput))
            return new(Unknown(NetworkServiceStatus.Unknown, "netbird_status_unknown"), null);

        try
        {
            using var document = JsonDocument.Parse(status.StandardOutput);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new(Unknown(NetworkServiceStatus.Unknown, "netbird_status_invalid"), null);
            var daemonStatus = ReadString(root, "daemonStatus") ?? ReadString(root, "status");
            var managementConnected = ReadNestedBoolean(root, "management", "connected");
            var signalConnected = ReadNestedBoolean(root, "signal", "connected");
            var addresses = ReadAssignedAddresses(root);
            var managementUri = ReadManagementUri(root);
            var publicKey = ReadPublicKey(root);
            var canonicalWireGuardPublicKey = ReadCanonicalWireGuardPublicKey(root);
            var startupPassed = startup.ExitCode == 0;
            var managementMatches = expectedManagementUri is null || SameManagementUri(expectedManagementUri, managementUri);
            var serviceStatus = startupPassed && managementConnected && signalConnected && addresses.Count > 0 &&
                                managementMatches && publicKey is not null
                ? NetworkServiceStatus.Ready
                : string.Equals(daemonStatus, "Connecting", StringComparison.OrdinalIgnoreCase)
                    ? NetworkServiceStatus.Connecting
                    : string.Equals(daemonStatus, "Idle", StringComparison.OrdinalIgnoreCase)
                        ? NetworkServiceStatus.Disconnected
                        : NetworkServiceStatus.Degraded;
            var snapshot = new NetworkOverlaySnapshot(
                serviceStatus,
                startupPassed,
                managementConnected,
                signalConnected,
                addresses,
                _timeProvider.GetUtcNow(),
                serviceStatus == NetworkServiceStatus.Ready ? "ready" : managementMatches ? "netbird_degraded" : "netbird_management_mismatch",
                managementUri);
            return new(snapshot, canonicalWireGuardPublicKey);
        }
        catch (JsonException)
        {
            return new(Unknown(NetworkServiceStatus.Unknown, "netbird_status_invalid"), null);
        }
    }

    private NetworkOverlaySnapshot Unknown(NetworkServiceStatus status, string code) =>
        new(status, false, false, false, [], _timeProvider.GetUtcNow(), code);

    private async ValueTask RequireTrustedInstallationAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.ExecutablePath))
            throw new NetworkGateException("netbird_not_installed", "The NetBird CLI executable is not installed.");

        var ownership = await _ownershipGate.CheckAsync(_options.ExecutablePath, cancellationToken).ConfigureAwait(false);
        if (!ownership.IsTrusted)
            throw new NetworkGateException(ownership.DiagnosticCode, "NetBird installation ownership is not trusted.");
    }

    private async ValueTask RequireSafeDaemonStateBeforeUpAsync(Uri expectedManagementUri, CancellationToken cancellationToken)
    {
        var status = await _runner.RunAsync(
            _options.ExecutablePath,
            ["status", "--json"],
            null,
            _options.CommandTimeout,
            cancellationToken).ConfigureAwait(false);
        if (status.ExitCode != 0 || string.IsNullOrWhiteSpace(status.StandardOutput))
            throw new NetworkGateException("netbird_pre_up_status_unavailable", "NetBird daemon status cannot be verified before bootstrap.");

        try
        {
            using var document = JsonDocument.Parse(status.StandardOutput);
            var root = document.RootElement;
            var managementUri = ReadManagementUri(root);
            if (managementUri is not null)
            {
                if (!SameManagementUri(expectedManagementUri, managementUri))
                    throw new NetworkGateException("netbird_management_mismatch", "NetBird is configured for a different management URL.");
                return;
            }

            if (!string.Equals(ReadString(root, "daemonStatus") ?? ReadString(root, "status"), "Idle", StringComparison.OrdinalIgnoreCase))
                throw new NetworkGateException("netbird_pre_up_status_unsafe", "NetBird daemon is not a known inactive unregistered instance.");
        }
        catch (JsonException exception)
        {
            throw new NetworkGateException("netbird_pre_up_status_unavailable", "NetBird daemon status cannot be parsed before bootstrap.", exception);
        }
    }

    private static bool SameManagementUri(Uri expected, Uri? actual) =>
        actual is not null && string.Equals(
            expected.AbsoluteUri.TrimEnd('/'),
            actual.AbsoluteUri.TrimEnd('/'),
            StringComparison.Ordinal);

    internal static string? ReadPublicKey(JsonElement root)
    {
        var value = ReadString(root, "publicKey");
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            return Convert.FromBase64String(value).Length == 32 ? value : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static string? ReadCanonicalWireGuardPublicKey(JsonElement root)
    {
        var value = ReadString(root, "publicKey");
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var bytes = Convert.FromBase64String(value);
            var canonical = Convert.ToBase64String(bytes);
            return bytes.Length == 32 && string.Equals(value, canonical, StringComparison.Ordinal) ? canonical : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static IReadOnlyList<IPAddress> ReadAssignedAddresses(JsonElement root)
    {
        var values = new List<string>();
        foreach (var name in new[] { "netbirdIp", "netbirdIP", "netbirdIpv4", "netbirdIpv6", "ip", "ipv6" })
        {
            if (TryGetProperty(root, name, out var element) && element.ValueKind == JsonValueKind.String)
                values.Add(element.GetString()!);
        }
        if (TryGetProperty(root, "localPeer", out var localPeer) && localPeer.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "ip", "ipv4", "ipv6" })
                if (TryGetProperty(localPeer, name, out var element) && element.ValueKind == JsonValueKind.String)
                    values.Add(element.GetString()!);
        }
        return values.Select(value => value.Split('/', 2)[0])
            .Select(value => IPAddress.TryParse(value, out var address) ? address : null)
            .Where(address => address is not null && !IPAddress.IsLoopback(address) &&
                              !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any))
            .Cast<IPAddress>()
            .Distinct()
            .ToArray();
    }

    private static bool ReadNestedBoolean(JsonElement root, string owner, string property) =>
        TryGetProperty(root, owner, out var nested) && nested.ValueKind == JsonValueKind.Object &&
        TryGetProperty(nested, property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? ReadString(JsonElement root, string property) =>
        TryGetProperty(root, property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Uri? ReadManagementUri(JsonElement root)
    {
        string? value = null;
        if (TryGetProperty(root, "management", out var management) && management.ValueKind == JsonValueKind.Object)
            value = ReadString(management, "url") ?? ReadString(management, "managementUrl");
        value ??= ReadString(root, "managementUrl");
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
               string.IsNullOrEmpty(uri.UserInfo)
            ? uri
            : null;
    }

    private static bool TryGetProperty(JsonElement owner, string name, out JsonElement value)
    {
        foreach (var property in owner.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string Sanitize(string value, string setupKey)
    {
        var sanitized = string.IsNullOrEmpty(setupKey) ? value : value.Replace(setupKey, "[redacted]", StringComparison.Ordinal);
        return sanitized.Length <= 1024 ? sanitized : sanitized[..1024];
    }
}

internal sealed record ValidatedNetBirdCliOptions(string ExecutablePath, TimeSpan CommandTimeout);
internal sealed record NetBirdStatusRead(NetworkOverlaySnapshot Snapshot, string? CanonicalWireGuardPublicKey);
internal sealed record NetBirdCommandResult(int ExitCode, string StandardOutput, string StandardError);
internal sealed record NetBirdInstallationOwnership(bool IsTrusted, string DiagnosticCode);

internal interface INetBirdInstallationOwnershipGate
{
    ValueTask<NetBirdInstallationOwnership> CheckAsync(string executablePath, CancellationToken cancellationToken);
}

internal sealed class UntrustedNetBirdInstallationOwnershipGate : INetBirdInstallationOwnershipGate
{
    public ValueTask<NetBirdInstallationOwnership> CheckAsync(string executablePath, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new NetBirdInstallationOwnership(false, "netbird_installation_untrusted"));
}

internal interface INetBirdCommandRunner
{
    ValueTask<NetBirdCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class NetBirdCommandRunner : INetBirdCommandRunner
{
    public async ValueTask<NetBirdCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        ApplyEnvironment(info, environment);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("NetBird process could not be started.");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var stdout = ReadBoundedAsync(process.StandardOutput, timeoutSource.Token);
        var stderr = ReadBoundedAsync(process.StandardError, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            return new NetBirdCommandResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            await TerminateChildAsync(process).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("NetBird command timed out.");
        }
        catch
        {
            await TerminateChildAsync(process).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task TerminateChildAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (TimeoutException) { }
    }

    internal static void ApplyEnvironment(ProcessStartInfo info, IReadOnlyDictionary<string, string?>? environment)
    {
        ArgumentNullException.ThrowIfNull(info);
        foreach (var name in info.Environment.Keys
                     .Where(name => name.StartsWith("NB_", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            info.Environment.Remove(name);
        if (environment is not null)
            foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        const int maximumCharacters = 64 * 1024;
        var buffer = new char[4096];
        var builder = new System.Text.StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return builder.ToString();
            if (builder.Length + read > maximumCharacters) throw new InvalidDataException("NetBird command output exceeded the allowed size.");
            builder.Append(buffer, 0, read);
        }
    }
}
