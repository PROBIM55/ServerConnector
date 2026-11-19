using System.Net;
using System.Net.Http.Json;
using Connector.Access.Contracts;

namespace Connector.Access.Client.Tests;

public sealed class HttpConnectorDeviceSelfRevocationTests
{
    [Fact]
    public async Task ConfirmedSignOutClearsDurableSessionAndNextLoginCreatesANewDeviceRequest()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var requests = new List<Guid>();
        var transport = new DelegateHttpInvokerFactory(async (_, request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/enroll", StringComparison.Ordinal))
            {
                var enrollment = await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken);
                requests.Add(enrollment!.RequestId);
                return TestCertificateAuthority.JsonResponse(authority.Issue(enrollment));
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        var client = CreateClient(store, authority, transport);
        await client.EnrollAsync("first-secret", "test-device");
        var identity = (await client.GetSignOutIdentityAsync())!;
        await Assert.ThrowsAsync<ConnectorEnrollmentStateException>(() => client.ClearRevokedSessionAsync(identity).AsTask());
        await client.RevokeAndConfirmExactAsync(identity.DeviceId!);
        await client.ClearRevokedSessionAsync(identity);
        Assert.Null(await store.LoadAsync(default));
        var restarted = CreateClient(store, authority, transport);
        await restarted.EnrollAsync("second-account-secret", "test-device");
        Assert.Equal(2, requests.Count);
        Assert.NotEqual(requests[0], requests[1]);
    }

    [Fact]
    public async Task FailedSignOutRetainsCredentialForRetry()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var transport = new DelegateHttpInvokerFactory(async (_, request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/enroll", StringComparison.Ordinal))
                return TestCertificateAuthority.JsonResponse(authority.Issue(
                    (await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken))!));
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        var client = CreateClient(store, authority, transport);
        await client.EnrollAsync("first-secret", "test-device");
        var identity = (await client.GetSignOutIdentityAsync())!;
        await Assert.ThrowsAsync<ConnectorEnrollmentProtocolException>(() => client.RevokeAndConfirmExactAsync(identity.DeviceId!).AsTask());
        await Assert.ThrowsAsync<ConnectorEnrollmentStateException>(() => client.ClearRevokedSessionAsync(identity).AsTask());
        Assert.Equal(identity.RequestId, (await store.LoadAsync(default))!.RequestId);
    }

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

    [Fact]
    public void Revoke_DefaultConfirmationWindowCoversProviderRevocation()
    {
        // Pilot 1.1.13: the server applied the NetBird revocation ~37 s after the
        // request; the former 20 x 250 ms (15 s cap) window reported that success
        // as a failure and kept the local enrollment.
        Assert.Equal(TimeSpan.FromSeconds(120), HttpConnectorEnrollmentClient.DefaultRevocationOperationTimeout);
        Assert.Equal(TimeSpan.FromSeconds(1), HttpConnectorEnrollmentClient.DefaultRevocationPollInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), HttpConnectorEnrollmentClient.DefaultRejectionConfirmationDelay);
    }

