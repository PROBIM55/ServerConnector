using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Connector.Access.Smb;

namespace Connector.Access.Smb.Helper;

public sealed class SmbHelperHostOptions
{
    public bool Enabled { get; init; }
    public Uri ListenUri { get; init; } = new("https://127.0.0.1:7443");
    public string ServerCertificatePfxPath { get; init; } = "";
    public string ServerCertificatePasswordEnvironmentVariable { get; init; } = "";
    public string ServerCertificatePasswordFilePath { get; init; } = "";
    public string ServerCertificatePasswordSecretName { get; init; } = SmbHelperServerCertificatePassword.DefaultSecretName;
    public IReadOnlyList<string> AllowedBackendCertificateSha256 { get; init; } = [];
    public TimeSpan RunnerTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public string RunnerScriptSha256 { get; init; } = "";
    public string StateDirectory { get; init; } = "";
    public IReadOnlyList<SmbHelperResourceOptions> Resources { get; init; } = [];
}

public sealed class SmbHelperResourceOptions
{
    public required string ResourceId { get; init; }
    public required string ShareName { get; init; }
    public required string RootPath { get; init; }
}

public sealed record ValidatedHelperResource(string ResourceId, string ShareName, string RootPath);
internal sealed record SmbRunnerResource(string ResourceId, string ShareName, string RootPath, string Permission);
internal sealed record SmbRunnerPayload(SmbHelperRequest Request, IReadOnlyList<SmbRunnerResource> Resources);

public interface ISmbAclExecutor
{
    ValueTask<SmbHelperResponse> ReconcileAsync(
        SmbHelperRequest request,
        IReadOnlyList<ValidatedHelperResource> resources,
        CancellationToken cancellationToken);
}

public sealed class SmbHelperReconciler
{
    private readonly IReadOnlyList<ValidatedHelperResource> _resources;
    private readonly ISmbAclExecutor _executor;
    private readonly SmbHelperFenceStore _fences;

    public SmbHelperReconciler(SmbHelperHostOptions options, ISmbAclExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(options);
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _resources = ValidateResources(options.Resources);
        if (string.IsNullOrWhiteSpace(options.StateDirectory) || !Path.IsPathFullyQualified(options.StateDirectory))
            throw new ArgumentException("SMB helper state directory must be absolute.");
        _fences = new SmbHelperFenceStore(Path.GetFullPath(options.StateDirectory));
    }

    public async ValueTask<SmbHelperResponse> ReconcileAsync(SmbHelperRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var known = _resources.Select(resource => resource.ResourceId).ToHashSet(StringComparer.Ordinal);
        if (request.Grants.Any(grant => !known.Contains(grant.ResourceId)))
            throw new SmbHelperValidationException("The request contains an unmapped SMB resource.");
        await using var fence = await _fences.LockAsync(request.DeviceId, cancellationToken);
        await fence.AcceptAsync(request, cancellationToken);
        var observation = await _executor.ReconcileAsync(request, _resources, cancellationToken);
        ValidateObservation(request, observation);
        return observation;
    }

