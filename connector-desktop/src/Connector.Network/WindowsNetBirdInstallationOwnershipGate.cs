using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace Connector.Network;

public sealed class WindowsNetBirdInstallationOwnershipOptions
{
    public required string PreflightScriptPath { get; init; }
    public required string PinnedMsiPath { get; init; }
    public required string VerifierScriptPath { get; init; }
    public required string IdentityLockPath { get; init; }
    public required string PreflightScriptSha256 { get; init; }
    public required string VerifierScriptSha256 { get; init; }
    public required string IdentityLockSha256 { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    internal ValidatedWindowsNetBirdInstallationOwnershipOptions Validate()
    {
        var preflight = RequireAbsolutePath(PreflightScriptPath, nameof(PreflightScriptPath));
        var msi = RequireAbsolutePath(PinnedMsiPath, nameof(PinnedMsiPath));
        var verifier = RequireAbsolutePath(VerifierScriptPath, nameof(VerifierScriptPath));
        var identityLock = RequireAbsolutePath(IdentityLockPath, nameof(IdentityLockPath));
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(Timeout));

        var scriptsDirectory = Path.GetDirectoryName(preflight)!;
        var projectDirectory = Path.GetDirectoryName(scriptsDirectory);
        if (projectDirectory is null ||
            !PathEquals(verifier, Path.Combine(scriptsDirectory, "verify_netbird_installer.ps1")) ||
            !PathEquals(identityLock, Path.Combine(projectDirectory, "infra", "netbird", "v0.79.0-windows-x64.lock.json")))
            throw new ArgumentException("NetBird verifier and identity lock paths must match the files used by the pinned preflight script.");

        return new(
            preflight,
            msi,
            verifier,
            identityLock,
            RequireSha256(PreflightScriptSha256, nameof(PreflightScriptSha256)),
            RequireSha256(VerifierScriptSha256, nameof(VerifierScriptSha256)),
            RequireSha256(IdentityLockSha256, nameof(IdentityLockSha256)),
            Timeout);
    }

    private static string RequireAbsolutePath(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new ArgumentException("Path must be absolute.", parameterName);
        return Path.GetFullPath(value);
    }

    private static string RequireSha256(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new ArgumentException("SHA-256 must contain exactly 64 hexadecimal characters.", parameterName);
        return value.ToUpperInvariant();
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
}

internal sealed class WindowsNetBirdInstallationOwnershipGate : INetBirdInstallationOwnershipGate
{
    private const int MaximumPreflightBytes = 512 * 1024;
    private const int MaximumVerifierBytes = 128 * 1024;
    private const int MaximumLockBytes = 64 * 1024;
    private readonly ValidatedWindowsNetBirdInstallationOwnershipOptions _options;
    private readonly INetBirdCommandRunner _runner;
    private readonly IWindowsNetBirdPathTrustPolicy _pathTrustPolicy;

    internal WindowsNetBirdInstallationOwnershipGate(
        WindowsNetBirdInstallationOwnershipOptions options,
        INetBirdCommandRunner runner)
        : this(options, runner, new WindowsMachineProtectedPathTrustPolicy()) { }

    internal WindowsNetBirdInstallationOwnershipGate(
        WindowsNetBirdInstallationOwnershipOptions options,
        INetBirdCommandRunner runner,
        IWindowsNetBirdPathTrustPolicy pathTrustPolicy)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Validate();
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _pathTrustPolicy = pathTrustPolicy ?? throw new ArgumentNullException(nameof(pathTrustPolicy));
    }

