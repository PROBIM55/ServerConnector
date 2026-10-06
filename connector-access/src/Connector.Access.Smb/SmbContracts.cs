using System.Net;
using Connector.Access.Contracts;

namespace Connector.Access.Smb;

public static class SmbHelperProtocol
{
    public const int Version = 2;
    public const string Apply = "apply";
    public const string Revoke = "revoke";
    public const string Read = "read";
    public const string Change = "change";
    public const string None = "none";
}

public interface IConnectorSmbAccessReader
{
    ValueTask<DeviceAccessResult<ConnectorSmbAccess>> GetAsync(
        AuthenticatedDevice device,
        DeviceAccessProfile profile,
        CancellationToken cancellationToken);
}

public sealed record SmbHelperGrant(string ResourceId, string Permission);

public sealed record SmbHelperRequest(
    int SchemaVersion,
    string CommandId,
    string DeviceId,
    long Revision,
    string Action,
    string LocalUserName,
    string? ExpectedLocalUserSid,
    string? Password,
    IReadOnlyList<SmbHelperGrant> Grants);

public sealed record SmbHelperResourceObservation(
    string ResourceId,
    string ShareName,
    string CanonicalRootPath,
    string ShareAccess,
    string NtfsAccess);

public sealed record SmbHelperResponse(
    int SchemaVersion,
    string CommandId,
    string DeviceId,
    long Revision,
    string Action,
    string LocalUserName,
    string LocalAccountAuthority,
    string? LocalUserSid,
    bool AccountEnabled,
    int ActiveSessionCount,
    IReadOnlyList<SmbHelperResourceObservation> Resources);

public sealed class SmbResourceBinding
{
    public required string ResourceId { get; init; }
    public string ResourceKind { get; init; } = "smb-folder";
    public required string HelperResourceId { get; init; }
    public required string ClientShareUnc { get; init; }
}