    private void ValidateObservation(SmbHelperRequest request, SmbHelperResponse response)
    {
        if (response.SchemaVersion != SmbHelperProtocol.Version || response.CommandId != request.CommandId ||
            response.DeviceId != request.DeviceId || response.Revision != request.Revision || response.Action != request.Action ||
            response.LocalUserName != request.LocalUserName ||
            !string.Equals(response.LocalAccountAuthority, Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
            response.Resources.Count != _resources.Count ||
            response.Resources.Select(resource => resource.ResourceId).Distinct(StringComparer.Ordinal).Count() != _resources.Count)
            throw new SmbHelperPostconditionException("The Windows observation does not match the requested command identity.");
        if (!string.IsNullOrWhiteSpace(request.ExpectedLocalUserSid) &&
            !string.Equals(response.LocalUserSid, request.ExpectedLocalUserSid, StringComparison.OrdinalIgnoreCase))
            throw new SmbHelperPostconditionException("The managed local-user SID changed.");

        var desired = request.Grants.ToDictionary(grant => grant.ResourceId, grant => grant.Permission, StringComparer.Ordinal);
        foreach (var configured in _resources)
        {
            var observed = response.Resources.SingleOrDefault(resource => resource.ResourceId == configured.ResourceId)
                ?? throw new SmbHelperPostconditionException("A configured SMB resource was not observed.");
            var expectedPermission = request.Action == SmbHelperProtocol.Apply && desired.TryGetValue(configured.ResourceId, out var permission)
                ? permission : SmbHelperProtocol.None;
            if (!string.Equals(observed.ShareName, configured.ShareName, StringComparison.OrdinalIgnoreCase) ||
                !SameCanonicalPath(observed.CanonicalRootPath, configured.RootPath) ||
                observed.ShareAccess != expectedPermission || observed.NtfsAccess != expectedPermission)
                throw new SmbHelperPostconditionException("The exact share path or ACL postcondition was not confirmed.");
        }

        var shouldEnable = request.Action == SmbHelperProtocol.Apply && request.Grants.Count > 0;
        if (response.AccountEnabled != shouldEnable || response.ActiveSessionCount != 0 && !shouldEnable ||
            shouldEnable && string.IsNullOrWhiteSpace(response.LocalUserSid))
            throw new SmbHelperPostconditionException("The local-user or session postcondition was not confirmed.");
    }

    private static IReadOnlyList<ValidatedHelperResource> ValidateResources(IReadOnlyList<SmbHelperResourceOptions> resources)
    {
        var validated = resources.Select(resource =>
        {
            if (string.IsNullOrWhiteSpace(resource.ResourceId) || string.IsNullOrWhiteSpace(resource.ShareName) ||
                string.IsNullOrWhiteSpace(resource.RootPath) || !Path.IsPathFullyQualified(resource.RootPath))
                throw new ArgumentException("SMB helper resources require fixed ids, share names, and absolute roots.");
            return new ValidatedHelperResource(resource.ResourceId.Trim(), resource.ShareName.Trim(), Path.GetFullPath(resource.RootPath));
        }).ToArray();
        if (validated.Length == 0 || validated.Select(resource => resource.ResourceId).Distinct(StringComparer.Ordinal).Count() != validated.Length)
            throw new ArgumentException("SMB helper resource ids must be non-empty and unique.");
        return validated;
    }

    private static void ValidateRequest(SmbHelperRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SchemaVersion != SmbHelperProtocol.Version || request.Revision < 1 ||
            request.Action is not (SmbHelperProtocol.Apply or SmbHelperProtocol.Revoke) ||
            string.IsNullOrWhiteSpace(request.CommandId) || string.IsNullOrWhiteSpace(request.DeviceId) ||
            string.IsNullOrWhiteSpace(request.LocalUserName) || request.LocalUserName.Length > 20 ||
            !System.Text.RegularExpressions.Regex.IsMatch(request.LocalUserName, "^[A-Za-z0-9_]{1,20}$") ||
            request.Grants.Count > 32 || request.Grants.Select(grant => grant.ResourceId).Distinct(StringComparer.Ordinal).Count() != request.Grants.Count ||
            request.Grants.Any(grant => grant.Permission is not (SmbHelperProtocol.Read or SmbHelperProtocol.Change)) ||
            request.Action == SmbHelperProtocol.Apply && request.Grants.Count > 0 && string.IsNullOrWhiteSpace(request.Password) ||
            request.Action == SmbHelperProtocol.Revoke && request.Password is not null)
            throw new SmbHelperValidationException("The SMB helper request is invalid.");
    }

    private static bool SameCanonicalPath(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}

internal sealed class SmbHelperFenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory;

