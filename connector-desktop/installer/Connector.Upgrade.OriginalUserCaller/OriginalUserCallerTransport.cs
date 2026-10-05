using System.Security;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Connector.Upgrade.HelperLauncher;
using Connector.Upgrade.HelperReleaseTrust;
using Connector.Upgrade.MachineCommandChannel;
using Connector.Upgrade.MutualMachineChannel;
using Connector.Upgrade.ProtectedCallerImage;
using MutualChannel = global::Connector.Upgrade.MutualMachineChannel.MutualMachineChannel;

namespace Connector.Upgrade.OriginalUserCaller;

/// <summary>Opens one authenticated command session for the current interactive Windows user.</summary>
public sealed class OriginalUserCallerTransport
{
    private readonly HelperReleasePinSource _releasePins;
    private readonly HelperLauncher.HelperLauncher _launcher;
    private readonly MutualChannel _channel;

    public OriginalUserCallerTransport()
        : this(new HelperReleasePinSource(), new HelperLauncher.HelperLauncher(),
            new MutualChannel(new ProtectedCallerImagePinSource())) { }

    internal OriginalUserCallerTransport(HelperReleasePinSource releasePins,
        HelperLauncher.HelperLauncher launcher, MutualChannel channel)
    {
        _releasePins = releasePins ?? throw new ArgumentNullException(nameof(releasePins));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
    }

    /// <summary>
    /// Opens a command-capable session for the trusted coordinator's operation ID. The ID is
    /// carried only in the local mutual channel bootstrap; it is never accepted from CLI input.
    /// </summary>
    public ValueTask<MachineCommandClient> OpenAsync(Guid operationId, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Validate(operationId, timeout);
        return OriginalUserCallerFlow.OpenAsync<MutualChannelPipe, SafeProcessHandle,
            MutualMachineAuthenticatedSession, MachineCommandClient>(
            ReadCurrentUserSid,
            _releasePins.GetPin,
            (name, sid, operation) => _channel.CreateCallerPipe(operation, sid),
            (pin, name, operation, sid) => _launcher.LaunchMutual(pin, name, operation, sid),
            (operation, sid, pipe, process, limit, token) =>
                _channel.AuthenticateCallerSessionAsync(operation, sid, pipe, process, limit, token),
            session => new MachineCommandClient(session),
            operationId, timeout, cancellationToken);
    }

    private static SecurityIdentifier ReadCurrentUserSid()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Original-user helper transport requires Windows.");
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return identity.User ?? throw new SecurityException("The current Windows token has no user SID.");
    }

    private static void Validate(Guid operationId, TimeSpan timeout)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("Operation ID must not be empty.", nameof(operationId));
        if (timeout <= TimeSpan.Zero || timeout > MutualChannel.MaximumTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Handshake timeout must be positive and no greater than two minutes.");
    }
}

/// <summary>Testable resource-ordering core; production callbacks remain bound to pinned Windows APIs.</summary>
internal static class OriginalUserCallerFlow
{
    internal static async ValueTask<TClient> OpenAsync<TPipe, TProcess, TSession, TClient>(
        Func<SecurityIdentifier> readCurrentSid,
        Func<HelperImagePin> getReleasePin,
        Func<string, SecurityIdentifier, Guid, TPipe> createPipe,
        Func<HelperImagePin, string, Guid, SecurityIdentifier, TProcess> launchHelper,
        Func<Guid, SecurityIdentifier, TPipe, TProcess, TimeSpan, CancellationToken, ValueTask<TSession>> authenticate,
        Func<TSession, TClient> createClient,
        Guid operationId, TimeSpan timeout, CancellationToken cancellationToken)
        where TPipe : class, IDisposable
        where TProcess : class, IDisposable
        where TSession : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(readCurrentSid);
        ArgumentNullException.ThrowIfNull(getReleasePin);
        ArgumentNullException.ThrowIfNull(createPipe);
        ArgumentNullException.ThrowIfNull(launchHelper);
        ArgumentNullException.ThrowIfNull(authenticate);
        ArgumentNullException.ThrowIfNull(createClient);
        cancellationToken.ThrowIfCancellationRequested();

        var sid = readCurrentSid();
        var pin = getReleasePin(); // Fail closed before creating an endpoint or invoking UAC.
        cancellationToken.ThrowIfCancellationRequested();
        var pipeName = MutualChannel.GetPipeName("A", operationId, sid);
        TPipe? pipe = null;
        TProcess? process = null;
        try
        {
            pipe = createPipe(pipeName, sid, operationId);
            cancellationToken.ThrowIfCancellationRequested();
            process = launchHelper(pin, pipeName, operationId, sid);
            var session = await authenticate(operationId, sid, pipe, process, timeout, cancellationToken)
                .ConfigureAwait(false);
            pipe = null; // The authenticated session now owns the A endpoint.
            try { return createClient(session); }
            catch { session.Dispose(); throw; }
        }
        finally
        {
            pipe?.Dispose();
            process?.Dispose(); // Session holds its own SafeHandle reference after authentication.
        }
    }
}
