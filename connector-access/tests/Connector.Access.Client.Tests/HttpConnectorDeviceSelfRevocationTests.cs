using System.Net;
using System.Net.Http.Json;
using Connector.Access.Contracts;

namespace Connector.Access.Client.Tests;

public sealed class HttpConnectorDeviceSelfRevocationTests
{
    [Fact]
    public async Task Inspect_RequiresExactAnonymousRouteAndUnauthorizedCode()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var transport = new DelegateHttpInvokerFactory((certificate, request, _) =>
        {
            Assert.Null(certificate);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                "/api/platform/connector/access/v1/devices/dev_00000000000000000000000000000000/enrollments/00000000-0000-0000-0000-000000000001/self-revoke",
                request.RequestUri!.AbsolutePath);
            Assert.False(request.Headers.Contains("Cookie"));
            return Task.FromResult(TestCertificateAuthority.Failure(
                HttpStatusCode.Unauthorized,
                "device_unauthorized"));
        });
        var client = CreateClient(directory, authority, transport);

        Assert.True(await client.InspectExactDeviceRevocationAsync());
        Assert.Equal(1, transport.CreateCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "device_unauthorized")]
    [InlineData(HttpStatusCode.Unauthorized, "access_denied")]
    public async Task Inspect_RouteMissingOrAmbiguous_FailsClosed(
        HttpStatusCode statusCode,
        string errorCode)
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var transport = new DelegateHttpInvokerFactory((certificate, request, _) =>
        {
            Assert.Null(certificate);
            Assert.Contains("/self-revoke", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            return Task.FromResult(TestCertificateAuthority.Failure(statusCode, errorCode));
        });
        var client = CreateClient(directory, authority, transport);

        Assert.False(await client.InspectExactDeviceRevocationAsync());
    }

    [Fact]
    public async Task Revoke_PendingThenNoContent_UsesReceiptAndIssuedCertificate()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        DeviceEnrollmentResponse? issued = null;
        var revocationMethods = new List<HttpMethod>();
        var transport = new DelegateHttpInvokerFactory(async (certificate, request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/enroll", StringComparison.Ordinal))
            {
                var enrollment = await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken);
                issued = authority.Issue(enrollment!);
                return TestCertificateAuthority.JsonResponse(issued);
            }

            Assert.NotNull(issued);
            AssertIssuedRoute(certificate, request, issued!);
            revocationMethods.Add(request.Method);
            return request.Method == HttpMethod.Post
                ? Pending(issued!, issued!.AccessRevision + 1)
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        var client = CreateClient(store, authority, transport);
        issued = await client.EnrollAsync("one-use-secret", "test-device");

        await client.RevokeAndConfirmExactAsync(issued.DeviceId);

        Assert.Equal([HttpMethod.Post, HttpMethod.Get], revocationMethods);
        var persisted = await store.LoadAsync(default);
        Assert.NotNull(persisted?.Response);
        Assert.Equal(issued.RequestId, persisted!.Response!.RequestId);
        Assert.Equal(issued.DeviceId, persisted.Response.DeviceId);
    }

    [Fact]
    public async Task Revoke_WrongRequestedDevice_DoesNotSendRevocation()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        DeviceEnrollmentResponse? issued = null;
        var revocationCalls = 0;
        var transport = new DelegateHttpInvokerFactory(async (_, request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/enroll", StringComparison.Ordinal))
            {
                var enrollment = await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken);
                issued = authority.Issue(enrollment!);
                return TestCertificateAuthority.JsonResponse(issued);
            }

            revocationCalls++;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var client = CreateClient(directory, authority, transport);
        issued = await client.EnrollAsync("one-use-secret", "test-device");

        await Assert.ThrowsAsync<ConnectorEnrollmentStateException>(async () =>
            await client.RevokeAndConfirmExactAsync(issued.DeviceId + "-other"));
        Assert.Equal(0, revocationCalls);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Revoke_StatusForWrongDeviceOrRequest_FailsClosed(
        bool wrongDevice,
        bool wrongRequest)
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        DeviceEnrollmentResponse? issued = null;
        var transport = new DelegateHttpInvokerFactory(async (certificate, request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/enroll", StringComparison.Ordinal))
            {
                var enrollment = await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken);
                issued = authority.Issue(enrollment!);
                return TestCertificateAuthority.JsonResponse(issued);
            }

            AssertIssuedRoute(certificate, request, issued!);
            return AcceptedStatus(
                wrongDevice ? issued!.DeviceId + "-other" : issued!.DeviceId,
                wrongRequest ? Guid.NewGuid() : issued!.RequestId,
                issued!.AccessRevision + 1);
        });
        var client = CreateClient(directory, authority, transport);
        issued = await client.EnrollAsync("one-use-secret", "test-device");

        await Assert.ThrowsAsync<ConnectorEnrollmentProtocolException>(async () =>
            await client.RevokeAndConfirmExactAsync(issued.DeviceId));
    }

    [Fact]
    public async Task Revoke_RevisionDowngradeDuringPolling_FailsClosed()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        DeviceEnrollmentResponse? issued = null;
        var revocationCalls = 0;
        var transport = new DelegateHttpInvokerFactory(async (certificate, request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/enroll", StringComparison.Ordinal))
            {
                var enrollment = await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken);
                issued = authority.Issue(enrollment!);
                return TestCertificateAuthority.JsonResponse(issued);
            }

            AssertIssuedRoute(certificate, request, issued!);
            revocationCalls++;
            return Pending(issued!, revocationCalls == 1
                ? issued!.AccessRevision + 2
                : issued!.AccessRevision + 1);
        });
        var client = CreateClient(directory, authority, transport);
        issued = await client.EnrollAsync("one-use-secret", "test-device");

        await Assert.ThrowsAsync<ConnectorEnrollmentProtocolException>(async () =>
            await client.RevokeAndConfirmExactAsync(issued.DeviceId));
        Assert.Equal(2, revocationCalls);
    }

    [Fact]
    public async Task Revoke_AfterRestart_RetriesWithStoredRevokedCredential()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        DeviceEnrollmentResponse? issued = null;
        var postCalls = 0;
        var transport = new DelegateHttpInvokerFactory(async (certificate, request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/enroll", StringComparison.Ordinal))
            {
                var enrollment = await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken);
                issued = authority.Issue(enrollment!);
                return TestCertificateAuthority.JsonResponse(issued);
            }

            AssertIssuedRoute(certificate, request, issued!);
            if (request.Method == HttpMethod.Post)
            {
                postCalls++;
                return postCalls == 1
                    ? Pending(issued!, issued!.AccessRevision + 1)
                    : new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        var firstProcess = CreateClient(store, authority, transport);
        issued = await firstProcess.EnrollAsync("one-use-secret", "test-device");
        await firstProcess.RevokeAndConfirmExactAsync(issued.DeviceId);

        var restartedProcess = CreateClient(store, authority, transport);
        await restartedProcess.RevokeAndConfirmExactAsync(issued.DeviceId);

        Assert.Equal(2, postCalls);
        Assert.NotNull((await store.LoadAsync(default))?.Response);
    }

    [Fact]
    public async Task Operations_PropagateCallerCancellationWithoutSending()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var transport = new DelegateHttpInvokerFactory((_, _, _) =>
            throw new InvalidOperationException("HTTP must not be reached."));
        var client = CreateClient(directory, authority, transport);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.InspectExactDeviceRevocationAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.RevokeAndConfirmExactAsync("device-placeholder", cancellation.Token));
        Assert.Equal(0, transport.CreateCount);
    }

    private static HttpConnectorEnrollmentClient CreateClient(
        TemporaryDirectory directory,
        TestCertificateAuthority authority,
        IEnrollmentHttpInvokerFactory transport) =>
        CreateClient(new FileConnectorEnrollmentStateStore(directory.Path), authority, transport);

    private static HttpConnectorEnrollmentClient CreateClient(
        FileConnectorEnrollmentStateStore store,
        TestCertificateAuthority authority,
        IEnrollmentHttpInvokerFactory transport) =>
        new(new ConnectorAccessClientOptions
        {
            ServiceBaseUri = new Uri("https://connector.example.test/"),
            TrustedIssuerCertificateSha256 = authority.Pin,
        }, store, transport);

    private static void AssertIssuedRoute(
        System.Security.Cryptography.X509Certificates.X509Certificate2? certificate,
        HttpRequestMessage request,
        DeviceEnrollmentResponse receipt)
    {
        Assert.NotNull(certificate);
        Assert.True(certificate!.HasPrivateKey);
        Assert.Equal(
            $"/api/platform/connector/access/v1/devices/{receipt.DeviceId}/enrollments/{receipt.RequestId:D}/self-revoke",
            request.RequestUri!.AbsolutePath);
        Assert.False(request.Headers.Contains("Cookie"));
    }

    private static HttpResponseMessage Pending(DeviceEnrollmentResponse receipt, long revision) =>
        AcceptedStatus(receipt.DeviceId, receipt.RequestId, revision);

    private static HttpResponseMessage AcceptedStatus(string deviceId, Guid requestId, long revision) =>
        new(HttpStatusCode.Accepted)
        {
            Content = JsonContent.Create(new
            {
                schemaVersion = DeviceAccessProtocol.Version,
                deviceId,
                enrollmentRequestId = requestId,
                state = "pending",
                desiredRevision = revision,
                completed = false,
            }),
        };
}