    public SmbHelperFenceStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    public async ValueTask<LockedFence> LockAsync(string deviceId, CancellationToken cancellationToken)
    {
        var stem = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId))).ToLowerInvariant();
        var path = Path.Combine(_directory, stem + ".json");
        FileStream gate;
        try { gate = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) { throw new SmbHelperValidationException("SMB helper device state is busy.", exception); }
        SmbHelperFenceState? state = null;
        try
        {
            if (File.Exists(path))
            {
                await using var input = File.OpenRead(path);
                state = await JsonSerializer.DeserializeAsync<SmbHelperFenceState>(input, JsonOptions, cancellationToken)
                    ?? throw new InvalidDataException("SMB helper fence state is empty.");
                if (state.DeviceId != deviceId) throw new InvalidDataException("SMB helper fence identity changed.");
            }
            return new LockedFence(path, gate, state);
        }
        catch
        {
            await gate.DisposeAsync();
            throw;
        }
    }

    internal sealed class LockedFence(string path, FileStream gate, SmbHelperFenceState? state) : IAsyncDisposable
    {
        private SmbHelperFenceState? _state = state;

        public async ValueTask AcceptAsync(SmbHelperRequest request, CancellationToken cancellationToken)
        {
            var grants = request.Grants.OrderBy(grant => grant.ResourceId, StringComparer.Ordinal).ToArray();
            if (_state is not null && (request.Revision < _state.Revision ||
                request.Revision == _state.Revision &&
                (request.CommandId != _state.CommandId || request.Action != _state.Action || !grants.SequenceEqual(_state.Grants))))
                throw new SmbHelperValidationException("A stale or conflicting SMB helper command was rejected.");
            if (_state is not null && request.Revision == _state.Revision) return;
            _state = new SmbHelperFenceState(request.DeviceId, request.CommandId, request.Revision, request.Action, grants);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(output, _state, JsonOptions, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        public ValueTask DisposeAsync() => gate.DisposeAsync();
    }
}

internal sealed record SmbHelperFenceState(
    string DeviceId, string CommandId, long Revision, string Action, IReadOnlyList<SmbHelperGrant> Grants);

public sealed class PowerShellSmbAclExecutor : ISmbAclExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _scriptPath;
    private readonly TimeSpan _timeout;

    public PowerShellSmbAclExecutor(string scriptPath, TimeSpan timeout, string expectedSha256)
    {
        _scriptPath = Path.GetFullPath(scriptPath);
        _timeout = timeout;
        if (!File.Exists(_scriptPath)) throw new FileNotFoundException("The packaged SMB ACL script is absent.", _scriptPath);
        byte[] expected;
        try { expected = Convert.FromHexString(expectedSha256); }
        catch (FormatException exception) { throw new ArgumentException("SMB ACL script SHA-256 is invalid.", nameof(expectedSha256), exception); }
        var actual = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(_scriptPath));
        if (expected.Length != 32 || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new InvalidOperationException("The packaged SMB ACL script does not match its configured SHA-256.");
    }

    public async ValueTask<SmbHelperResponse> ReconcileAsync(
        SmbHelperRequest request,
        IReadOnlyList<ValidatedHelperResource> resources,
        CancellationToken cancellationToken)
    {
        var desired = request.Grants.ToDictionary(grant => grant.ResourceId, grant => grant.Permission, StringComparer.Ordinal);
        var payload = new SmbRunnerPayload(request, resources.Select(resource => new SmbRunnerResource(
            resource.ResourceId, resource.ShareName, resource.RootPath,
            request.Action == SmbHelperProtocol.Apply && desired.TryGetValue(resource.ResourceId, out var permission)
                ? permission : SmbHelperProtocol.None)).ToArray());
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell)) throw new FileNotFoundException("The fixed Windows PowerShell runtime is absent.", powershell);
        var info = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-File");
        info.ArgumentList.Add(_scriptPath);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("SMB ACL runner could not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > 64 * 1024) throw new SmbHelperValidationException("SMB runner payload exceeded the limit.");
        await process.StandardInput.WriteAsync(json.AsMemory(), timeout.Token);
        process.StandardInput.Close();
        var stdout = ReadBoundedAsync(process.StandardOutput, 256 * 1024, timeout.Token);
        var stderr = ReadBoundedAsync(process.StandardError, 8 * 1024, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            try { process.Kill(true); } catch { }
            throw;
        }
        var output = await stdout;
        _ = await stderr; // Deliberately never include runner text in exceptions or logs.
        if (process.ExitCode != 0) throw new SmbHelperPostconditionException("Windows rejected SMB reconciliation.");
        try
        {
            return JsonSerializer.Deserialize<SmbHelperResponse>(output, JsonOptions)
                ?? throw new SmbHelperPostconditionException("SMB runner returned an empty observation.");
        }
        catch (JsonException exception)
        {
            throw new SmbHelperPostconditionException("SMB runner returned invalid observation JSON.", exception);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var output = new System.Text.StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return output.ToString();
            if (output.Length + read > limit) throw new SmbHelperPostconditionException("SMB runner output exceeded the limit.");
            output.Append(buffer, 0, read);
        }
    }
}

public sealed class SmbHelperValidationException : Exception
{
    public SmbHelperValidationException(string message) : base(message) { }
    public SmbHelperValidationException(string message, Exception innerException) : base(message, innerException) { }
}
public sealed class SmbHelperPostconditionException : Exception
{
    public SmbHelperPostconditionException(string message) : base(message) { }
    public SmbHelperPostconditionException(string message, Exception innerException) : base(message, innerException) { }
}