    [Fact]
    public async Task Revoke_ServerProvidersTakeTensOfSeconds_ConfirmsWithinBoundedWindow()
    {
        // Pilot 1.1.13: the server applied the NetBird revocation ~37 s after the
        // request; the former 20 x 250 ms window reported that success as a failure.
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        DeviceEnrollmentResponse? issued = null;
        var statusPolls = 0;
        var transport = new DelegateHttpInvokerFactory(async (certificate, request, cancellationToken) =>
        {
            if (IsEnroll(request)) return TestCertificateAuthority.JsonResponse(issued = await IssueAsync(authority, request, cancellationToken));
            AssertIssuedRoute(certificate, request, issued!);
            if (request.Method == HttpMethod.Post) return Pending(issued!, issued!.AccessRevision + 1);
            return ++statusPolls < 37 ? Pending(issued!, issued!.AccessRevision + 1) : new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        // 37 polls stand for the pilot's ~37 s; the window is far longer than they take.
        var client = CreateClient(store, authority, transport, TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(60));
        await client.EnrollAsync("one-use-secret", "test-device");
        var identity = (await client.GetSignOutIdentityAsync())!;

        await client.RevokeAndConfirmExactAsync(identity.DeviceId!);
        await client.ClearRevokedSessionAsync(identity);

        Assert.Equal(37, statusPolls);
        Assert.Null(await store.LoadAsync(default));
    }

    [Fact]
    public async Task Revoke_PendingBeyondWindow_TimesOutAndKeepsCredential()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        DeviceEnrollmentResponse? issued = null;
        var statusPolls = 0;
        var transport = new DelegateHttpInvokerFactory(async (certificate, request, cancellationToken) =>
        {
            if (IsEnroll(request)) return TestCertificateAuthority.JsonResponse(issued = await IssueAsync(authority, request, cancellationToken));
            AssertIssuedRoute(certificate, request, issued!);
            if (request.Method == HttpMethod.Get) statusPolls++;
            return Pending(issued!, issued!.AccessRevision + 1);
        });
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        var client = CreateClient(store, authority, transport, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(400));
        await client.EnrollAsync("one-use-secret", "test-device");
        var identity = (await client.GetSignOutIdentityAsync())!;
        var started = System.Diagnostics.Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<ConnectorEnrollmentProtocolException>(() =>
            client.RevokeAndConfirmExactAsync(identity.DeviceId!).AsTask());

        Assert.Equal((int)HttpStatusCode.RequestTimeout, error.StatusCode);
        Assert.Contains(error.ErrorCode, new[] { "self_revoke_timeout", "self_revoke_pending" });
        Assert.True(statusPolls > 0);
        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(350), "the configured window must be used");
        await Assert.ThrowsAsync<ConnectorEnrollmentStateException>(() => client.ClearRevokedSessionAsync(identity).AsTask());
        Assert.Equal(identity.RequestId, (await store.LoadAsync(default))!.RequestId);
    }

    [Fact]
    public async Task Revoke_CurrentCertificateRejectedAndKeyRejected_KeepsCredential()
    {
        // A current certificate of a revoked device gets 202/204; a rejection of a
        // current certificate is never treated as "already revoked".
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        DeviceEnrollmentResponse? issued = null;
        var calls = new List<string>();
        var transport = new DelegateHttpInvokerFactory(async (certificate, request, cancellationToken) =>
        {
            if (IsEnroll(request)) return TestCertificateAuthority.JsonResponse(issued = await IssueAsync(authority, request, cancellationToken));
            calls.Add(Route(request));
            if (IsResume(request))
            {
                Assert.Equal(certificate!.SubjectName.RawData, certificate.IssuerName.RawData);
                return Rejected(DateTimeOffset.UtcNow);
            }
            AssertIssuedRoute(certificate, request, issued!);
            return Rejected(DateTimeOffset.UtcNow);
        });
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        var client = CreateClient(store, authority, transport);
        await client.EnrollAsync("one-use-secret", "test-device");
        var identity = (await client.GetSignOutIdentityAsync())!;

        var error = await Assert.ThrowsAsync<ConnectorEnrollmentProtocolException>(() =>
            client.RevokeAndConfirmExactAsync(identity.DeviceId!).AsTask());

        Assert.Equal("device_unauthorized", error.ErrorCode);
        Assert.Equal(["POST self-revoke", "POST resume"], calls);
        await AssertCredentialKeptAsync(client, store, identity);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(null)]
    public async Task Revoke_InsideRenewalWindowWithUntrustedRejection_KeepsCredential(int? serverMinutesBehind)
    {
        // Review scenario (a): certificate within the 1 h renewal lead, PC clock fast.
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var calls = new List<string>();
        var transport = new DelegateHttpInvokerFactory(async (_, request, cancellationToken) =>
        {
            if (IsEnroll(request))
                return TestCertificateAuthority.JsonResponse(await IssueAsync(authority, request, cancellationToken, DateTimeOffset.UtcNow.AddMinutes(30)));
            calls.Add(Route(request));
            return Rejected(serverMinutesBehind is { } minutes ? DateTimeOffset.UtcNow.AddMinutes(minutes) : null);
        });
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        var client = CreateClient(store, authority, transport);
        await client.EnrollAsync("one-use-secret", "test-device");
        var identity = (await client.GetSignOutIdentityAsync())!;

        if (serverMinutesBehind is null)
        {
            var unverified = await Assert.ThrowsAsync<ConnectorEnrollmentProtocolException>(() =>
                client.RevokeAndConfirmExactAsync(identity.DeviceId!).AsTask());
            Assert.Equal("rejection_unverified", unverified.ErrorCode);
        }
        else
        {
            var skew = await Assert.ThrowsAsync<ConnectorClockSkewException>(() =>
                client.RevokeAndConfirmExactAsync(identity.DeviceId!).AsTask());
            Assert.InRange(skew.Skew.TotalMinutes, 4.9, 5.1);
        }

        Assert.Equal(["POST self-revoke", "POST resume"], calls);
        await AssertCredentialKeptAsync(client, store, identity);
    }

