using System.Net;
using System.Text.Json;

namespace Connector.Access.NetBird;

public interface INetBirdDaemonPeerSnapshotReader
{
    ValueTask<NetBirdDaemonPeerSnapshotResult> ReadAsync(
        string expectedIp,
        string expectedPublicKey,
        CancellationToken cancellationToken = default);
}

public sealed class NetBirdDaemonPeerSnapshotReader : INetBirdDaemonPeerSnapshotReader
{
    private readonly NetBirdDaemonPeerSnapshotOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly INetBirdDaemonCommandRunner _commandRunner;

    public NetBirdDaemonPeerSnapshotReader(
        NetBirdDaemonPeerSnapshotOptions options,
        TimeProvider? timeProvider = null)
        : this(options, timeProvider ?? TimeProvider.System, ProcessNetBirdDaemonCommandRunner.Instance)
    {
    }

    internal NetBirdDaemonPeerSnapshotReader(
        NetBirdDaemonPeerSnapshotOptions options,
        TimeProvider timeProvider,
        INetBirdDaemonCommandRunner commandRunner)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
    }

    public async ValueTask<NetBirdDaemonPeerSnapshotResult> ReadAsync(
        string expectedIp,
        string expectedPublicKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_options.TryValidate(out var options) || options is null)
        {
            return new NetBirdDaemonPeerUnavailable(NetBirdDaemonPeerUnavailableReason.InvalidConfiguration);
        }

        if (!IPAddress.TryParse(expectedIp, out var expectedAddress) ||
            !NetBirdWireGuardKey.TryValidate(expectedPublicKey))
        {
            return new NetBirdDaemonPeerUnavailable(NetBirdDaemonPeerUnavailableReason.InvalidExpectedPeer);
        }

        var command = new NetBirdDaemonCommand(
            options.ExecutablePath,
            ["status", "--json"],
            options.CommandTimeout,
            options.MaximumOutputBytes);
        var commandResult = await _commandRunner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        if (commandResult.Status != NetBirdDaemonCommandStatus.Success)
        {
            return new NetBirdDaemonPeerUnavailable(MapUnavailableReason(commandResult.Status));
        }
        if (commandResult.StandardOutput.Length > options.MaximumOutputBytes)
        {
            return new NetBirdDaemonPeerUnavailable(NetBirdDaemonPeerUnavailableReason.OutputTooLarge);
        }

        var parseResult = NetBirdDaemonStatusParser.Parse(commandResult.StandardOutput);
        if (!parseResult.IsSuccess)
        {
            return new NetBirdDaemonPeerUnavailable(NetBirdDaemonPeerUnavailableReason.MalformedOutput);
        }

        var addressMatches = parseResult.Peers!
            .Where(peer =>
                peer.ParsedNetBirdIp.Equals(expectedAddress) ||
                peer.ParsedNetBirdIpv6?.Equals(expectedAddress) == true)
            .ToArray();
        var keyMatches = parseResult.Peers!
            .Where(peer => string.Equals(peer.PublicKey, expectedPublicKey, StringComparison.Ordinal))
            .ToArray();

        if (addressMatches.Length > 1)
        {
            return new NetBirdDaemonPeerAmbiguous(addressMatches.Length);
        }
        if (keyMatches.Length > 1)
        {
            return new NetBirdDaemonPeerAmbiguous(keyMatches.Length);
        }
        if (addressMatches.Length != 1 ||
            keyMatches.Length != 1 ||
            !ReferenceEquals(addressMatches[0], keyMatches[0]))
        {
            return new NetBirdDaemonPeerUnavailable(NetBirdDaemonPeerUnavailableReason.ExactPeerNotFound);
        }

        var peer = addressMatches[0];
        if (!string.Equals(peer.Status, "Connected", StringComparison.Ordinal))
        {
            return new NetBirdDaemonPeerStale(NetBirdDaemonPeerStaleReason.NotConnected);
        }

        var now = _timeProvider.GetUtcNow();
        if (peer.LastWireguardHandshake == DateTimeOffset.MinValue)
        {
            return new NetBirdDaemonPeerStale(NetBirdDaemonPeerStaleReason.MissingHandshake);
        }
        if (peer.LastWireguardHandshake > now)
        {
            return new NetBirdDaemonPeerStale(NetBirdDaemonPeerStaleReason.FutureHandshake);
        }
        if (peer.LastWireguardHandshake < now.Subtract(options.MaximumHandshakeAge))
        {
            return new NetBirdDaemonPeerStale(NetBirdDaemonPeerStaleReason.ExpiredHandshake);
        }

        return new NetBirdDaemonPeerExact(new NetBirdDaemonPeerEvidence(
            peer.NetBirdIp,
            peer.NetBirdIpv6,
            peer.PublicKey,
            peer.Status,
            peer.LastWireguardHandshake));
    }

    private static NetBirdDaemonPeerUnavailableReason MapUnavailableReason(NetBirdDaemonCommandStatus status) =>
        status switch
        {
            NetBirdDaemonCommandStatus.ExecutableUnavailable => NetBirdDaemonPeerUnavailableReason.ExecutableUnavailable,
            NetBirdDaemonCommandStatus.TimedOut => NetBirdDaemonPeerUnavailableReason.CommandTimedOut,
            NetBirdDaemonCommandStatus.OutputTooLarge => NetBirdDaemonPeerUnavailableReason.OutputTooLarge,
            _ => NetBirdDaemonPeerUnavailableReason.CommandFailed
        };
}

