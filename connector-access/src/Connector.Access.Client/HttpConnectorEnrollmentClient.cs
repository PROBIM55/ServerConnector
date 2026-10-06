using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Connector.Access.Contracts;

namespace Connector.Access.Client;

public sealed class HttpConnectorEnrollmentClient : IConnectorEnrollmentClient, IConnectorDeviceAccessClient,
    IConnectorIssuedCredentialSource, IConnectorExactDeviceRevocationClient
{
    private const int MaximumResponseBytes = 128 * 1024;
    private const int MaximumRevocationStatusPolls = 20;
    private static readonly TimeSpan RevocationPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RevocationOperationTimeout = TimeSpan.FromSeconds(15);
    private const string RevocationProbeDeviceId = "dev_00000000000000000000000000000000";
    private static readonly Guid RevocationProbeRequestId = new("00000000-0000-0000-0000-000000000001");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ValidatedClientOptions _options;
    private readonly FileConnectorEnrollmentStateStore _store;
    private readonly IEnrollmentHttpInvokerFactory _httpFactory;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public HttpConnectorEnrollmentClient(
        ConnectorAccessClientOptions options,
        FileConnectorEnrollmentStateStore store)
        : this(options, store, new NativeEnrollmentHttpInvokerFactory())
    {
    }

    internal HttpConnectorEnrollmentClient(
        ConnectorAccessClientOptions options,
        FileConnectorEnrollmentStateStore store,
        IEnrollmentHttpInvokerFactory httpFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Validate();
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
    }

    public async ValueTask<DeviceEnrollmentResponse> EnrollAsync(
        string enrollmentToken,
        string deviceDisplayName,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        StoredEnrollmentState? state = null;
        try
        {
            state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (state is not null)
            {
                ValidateBinding(state);
                if (state.Status == StoredEnrollmentStatus.Completed)
                {
                    try
                    {
                        return await ResumeStateAsync(state, cancellationToken).ConfigureAwait(false);
                    }
                    catch (ConnectorEnrollmentProtocolException exception) when (IssuedCredentialIsExplicitlyRejected(exception))
                    {
                        ValidateEnrollmentToken(enrollmentToken);
                        var replacement = EnrollmentCryptography.CreatePending(_options, deviceDisplayName);
                        try
                        {
                            // Persist the replacement request and key before disclosing the
                            // new one-time token. A failed POST is therefore token-free to retry.
                            await _store.SaveAsync(replacement, cancellationToken).ConfigureAwait(false);
                        }
                        catch
                        {
                            CryptographicOperations.ZeroMemory(replacement.PrivateKeyPkcs8);
                            throw;
                        }

                        CryptographicOperations.ZeroMemory(state.PrivateKeyPkcs8);
                        state = replacement;
                        return await EnrollPendingWithTokenAsync(state, enrollmentToken, cancellationToken).ConfigureAwait(false);
                    }
                }

                try
                {
                    return await ResumeStateAsync(state, cancellationToken).ConfigureAwait(false);
                }
                catch (ConnectorEnrollmentProtocolException exception) when (EnrollmentIsExplicitlyAbsent(exception))
                {
                    ValidateEnrollmentToken(enrollmentToken);
                    return await EnrollPendingWithTokenAsync(state, enrollmentToken, cancellationToken).ConfigureAwait(false);
                }
            }

            ValidateEnrollmentToken(enrollmentToken);

            state = EnrollmentCryptography.CreatePending(_options, deviceDisplayName);
            // This crash boundary is intentional: request id, CSR and DPAPI key
            // reach durable storage before the one-time token is sent.
            await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);

            return await EnrollPendingWithTokenAsync(state, enrollmentToken, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (state is not null)
            {
                CryptographicOperations.ZeroMemory(state.PrivateKeyPkcs8);
            }

            _operationGate.Release();
        }
    }

    public async ValueTask<DeviceEnrollmentResponse> ResumeAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        StoredEnrollmentState? state = null;
        try
        {
            state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new ConnectorEnrollmentStateException("No connector enrollment is available to resume.");
            ValidateBinding(state);
            return await ResumeStateAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (state is not null)
            {
                CryptographicOperations.ZeroMemory(state.PrivateKeyPkcs8);
            }

            _operationGate.Release();
        }
    }

    public async ValueTask<DeviceEnrollmentResponse?> GetReceiptAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        StoredEnrollmentState? state = null;
        try
        {
            state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                return null;
            }

            ValidateBinding(state);
            if (state.Status == StoredEnrollmentStatus.Pending)
            {
                return null;
            }

            return await GetIssuedReceiptAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (state is not null)
            {
                CryptographicOperations.ZeroMemory(state.PrivateKeyPkcs8);
            }

            _operationGate.Release();
        }
    }

    private async ValueTask<DeviceEnrollmentResponse> ResumeStateAsync(
        StoredEnrollmentState state,
        CancellationToken cancellationToken)
    {
        using var key = EnrollmentCryptography.LoadAndValidateKey(state);
        if (state.Status == StoredEnrollmentStatus.Completed)
        {
            return await GetIssuedReceiptAsync(state, key, cancellationToken).ConfigureAwait(false);
        }

        using var bootstrapCertificate = EnrollmentCryptography.CreateBootstrapCertificate(state, key, _options);
        var response = await SendForEnrollmentResponseAsync(
            HttpMethod.Post,
            Endpoint($"enrollments/{state.RequestId:D}/resume"),
            body: null,
            bootstrapCertificate,
            cancellationToken).ConfigureAwait(false);
        return await ValidateAndCompleteAsync(state, response, key, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DeviceEnrollmentResponse> EnrollPendingWithTokenAsync(
        StoredEnrollmentState state,
        string enrollmentToken,
        CancellationToken cancellationToken)
    {
        var request = new DeviceEnrollmentRequest(
            DeviceAccessProtocol.Version,
            state.RequestId,
            enrollmentToken,
            state.CertificateSigningRequestPem,
            state.DeviceDisplayName);
        var response = await SendForEnrollmentResponseAsync(
            HttpMethod.Post,
            Endpoint("enroll"),
            request,
            clientCertificate: null,
            cancellationToken).ConfigureAwait(false);
        return await ValidateAndCompleteAsync(state, response, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DeviceEnrollmentResponse> GetIssuedReceiptAsync(
        StoredEnrollmentState state,
        CancellationToken cancellationToken)
    {
        using var key = EnrollmentCryptography.LoadAndValidateKey(state);
        return await GetIssuedReceiptAsync(state, key, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DeviceEnrollmentResponse> GetIssuedReceiptAsync(
        StoredEnrollmentState state,
        EnrollmentCryptography.KeyMaterial key,
        CancellationToken cancellationToken)
    {
        using var issuedCertificate = EnrollmentCryptography.CreateIssuedTlsCertificate(state, key, _options);
        var response = await SendForEnrollmentResponseAsync(
            HttpMethod.Get,
            Endpoint($"enrollments/{state.RequestId:D}"),
            body: null,
            issuedCertificate,
            cancellationToken).ConfigureAwait(false);
        return await ValidateAndCompleteAsync(state, response, key, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DeviceEnrollmentResponse> ValidateAndCompleteAsync(
        StoredEnrollmentState state,
        DeviceEnrollmentResponse response,
        CancellationToken cancellationToken)
    {
        using var key = EnrollmentCryptography.LoadAndValidateKey(state);
        return await ValidateAndCompleteAsync(state, response, key, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<DeviceEnrollmentResponse> ValidateAndCompleteAsync(
        StoredEnrollmentState state,
        DeviceEnrollmentResponse response,
        EnrollmentCryptography.KeyMaterial key,
        CancellationToken cancellationToken)
    {
        using var validatedCertificate = EnrollmentCryptography.ValidateResponseAndAttachKey(response, state, key, _options);
        var completed = new StoredEnrollmentState
        {
            SchemaVersion = state.SchemaVersion,
            RequestId = state.RequestId,
            ServiceBaseUri = state.ServiceBaseUri,
            IssuerPinSha256 = state.IssuerPinSha256,
            KeyAlgorithm = state.KeyAlgorithm,
            PrivateKeyPkcs8 = state.PrivateKeyPkcs8,
            CertificateSigningRequestPem = state.CertificateSigningRequestPem,
            PublicKeySha256 = state.PublicKeySha256,
            DeviceDisplayName = state.DeviceDisplayName,
            Status = StoredEnrollmentStatus.Completed,
            Response = response,
        };
        await _store.SaveAsync(completed, cancellationToken).ConfigureAwait(false);
        return response;
    }

    public async ValueTask<DeviceAccessProfile> GetAccessProfileAsync(CancellationToken cancellationToken = default)
    {
        return await GetIssuedDeviceResponseAsync<DeviceAccessProfile>("profile", (profile, receipt) =>
        {
            if (profile.SchemaVersion != DeviceAccessProtocol.Version || profile.DeviceId != receipt.DeviceId ||
                string.IsNullOrWhiteSpace(profile.UserId) || string.IsNullOrWhiteSpace(profile.CompanyId) ||
                profile.DesiredRevision < receipt.AccessRevision || profile.AppliedRevision != profile.DesiredRevision ||
                profile.AppliedRevision <= 0 || profile.ExpiresAtUtc <= _options.TimeProvider.GetUtcNow() ||
                profile.ExpiresAtUtc > receipt.CertificateExpiresAtUtc + _options.ClockSkew ||
                profile.Modules is null || profile.Resources is null ||
                profile.Modules.Any(grant => !Enum.IsDefined(grant.Product) || string.IsNullOrWhiteSpace(grant.ModuleId) || grant.Permissions is null || grant.Permissions.Any(permission => !Enum.IsDefined(permission))) ||
                profile.Resources.Any(grant => string.IsNullOrWhiteSpace(grant.ResourceId) || string.IsNullOrWhiteSpace(grant.ResourceKind) || grant.Permissions is null || grant.Permissions.Any(permission => !Enum.IsDefined(permission))))
                throw new ConnectorEnrollmentProtocolException("The device access profile is not current applied access for this device.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ConnectorVpnBootstrap> GetVpnBootstrapAsync(CancellationToken cancellationToken = default)
    {
        return await GetIssuedDeviceResponseAsync<ConnectorVpnBootstrap>("vpn/bootstrap", (bootstrap, receipt) =>
        {
            if (bootstrap.Revision < receipt.AccessRevision || bootstrap.ExpiresAtUtc <= _options.TimeProvider.GetUtcNow() ||
                string.IsNullOrWhiteSpace(bootstrap.SetupKey) || bootstrap.SetupKey.Length > 4096 ||
                !Uri.TryCreate(bootstrap.ManagementUri, UriKind.Absolute, out var management) || management.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(management.UserInfo) || !string.IsNullOrEmpty(management.Query) || !string.IsNullOrEmpty(management.Fragment))
                throw new ConnectorEnrollmentProtocolException("The VPN bootstrap is invalid or expired.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ConnectorVpnTransportState> GetVpnStateAsync(CancellationToken cancellationToken = default)
    {
        return await GetIssuedDeviceResponseAsync<ConnectorVpnTransportState>("vpn/state", (state, receipt) =>
        {
            var now = _options.TimeProvider.GetUtcNow();
            if (state.DeviceId != receipt.DeviceId || state.Revision < receipt.AccessRevision ||
                state.Status is not ("ready" or "connecting" or "degraded" or "unknown" or "revoked" or "denied") ||
                state.ObservedAtUtc < now - TimeSpan.FromMinutes(2) - _options.ClockSkew ||
                state.ObservedAtUtc > now + _options.ClockSkew ||
                !Uri.TryCreate(state.ManagementUri, UriKind.Absolute, out var management) || management.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(management.UserInfo) || !string.IsNullOrEmpty(management.Query) || !string.IsNullOrEmpty(management.Fragment) ||
                state.ExpectedAssignedAddresses is null || state.ExpectedAssignedAddresses.Count > 16 ||
                state.ExpectedAssignedAddresses.Any(address => !System.Net.IPAddress.TryParse(address, out var ip) ||
                    System.Net.IPAddress.IsLoopback(ip) || ip.Equals(System.Net.IPAddress.Any) || ip.Equals(System.Net.IPAddress.IPv6Any)) ||
                (state.Status == "ready" && (string.IsNullOrWhiteSpace(state.PeerId) || state.ExpectedAssignedAddresses.Count == 0)))
                throw new ConnectorEnrollmentProtocolException("The VPN state is not current transport for this device.");
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ConnectorIssuedCertificateLease> AcquireIssuedCertificateAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        StoredEnrollmentState? state = null;
        try
        {
            state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new ConnectorEnrollmentStateException("Device enrollment is required.");
            ValidateBinding(state);
            if (state.Status != StoredEnrollmentStatus.Completed || state.Response is null)
                throw new ConnectorEnrollmentStateException("Device enrollment has not completed.");
            using var key = EnrollmentCryptography.LoadAndValidateKey(state);
            var certificate = EnrollmentCryptography.CreateIssuedTlsCertificate(state, key, _options);
            return new ConnectorIssuedCertificateLease(state.Response.DeviceId, state.Response.AccessRevision, certificate);
        }
        finally
        {
            if (state is not null) CryptographicOperations.ZeroMemory(state.PrivateKeyPkcs8);
            _operationGate.Release();
        }
    }

    public async ValueTask<bool> InspectExactDeviceRevocationAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CreateRevocationTimeout(cancellationToken);
            try
            {
                using var request = CreateRequest(
                    HttpMethod.Get,
                    ExactRevocationEndpoint(RevocationProbeDeviceId, RevocationProbeRequestId));
                using var invoker = _httpFactory.Create(clientCertificate: null);
                using var response = await invoker.SendAsync(request, timeout.Token).ConfigureAwait(false);
                var payload = await ReadBoundedPayloadAsync(response.Content, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.Unauthorized)
                {
                    return false;
                }

                try
                {
                    var failure = JsonSerializer.Deserialize<DeviceAccessFailure>(payload, JsonOptions);
                    return string.Equals(failure?.Code, "device_unauthorized", StringComparison.Ordinal);
                }
                catch (JsonException)
                {
                    return false;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return false;
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async ValueTask RevokeAndConfirmExactAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 256)
        {
            throw new ArgumentException("A bounded device identity is required.", nameof(deviceId));
        }

        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        StoredEnrollmentState? state = null;
        try
        {
            state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new ConnectorEnrollmentStateException("Device enrollment is required for exact revocation.");
            ValidateBinding(state);
            if (state.Status != StoredEnrollmentStatus.Completed || state.Response is null)
            {
                throw new ConnectorEnrollmentStateException("Device enrollment has not completed.");
            }

            var receipt = state.Response;
            if (!string.Equals(receipt.DeviceId, deviceId, StringComparison.Ordinal) ||
                receipt.RequestId == Guid.Empty || receipt.AccessRevision < 0)
            {
                throw new ConnectorEnrollmentStateException(
                    "The requested revocation does not match the durable enrollment receipt.");
            }

            using var key = EnrollmentCryptography.LoadAndValidateKey(state);
            using var certificate = EnrollmentCryptography.CreateIssuedTlsCertificate(state, key, _options);
            using var invoker = _httpFactory.Create(certificate);
            using var timeout = CreateRevocationTimeout(cancellationToken);
            var endpoint = ExactRevocationEndpoint(receipt.DeviceId, receipt.RequestId);
            var minimumRevision = receipt.AccessRevision;

            try
            {
                var postStatus = await SendSelfRevocationAsync(
                    invoker, HttpMethod.Post, endpoint, timeout.Token).ConfigureAwait(false);
                if (postStatus is null)
                {
                    return;
                }

                minimumRevision = ValidatePendingSelfRevocation(postStatus, receipt, minimumRevision);
                for (var attempt = 0; attempt < MaximumRevocationStatusPolls; attempt++)
                {
                    await Task.Delay(RevocationPollInterval, timeout.Token).ConfigureAwait(false);
                    var status = await SendSelfRevocationAsync(
                        invoker, HttpMethod.Get, endpoint, timeout.Token).ConfigureAwait(false);
                    if (status is null)
                    {
                        return;
                    }

                    minimumRevision = ValidatePendingSelfRevocation(status, receipt, minimumRevision);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ConnectorEnrollmentProtocolException(
                    "Exact device revocation timed out before the server confirmed completion.",
                    (int)HttpStatusCode.RequestTimeout,
                    "self_revoke_timeout");
            }

            throw new ConnectorEnrollmentProtocolException(
                "Exact device revocation remained pending beyond the bounded confirmation window.",
                (int)HttpStatusCode.RequestTimeout,
                "self_revoke_pending");
        }
        finally
        {
            if (state is not null)
            {
                CryptographicOperations.ZeroMemory(state.PrivateKeyPkcs8);
            }

            _operationGate.Release();
        }
    }

    private async ValueTask<T> GetIssuedDeviceResponseAsync<T>(string path, Action<T, DeviceEnrollmentResponse> validate, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        StoredEnrollmentState? state = null;
        try
        {
            state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new ConnectorEnrollmentStateException("Device enrollment is required.");
            ValidateBinding(state);
            if (state.Status != StoredEnrollmentStatus.Completed || state.Response is null)
                throw new ConnectorEnrollmentStateException("Device enrollment has not completed.");
            using var key = EnrollmentCryptography.LoadAndValidateKey(state);
            using var certificate = EnrollmentCryptography.CreateIssuedTlsCertificate(state, key, _options);
            var value = await SendForJsonResponseAsync<T>(HttpMethod.Get, Endpoint(path), null, certificate, cancellationToken).ConfigureAwait(false);
            validate(value, state.Response);
            return value;
        }
        finally
        {
            if (state is not null) CryptographicOperations.ZeroMemory(state.PrivateKeyPkcs8);
            _operationGate.Release();
        }
    }

    private async ValueTask<DeviceSelfRevocationStatus?> SendSelfRevocationAsync(
        HttpMessageInvoker invoker,
        HttpMethod method,
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, endpoint);
        using var response = await invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        var payload = await ReadBoundedPayloadAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Accepted)
        {
            DeviceAccessFailure? failure = null;
            try { failure = JsonSerializer.Deserialize<DeviceAccessFailure>(payload, JsonOptions); }
            catch (JsonException) { }
            throw new ConnectorEnrollmentProtocolException(
                failure?.Message ?? $"Exact device revocation failed with status {(int)response.StatusCode}.",
                (int)response.StatusCode,
                failure?.Code);
        }

        try
        {
            return JsonSerializer.Deserialize<DeviceSelfRevocationStatus>(payload, JsonOptions)
                ?? throw new ConnectorEnrollmentProtocolException("The exact revocation status is empty.");
        }
        catch (JsonException exception)
        {
            throw new ConnectorEnrollmentProtocolException(
                $"The exact revocation status is invalid JSON: {exception.Message}");
        }
    }

    private static long ValidatePendingSelfRevocation(
        DeviceSelfRevocationStatus status,
        DeviceEnrollmentResponse receipt,
        long minimumRevision)
    {
        if (status.SchemaVersion != DeviceAccessProtocol.Version ||
            !string.Equals(status.DeviceId, receipt.DeviceId, StringComparison.Ordinal) ||
            status.EnrollmentRequestId != receipt.RequestId ||
            !string.Equals(status.State, "pending", StringComparison.Ordinal) ||
            status.Completed || status.DesiredRevision < minimumRevision)
        {
            throw new ConnectorEnrollmentProtocolException(
                "The exact revocation status does not match the durable enrollment receipt or revision fence.");
        }

        return status.DesiredRevision;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri endpoint)
    {
        var request = new HttpRequestMessage(method, endpoint)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static CancellationTokenSource CreateRevocationTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RevocationOperationTimeout);
        return timeout;
    }

    private ValueTask<DeviceEnrollmentResponse> SendForEnrollmentResponseAsync(
        HttpMethod method, Uri endpoint, object? body, X509Certificate2? clientCertificate, CancellationToken cancellationToken)
        => SendForJsonResponseAsync<DeviceEnrollmentResponse>(method, endpoint, body, clientCertificate, cancellationToken);

    private async ValueTask<T> SendForJsonResponseAsync<T>(
        HttpMethod method,
        Uri endpoint,
        object? body,
        X509Certificate2? clientCertificate,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, endpoint);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
        }

        using var invoker = _httpFactory.Create(clientCertificate);
        using var response = await invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = await ReadBoundedPayloadAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            DeviceAccessFailure? failure = null;
            try { failure = JsonSerializer.Deserialize<DeviceAccessFailure>(payload, JsonOptions); }
            catch (JsonException) { }
            throw new ConnectorEnrollmentProtocolException(
                failure?.Message ?? $"Connector enrollment HTTP request failed with status {(int)response.StatusCode}.",
                (int)response.StatusCode,
                failure?.Code);
        }

        try
        {
            return JsonSerializer.Deserialize<T>(payload, JsonOptions)
                ?? throw new ConnectorEnrollmentProtocolException("The connector enrollment response is empty.");
        }
        catch (JsonException exception)
        {
            throw new ConnectorEnrollmentProtocolException($"The connector enrollment response is invalid JSON: {exception.Message}");
        }
    }

    private static async ValueTask<byte[]> ReadBoundedPayloadAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new ConnectorEnrollmentProtocolException("The connector enrollment response is too large.");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > MaximumResponseBytes)
            {
                throw new ConnectorEnrollmentProtocolException("The connector enrollment response is too large.");
            }

            output.Write(buffer, 0, read);
        }
    }

    private void ValidateBinding(StoredEnrollmentState state)
    {
        byte[] storedPin;
        try
        {
            storedPin = Convert.FromHexString(state.IssuerPinSha256);
        }
        catch (FormatException exception)
        {
            throw new ConnectorEnrollmentStateException("The stored issuer pin is invalid.", exception);
        }

        if (!string.Equals(state.ServiceBaseUri, _options.ServiceBaseUri.AbsoluteUri, StringComparison.Ordinal) ||
            !CryptographicOperations.FixedTimeEquals(storedPin, _options.IssuerPin))
        {
            throw new ConnectorEnrollmentStateException(
                "The pending enrollment belongs to another service or configured issuer. Refusing to create a second device.");
        }
    }

    private static bool EnrollmentIsExplicitlyAbsent(ConnectorEnrollmentProtocolException exception) =>
        IssuedCredentialIsExplicitlyRejected(exception) ||
        (exception.StatusCode == (int)HttpStatusCode.NotFound &&
         exception.ErrorCode == "enrollment_not_available");

    private static bool IssuedCredentialIsExplicitlyRejected(ConnectorEnrollmentProtocolException exception) =>
        exception.StatusCode == (int)HttpStatusCode.Unauthorized &&
        exception.ErrorCode == "device_unauthorized";

    private static void ValidateEnrollmentToken(string enrollmentToken)
    {
        if (string.IsNullOrWhiteSpace(enrollmentToken) || enrollmentToken.Length > 4096)
        {
            throw new ArgumentException("An enrollment token is required.", nameof(enrollmentToken));
        }
    }

    private Uri Endpoint(string relativePath) => new(
        _options.ServiceBaseUri,
        $"api/platform/connector/access/v1/{relativePath}");

    private Uri ExactRevocationEndpoint(string deviceId, Guid requestId) => Endpoint(
        $"devices/{Uri.EscapeDataString(deviceId)}/enrollments/{requestId:D}/self-revoke");

    private sealed record DeviceSelfRevocationStatus(
        int SchemaVersion,
        string DeviceId,
        Guid EnrollmentRequestId,
        string State,
        long DesiredRevision,
        bool Completed);
}

internal interface IEnrollmentHttpInvokerFactory
{
    HttpMessageInvoker Create(X509Certificate2? clientCertificate);
}

internal sealed class NativeEnrollmentHttpInvokerFactory : IEnrollmentHttpInvokerFactory
{
    public HttpMessageInvoker Create(X509Certificate2? clientCertificate)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            CheckCertificateRevocationList = true,
            ClientCertificateOptions = ClientCertificateOption.Manual,
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        };
        if (clientCertificate is not null)
        {
            handler.ClientCertificates.Add(clientCertificate);
        }

        // No custom validation callback: Windows/.NET perform normal hostname,
        // validity, chain and trust-store validation for the HTTPS server.
        return new HttpMessageInvoker(handler, disposeHandler: true);
    }
}