    [Fact]
    public async Task Revoke_ExpiredCertificateOfRevokedDevice_IsConfirmedByKeyProofAndCleared()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var clock = new ShiftedClock();
        var calls = new List<string>();
        var transport = new DelegateHttpInvokerFactory(async (_, request, cancellationToken) =>
        {
            if (IsEnroll(request))
                return TestCertificateAuthority.JsonResponse(await IssueAsync(authority, request, cancellationToken, DateTimeOffset.UtcNow.AddMinutes(30)));
            calls.Add(Route(request));
            return Rejected(clock.GetUtcNow());
        });
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        var client = CreateClient(store, authority, transport, timeProvider: clock);
        await client.EnrollAsync("one-use-secret", "test-device");
        var identity = (await client.GetSignOutIdentityAsync())!;
        clock.Shift = TimeSpan.FromHours(2); // the issued certificate is now expired on both sides

        await client.RevokeAndConfirmExactAsync(identity.DeviceId!);
        await client.ClearRevokedSessionAsync(identity);

        Assert.Equal(["POST resume"], calls);
        Assert.Null(await store.LoadAsync(default));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Revoke_RejectedCertificateWithAcceptedKey_RetriesOnceOtherwiseKeeps(HttpStatusCode keyProof)
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        DeviceEnrollmentResponse? issued = null;
        var calls = new List<string>();
        var transport = new DelegateHttpInvokerFactory(async (_, request, cancellationToken) =>
        {
            if (IsEnroll(request)) return TestCertificateAuthority.JsonResponse(issued = await IssueAsync(authority, request, cancellationToken));
            calls.Add(Route(request));
            if (IsResume(request))
                return keyProof == HttpStatusCode.OK
                    ? TestCertificateAuthority.JsonResponse(issued!)
                    : TestCertificateAuthority.Failure(keyProof, "temporarily_unavailable");
            return calls.Count == 1
                ? Rejected(DateTimeOffset.UtcNow)
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        var client = CreateClient(store, authority, transport);
        await client.EnrollAsync("one-use-secret", "test-device");
        var identity = (await client.GetSignOutIdentityAsync())!;

        if (keyProof == HttpStatusCode.OK)
        {
            await client.RevokeAndConfirmExactAsync(identity.DeviceId!);
            Assert.Equal(["POST self-revoke", "POST resume", "POST self-revoke"], calls);
            await client.ClearRevokedSessionAsync(identity);
            Assert.Null(await store.LoadAsync(default));
            return;
        }

        await Assert.ThrowsAsync<ConnectorEnrollmentProtocolException>(() =>
            client.RevokeAndConfirmExactAsync(identity.DeviceId!).AsTask());
        Assert.Equal(["POST self-revoke", "POST resume"], calls);
        await AssertCredentialKeptAsync(client, store, identity);
    }

    [Theory]
    [InlineData("no-code")]
    [InlineData("html")]
    [InlineData("other-code")]
    public async Task Revoke_UnrecognisedRejection_NeverProbesAndKeepsCredential(string answer)
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        var calls = new List<string>();
        var transport = new DelegateHttpInvokerFactory(async (_, request, cancellationToken) =>
        {
            if (IsEnroll(request)) return TestCertificateAuthority.JsonResponse(await IssueAsync(authority, request, cancellationToken));
            calls.Add(Route(request));
            return answer switch
            {
                "no-code" => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = JsonContent.Create(new { message = "denied" }) },
                "html" => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("<html><body>401</body></html>", System.Text.Encoding.UTF8, "text/html") },
                _ => TestCertificateAuthority.Failure(HttpStatusCode.Unauthorized, "access_denied"),
            };
        });
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        var client = CreateClient(store, authority, transport);
        await client.EnrollAsync("one-use-secret", "test-device");
        var identity = (await client.GetSignOutIdentityAsync())!;

        var error = await Assert.ThrowsAsync<ConnectorEnrollmentProtocolException>(() =>
            client.RevokeAndConfirmExactAsync(identity.DeviceId!).AsTask());

        Assert.Equal(401, error.StatusCode);
        Assert.Equal(["POST self-revoke"], calls);
        await AssertCredentialKeptAsync(client, store, identity);
    }

    [Fact]
    public async Task Revoke_CancelledDuringConfirmationWait_KeepsCredential()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        DeviceEnrollmentResponse? issued = null;
        using var cancellation = new CancellationTokenSource();
        var transport = new DelegateHttpInvokerFactory(async (_, request, cancellationToken) =>
        {
            if (IsEnroll(request)) return TestCertificateAuthority.JsonResponse(issued = await IssueAsync(authority, request, cancellationToken));
            if (request.Method == HttpMethod.Get) cancellation.Cancel();
            return Pending(issued!, issued!.AccessRevision + 1);
        });
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        var client = CreateClient(store, authority, transport, TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(60));
        await client.EnrollAsync("one-use-secret", "test-device");
        var identity = (await client.GetSignOutIdentityAsync())!;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.RevokeAndConfirmExactAsync(identity.DeviceId!, cancellation.Token).AsTask());

        await AssertCredentialKeptAsync(client, store, identity);
    }

    [Fact]
    public async Task ForgetRejectedEnrollment_ArchivesOnlyAfterTwoTrustedRejections()
    {
        using var directory = new TemporaryDirectory();
        using var authority = new TestCertificateAuthority();
        DeviceEnrollmentResponse? issued = null;
        var answers = new Queue<Func<HttpResponseMessage>>();
        var transport = new DelegateHttpInvokerFactory(async (_, request, cancellationToken) =>
        {
            if (IsEnroll(request)) return TestCertificateAuthority.JsonResponse(issued = await IssueAsync(authority, request, cancellationToken));
            Assert.True(IsResume(request));
            return answers.Dequeue()();
        });
        var store = new FileConnectorEnrollmentStateStore(directory.Path);
        var client = CreateClient(store, authority, transport, rejectionConfirmationDelay: TimeSpan.FromMilliseconds(10));
        await client.EnrollAsync("one-use-secret", "test-device");
        var requestId = (await store.LoadAsync(default))!.RequestId;
        async Task AssertKeptAsync() => Assert.Equal(requestId, (await store.LoadAsync(default))!.RequestId);
        Func<HttpResponseMessage> trusted = () => Rejected(DateTimeOffset.UtcNow);

        answers.Enqueue(() => throw new HttpRequestException("network unreachable"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.ForgetRejectedEnrollmentAsync().AsTask());
        await AssertKeptAsync();

        answers.Enqueue(() => TestCertificateAuthority.Failure(HttpStatusCode.ServiceUnavailable, "unavailable"));
        await Assert.ThrowsAsync<ConnectorEnrollmentProtocolException>(() => client.ForgetRejectedEnrollmentAsync().AsTask());
        await AssertKeptAsync();

        answers.Enqueue(() => TestCertificateAuthority.JsonResponse(issued!));
        Assert.False(await client.ForgetRejectedEnrollmentAsync());
        await AssertKeptAsync();

        // One rejection followed by acceptance (a proxy hiccup) keeps the device.
        answers.Enqueue(trusted);
        answers.Enqueue(() => TestCertificateAuthority.JsonResponse(issued!));
        Assert.False(await client.ForgetRejectedEnrollmentAsync());
        await AssertKeptAsync();

        // Review scenario (b): fast PC clock.
        answers.Enqueue(() => Rejected(DateTimeOffset.UtcNow.AddMinutes(-4)));
        var skew = await Assert.ThrowsAsync<ConnectorClockSkewException>(() => client.ForgetRejectedEnrollmentAsync().AsTask());
        Assert.InRange(skew.Skew.TotalMinutes, 3.9, 4.1);
        await AssertKeptAsync();

        answers.Enqueue(() => Rejected(null));
        var unverified = await Assert.ThrowsAsync<ConnectorEnrollmentProtocolException>(() => client.ForgetRejectedEnrollmentAsync().AsTask());
        Assert.Equal("rejection_unverified", unverified.ErrorCode);
        await AssertKeptAsync();

        answers.Enqueue(trusted);
        answers.Enqueue(trusted);
        Assert.True(await client.ForgetRejectedEnrollmentAsync());
        Assert.Null(await store.LoadAsync(default));
        Assert.True(await client.ForgetRejectedEnrollmentAsync());
        Assert.Empty(answers);
        // The rejected enrollment is set aside, not deleted: renaming it back restores it.
        var archived = Assert.Single(Directory.GetFiles(directory.Path, "connector-enrollment-v1.json.rejected-*"));
        File.Move(archived, store.StateFilePath);
        await AssertKeptAsync();
    }

    private static async Task AssertCredentialKeptAsync(
        HttpConnectorEnrollmentClient client,
        FileConnectorEnrollmentStateStore store,
        ConnectorSignOutIdentity identity)
    {
        await Assert.ThrowsAsync<ConnectorEnrollmentStateException>(() => client.ClearRevokedSessionAsync(identity).AsTask());
        Assert.Equal(identity.RequestId, (await store.LoadAsync(default))!.RequestId);
    }

    private static HttpResponseMessage Rejected(DateTimeOffset? serverDate)
    {
        var response = TestCertificateAuthority.Failure(HttpStatusCode.Unauthorized, "device_unauthorized");
        response.Headers.Date = serverDate;
        return response;
    }

    private sealed class ShiftedClock : TimeProvider
    {
        public TimeSpan Shift { get; set; }
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Shift;
    }

    private static bool IsEnroll(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.EndsWith("/enroll", StringComparison.Ordinal);

    private static bool IsResume(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.EndsWith("/resume", StringComparison.Ordinal);

    private static string Route(HttpRequestMessage request) =>
        $"{request.Method} {request.RequestUri!.AbsolutePath.Split('/')[^1]}";

    private static async Task<DeviceEnrollmentResponse> IssueAsync(
        TestCertificateAuthority authority,
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        DateTimeOffset? expiresAt = null) =>
        authority.Issue((await request.Content!.ReadFromJsonAsync<DeviceEnrollmentRequest>(cancellationToken))!, expiresAt);

    private static HttpConnectorEnrollmentClient CreateClient(
        TemporaryDirectory directory,
        TestCertificateAuthority authority,
        IEnrollmentHttpInvokerFactory transport) =>
        CreateClient(new FileConnectorEnrollmentStateStore(directory.Path), authority, transport);

    private static HttpConnectorEnrollmentClient CreateClient(
        FileConnectorEnrollmentStateStore store,
        TestCertificateAuthority authority,
        IEnrollmentHttpInvokerFactory transport,
        TimeSpan? revocationPollInterval = null,
        TimeSpan? revocationOperationTimeout = null,
        TimeProvider? timeProvider = null,
        TimeSpan? rejectionConfirmationDelay = null) =>
        new(new ConnectorAccessClientOptions
        {
            ServiceBaseUri = new Uri("https://connector.example.test/"),
            TrustedIssuerCertificateSha256 = authority.Pin,
            TimeProvider = timeProvider ?? TimeProvider.System,
        }, store, transport, revocationPollInterval, revocationOperationTimeout, rejectionConfirmationDelay);

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