internal static class NetBirdDaemonStatusParser
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16
    };

    public static NetBirdDaemonStatusParseResult Parse(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, DocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryGetUniqueProperty(document.RootElement, "peers", out var peers) ||
                peers.ValueKind != JsonValueKind.Object ||
                !TryGetUniqueProperty(peers, "details", out var details) ||
                details.ValueKind != JsonValueKind.Array)
            {
                return NetBirdDaemonStatusParseResult.Malformed;
            }

            var parsed = new List<ParsedNetBirdDaemonPeer>();
            foreach (var peer in details.EnumerateArray())
            {
                if (!TryParsePeer(peer, out var parsedPeer))
                {
                    return NetBirdDaemonStatusParseResult.Malformed;
                }
                parsed.Add(parsedPeer!);
            }

            return new NetBirdDaemonStatusParseResult(parsed);
        }
        catch (JsonException)
        {
            return NetBirdDaemonStatusParseResult.Malformed;
        }
    }

    private static bool TryParsePeer(JsonElement peer, out ParsedNetBirdDaemonPeer? parsed)
    {
        parsed = null;
        if (peer.ValueKind != JsonValueKind.Object ||
            !TryGetRequiredString(peer, "netbirdIp", out var netBirdIp) ||
            !TryGetOptionalString(peer, "netbirdIpv6", out var netBirdIpv6) ||
            !TryGetRequiredString(peer, "publicKey", out var publicKey) ||
            !TryGetRequiredString(peer, "status", out var status) ||
            !TryGetRequiredString(peer, "lastWireguardHandshake", out var handshakeText) ||
            !TryParseAddress(netBirdIp!, System.Net.Sockets.AddressFamily.InterNetwork, out var parsedIpv4) ||
            !TryParseOptionalAddress(netBirdIpv6, System.Net.Sockets.AddressFamily.InterNetworkV6, out var parsedIpv6) ||
            !NetBirdWireGuardKey.TryValidate(publicKey!) ||
            !DateTimeOffset.TryParse(
                handshakeText,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var handshake))
        {
            return false;
        }

        parsed = new ParsedNetBirdDaemonPeer(
            netBirdIp!,
            parsedIpv4!,
            netBirdIpv6,
            parsedIpv6,
            publicKey!,
            status!,
            handshake);
        return true;
    }

    private static bool TryGetRequiredString(JsonElement owner, string name, out string? value)
    {
        value = null;
        if (!TryGetUniqueProperty(owner, name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString();
        return !string.IsNullOrEmpty(value);
    }

    private static bool TryGetOptionalString(JsonElement owner, string name, out string? value)
    {
        value = null;
        var count = 0;
        foreach (var property in owner.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.Ordinal))
            {
                continue;
            }
            count++;
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            value = property.Value.GetString();
        }
        return count <= 1;
    }

    private static bool TryGetUniqueProperty(JsonElement owner, string name, out JsonElement value)
    {
        value = default;
        var count = 0;
        foreach (var property in owner.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.Ordinal))
            {
                continue;
            }
            value = property.Value;
            count++;
        }
        return count == 1;
    }

    private static bool TryParseAddress(
        string text,
        System.Net.Sockets.AddressFamily family,
        out IPAddress? address)
    {
        address = null;
        if (!IPAddress.TryParse(text, out var parsed) || parsed.AddressFamily != family)
        {
            return false;
        }
        address = parsed;
        return true;
    }

    private static bool TryParseOptionalAddress(
        string? text,
        System.Net.Sockets.AddressFamily family,
        out IPAddress? address)
    {
        address = null;
        return string.IsNullOrEmpty(text) || TryParseAddress(text, family, out address);
    }
}

internal sealed record NetBirdDaemonStatusParseResult(IReadOnlyList<ParsedNetBirdDaemonPeer>? Peers)
{
    public static NetBirdDaemonStatusParseResult Malformed { get; } =
        new((IReadOnlyList<ParsedNetBirdDaemonPeer>?)null);
    public bool IsSuccess => Peers is not null;
}

internal static class NetBirdWireGuardKey
{
    public static bool TryValidate(string? value)
    {
        if (value is null || value.Length != 44)
        {
            return false;
        }

        Span<byte> decoded = stackalloc byte[32];
        return Convert.TryFromBase64String(value, decoded, out var written) &&
               written == decoded.Length &&
               string.Equals(Convert.ToBase64String(decoded), value, StringComparison.Ordinal);
    }
}
