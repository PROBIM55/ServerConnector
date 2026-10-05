using Connector.Access.Contracts;

namespace Connector.Access.NetBird;

public sealed class NetBirdOptions
{
    public required Uri ManagementUri { get; init; }
    public required string AccessToken { get; init; }
    public required string StateDirectory { get; init; }
    public TimeSpan SetupKeyLifetime { get; init; } = TimeSpan.FromDays(1);
    public TimeSpan MaximumPeerLastSeenAge { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ManagementRequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public IReadOnlyList<NetBirdAccessBinding> AccessBindings { get; init; } = [];

    internal ValidatedNetBirdOptions Validate()
    {
        if (!ManagementUri.IsAbsoluteUri ||
            !string.Equals(ManagementUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(ManagementUri.UserInfo) ||
            !string.IsNullOrEmpty(ManagementUri.Query) ||
            !string.IsNullOrEmpty(ManagementUri.Fragment))
        {
            throw new ArgumentException("NetBird ManagementUri must be an HTTPS origin without credentials, query or fragment.");
        }
        if (string.IsNullOrWhiteSpace(AccessToken) || AccessToken.Length > 4096)
        {
            throw new ArgumentException("A bounded NetBird service-user access token is required.");
        }
        if (string.IsNullOrWhiteSpace(StateDirectory) || !Path.IsPathFullyQualified(StateDirectory))
        {
            throw new ArgumentException("NetBird StateDirectory must be an absolute path.");
        }
        if (SetupKeyLifetime < TimeSpan.FromDays(1) || SetupKeyLifetime > TimeSpan.FromDays(365))
        {
            throw new ArgumentOutOfRangeException(nameof(SetupKeyLifetime), "NetBird accepts expires_in from 86400 to 31536000 seconds.");
        }
        if (MaximumPeerLastSeenAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumPeerLastSeenAge));
        }
        if (ManagementRequestTimeout <= TimeSpan.Zero || ManagementRequestTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(ManagementRequestTimeout));
        }

        var bindings = AccessBindings.Select(binding => binding.Validate()).ToArray();
        return new ValidatedNetBirdOptions(
            new Uri(ManagementUri.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute),
            AccessToken,
            Path.GetFullPath(StateDirectory),
            checked((int)SetupKeyLifetime.TotalSeconds),
            MaximumPeerLastSeenAge,
            ManagementRequestTimeout,
            bindings);
    }
}

public sealed class NetBirdAccessBinding
{
    public ConnectorProduct? Product { get; init; }
    public string? ModuleId { get; init; }
    public string? ResourceKind { get; init; }
    public string? ResourceId { get; init; }
    public required string DestinationGroupId { get; init; }
    public string Protocol { get; init; } = "tcp";
    public IReadOnlyList<int> Ports { get; init; } = [];
    public bool Bidirectional { get; init; }

    internal ValidatedNetBirdAccessBinding Validate()
    {
        if (string.IsNullOrWhiteSpace(DestinationGroupId) || DestinationGroupId.Length > 256)
        {
            throw new ArgumentException("Every NetBird access binding needs a destination group id.");
        }
        if (Product is null && string.IsNullOrWhiteSpace(ModuleId) && string.IsNullOrWhiteSpace(ResourceKind) && string.IsNullOrWhiteSpace(ResourceId))
        {
            throw new ArgumentException("A NetBird access binding must select a module or resource.");
        }
        if (Protocol is not ("tcp" or "udp" or "icmp" or "all"))
        {
            throw new ArgumentException("NetBird binding protocol must be tcp, udp, icmp or all.");
        }
        if (Ports.Any(port => port is < 1 or > 65535) || Ports.Count != Ports.Distinct().Count())
        {
            throw new ArgumentException("NetBird binding ports must be unique values from 1 through 65535.");
        }
        if ((Protocol is "icmp" or "all") && Ports.Count != 0)
        {
            throw new ArgumentException("ICMP/all NetBird bindings cannot carry ports.");
        }

        return new ValidatedNetBirdAccessBinding(
            Product,
            NormalizeOptional(ModuleId),
            NormalizeOptional(ResourceKind),
            NormalizeOptional(ResourceId),
            DestinationGroupId.Trim(),
            Protocol,
            Ports.Order().ToArray(),
            Bidirectional);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed record ValidatedNetBirdOptions(
    Uri ManagementUri,
    string AccessToken,
    string StateDirectory,
    int SetupKeyLifetimeSeconds,
    TimeSpan MaximumPeerLastSeenAge,
    TimeSpan ManagementRequestTimeout,
    IReadOnlyList<ValidatedNetBirdAccessBinding> AccessBindings);

internal sealed record ValidatedNetBirdAccessBinding(
    ConnectorProduct? Product,
    string? ModuleId,
    string? ResourceKind,
    string? ResourceId,
    string DestinationGroupId,
    string Protocol,
    IReadOnlyList<int> Ports,
    bool Bidirectional);
