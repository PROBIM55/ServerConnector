using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using Connector.Access.Client;

namespace Connector.Network;

/// <summary>What the server is doing for this computer while the connection waits.</summary>
public enum CommonConnectorConnectionPhase
{
    /// <summary>The server issues the device's access (providers, setup key).</summary>
    Access,
    /// <summary>The issued NetBird peer joins the protected network.</summary>
    Network,
    /// <summary>The server-confirmed peer, applied profile and route are checked.</summary>
    Verify,
}

/// <summary>
/// One report of the wait. <see cref="TransientFailure"/> is a local descriptor (exception type,
/// HTTP status, local or bounded server code) and never a server message or body.
/// </summary>
public sealed record CommonConnectorProvisioningProgress(
    CommonConnectorConnectionPhase Phase,
    TimeSpan Elapsed,
    bool Background,
    string DiagnosticCode,
    string? TransientFailure = null);

public sealed class CommonConnectorProvisioningOptions
{
    /// <summary>After this the wait is reported as background work; it does not end.</summary>
    public TimeSpan ForegroundBudget { get; init; } = TimeSpan.FromMinutes(4);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan BackgroundPollInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan MaximumForegroundBackoff { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan MaximumBackgroundBackoff { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaximumRetryAfter { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>
    /// An older server answers «access_pending_or_denied» both for pending and for denied access.
    /// While only that ambiguous answer is seen the wait ends after this total time.
    /// </summary>
    public TimeSpan AmbiguousPendingLimit { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>The wait itself reached a definitive local outcome (<see cref="Code"/> is a local constant).</summary>
public sealed class CommonConnectorWaitEndedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Waits for the server to finish provisioning the same durable device. Transient server and
/// network failures keep the wait going with bounded backoff; only a definitive state (revoked,
/// denied, sign-in required, a protocol violation) or the caller's cancellation ends it.
/// Ready is returned only after the optional route confirmation succeeds.
/// </summary>
public static class CommonConnectorProvisioningWaiter
{
    private static readonly HashSet<string> TransientNetworkCodes = new(StringComparer.Ordinal)
    {
        "netbird_up_failed", "netbird_not_ready", "netbird_pre_up_status_unavailable",
        "service_offline", "overlay_not_ready", "common_connection_not_ready", "route_authorization_expired",
    };

    /// <summary>Server codes that no retry resolves, whatever HTTP status carries them.</summary>
    public static readonly IReadOnlySet<string> DefinitiveServerCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "access_denied", "device_not_active", "provider_configuration", "vpn_not_configured",
    };

    /// <summary>Pending diagnostics that come only from the ambiguous «access_pending_or_denied» answer.</summary>
    public static readonly IReadOnlySet<string> AmbiguousPendingCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "access_providers_pending_or_denied", "access_profile_pending_or_denied",
    };

    public static async ValueTask<CommonConnectorConnectionSnapshot> WaitAsync(
        Func<CancellationToken, ValueTask<CommonConnectorConnectionSnapshot>> start,
        Func<CancellationToken, ValueTask<CommonConnectorConnectionSnapshot>> resume,
        Func<CancellationToken, ValueTask> closeAuthorization,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null,
        Func<CancellationToken, ValueTask>? confirmReady = null,
        Action<CommonConnectorProvisioningProgress>? progress = null,
        CommonConnectorProvisioningOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(resume);
        ArgumentNullException.ThrowIfNull(closeAuthorization);
        options ??= new CommonConnectorProvisioningOptions();
        var clock = timeProvider ?? TimeProvider.System;
        var started = clock.GetTimestamp();
        var phase = CommonConnectorConnectionPhase.Access;
        var diagnostic = "starting";
        var failures = 0;
        string? failure = null;
        TimeSpan? retryAfter = null;
        CommonConnectorConnectionSnapshot? current = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { current = await start(cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (IsTransient(exception, cancellationToken))
            {
                (failure, retryAfter) = (Describe(exception), RetryAfterOf(exception));
            }
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (failure is null && current is not null)
                {
                    diagnostic = current.DiagnosticCode;
                    if (current.Status == CommonConnectorConnectionStatus.Ready)
                    {
                        phase = CommonConnectorConnectionPhase.Verify;
                        if (confirmReady is null) return current;
                        try
                        {
                            await confirmReady(cancellationToken).ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();
                            return current;
                        }
                        catch (Exception exception) when (IsTransient(exception, cancellationToken))
                        {
                            // The route is not reachable yet (routes still propagate). The next
                            // resume re-validates the peer and profile with the server first.
                            (failure, retryAfter) = (Describe(exception), RetryAfterOf(exception));
                        }
                    }
                    else if (!IsPending(current)) return current;
                    else phase = PhaseOf(current) ?? phase;
                }

                var elapsed = clock.GetElapsedTime(started);
                if (failure is null && current is not null && AmbiguousPendingCodes.Contains(current.DiagnosticCode) &&
                    elapsed >= options.AmbiguousPendingLimit)
                {
                    // An older server cannot tell pending from denied; never wait forever on a denial.
                    await closeAuthorization(CancellationToken.None).ConfigureAwait(false);
                    throw new CommonConnectorWaitEndedException("access_not_granted",
                        "The server did not grant access within the limit for an ambiguous pending answer.");
                }
                var background = elapsed >= options.ForegroundBudget;
                progress?.Invoke(new(phase, elapsed, background, diagnostic, failure));
                var delay = failure is null
                    ? background ? options.BackgroundPollInterval : options.PollInterval
                    : Backoff(++failures, retryAfter, background, options);
                if (failure is null) failures = 0;
                (failure, retryAfter) = (null, null);

                await Task.Delay(delay, clock, cancellationToken).ConfigureAwait(false);
                try
                {
                    current = await resume(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (Exception exception) when (IsTransient(exception, cancellationToken))
                {
                    (failure, retryAfter) = (Describe(exception), RetryAfterOf(exception));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Resume may have installed Ready just before its caller cancelled.
            // Cleanup cannot borrow that already-cancelled token. Definitive
            // failures keep the coordinator's own actionable state instead.
            await closeAuthorization(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The server phase a pending snapshot belongs to; null keeps the previous phase.</summary>
    public static CommonConnectorConnectionPhase? PhaseOf(CommonConnectorConnectionSnapshot snapshot) =>
        snapshot.DiagnosticCode switch
        {
            "access_profile_pending" or "access_profile_pending_or_denied" or "access_profile_revision_pending" or
                "overlay_address_mismatch" or "ready" =>
                CommonConnectorConnectionPhase.Verify,
            "access_providers_initializing" or "access_providers_pending_or_denied" or "vpn_bootstrap_pending" or
                "enrolling" or "resuming_enrollment" =>
                CommonConnectorConnectionPhase.Access,
            _ => snapshot.Status switch
            {
                CommonConnectorConnectionStatus.Enrolling or CommonConnectorConnectionStatus.AccessPending =>
                    CommonConnectorConnectionPhase.Access,
                CommonConnectorConnectionStatus.ConnectingOverlay or CommonConnectorConnectionStatus.AwaitingVpnAuthorization =>
                    CommonConnectorConnectionPhase.Network,
                CommonConnectorConnectionStatus.Ready => CommonConnectorConnectionPhase.Verify,
                _ => null,
            },
        };

    /// <summary>
    /// A failure that a later poll of the same device can resolve: server busy/unavailable,
    /// transport errors and timeouts, a NetBird daemon still settling. Authentication, policy
    /// denial, clock skew, local state and protocol violations are definitive.
    /// </summary>
    public static bool IsTransient(Exception exception, CancellationToken callerToken = default) => exception switch
    {
        OperationCanceledException => !callerToken.IsCancellationRequested, // HttpClient or nested deadline
        ConnectorClockSkewException or ConnectorEnrollmentStateException => false,
        ConnectorEnrollmentProtocolException protocol when protocol.ErrorCode is { } code && DefinitiveServerCodes.Contains(code) => false,
        ConnectorEnrollmentProtocolException protocol =>
            protocol.StatusCode is 408 or 425 or 429 or (>= 500 and <= 599) ||
            protocol.ErrorCode?.Contains("busy", StringComparison.OrdinalIgnoreCase) == true,
        HttpRequestException http => http.InnerException is not AuthenticationException &&
            (http.StatusCode is null || (int)http.StatusCode.Value is 408 or 425 or 429 or (>= 500 and <= 599)),
        TimeoutException or IOException or SocketException => true,
        NetworkGateException network => TransientNetworkCodes.Contains(network.Code),
        _ => false,
    };

    /// <summary>Type, status and a bounded code only: never a message, body or credential.</summary>
    public static string Describe(Exception exception)
    {
        var detail = exception switch
        {
            ConnectorEnrollmentProtocolException protocol => Join(protocol.StatusCode?.ToString(), SafeCode(protocol.ErrorCode)),
            HttpRequestException http => Join(http.StatusCode is { } status ? ((int)status).ToString() : null,
                http.HttpRequestError.ToString()),
            NetworkGateException network => SafeCode(network.Code),
            SocketException socket => socket.SocketErrorCode.ToString(),
            _ => null,
        };
        return string.IsNullOrEmpty(detail) ? exception.GetType().Name : exception.GetType().Name + "/" + detail;
    }

    private static TimeSpan? RetryAfterOf(Exception exception) =>
        exception is ConnectorEnrollmentProtocolException { RetryAfter: { } retryAfter } ? retryAfter : null;

    private static TimeSpan Backoff(int failures, TimeSpan? retryAfter, bool background, CommonConnectorProvisioningOptions options)
    {
        if (retryAfter is { } hint)
            return hint < options.PollInterval ? options.PollInterval : hint > options.MaximumRetryAfter ? options.MaximumRetryAfter : hint;
        var maximum = background ? options.MaximumBackgroundBackoff : options.MaximumForegroundBackoff;
        var delay = TimeSpan.FromTicks(options.PollInterval.Ticks * (1L << Math.Min(failures, 6)));
        return delay > maximum ? maximum : delay;
    }

    private static bool IsPending(CommonConnectorConnectionSnapshot snapshot) =>
        snapshot.Status is CommonConnectorConnectionStatus.AccessPending or
            CommonConnectorConnectionStatus.AwaitingVpnAuthorization or
            CommonConnectorConnectionStatus.ConnectingOverlay;

    private static string? SafeCode(string? code) =>
        code is { Length: > 0 and <= 64 } && code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            ? code
            : null;

    private static string? Join(string? first, string? second) =>
        first is null ? second : second is null ? first : first + "/" + second;
}