public sealed class SmbProviderOptions
{
    public required Uri HelperBaseUri { get; init; }
    public required string StateDirectory { get; init; }
    public required string DataProtectionKeyDirectory { get; init; }
    public required string DataProtectionCertificatePfxPath { get; init; }
    public required string DataProtectionCertificatePasswordEnvironmentVariable { get; init; }
    public required string ClientCertificatePfxPath { get; init; }
    public required string ClientCertificatePasswordEnvironmentVariable { get; init; }
    public string? ServerCertificateSha256 { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan AccessReceiptLifetime { get; init; } = TimeSpan.FromMinutes(2);
    public IReadOnlyList<SmbResourceBinding> ResourceBindings { get; init; } = [];

    internal ValidatedSmbProviderOptions Validate(bool requireCertificate = true)
    {
        if (HelperBaseUri is null || !HelperBaseUri.IsAbsoluteUri || HelperBaseUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(HelperBaseUri.UserInfo) || !string.IsNullOrEmpty(HelperBaseUri.Query) ||
            !string.IsNullOrEmpty(HelperBaseUri.Fragment))
            throw new ArgumentException("SMB helper URI must be an absolute HTTPS origin.");
        var origin = new Uri(HelperBaseUri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/", UriKind.Absolute);
        if (HelperBaseUri.AbsolutePath.Trim('/') is not "")
            throw new ArgumentException("SMB helper URI must not contain a path.");
        string? serverCertificateSha256 = null;
        if (ServerCertificateSha256 is not null)
        {
            if (!System.Net.IPAddress.TryParse(origin.Host.Trim('[', ']'), out var address) ||
                !System.Net.IPAddress.IsLoopback(address) || ServerCertificateSha256.Length != 64 ||
                !ServerCertificateSha256.All(Uri.IsHexDigit))
                throw new ArgumentException("SMB helper certificate pin requires a literal loopback HTTPS origin and a SHA-256 hash.");
            serverCertificateSha256 = ServerCertificateSha256.ToLowerInvariant();
        }
        if (string.IsNullOrWhiteSpace(StateDirectory) || !Path.IsPathFullyQualified(StateDirectory))
            throw new ArgumentException("SMB provider state directory must be absolute.");
        if (string.IsNullOrWhiteSpace(DataProtectionKeyDirectory) || !Path.IsPathFullyQualified(DataProtectionKeyDirectory))
            throw new ArgumentException("SMB Data Protection key directory must be absolute.");
        if (requireCertificate && (string.IsNullOrWhiteSpace(ClientCertificatePfxPath) ||
            !Path.IsPathFullyQualified(ClientCertificatePfxPath) ||
            string.IsNullOrWhiteSpace(ClientCertificatePasswordEnvironmentVariable) ||
            string.IsNullOrWhiteSpace(DataProtectionCertificatePfxPath) || !Path.IsPathFullyQualified(DataProtectionCertificatePfxPath) ||
            string.IsNullOrWhiteSpace(DataProtectionCertificatePasswordEnvironmentVariable)))
            throw new ArgumentException("SMB mTLS and Data Protection certificate paths and password environment variables are required.");
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
        if (AccessReceiptLifetime <= TimeSpan.Zero || AccessReceiptLifetime > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(AccessReceiptLifetime));
        var bindings = ResourceBindings.Select(binding => new ValidatedSmbResourceBinding(
            Required(binding.ResourceId, 256), Required(binding.ResourceKind, 128), Required(binding.HelperResourceId, 256),
            ValidateClientShareUnc(binding.ClientShareUnc))).ToArray();
        if (bindings.Length == 0 || bindings.Select(binding => (binding.ResourceKind, binding.ResourceId)).Distinct().Count() != bindings.Length ||
            bindings.Select(binding => binding.HelperResourceId).Distinct(StringComparer.Ordinal).Count() != bindings.Length ||
            bindings.Select(binding => binding.ClientShareUnc).Distinct(StringComparer.OrdinalIgnoreCase).Count() != bindings.Length)
            throw new ArgumentException("SMB resource bindings must be non-empty and unique.");
        return new ValidatedSmbProviderOptions(origin, Path.GetFullPath(StateDirectory), Path.GetFullPath(DataProtectionKeyDirectory), ClientCertificatePfxPath,
            ClientCertificatePasswordEnvironmentVariable, DataProtectionCertificatePfxPath,
            DataProtectionCertificatePasswordEnvironmentVariable, RequestTimeout, AccessReceiptLifetime, bindings, serverCertificateSha256);
    }

    private static string Required(string value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum ? value.Trim() :
        throw new ArgumentException("SMB resource binding contains an invalid identifier.");

    private static string ValidateClientShareUnc(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || !value.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("SMB client share must be a fixed UNC root.");
        var parts = value[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) ||
            !IsPrivate(address) || parts[1] is "." or ".." || parts[1].IndexOfAny(['/', ':']) >= 0)
            throw new ArgumentException("SMB client share must use a literal private overlay IP and a single share name.");
        return $@"\\{address}\{parts[1]}";
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? bytes[0] == 10 || bytes[0] == 100 && bytes[1] is >= 64 and <= 127 ||
              bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
            : bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc;
    }
}

internal sealed record ValidatedSmbProviderOptions(
    Uri HelperBaseUri,
    string StateDirectory,
    string DataProtectionKeyDirectory,
    string ClientCertificatePfxPath,
    string ClientCertificatePasswordEnvironmentVariable,
    string DataProtectionCertificatePfxPath,
    string DataProtectionCertificatePasswordEnvironmentVariable,
    TimeSpan RequestTimeout,
    TimeSpan AccessReceiptLifetime,
    IReadOnlyList<ValidatedSmbResourceBinding> ResourceBindings,
    string? ServerCertificateSha256);

internal sealed record ValidatedSmbResourceBinding(
    string ResourceId, string ResourceKind, string HelperResourceId, string ClientShareUnc)
{
    public string ShareName => ClientShareUnc[(ClientShareUnc.LastIndexOf('\\') + 1)..];
}
