using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Connector.Access.Contracts;

namespace Connector.Access.Client.Tests;

public sealed class HttpConnectorEnrollmentClientTests
{
    [Fact]
    public async Task LostResponseLeavesTokenFreePendingRecordBeforeFirstPost()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        const string token = "one-time-token-that-must-not-reach-disk";
        DeviceEnrollmentRequest? captured = null;
        var transport = new DelegateHttpInvokerFactory(async (certificate, request, cancellationToken) =>
        {
            Assert.Null(certificate);
            Assert.True(File.Exists(store.StateFilePath));
            Assert.DoesNotContain(token, await File.ReadAllTextAsync(store.StateFilePath, cancellationToken), StringComparison.Ordinal);
            captured = await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken: cancellationToken);
            throw new HttpRequestException("simulated lost response");
        });
        var client = CreateClient(store, authority.Pin, transport);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.EnrollAsync(token, "Test workstation").AsTask());

        Assert.NotNull(captured);
        var pending = await store.LoadAsync(CancellationToken.None);
        Assert.NotNull(pending);
        Assert.Equal(captured!.RequestId, pending!.RequestId);
        Assert.Equal(captured.CertificateSigningRequestPem, pending.CertificateSigningRequestPem);
        Assert.Equal(StoredEnrollmentStatus.Pending, pending.Status);
        CryptographicOperations.ZeroMemory(pending.PrivateKeyPkcs8);
    }

    [Fact]
    public async Task RestartResumesSameRequestAndKeyWithoutToken()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        DeviceEnrollmentRequest? enrollment = null;
        var firstTransport = new DelegateHttpInvokerFactory(async (_, request, cancellationToken) =>
        {
            enrollment = await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken: cancellationToken);
            throw new HttpRequestException("response lost after server commit");
        });
        var firstClient = CreateClient(store, authority.Pin, firstTransport);
        await Assert.ThrowsAsync<HttpRequestException>(() => firstClient.EnrollAsync("first-and-only-token", "Device A").AsTask());
        Assert.NotNull(enrollment);

        var resumeTransport = new DelegateHttpInvokerFactory((certificate, request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith($"/enrollments/{enrollment!.RequestId:D}/resume", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            Assert.Null(request.Content);
            Assert.NotNull(certificate);
            Assert.True(certificate!.HasPrivateKey);
            Assert.Equal(certificate.SubjectName.RawData, certificate.IssuerName.RawData);
            Assert.Equal(
                TestCertificateAuthority.ReadCsrSpki(enrollment.CertificateSigningRequestPem),
                certificate.PublicKey.ExportSubjectPublicKeyInfo());
            return Task.FromResult(TestCertificateAuthority.JsonResponse(authority.Issue(enrollment)));
        });
        var restartedClient = CreateClient(store, authority.Pin, resumeTransport);

        var response = await restartedClient.ResumeAsync();

        Assert.Equal(enrollment!.RequestId, response.RequestId);
        var completed = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(StoredEnrollmentStatus.Completed, completed!.Status);
        Assert.Equal(response, completed.Response);
        CryptographicOperations.ZeroMemory(completed.PrivateKeyPkcs8);
    }

    [Fact]
    public async Task ExistingPendingStateMakesEnrollResumeInsteadOfCreatingSecondDevice()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        DeviceEnrollmentRequest? enrollment = null;
        var firstTransport = new DelegateHttpInvokerFactory(async (_, request, cancellationToken) =>
        {
            enrollment = await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken: cancellationToken);
            throw new HttpRequestException("lost");
        });
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateClient(store, authority.Pin, firstTransport).EnrollAsync("used-token", "Device A").AsTask());

        var retryTransport = new DelegateHttpInvokerFactory((certificate, request, _) =>
        {
            Assert.NotNull(certificate);
            Assert.EndsWith("/resume", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            return Task.FromResult(TestCertificateAuthority.JsonResponse(authority.Issue(enrollment!)));
        });

        var response = await CreateClient(store, authority.Pin, retryTransport)
            .EnrollAsync("a-different-token-must-not-be-used", "Ignored name");

        Assert.Equal(enrollment!.RequestId, response.RequestId);
    }

    [Fact]
    public async Task CorrectedTokenRetriesOriginalPendingRequestAfterExplicitUnregisteredResponse()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        DeviceEnrollmentRequest? rejected = null;
        var rejectingTransport = new DelegateHttpInvokerFactory(async (_, request, cancellationToken) =>
        {
            rejected = await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken: cancellationToken);
            return TestCertificateAuthority.Failure(HttpStatusCode.Forbidden, "enrollment_rejected");
        });
        await Assert.ThrowsAsync<ConnectorEnrollmentProtocolException>(() =>
            CreateClient(store, authority.Pin, rejectingTransport).EnrollAsync("wrong-token", "Device A").AsTask());
        Assert.NotNull(rejected);

        var call = 0;
        var retryTransport = new DelegateHttpInvokerFactory(async (certificate, request, cancellationToken) =>
        {
            call++;
            if (call == 1)
            {
                Assert.NotNull(certificate);
                Assert.EndsWith("/resume", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
                return TestCertificateAuthority.Failure(HttpStatusCode.Unauthorized, "device_unauthorized");
            }

            Assert.Null(certificate);
            Assert.Equal("/api/platform/connector/access/v1/enroll", request.RequestUri!.AbsolutePath);
            var retried = await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken: cancellationToken);
            Assert.Equal("corrected-token", retried!.EnrollmentToken);
            Assert.Equal(rejected!.RequestId, retried.RequestId);
            Assert.Equal(rejected.CertificateSigningRequestPem, retried.CertificateSigningRequestPem);
            return TestCertificateAuthority.JsonResponse(authority.Issue(retried));
        });

        var response = await CreateClient(store, authority.Pin, retryTransport)
            .EnrollAsync("corrected-token", "Ignored name");

        Assert.Equal(2, call);
        Assert.Equal(rejected!.RequestId, response.RequestId);
    }

    [Fact]
    public async Task FirstPostNeverReachedRetriesSamePendingRequestOnlyAfterExplicitNotFound()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        var offlineTransport = new DelegateHttpInvokerFactory((_, _, _) =>
            throw new HttpRequestException("offline before server received request"));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateClient(store, authority.Pin, offlineTransport).EnrollAsync("unused-token", "Device A").AsTask());
        var pending = await store.LoadAsync(CancellationToken.None);
        Assert.NotNull(pending);

        var call = 0;
        var retryTransport = new DelegateHttpInvokerFactory(async (certificate, request, cancellationToken) =>
        {
            call++;
            if (call == 1)
            {
                Assert.NotNull(certificate);
                return TestCertificateAuthority.Failure(HttpStatusCode.NotFound, "enrollment_not_available");
            }

            Assert.Null(certificate);
            var retried = await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken: cancellationToken);
            Assert.Equal(pending!.RequestId, retried!.RequestId);
            Assert.Equal(pending.CertificateSigningRequestPem, retried.CertificateSigningRequestPem);
            Assert.Equal("still-unused-token", retried.EnrollmentToken);
            return TestCertificateAuthority.JsonResponse(authority.Issue(retried));
        });

        var response = await CreateClient(store, authority.Pin, retryTransport)
            .EnrollAsync("still-unused-token", "Ignored name");

        Assert.Equal(2, call);
        Assert.Equal(pending!.RequestId, response.RequestId);
        CryptographicOperations.ZeroMemory(pending.PrivateKeyPkcs8);
    }

    [Fact]
    public async Task ResumeNetworkFailureNeverFallsBackToTokenPost()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        var initialOffline = new DelegateHttpInvokerFactory((_, _, _) => throw new HttpRequestException("offline"));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateClient(store, authority.Pin, initialOffline).EnrollAsync("unused-token", "Device A").AsTask());

        var retryOffline = new DelegateHttpInvokerFactory((certificate, request, _) =>
        {
            Assert.NotNull(certificate);
            Assert.EndsWith("/resume", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            throw new HttpRequestException("resume transport failed");
        });

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateClient(store, authority.Pin, retryOffline).EnrollAsync("must-not-be-posted", "Ignored").AsTask());
        Assert.Equal(1, retryOffline.CreateCount);
    }

    [Fact]
    public async Task CompletedStateUsesIssuedCertificateForReceiptGet()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        DeviceEnrollmentRequest? enrollment = null;
        var enrollTransport = new DelegateHttpInvokerFactory(async (_, request, cancellationToken) =>
        {
            enrollment = await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken: cancellationToken);
            return TestCertificateAuthority.JsonResponse(authority.Issue(enrollment!));
        });
        var enrolled = await CreateClient(store, authority.Pin, enrollTransport).EnrollAsync("token", "Device A");

        var receiptTransport = new DelegateHttpInvokerFactory((certificate, request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.NotNull(certificate);
            Assert.True(certificate!.HasPrivateKey);
            Assert.Equal(enrolled.DeviceId, ExtractDeviceId(certificate.Subject));
            return Task.FromResult(TestCertificateAuthority.JsonResponse(authority.Issue(enrollment!)));
        });
        var receipt = await CreateClient(store, authority.Pin, receiptTransport).GetReceiptAsync();

        Assert.Equal(enrolled.RequestId, receipt!.RequestId);
    }

    [Fact]
    public async Task CompletedStateAcceptsNewTokenOnlyAfterTypedIssuedCredentialRejection()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        DeviceEnrollmentRequest? original = null;
        var firstTransport = new DelegateHttpInvokerFactory(async (_, request, cancellationToken) =>
        {
            original = await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken: cancellationToken);
            return TestCertificateAuthority.JsonResponse(authority.Issue(original!));
        });
        await CreateClient(store, authority.Pin, firstTransport).EnrollAsync("first-token", "Device A");

        DeviceEnrollmentRequest? replacement = null;
        var call = 0;
        var replacementTransport = new DelegateHttpInvokerFactory(async (certificate, request, cancellationToken) =>
        {
            call++;
            if (call == 1)
            {
                Assert.NotNull(certificate);
                Assert.Equal(HttpMethod.Get, request.Method);
                return TestCertificateAuthority.Failure(HttpStatusCode.Unauthorized, "device_unauthorized");
            }

            Assert.Null(certificate);
            Assert.Equal(HttpMethod.Post, request.Method);
            replacement = await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken: cancellationToken);
            Assert.Equal("replacement-token", replacement!.EnrollmentToken);
            Assert.Equal("Replacement device", replacement.DeviceDisplayName);
            Assert.NotEqual(original!.RequestId, replacement.RequestId);
            Assert.NotEqual(original.CertificateSigningRequestPem, replacement.CertificateSigningRequestPem);
            return TestCertificateAuthority.JsonResponse(authority.Issue(replacement));
        });

        var response = await CreateClient(store, authority.Pin, replacementTransport)
            .EnrollAsync("replacement-token", "Replacement device");

        Assert.Equal(2, call);
        Assert.Equal(replacement!.RequestId, response.RequestId);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("server")]
    [InlineData("missing_receipt")]
    [InlineData("untyped_unauthorized")]
    public async Task CompletedStateUntrustedFailurePreservesOldStateAndNeverPostsToken(string failureKind)
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        DeviceEnrollmentRequest? original = null;
        var firstTransport = new DelegateHttpInvokerFactory(async (_, request, cancellationToken) =>
        {
            original = await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken: cancellationToken);
            return TestCertificateAuthority.JsonResponse(authority.Issue(original!));
        });
        await CreateClient(store, authority.Pin, firstTransport).EnrollAsync("first-token", "Device A");
        var durableStateBefore = await File.ReadAllBytesAsync(store.StateFilePath);

        var failingTransport = new DelegateHttpInvokerFactory((certificate, request, _) =>
        {
            Assert.NotNull(certificate);
            Assert.Equal(HttpMethod.Get, request.Method);
            if (failureKind == "server")
                return Task.FromResult(TestCertificateAuthority.Failure(HttpStatusCode.InternalServerError, "server_error"));
            if (failureKind == "missing_receipt")
                return Task.FromResult(TestCertificateAuthority.Failure(HttpStatusCode.NotFound, "enrollment_not_available"));
            if (failureKind == "untyped_unauthorized")
                return Task.FromResult(TestCertificateAuthority.Failure(HttpStatusCode.Unauthorized, "authentication_failed"));
            throw new HttpRequestException("issued credential probe timed out", new TimeoutException());
        });

        var error = await Record.ExceptionAsync(() => CreateClient(store, authority.Pin, failingTransport)
            .EnrollAsync("must-not-be-posted", "Replacement device").AsTask());

        if (failureKind == "network")
            Assert.IsType<HttpRequestException>(error);
        else
            Assert.IsType<ConnectorEnrollmentProtocolException>(error);
        Assert.Equal(1, failingTransport.CreateCount);
        Assert.Equal(durableStateBefore, await File.ReadAllBytesAsync(store.StateFilePath));
    }

    [Fact]
    public async Task ReplacementPostTimeoutLeavesFreshPendingRequestForSameKeyRetry()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        DeviceEnrollmentRequest? original = null;
        var firstTransport = new DelegateHttpInvokerFactory(async (_, request, cancellationToken) =>
        {
            original = await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken: cancellationToken);
            return TestCertificateAuthority.JsonResponse(authority.Issue(original!));
        });
        await CreateClient(store, authority.Pin, firstTransport).EnrollAsync("first-token", "Device A");

        DeviceEnrollmentRequest? timedOutRequest = null;
        var call = 0;
        var lostReplacement = new DelegateHttpInvokerFactory(async (certificate, request, cancellationToken) =>
        {
            call++;
            if (call == 1)
            {
                Assert.NotNull(certificate);
                return TestCertificateAuthority.Failure(HttpStatusCode.Unauthorized, "device_unauthorized");
            }

            Assert.Null(certificate);
            timedOutRequest = await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken: cancellationToken);
            var durablePending = await store.LoadAsync(cancellationToken);
            Assert.NotNull(durablePending);
            Assert.Equal(StoredEnrollmentStatus.Pending, durablePending!.Status);
            Assert.Equal(timedOutRequest!.RequestId, durablePending.RequestId);
            Assert.Equal(timedOutRequest.CertificateSigningRequestPem, durablePending.CertificateSigningRequestPem);
            CryptographicOperations.ZeroMemory(durablePending.PrivateKeyPkcs8);
            throw new HttpRequestException("replacement response timed out");
        });
        await Assert.ThrowsAsync<HttpRequestException>(() => CreateClient(store, authority.Pin, lostReplacement)
            .EnrollAsync("replacement-token", "Replacement device").AsTask());

        var retryCall = 0;
        var retryTransport = new DelegateHttpInvokerFactory(async (certificate, request, cancellationToken) =>
        {
            retryCall++;
            if (retryCall == 1)
            {
                Assert.NotNull(certificate);
                Assert.EndsWith("/resume", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
                return TestCertificateAuthority.Failure(HttpStatusCode.Unauthorized, "device_unauthorized");
            }

            Assert.Null(certificate);
            var retried = await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken: cancellationToken);
            Assert.Equal(timedOutRequest!.RequestId, retried!.RequestId);
            Assert.Equal(timedOutRequest.CertificateSigningRequestPem, retried.CertificateSigningRequestPem);
            Assert.Equal("retry-token", retried.EnrollmentToken);
            return TestCertificateAuthority.JsonResponse(authority.Issue(retried));
        });

        var response = await CreateClient(store, authority.Pin, retryTransport)
            .EnrollAsync("retry-token", "Ignored device name");

        Assert.Equal(2, retryCall);
        Assert.Equal(timedOutRequest!.RequestId, response.RequestId);
    }

    [Fact]
    public async Task ForeignBindingAndCorruptStateFailBeforeHttp()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        var losingTransport = new DelegateHttpInvokerFactory((_, _, _) => throw new HttpRequestException("lost"));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateClient(store, authority.Pin, losingTransport).EnrollAsync("token", "Device A").AsTask());

        var noHttp = new DelegateHttpInvokerFactory((_, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var foreignOptions = WithBase(Options(authority.Pin), new Uri("https://foreign.example.test/"));
        var foreignClient = new HttpConnectorEnrollmentClient(foreignOptions, store, noHttp);
        await Assert.ThrowsAsync<ConnectorEnrollmentStateException>(() => foreignClient.ResumeAsync().AsTask());
        Assert.Equal(0, noHttp.CreateCount);

        await File.WriteAllTextAsync(store.StateFilePath, "{ definitely-not-json");
        await Assert.ThrowsAsync<ConnectorEnrollmentStateException>(() => foreignClient.ResumeAsync().AsTask());
        Assert.Equal(0, noHttp.CreateCount);
    }

    [Fact]
    public async Task CsrKeyMismatchAndForeignIssuerResponseRemainPending()
    {
        using var directory = new TemporaryDirectory();
        using var configuredAuthority = new TestCertificateAuthority();
        using var foreignAuthority = new TestCertificateAuthority();
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        DeviceEnrollmentRequest? enrollment = null;
        var foreignIssuerTransport = new DelegateHttpInvokerFactory(async (_, request, cancellationToken) =>
        {
            enrollment = await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken: cancellationToken);
            return TestCertificateAuthority.JsonResponse(foreignAuthority.Issue(enrollment!));
        });
        await Assert.ThrowsAsync<ConnectorEnrollmentProtocolException>(() =>
            CreateClient(store, configuredAuthority.Pin, foreignIssuerTransport).EnrollAsync("token", "Device A").AsTask());
        var pending = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(StoredEnrollmentStatus.Pending, pending!.Status);
        CryptographicOperations.ZeroMemory(pending.PrivateKeyPkcs8);

        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var otherRequest = new CertificateRequest("CN=foreign", otherKey, HashAlgorithmName.SHA256);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(store.StateFilePath))!.AsObject();
        json["certificateSigningRequestPem"] = otherRequest.CreateSigningRequestPem();
        await File.WriteAllTextAsync(store.StateFilePath, json.ToJsonString());
        var noHttp = new DelegateHttpInvokerFactory((_, _, _) => throw new InvalidOperationException("HTTP must not run"));

        await Assert.ThrowsAsync<ConnectorEnrollmentStateException>(() =>
            CreateClient(store, configuredAuthority.Pin, noHttp).ResumeAsync().AsTask());
        Assert.Equal(0, noHttp.CreateCount);
    }

    [Fact]
    public void HttpServiceUriIsRejectedBeforeStoreOrTransportUse()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        var transport = new DelegateHttpInvokerFactory((_, _, _) => throw new InvalidOperationException());
        var options = WithBase(Options(authority.Pin), new Uri("http://localhost:5000/"));

        Assert.Throws<ArgumentException>(() => new HttpConnectorEnrollmentClient(options, store, transport));
        Assert.False(File.Exists(store.StateFilePath));
        Assert.Equal(0, transport.CreateCount);
    }

    [Fact]
    public async Task IssuedCertificateReadsAppliedProfileAndTokenFreeVpnBootstrap()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        DeviceEnrollmentResponse? receipt = null;
        var transport = new DelegateHttpInvokerFactory(async (certificate, request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Null(certificate);
                receipt = authority.Issue((await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken: cancellationToken))!);
                return TestCertificateAuthority.JsonResponse(receipt);
            }
            Assert.NotNull(certificate);
            Assert.Equal(receipt!.DeviceId, ExtractDeviceId(certificate!.Subject));
            Assert.Null(request.Content);
            if (request.RequestUri!.AbsolutePath.EndsWith("vpn/bootstrap", StringComparison.Ordinal))
                return TestCertificateAuthority.JsonResponse(new ConnectorVpnBootstrap("one-off-vpn-key", "https://vpn.example.test", 17, DateTimeOffset.UtcNow.AddMinutes(15)));
            if (request.RequestUri!.AbsolutePath.EndsWith("vpn/state", StringComparison.Ordinal))
                return TestCertificateAuthority.JsonResponse(new ConnectorVpnTransportState(receipt.DeviceId, 17, "ready", "peer-1", "https://vpn.example.test", ["100.90.1.2"], DateTimeOffset.UtcNow));
            return TestCertificateAuthority.JsonResponse(new DeviceAccessProfile(1, receipt.DeviceId, "user-1", "company-1", 17, 17, DateTimeOffset.UtcNow.AddMinutes(5), [], []));
        });
        var client = CreateClient(store, authority.Pin, transport);
        await client.EnrollAsync("enrollment-fixture", "Test device");
        Assert.Equal("company-1", (await client.GetAccessProfileAsync()).CompanyId);
        Assert.Equal("one-off-vpn-key", (await client.GetVpnBootstrapAsync()).SetupKey);
        Assert.Equal("peer-1", (await client.GetVpnStateAsync()).PeerId);
        Assert.DoesNotContain("one-off-vpn-key", await File.ReadAllTextAsync(store.StateFilePath));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("unapplied")]
    [InlineData("expired")]
    public async Task ProfileForForeignOrUnappliedOrExpiredAccessIsRejected(string invalid)
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        DeviceEnrollmentResponse? receipt = null;
        var transport = new DelegateHttpInvokerFactory(async (certificate, request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                receipt = authority.Issue((await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken: cancellationToken))!);
                return TestCertificateAuthority.JsonResponse(receipt);
            }
            Assert.NotNull(certificate);
            return TestCertificateAuthority.JsonResponse(new DeviceAccessProfile(1,
                invalid == "foreign" ? "other-device" : receipt!.DeviceId, "user-1", "company-1", 17,
                invalid == "unapplied" ? 16 : 17, DateTimeOffset.UtcNow.AddMinutes(invalid == "expired" ? -1 : 5), [], []));
        });
        var client = CreateClient(store, authority.Pin, transport);
        await client.EnrollAsync("enrollment-fixture", "Test device");
        await Assert.ThrowsAsync<ConnectorEnrollmentProtocolException>(() => client.GetAccessProfileAsync().AsTask());
    }

    [Fact]
    public async Task ProtectedDeviceReadsWithoutCompletedEnrollmentDoNotCreateTransport()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var transport = new DelegateHttpInvokerFactory((_, _, _) => throw new InvalidOperationException("Transport must not run."));
        var client = CreateClient(new FileConnectorEnrollmentStateStore(directory.Path), authority.Pin, transport);
        await Assert.ThrowsAsync<ConnectorEnrollmentStateException>(() => client.GetAccessProfileAsync().AsTask());
        await Assert.ThrowsAsync<ConnectorEnrollmentStateException>(() => client.GetVpnBootstrapAsync().AsTask());
        await Assert.ThrowsAsync<ConnectorEnrollmentStateException>(() => client.GetVpnStateAsync().AsTask());
        Assert.Equal(0, transport.CreateCount);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("stale")]
    [InlineData("status")]
    [InlineData("address")]
    [InlineData("peer")]
    public async Task VpnStateMustBeFreshAndBoundToIssuedDevice(string invalid)
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        DeviceEnrollmentResponse? receipt = null;
        var transport = new DelegateHttpInvokerFactory(async (certificate, request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                receipt = authority.Issue((await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken: cancellationToken))!);
                return TestCertificateAuthority.JsonResponse(receipt);
            }
            Assert.NotNull(certificate);
            return TestCertificateAuthority.JsonResponse(new ConnectorVpnTransportState(
                invalid == "foreign" ? "other-device" : receipt!.DeviceId, 17,
                invalid == "status" ? "unrecognized" : "ready", invalid == "peer" ? null : "peer-1",
                "https://vpn.example.test", [invalid == "address" ? "127.0.0.1" : "100.90.1.2"],
                DateTimeOffset.UtcNow.AddMinutes(invalid == "stale" ? -10 : 0)));
        });
        var client = CreateClient(store, authority.Pin, transport);
        await client.EnrollAsync("enrollment-fixture", "Test device");
        await Assert.ThrowsAsync<ConnectorEnrollmentProtocolException>(() => client.GetVpnStateAsync().AsTask());
    }

    [Fact]
    public async Task IssuedCertificateLeaseOwnsValidatedPrivateKeyWithoutAnotherHttpRequest()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        var transport = new DelegateHttpInvokerFactory(async (_, request, cancellationToken) =>
            TestCertificateAuthority.JsonResponse(authority.Issue(
                (await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken: cancellationToken))!)));
        var client = CreateClient(store, authority.Pin, transport);
        var receipt = await client.EnrollAsync("lease-fixture", "Test device");
        var requests = transport.CreateCount;
        var lease = await client.AcquireIssuedCertificateAsync();
        using (lease)
        {
            Assert.Equal(receipt.DeviceId, lease.DeviceId);
            Assert.Equal(receipt.AccessRevision, lease.EnrollmentRevision);
            Assert.True(lease.Certificate.HasPrivateKey);
            Assert.Equal(receipt.ClientCertificatePem, lease.Certificate.ExportCertificatePem());
            var data = new byte[] { 1, 3, 5, 7 };
            using var privateKey = lease.Certificate.GetECDsaPrivateKey();
            using var publicKey = lease.Certificate.GetECDsaPublicKey();
            Assert.NotNull(privateKey);
            Assert.True(publicKey!.VerifyData(data, privateKey!.SignData(data, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256));
        }
        Assert.Equal(requests, transport.CreateCount);
        Assert.ThrowsAny<CryptographicException>(() => lease.Certificate.GetECDsaPrivateKey());
        using var nextLease = await client.AcquireIssuedCertificateAsync();
        Assert.True(nextLease.Certificate.HasPrivateKey);
    }

    [Fact]
    public async Task LeaseRejectsPendingEnrollmentAndForeignIssuerWithoutNetworking()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        using var otherAuthority = new TestCertificateAuthority();
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        DeviceEnrollmentRequest? original = null;
        var offline = new DelegateHttpInvokerFactory(async (_, request, cancellationToken) =>
        {
            original = await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken: cancellationToken);
            throw new HttpRequestException("fixture offline");
        });
        var pendingClient = CreateClient(store, authority.Pin, offline);
        await Assert.ThrowsAsync<HttpRequestException>(() => pendingClient.EnrollAsync("lease-fixture", "Test device").AsTask());
        await Assert.ThrowsAsync<ConnectorEnrollmentStateException>(() => pendingClient.AcquireIssuedCertificateAsync().AsTask());
        Assert.Equal(1, offline.CreateCount);
        var online = new DelegateHttpInvokerFactory((_, _, _) =>
            Task.FromResult(TestCertificateAuthority.JsonResponse(authority.Issue(original!))));
        await CreateClient(store, authority.Pin, online).EnrollAsync("lease-fixture", "Test device");
        var unexpected = new DelegateHttpInvokerFactory((_, _, _) => throw new InvalidOperationException("No network expected."));
        await Assert.ThrowsAsync<ConnectorEnrollmentStateException>(() =>
            CreateClient(store, otherAuthority.Pin, unexpected).AcquireIssuedCertificateAsync().AsTask());
        Assert.Equal(0, unexpected.CreateCount);
    }

    private static HttpConnectorEnrollmentClient CreateClient(
        FileConnectorEnrollmentStateStore store,
        string issuerPin,
        IEnrollmentHttpInvokerFactory transport) =>
        new(Options(issuerPin), store, transport);

    private static ConnectorAccessClientOptions Options(string issuerPin) => new()
    {
        ServiceBaseUri = new Uri("https://connector.example.test/"),
        TrustedIssuerCertificateSha256 = issuerPin,
    };

    private static ConnectorAccessClientOptions WithBase(ConnectorAccessClientOptions options, Uri baseUri) => new()
    {
        ServiceBaseUri = baseUri,
        TrustedIssuerCertificateSha256 = options.TrustedIssuerCertificateSha256,
        KeyAlgorithm = options.KeyAlgorithm,
        BootstrapCertificateLifetime = options.BootstrapCertificateLifetime,
        ClockSkew = options.ClockSkew,
        TimeProvider = options.TimeProvider,
    };

    private static string ExtractDeviceId(string subject) =>
        subject.StartsWith("CN=connector-device-", StringComparison.Ordinal)
            ? subject["CN=connector-device-".Length..]
            : string.Empty;
}
