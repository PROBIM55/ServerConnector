using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Connector.Access.Client;
using Connector.Network;

namespace Connector.Upgrade.PlatformAccess;

/// <summary>
/// Binds the local NetBird WireGuard identity to the server-issued peer receipt over the
/// coordinator-owned protected mTLS route. Any incomplete or inconsistent proof fails closed.
/// </summary>
public sealed class NetBirdExactPeerBindingProbe : IExactVpnPeerBindingProbe
{
    internal const string ServiceId = "peer-attestation";
    internal const string RelativeRoute = "api/platform/connector/private/v1/peer-attestation";
    private const string AbsoluteRoute = "/api/platform/connector/private/v1/peer-attestation";
    private const int MaximumResponseBytes = 8 * 1024;
    private const int MaximumAcceptedNonceHistory = 256;
    private static readonly JsonSerializerOptions RequestJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly INetBirdExactPeerIdentitySource _identity;
    private readonly INetBirdExactPeerRouteSource _routes;
    private readonly INetBirdExactPeerCertificateSource _certificates;
    private readonly IExactPeerNonceSource _nonces;
    private readonly TimeProvider _time;
    private readonly NetBirdExactPeerBindingOptions _options;
    private readonly object _nonceGate = new();
    private readonly HashSet<string> _acceptedNonces = new(StringComparer.Ordinal);
    private readonly Queue<string> _acceptedNonceOrder = new();

    public NetBirdExactPeerBindingProbe(
        NetBirdCliClient netBird,
        CommonConnectorConnectionCoordinator coordinator,
        HttpConnectorEnrollmentClient enrollment)
        : this(
            new NetBirdCliExactPeerIdentitySource(netBird),
            new CoordinatorExactPeerRouteSource(coordinator),
            new EnrollmentExactPeerCertificateSource(enrollment),
            new CryptographicExactPeerNonceSource(),
            TimeProvider.System,
            NetBirdExactPeerBindingOptions.Default)
    {
    }

    internal NetBirdExactPeerBindingProbe(
        INetBirdExactPeerIdentitySource identity,
        INetBirdExactPeerRouteSource routes,
        INetBirdExactPeerCertificateSource certificates,
        IExactPeerNonceSource nonces,
        TimeProvider timeProvider,
        NetBirdExactPeerBindingOptions options)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _certificates = certificates ?? throw new ArgumentNullException(nameof(certificates));
        _nonces = nonces ?? throw new ArgumentNullException(nameof(nonces));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async ValueTask<bool> VerifyAsync(
        ExactVpnPeerBindingProbeRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return false;

        try
        {
            return await VerifyCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async ValueTask<bool> VerifyCoreAsync(
        ExactVpnPeerBindingProbeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryValidateRequest(request, out var managementUri, out var expectedAddresses))
            return false;

        var beforeRead = await _identity.ReadAsync(managementUri!, cancellationToken).ConfigureAwait(false);
        if (!TryValidateIdentity(beforeRead, managementUri!, expectedAddresses!, out var before))
            return false;

        using var route = await _routes
            .OpenAsync(ServiceId, RelativeRoute, cancellationToken)
            .ConfigureAwait(false);
        if (!ValidateRoute(route, expectedAddresses!))
            return false;

        // The protected route has already leased its mTLS credential from the coordinator.
        // A second lease from the same concrete enrollment client must hash to the certificate
        // authenticated by the receipt; a concurrent credential rotation therefore fails closed.
        using var certificate = await _certificates.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(certificate.DeviceId, request.DeviceId, StringComparison.Ordinal) ||
            certificate.EnrollmentRevision < 1 || certificate.EnrollmentRevision > request.Revision ||
            !certificate.Certificate.HasPrivateKey)
            return false;

        var nonce = _nonces.CreateNonce();
        if (!IsCanonicalNonce(nonce))
            return false;

        using var message = new HttpRequestMessage(HttpMethod.Post, route.Uri)
        {
            Content = JsonContent.Create(
                new PeerAttestationRequestBody(nonce, before!.WireGuardPublicKey),
                options: RequestJsonOptions)
        };
        using var response = await route.Client
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK ||
            response.RequestMessage?.RequestUri is not { } completedUri ||
            !SameUri(route.Uri, completedUri) ||
            !string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            return false;

        var payload = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!TryParseReceipt(payload, out var receipt) ||
            !ValidateReceipt(receipt!, nonce, request, before, certificate.Certificate, route.Uri))
            return false;