    public async ValueTask<NetBirdInstallationOwnership> CheckAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        string requestedExecutable;
        try
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath))
                return Deny("netbird_preflight_cli_path_invalid");
            requestedExecutable = Path.GetFullPath(executablePath);

            if (!File.Exists(_options.PinnedMsiPath))
                return Deny("netbird_preflight_msi_missing");
            if (!HasTrustedInputs())
                return Deny("netbird_preflight_path_untrusted");
            if (!await HasExpectedHashAsync(_options.PreflightScriptPath, _options.PreflightScriptSha256, MaximumPreflightBytes, cancellationToken).ConfigureAwait(false) ||
                !await HasExpectedHashAsync(_options.VerifierScriptPath, _options.VerifierScriptSha256, MaximumVerifierBytes, cancellationToken).ConfigureAwait(false) ||
                !await HasExpectedHashAsync(_options.IdentityLockPath, _options.IdentityLockSha256, MaximumLockBytes, cancellationToken).ConfigureAwait(false))
                return Deny("netbird_preflight_integrity_failed");
            // Hashing alone does not bind PowerShell -File to the inspected bytes. Re-check the
            // protected path chain immediately before execution; a passing production policy
            // guarantees that an unprivileged user cannot replace any input after inspection.
            if (!HasTrustedInputs())
                return Deny("netbird_preflight_path_untrusted");

            var powerShell = GetSystemPowerShellPath();
            if (!File.Exists(powerShell))
                return Deny("netbird_preflight_powershell_missing");

            var result = await _runner.RunAsync(
                powerShell,
                [
                    "-NoLogo",
                    "-NoProfile",
                    "-NonInteractive",
                    "-ExecutionPolicy", "Bypass",
                    "-File", _options.PreflightScriptPath,
                    "-MsiPath", _options.PinnedMsiPath,
                ],
                null,
                _options.Timeout,
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
                return Deny("netbird_preflight_failed");

            return IsOwnedResult(result.StandardOutput, requestedExecutable)
                ? new NetBirdInstallationOwnership(true, "netbird_installation_owned")
                : Deny("netbird_preflight_not_owned");
        }
        catch (OperationCanceledException)
        {
            return Deny("netbird_preflight_cancelled");
        }
        catch (TimeoutException)
        {
            return Deny("netbird_preflight_timeout");
        }
        catch (Exception)
        {
            return Deny("netbird_preflight_error");
        }
    }

    private static bool IsOwnedResult(string json, string requestedExecutable)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        });
        var root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object &&
               TryReadInt32(root, "schemaVersion", out var schemaVersion) && schemaVersion == 1 &&
               TryReadBoolean(root, "readOnly", out var readOnly) && readOnly &&
               TryReadString(root, "state", out var state) && state == "owned" &&
               TryReadString(root, "decision", out var decision) && decision == "owned-ready" &&
               TryReadBoolean(root, "safeToUse", out var safeToUse) && safeToUse &&
               TryReadObject(root, "expected", out var expected) &&
               TryReadString(expected, "cliPath", out var expectedCliPath) &&
               PathEquals(expectedCliPath, requestedExecutable);
    }

    private bool HasTrustedInputs() => _pathTrustPolicy.AreTrusted(
        [
            _options.PreflightScriptPath,
            _options.VerifierScriptPath,
            _options.IdentityLockPath,
            _options.PinnedMsiPath,
        ]);

    private static async ValueTask<bool> HasExpectedHashAsync(
        string path,
        string expectedHash,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0 || file.Length > maximumBytes ||
            (file.Attributes & FileAttributes.ReparsePoint) != 0)
            return false;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(actual),
            Convert.FromHexString(expectedHash));
    }

    private static string GetSystemPowerShellPath()
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return string.IsNullOrWhiteSpace(systemDirectory)
            ? string.Empty
            : Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    private static bool TryReadObject(JsonElement owner, string name, out JsonElement value) =>
        TryGetUniqueProperty(owner, name, out value) && value.ValueKind == JsonValueKind.Object;

    private static bool TryReadString(JsonElement owner, string name, out string value)
    {
        value = string.Empty;
        if (!TryGetUniqueProperty(owner, name, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString()!;
        return true;
    }

    private static bool TryReadBoolean(JsonElement owner, string name, out bool value)
    {
        value = false;
        if (!TryGetUniqueProperty(owner, name, out var element) ||
            element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        value = element.GetBoolean();
        return true;
    }

    private static bool TryReadInt32(JsonElement owner, string name, out int value)
    {
        value = 0;
        return TryGetUniqueProperty(owner, name, out var element) &&
               element.ValueKind == JsonValueKind.Number &&
               element.TryGetInt32(out value);
    }

    private static bool TryGetUniqueProperty(JsonElement owner, string name, out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in owner.EnumerateObject())
        {
            if (property.Name != name) continue;
            if (found) return false;
            value = property.Value;
            found = true;
        }
        return found;
    }

    private static bool PathEquals(string left, string right)
    {
        try
        {
            return Path.IsPathFullyQualified(left) &&
                   string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static NetBirdInstallationOwnership Deny(string diagnosticCode) => new(false, diagnosticCode);
}

internal sealed record ValidatedWindowsNetBirdInstallationOwnershipOptions(
    string PreflightScriptPath,
    string PinnedMsiPath,
    string VerifierScriptPath,
    string IdentityLockPath,
    string PreflightScriptSha256,
    string VerifierScriptSha256,
    string IdentityLockSha256,
    TimeSpan Timeout);

public interface IWindowsNetBirdPathTrustPolicy
{
    bool AreTrusted(IReadOnlyCollection<string> paths);
}

public sealed class WindowsMachineProtectedPathTrustPolicy : IWindowsNetBirdPathTrustPolicy
{
    private static readonly HashSet<string> TrustedOwnerSids = new(StringComparer.Ordinal)
    {
        "S-1-5-18",       // LocalSystem
        "S-1-5-32-544",   // BUILTIN\Administrators
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", // TrustedInstaller
    };

    private const FileSystemRights DirectoryMutationRights =
        (FileSystemRights)0x10000000 | // GENERIC_ALL (may be left unexpanded in an ACE)
        (FileSystemRights)0x40000000 | // GENERIC_WRITE
        FileSystemRights.WriteAttributes |
        FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership;

    private const FileSystemRights FileMutationRights =
        DirectoryMutationRights |
        FileSystemRights.WriteData |
        FileSystemRights.AppendData;

    private readonly string[] _trustedRoots;

    public WindowsMachineProtectedPathTrustPolicy()
    {
        _trustedRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];
        _trustedRoots = _trustedRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool AreTrusted(IReadOnlyCollection<string> paths)
    {
        if (!OperatingSystem.IsWindows() || paths.Count == 0)
            return false;

        try
        {
            return paths.All(IsTrusted);
        }
        catch (SystemException)
        {
            return false;
        }
    }

    private bool IsTrusted(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!_trustedRoots.Any(root => IsStrictDescendant(fullPath, root)))
            return false;

        var volumeRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(volumeRoot))
            return false;

        var current = volumeRoot;
        if (!IsProtectedEntry(current))
            return false;
        foreach (var part in fullPath[volumeRoot.Length..].Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!IsProtectedEntry(current))
                return false;
        }

        return true;
    }

    private static bool IsProtectedEntry(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            return false;

        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        FileSystemSecurity security = isDirectory
            ? FileSystemAclExtensions.GetAccessControl(
                new DirectoryInfo(path),
                AccessControlSections.Owner | AccessControlSections.Access)
            : FileSystemAclExtensions.GetAccessControl(
                new FileInfo(path),
                AccessControlSections.Owner | AccessControlSections.Access);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !TrustedOwnerSids.Contains(owner.Value))
            return false;
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null)
            return false;

        var mutationRights = isDirectory ? DirectoryMutationRights : FileMutationRights;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
                     includeExplicit: true,
                     includeInherited: true,
                     targetType: typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0 ||
                TrustedOwnerSids.Contains(rule.IdentityReference.Value))
                continue;
            if ((rule.FileSystemRights & mutationRights) != 0)
                return false;
        }

        return true;
    }

    private static bool IsStrictDescendant(string path, string root) =>
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}
