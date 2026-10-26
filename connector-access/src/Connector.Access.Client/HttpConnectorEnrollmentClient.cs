using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Connector.Access.Contracts;

namespace Connector.Access.Client;

public sealed partial class HttpConnectorEnrollmentClient : IConnectorEnrollmentClient, IConnectorDeviceAccessClient,
    IConnectorIssuedCredentialSource, IConnectorExactDeviceRevocationClient, IConnectorAccountSessionClient
{
    private static readonly TimeSpan CertificateRenewalLead = TimeSpan.FromHours(1);
    private const int MaximumResponseBytes = 128 * 1024;
    // The server confirms a self-revocation only after every access provider
    // has applied it; NetBird alone took ~37 s on a pilot PC. The window must
    // cover that, or a successful server revocation is reported as a failure.
    private const int MaximumRevocationStatusPolls = 120;
    internal static readonly TimeSpan DefaultRevocationPollInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan DefaultRevocationOperationTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan RevocationProbeTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan DefaultRejectionConfirmationDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumRejectionClockSkew = TimeSpan.FromSeconds(60);
    private const string RevocationProbeDeviceId = "dev_00000000000000000000000000000000";
    private static readonly Guid RevocationProbeRequestId = new("00000000-0000-0000-0000-000000000001");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ValidatedClientOptions _options;
    private readonly FileConnectorEnrollmentStateStore _store;
    private readonly IEnrollmentHttpInvokerFactory _httpFactory;
    private readonly TimeSpan _revocationPollInterval;
    private readonly TimeSpan _revocationOperationTimeout;
    private readonly TimeSpan _rejectionConfirmationDelay;
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
        IEnrollmentHttpInvokerFactory httpFactory,
        TimeSpan? revocationPollInterval = null,
        TimeSpan? revocationOperationTimeout = null,
        TimeSpan? rejectionConfirmationDelay = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Validate();
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
        _revocationPollInterval = revocationPollInterval ?? DefaultRevocationPollInterval;
        _revocationOperationTimeout = revocationOperationTimeout ?? DefaultRevocationOperationTimeout;
        _rejectionConfirmationDelay = rejectionConfirmationDelay ?? DefaultRejectionConfirmationDelay;
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
            try
            {
                return await ResumeStateAsync(state, cancellationToken).ConfigureAwait(false);
            }
            catch (ConnectorEnrollmentProtocolException exception) when (
                state.Status == StoredEnrollmentStatus.Pending &&
                exception.StatusCode == (int)HttpStatusCode.NotFound &&
                exception.ErrorCode == "enrollment_not_available")
            {
                // Only a pending request can be restarted with the saved CSR and a
                // fresh browser token. A missing completed receipt is not proof
                // that the issued device identity may be replaced.
                throw new ConnectorEnrollmentProtocolException(
                    "The pending enrollment is absent on the server.",
                    exception.StatusCode,
                    "pending_enrollment_absent");
            }
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

    private async ValueTask<DeviceEnrollmentResponse> ResumeStateAsync(
        StoredEnrollmentState state,
        CancellationToken cancellationToken)
    {
        using var key = EnrollmentCryptography.LoadAndValidateKey(state);
        if (state.Status == StoredEnrollmentStatus.Completed)
        {
            if (state.Response is null)
                throw new ConnectorEnrollmentStateException("Completed enrollment has no issued receipt.");
            if (state.Response.CertificateExpiresAtUtc <= _options.TimeProvider.GetUtcNow().Add(CertificateRenewalLead))
            {
                // The short-lived self-issued proof binds this renewal to the
                // durable device key. The server retains the same device and can
                // still deny it after revocation or membership removal.
                using var proof = EnrollmentCryptography.CreateBootstrapCertificate(state, key, _options);
                var renewed = await SendForEnrollmentResponseAsync(HttpMethod.Post,
                    Endpoint($"enrollments/{state.RequestId:D}/resume"), null, proof, cancellationToken).ConfigureAwait(false);
                if (renewed.DeviceId != state.Response.DeviceId ||
                    renewed.AccessRevision < state.Response.AccessRevision)
                    throw new ConnectorEnrollmentProtocolException("Certificate renewal changed the durable device identity.");
                return await ValidateAndCompleteAsync(state, renewed, key, cancellationToken).ConfigureAwait(false);
            }
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
            if (state.Response.CertificateExpiresAtUtc <= _options.TimeProvider.GetUtcNow().Add(CertificateRenewalLead))
            {
                await ResumeStateAsync(state, cancellationToken).ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(state.PrivateKeyPkcs8);
                state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new ConnectorEnrollmentStateException("Renewed enrollment was not stored.");
                ValidateBinding(state);
                if (state.Status != StoredEnrollmentStatus.Completed || state.Response is null)
                    throw new ConnectorEnrollmentStateException("Renewed enrollment is incomplete.");
            }
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
            using var timeout = CreateBoundedTimeout(RevocationProbeTimeout, cancellationToken);
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

            using var timeout = CreateBoundedTimeout(_revocationOperationTimeout, cancellationToken);
            try
            {
                if (receipt.CertificateExpiresAtUtc <= _options.TimeProvider.GetUtcNow())
                {
                    // An expired issued certificate cannot pass TLS. The key-bound proof
                    // either renews it (the enrollment is active) or shows that the
                    // server holds no enrollment for this key any more: with the issued
                    // certificate already expired, nothing else can revoke this device.
                    KeyProof expiredProof;
                    using (var expiredKey = EnrollmentCryptography.LoadAndValidateKey(state))
                        expiredProof = await ProveEnrollmentKeyAsync(state, expiredKey, timeout.Token).ConfigureAwait(false);
                    if (expiredProof == KeyProof.Rejected)
                    {
                        _confirmedRevokedRequestId = receipt.RequestId;
                        return;
                    }

                    state = await ReloadSameEnrollmentAsync(state, timeout.Token).ConfigureAwait(false);
                    receipt = state.Response!;
                }

                for (var send = 1; ; send++)
                {
                    using var key = EnrollmentCryptography.LoadAndValidateKey(state);
                    using var certificate = EnrollmentCryptography.CreateIssuedTlsCertificate(state, key, _options);
                    using var invoker = _httpFactory.Create(certificate);
                    var endpoint = ExactRevocationEndpoint(receipt.DeviceId, receipt.RequestId);
                    var minimumRevision = receipt.AccessRevision;

                    DeviceSelfRevocationStatus? postStatus;
                    try
                    {
                        postStatus = await SendSelfRevocationAsync(
                            invoker, HttpMethod.Post, endpoint, timeout.Token).ConfigureAwait(false);
                    }
                    catch (ConnectorEnrollmentProtocolException exception) when (
                        send == 1 && IssuedCredentialIsExplicitlyRejected(exception))
                    {
                        // A current certificate of an already revoked device still gets
                        // 202/204, so this rejection never proves revocation and the
                        // credential is kept. An accepted key proof refreshes the receipt:
                        // retry once with it.
                        if (await ProveEnrollmentKeyAsync(state, key, timeout.Token).ConfigureAwait(false) == KeyProof.Rejected)
                            throw;
                        state = await ReloadSameEnrollmentAsync(state, timeout.Token).ConfigureAwait(false);
                        receipt = state.Response!;
                        continue;
                    }

                    if (postStatus is null)
                    {
                        _confirmedRevokedRequestId = receipt.RequestId;
                        return;
                    }

                    minimumRevision = ValidatePendingSelfRevocation(postStatus, receipt, minimumRevision);
                    for (var attempt = 0; attempt < MaximumRevocationStatusPolls; attempt++)
                    {
                        await Task.Delay(_revocationPollInterval, _options.TimeProvider, timeout.Token).ConfigureAwait(false);
                        var status = await SendSelfRevocationAsync(
                            invoker, HttpMethod.Get, endpoint, timeout.Token).ConfigureAwait(false);
                        if (status is null)
                        {
                            _confirmedRevokedRequestId = receipt.RequestId;
                            return;
                        }

                        minimumRevision = ValidatePendingSelfRevocation(status, receipt, minimumRevision);
                    }

                    throw new ConnectorEnrollmentProtocolException(
                        "Exact device revocation remained pending beyond the bounded confirmation window.",
                        (int)HttpStatusCode.RequestTimeout,
                        "self_revoke_pending");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ConnectorEnrollmentProtocolException(
                    "Exact device revocation timed out before the server confirmed completion.",
                    (int)HttpStatusCode.RequestTimeout,
                    "self_revoke_timeout");
            }
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
            if (state.Response.CertificateExpiresAtUtc <= _options.TimeProvider.GetUtcNow().Add(CertificateRenewalLead))
            {
                await ResumeStateAsync(state, cancellationToken).ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(state.PrivateKeyPkcs8);
                state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new ConnectorEnrollmentStateException("Renewed enrollment was not stored.");
                ValidateBinding(state);
                if (state.Status != StoredEnrollmentStatus.Completed || state.Response is null)
                    throw new ConnectorEnrollmentStateException("Renewed enrollment is incomplete.");
            }
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
            throw ToFailure(response.StatusCode, payload, "Exact device revocation failed");
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

    private BoundedTimeout CreateBoundedTimeout(TimeSpan limit, CancellationToken cancellationToken) =>
        new(limit, _options.TimeProvider, cancellationToken);

    internal enum KeyProof { Accepted, Rejected }

    /// <summary>
    /// device_unauthorized for an issued certificate says only that this
    /// certificate is not current. The key-bound recovery proof is the server's
    /// statement about the enrollment itself. An accepted proof refreshes the
    /// stored receipt. A rejection counts only while the local clock agrees
    /// with the server: a fast clock makes the server reject a valid short-lived
    /// proof. Any other answer or failure propagates and changes nothing.
    /// </summary>
    private async ValueTask<KeyProof> ProveEnrollmentKeyAsync(
        StoredEnrollmentState state,
        EnrollmentCryptography.KeyMaterial key,
        CancellationToken cancellationToken)
    {
        if (state.Status != StoredEnrollmentStatus.Completed || state.Response is null)
            throw new ConnectorEnrollmentStateException("Device enrollment has not completed.");
        DeviceEnrollmentResponse current;
        using (var proof = EnrollmentCryptography.CreateBootstrapCertificate(state, key, _options))
        using (var request = CreateRequest(HttpMethod.Post, Endpoint($"enrollments/{state.RequestId:D}/resume")))
        using (var invoker = _httpFactory.Create(proof))
        using (var response = await invoker.SendAsync(request, cancellationToken).ConfigureAwait(false))
        {
            var payload = await ReadBoundedPayloadAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var failure = ToFailure(response.StatusCode, payload, "Enrollment key proof failed");
                if (!IssuedCredentialIsExplicitlyRejected(failure)) throw failure;
                RequireServerClockAgreement(response.Headers.Date);
                return KeyProof.Rejected;
            }

            current = ParseJson<DeviceEnrollmentResponse>(payload);
        }

        if (current.DeviceId != state.Response.DeviceId || current.AccessRevision < state.Response.AccessRevision)
            throw new ConnectorEnrollmentProtocolException("Certificate renewal changed the durable device identity.");
        await ValidateAndCompleteAsync(state, current, key, cancellationToken).ConfigureAwait(false);
        return KeyProof.Accepted;
    }

    private void RequireServerClockAgreement(DateTimeOffset? serverDate)
    {
        if (serverDate is null)
            throw new ConnectorEnrollmentProtocolException(
                "The enrollment rejection carries no server time and cannot be trusted.",
                (int)HttpStatusCode.Unauthorized,
                "rejection_unverified");
        var skew = _options.TimeProvider.GetUtcNow() - serverDate.Value;
        if (skew.Duration() > MaximumRejectionClockSkew)
            throw new ConnectorClockSkewException(skew);
    }

    /// <summary>Re-reads the receipt a key proof refreshed; it must stay the same device.</summary>
    private async ValueTask<StoredEnrollmentState> ReloadSameEnrollmentAsync(
        StoredEnrollmentState previous,
        CancellationToken cancellationToken)
    {
        var expected = previous.Response!;
        CryptographicOperations.ZeroMemory(previous.PrivateKeyPkcs8);
        var state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new ConnectorEnrollmentStateException("Renewed enrollment was not stored.");
        ValidateBinding(state);
        if (state.Status != StoredEnrollmentStatus.Completed || state.Response is null ||
            state.Response.RequestId != expected.RequestId ||
            !string.Equals(state.Response.DeviceId, expected.DeviceId, StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(state.PrivateKeyPkcs8);
            throw new ConnectorEnrollmentStateException("Renewed enrollment does not match the device.");
        }

        return state;
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
            throw ToFailure(response.StatusCode, payload, "Connector enrollment HTTP request failed");
        }

        return ParseJson<T>(payload);
    }

    private static ConnectorEnrollmentProtocolException ToFailure(HttpStatusCode statusCode, byte[] payload, string context)
    {
        DeviceAccessFailure? failure = null;
        try { failure = JsonSerializer.Deserialize<DeviceAccessFailure>(payload, JsonOptions); }
        catch (JsonException) { }
        return new ConnectorEnrollmentProtocolException(
            failure?.Message ?? $"{context} with status {(int)statusCode}.",
            (int)statusCode,
            failure?.Code);
    }

    private static T ParseJson<T>(byte[] payload)
    {
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

    /// <summary>A caller-linked deadline on the client's clock.</summary>
    private sealed class BoundedTimeout : IDisposable
    {
        private readonly CancellationTokenSource _deadline;
        private readonly CancellationTokenSource _linked;

        public BoundedTimeout(TimeSpan limit, TimeProvider clock, CancellationToken cancellationToken)
        {
            _deadline = new CancellationTokenSource(limit, clock);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
        }

        public CancellationToken Token => _linked.Token;

        public void Dispose()
        {
            _linked.Dispose();
            _deadline.Dispose();
        }
    }

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