        var afterRead = await _identity.ReadAsync(managementUri!, cancellationToken).ConfigureAwait(false);
        if (!TryValidateIdentity(afterRead, managementUri!, expectedAddresses!, out var after) ||
            !SameIdentity(before, after!) ||
            !ValidateCurrentSession(_routes.Current, request, managementUri!, expectedAddresses!))
            return false;

        return RememberAcceptedNonce(nonce);
    }

    private bool TryValidateRequest(
        ExactVpnPeerBindingProbeRequest request,
        out Uri? managementUri,
        out HashSet<IPAddress>? expectedAddresses)
    {
        managementUri = request.Overlay.ManagementUri;
        expectedAddresses = null;
        if (!IsBoundedIdentity(request.DeviceId) || request.Revision < 1 ||
            !IsBoundedIdentity(request.PeerId) || !IsSafeManagementUri(managementUri) ||
            request.Overlay.Status != NetworkServiceStatus.Ready ||
            !request.Overlay.StartupCheckPassed || !request.Overlay.ManagementConnected ||
            !request.Overlay.SignalConnected || !IsFreshLocalObservation(request.Overlay.ObservedAtUtc) ||
            !TryAddressSet(request.ExpectedAssignedAddresses, out expectedAddresses) ||
            !TryAddressSet(request.Overlay.AssignedInternalAddresses, out var overlayAddresses) ||
            !expectedAddresses.SetEquals(overlayAddresses))
            return false;
        return true;
    }

    private bool TryValidateIdentity(
        NetBirdLocalIdentityReadResult read,
        Uri managementUri,
        HashSet<IPAddress> expectedAddresses,
        out NetBirdLocalIdentity? identity)
    {
        identity = read.Identity;
        return read.Status == NetBirdLocalIdentityStatus.Available && identity is not null &&
               IsCanonicalWireGuardKey(identity.WireGuardPublicKey) &&
               SameManagementUri(managementUri, identity.ManagementUri) &&
               IsFreshLocalObservation(identity.ObservedAtUtc) &&
               TryAddressSet(identity.AssignedInternalAddresses, out var identityAddresses) &&
               expectedAddresses.SetEquals(identityAddresses);
    }

    private static bool ValidateRoute(IExactPeerAttestationRoute route, HashSet<IPAddress> expectedAddresses)
    {
        if (!string.Equals(route.ServiceId, ServiceId, StringComparison.Ordinal) ||
            !route.Uri.IsAbsoluteUri || route.Uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(route.Uri.UserInfo) || !string.IsNullOrEmpty(route.Uri.Query) ||
            !string.IsNullOrEmpty(route.Uri.Fragment) ||
            !string.Equals(route.Uri.AbsolutePath, AbsoluteRoute, StringComparison.Ordinal) ||
            route.Overlay.Status != NetworkServiceStatus.Ready || !route.Overlay.StartupCheckPassed ||
            !route.Overlay.ManagementConnected || !route.Overlay.SignalConnected ||
            !TryAddressSet(route.Overlay.AssignedInternalAddresses, out var routeAddresses))
            return false;
        return expectedAddresses.SetEquals(routeAddresses);
    }

    private bool ValidateCurrentSession(
        CommonConnectorConnectionSnapshot current,
        ExactVpnPeerBindingProbeRequest request,
        Uri managementUri,
        HashSet<IPAddress> expectedAddresses)
    {
        var overlay = current.Overlay;
        return current.Status == CommonConnectorConnectionStatus.Ready &&
               string.Equals(current.DeviceId, request.DeviceId, StringComparison.Ordinal) &&
               current.Revision == request.Revision && overlay is not null &&
               overlay.Status == NetworkServiceStatus.Ready && overlay.StartupCheckPassed &&
               overlay.ManagementConnected && overlay.SignalConnected &&
               SameManagementUri(managementUri, overlay.ManagementUri!) &&
               IsFreshLocalObservation(overlay.ObservedAtUtc) &&
               TryAddressSet(overlay.AssignedInternalAddresses, out var currentAddresses) &&
               expectedAddresses.SetEquals(currentAddresses);
    }

    private bool ValidateReceipt(
        NetBirdPeerAttestationReceipt receipt,
        string nonce,
        ExactVpnPeerBindingProbeRequest request,
        NetBirdLocalIdentity identity,
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate,
        Uri routeUri)
    {
        if (!FixedTimeEquals(receipt.Nonce, nonce) ||
            !string.Equals(receipt.DeviceId, request.DeviceId, StringComparison.Ordinal) ||
            receipt.AccessRevision != request.Revision ||
            !string.Equals(receipt.PeerId, request.PeerId, StringComparison.Ordinal) ||
            !string.Equals(receipt.WireGuardPublicKey, identity.WireGuardPublicKey, StringComparison.Ordinal) ||
            !IsCanonicalWireGuardKey(receipt.WireGuardPublicKey) ||
            !TrySocketAddress(receipt.SourceIp, out var sourceIp) ||
            !TrySocketAddress(receipt.ListenerIp, out var listenerIp) ||
            !request.ExpectedAssignedAddresses.Select(Normalize).Contains(sourceIp!) ||
            !identity.AssignedInternalAddresses.Select(Normalize).Contains(sourceIp!) ||
            !ListenerMatchesObservableDestination(routeUri, listenerIp!) ||
            !CertificateHashMatches(receipt.CertificateSha256, certificate))
            return false;

        var now = _time.GetUtcNow();
        return IsUtc(receipt.TransportStateObservedAtUtc) && IsUtc(receipt.IssuedAtUtc) && IsUtc(receipt.ExpiresAtUtc) &&
               receipt.IssuedAtUtc >= now - _options.MaximumReceiptAge &&
               receipt.IssuedAtUtc <= now + _options.MaximumFutureClockSkew &&
               receipt.TransportStateObservedAtUtc >= receipt.IssuedAtUtc - _options.MaximumTransportStateAge &&
               receipt.TransportStateObservedAtUtc <= receipt.IssuedAtUtc + _options.MaximumFutureClockSkew &&
               receipt.ExpiresAtUtc > now && receipt.ExpiresAtUtc > receipt.IssuedAtUtc &&
               receipt.ExpiresAtUtc <= receipt.IssuedAtUtc + _options.MaximumAttestationLifetime &&
               receipt.ExpiresAtUtc <= receipt.TransportStateObservedAtUtc + _options.MaximumTransportStateAge;
    }

    private static bool SameIdentity(NetBirdLocalIdentity before, NetBirdLocalIdentity after) =>
        string.Equals(before.WireGuardPublicKey, after.WireGuardPublicKey, StringComparison.Ordinal) &&
        SameManagementUri(before.ManagementUri, after.ManagementUri) &&
        after.ObservedAtUtc >= before.ObservedAtUtc &&
        TryAddressSet(before.AssignedInternalAddresses, out var beforeAddresses) &&
        TryAddressSet(after.AssignedInternalAddresses, out var afterAddresses) &&
        beforeAddresses.SetEquals(afterAddresses);

    private bool IsFreshLocalObservation(DateTimeOffset observedAtUtc)
    {
        var now = _time.GetUtcNow();
        return IsUtc(observedAtUtc) &&
               observedAtUtc >= now - _options.MaximumLocalIdentityAge &&
               observedAtUtc <= now + _options.MaximumFutureClockSkew;
    }

    private static async ValueTask<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
            throw new InvalidDataException("Peer attestation response is too large.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("Peer attestation response is too large.");
            output.Write(buffer, 0, read);
        }
    }

    private static bool TryParseReceipt(byte[] payload, out NetBirdPeerAttestationReceipt? receipt)
    {
        receipt = null;
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 3
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!properties.TryAdd(property.Name, property.Value))
                    return false;
            if (properties.Count != 11 ||
                !TryString(properties, "nonce", out var nonce) ||
                !TryString(properties, "deviceId", out var deviceId) ||
                !properties.TryGetValue("accessRevision", out var revisionElement) ||
                !revisionElement.TryGetInt64(out var revision) ||
                !TryString(properties, "certificateSha256", out var certificateSha256) ||
                !TryString(properties, "peerId", out var peerId) ||
                !TryString(properties, "sourceIp", out var sourceIp) ||
                !TryString(properties, "listenerIp", out var listenerIp) ||
                !TryString(properties, "wireGuardPublicKey", out var wireGuardPublicKey) ||
                !TryDate(properties, "transportStateObservedAtUtc", out var transportObserved) ||
                !TryDate(properties, "issuedAtUtc", out var issuedAt) ||
                !TryDate(properties, "expiresAtUtc", out var expiresAt))
                return false;
            receipt = new NetBirdPeerAttestationReceipt(
                nonce!, deviceId!, revision, certificateSha256!, peerId!, sourceIp!, listenerIp!,
                wireGuardPublicKey!, transportObserved, issuedAt, expiresAt);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryString(Dictionary<string, JsonElement> properties, string name, out string? value)
    {
        value = null;
        return properties.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.String &&
               (value = element.GetString()) is not null;
    }

    private static bool TryDate(Dictionary<string, JsonElement> properties, string name, out DateTimeOffset value)
    {
        value = default;
        return properties.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.String &&
               element.TryGetDateTimeOffset(out value);
    }

    private static bool TryAddressSet(IEnumerable<IPAddress>? addresses, out HashSet<IPAddress> result)
    {
        result = new HashSet<IPAddress>();
        if (addresses is null)
            return false;
        var count = 0;
        foreach (var address in addresses)
        {
            count++;
            if (count > 16 || !IsSafeAddress(address) || !result.Add(Normalize(address)))
                return false;
        }
        return count > 0;
    }

    private static bool TrySocketAddress(string? value, out IPAddress? address)
    {
        address = null;
        if (value is null || value.Length is < 2 or > 45 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Contains('/') || value.Contains('%') || !IPAddress.TryParse(value, out var parsed) ||
            !IsSafeAddress(parsed))
            return false;
        address = Normalize(parsed);
        return true;
    }

    private static bool IsSafeAddress(IPAddress? address)
    {
        if (address is null)
            return false;
        address = Normalize(address);
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) ||
            IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork ? bytes[0] is not (>= 224 and <= 239) : bytes[0] != 0xff;
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool ListenerMatchesObservableDestination(Uri routeUri, IPAddress listenerIp) =>
        !IPAddress.TryParse(routeUri.IdnHost.Trim('[', ']'), out var destination) ||
        Normalize(destination).Equals(listenerIp);

    private static bool IsSafeManagementUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true } && uri.Scheme == Uri.UriSchemeHttps &&
        !string.IsNullOrWhiteSpace(uri.Host) && string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    private static bool SameManagementUri(Uri expected, Uri actual) =>
        IsSafeManagementUri(actual) && expected.Scheme == actual.Scheme && expected.Port == actual.Port &&
        string.Equals(expected.IdnHost, actual.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected.AbsolutePath.TrimEnd('/'), actual.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);

    private static bool SameUri(Uri expected, Uri actual) =>
        expected.Scheme == actual.Scheme && expected.Port == actual.Port &&
        string.Equals(expected.IdnHost, actual.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected.PathAndQuery, actual.PathAndQuery, StringComparison.Ordinal) &&
        string.Equals(expected.Fragment, actual.Fragment, StringComparison.Ordinal);

    private static bool IsCanonicalWireGuardKey(string? value)
    {
        if (value is null || value.Length != 44)
            return false;
        Span<byte> decoded = stackalloc byte[32];
        return Convert.TryFromBase64String(value, decoded, out var written) && written == decoded.Length &&
               string.Equals(Convert.ToBase64String(decoded), value, StringComparison.Ordinal);
    }

    private static bool IsCanonicalNonce(string? value)
    {
        if (value is null || value.Length is < 22 or > 86 || value.Any(character =>
                !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
            return false;
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 += new string('=', (4 - base64.Length % 4) % 4);
            var bytes = Convert.FromBase64String(base64);
            var canonical = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return bytes.Length is >= 16 and <= 64 && string.Equals(canonical, value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool CertificateHashMatches(
        string? claimedHash,
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate)
    {
        if (claimedHash is not { Length: 64 } || claimedHash.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            return false;
        var expected = SHA256.HashData(certificate.RawData);
        var claimed = Convert.FromHexString(claimedHash);
        return CryptographicOperations.FixedTimeEquals(expected, claimed);
    }

    private static bool FixedTimeEquals(string actual, string expected)
    {
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return actualBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    private static bool IsUtc(DateTimeOffset value) => value.Offset == TimeSpan.Zero;
    private static bool IsBoundedIdentity(string? value) => value is { Length: > 0 and <= 256 } &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private bool RememberAcceptedNonce(string nonce)
    {
        lock (_nonceGate)
        {
            if (!_acceptedNonces.Add(nonce))
                return false;
            _acceptedNonceOrder.Enqueue(nonce);
            if (_acceptedNonceOrder.Count > MaximumAcceptedNonceHistory)
                _acceptedNonces.Remove(_acceptedNonceOrder.Dequeue());
            return true;
        }
    }

    private sealed record PeerAttestationRequestBody(
        [property: JsonPropertyName("nonce")] string Nonce,
        [property: JsonPropertyName("publicKey")] string PublicKey);
}

internal sealed record NetBirdExactPeerBindingOptions(
    TimeSpan MaximumLocalIdentityAge,
    TimeSpan MaximumReceiptAge,
    TimeSpan MaximumTransportStateAge,
    TimeSpan MaximumFutureClockSkew,
    TimeSpan MaximumAttestationLifetime)
{
    public static NetBirdExactPeerBindingOptions Default { get; } = new(
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromMinutes(1));

    public void Validate()
    {
        if (MaximumLocalIdentityAge <= TimeSpan.Zero || MaximumLocalIdentityAge > TimeSpan.FromMinutes(5) ||
            MaximumReceiptAge <= TimeSpan.Zero || MaximumReceiptAge > TimeSpan.FromMinutes(5) ||
            MaximumTransportStateAge <= TimeSpan.Zero || MaximumTransportStateAge > TimeSpan.FromMinutes(5) ||
            MaximumFutureClockSkew < TimeSpan.Zero || MaximumFutureClockSkew > TimeSpan.FromMinutes(1) ||
            MaximumAttestationLifetime <= TimeSpan.Zero || MaximumAttestationLifetime > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(NetBirdExactPeerBindingOptions));
    }
}

internal sealed record NetBirdPeerAttestationReceipt(
    string Nonce,
    string DeviceId,
    long AccessRevision,
    string CertificateSha256,
    string PeerId,
    string SourceIp,
    string ListenerIp,
    string WireGuardPublicKey,
    DateTimeOffset TransportStateObservedAtUtc,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);
