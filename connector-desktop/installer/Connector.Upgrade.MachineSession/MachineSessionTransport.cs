using System.IO.Pipes;
using System.Security;
using System.Security.Principal;
using Connector.Upgrade.HelperLauncher;
using Connector.Upgrade.LaunchedHelperIdentity;
using Connector.Upgrade.MachinePipe;
using Microsoft.Win32.SafeHandles;
using TrustedHelperLauncher = Connector.Upgrade.HelperLauncher.HelperLauncher;

namespace Connector.Upgrade.MachineSession;

/// <summary>Runs a one-shot diagnostic probe against the pinned elevated helper. It grants no command authority.</summary>
public sealed class MachineSessionProbe
{
    public const byte BootstrapMarker = 0x7f;
    public static readonly TimeSpan MaximumHandshakeTimeout = TimeSpan.FromMinutes(2);

    private readonly IMachineSessionPipeFactory _pipes;
    private readonly IMachineSessionLauncher _launcher;
    private readonly IMachineSessionIdentity _identity;
    private readonly Func<SecurityIdentifier> _initiatingSid;

    public MachineSessionProbe() : this(
        new NamedMachineSessionPipeFactory(),
        new TrustedMachineSessionLauncher(),
        new LaunchedMachineSessionIdentity(),
        GetCurrentUserSid) { }

    internal MachineSessionProbe(
        IMachineSessionPipeFactory pipes,
        IMachineSessionLauncher launcher,
        IMachineSessionIdentity identity,
        Func<SecurityIdentifier> initiatingSid)
    {
        _pipes = pipes ?? throw new ArgumentNullException(nameof(pipes));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _initiatingSid = initiatingSid ?? throw new ArgumentNullException(nameof(initiatingSid));
    }

    /// <summary>
    /// Creates the server pipe before launching the pinned helper, then observes a marker and fresh
    /// kernel identity checks. The observation is diagnostic only and can never authorize a command.
    /// The timeout applies only after the elevated launcher returns; a user-controlled UAC prompt
    /// cannot be timed out or canceled through this API.
    /// </summary>
    public async ValueTask<MachineSessionObservation> ProbeAsync(
        HelperImagePin helperImage,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(helperImage);
        if (timeout <= TimeSpan.Zero || timeout > MaximumHandshakeTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout), $"Timeout must be between zero and {MaximumHandshakeTimeout}.");
        cancellationToken.ThrowIfCancellationRequested();

        var operationId = Guid.NewGuid();
        var initiatingSid = _initiatingSid();
        ArgumentNullException.ThrowIfNull(initiatingSid);
        var pipeName = MachinePipeIdentity.GetPipeName(operationId, initiatingSid);
        IMachineSessionPipe? pipe = null;
        SafeProcessHandle? process = null;
        CancellationTokenSource? handshakeDeadline = null;
        try
        {
            // The pipe must already be listening when ShellExecuteEx starts the elevated helper.
            pipe = _pipes.Create(operationId, initiatingSid);
            process = _launcher.Launch(helperImage, pipeName, operationId);

            // UAC is user-controlled and the launcher is synchronous. Start the bounded handshake
            // window only after it returns; this timeout covers connection and the first byte only.
            handshakeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeDeadline.CancelAfter(timeout);
            await pipe.WaitForConnectionAsync(handshakeDeadline.Token).ConfigureAwait(false);

            var marker = new byte[1];
            var count = await pipe.ReadAsync(marker, handshakeDeadline.Token).ConfigureAwait(false);
            if (count != 1 || marker[0] != BootstrapMarker)
                throw new InvalidDataException("Elevated helper sent an invalid machine-session bootstrap marker.");

            var identity = _identity.ObserveLaunchedElevatedPeer(pipe.Handle, process);
            handshakeDeadline.Token.ThrowIfCancellationRequested();
            // The finally block closes both kernel handles before this observation reaches callers.
            return new MachineSessionObservation(
                operationId,
                pipeConnected: true,
                bootstrapMarkerReceived: true,
                identity.PipeClientMatchesLaunchedProcess,
                identity.ElevatedAdministratorToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && handshakeDeadline?.IsCancellationRequested == true)
        {
            throw new TimeoutException("Timed out waiting for the elevated machine helper handshake.");
        }
        finally
        {
            try
            {
                pipe?.Dispose();
            }
            finally
            {
                try
                {
                    process?.Dispose();
                }
                finally
                {
                    handshakeDeadline?.Dispose();
                }
            }
        }
    }

    private static SecurityIdentifier GetCurrentUserSid()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Machine helper sessions require Windows.");
        return WindowsIdentity.GetCurrent().User ?? throw new SecurityException("Current user token has no SID.");
    }
}

/// <summary>Non-authorizing diagnostic evidence from a one-shot helper transport probe.</summary>
public sealed class MachineSessionObservation
{
    internal MachineSessionObservation(
        Guid operationId,
        bool pipeConnected,
        bool bootstrapMarkerReceived,
        bool pipeClientMatchesLaunchedProcess,
        bool elevatedAdministratorToken)
    {
        OperationId = operationId;
        PipeConnected = pipeConnected;
        BootstrapMarkerReceived = bootstrapMarkerReceived;
        PipeClientMatchesLaunchedProcess = pipeClientMatchesLaunchedProcess;
        ElevatedAdministratorToken = elevatedAdministratorToken;
    }

    public Guid OperationId { get; }
    public bool PipeConnected { get; }
    public bool BootstrapMarkerReceived { get; }
    public bool PipeClientMatchesLaunchedProcess { get; }
    public bool ElevatedAdministratorToken { get; }

    // Privileged command authorization remains a separate, permanently closed contract here.
    public bool IsAuthorized => false;
}

internal interface IMachineSessionPipe : IDisposable
{
    SafePipeHandle Handle { get; }
    ValueTask WaitForConnectionAsync(CancellationToken cancellationToken);
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}

internal interface IMachineSessionPipeFactory
{
    IMachineSessionPipe Create(Guid operationId, SecurityIdentifier initiatingSid);
}

internal interface IMachineSessionLauncher
{
    SafeProcessHandle Launch(HelperImagePin image, string pipeName, Guid operationId);
}

internal interface IMachineSessionIdentity
{
    MachineSessionIdentityEvidence ObserveLaunchedElevatedPeer(SafePipeHandle connectedPipe, SafeProcessHandle launchedProcess);
}

internal readonly record struct MachineSessionIdentityEvidence(
    bool PipeClientMatchesLaunchedProcess,
    bool ElevatedAdministratorToken);

internal sealed class NamedMachineSessionPipeFactory : IMachineSessionPipeFactory
{
    public IMachineSessionPipe Create(Guid operationId, SecurityIdentifier initiatingSid) =>
        new NamedMachineSessionPipe(MachinePipeIdentity.CreateServer(operationId, initiatingSid));
}

internal sealed class NamedMachineSessionPipe(NamedPipeServerStream stream) : IMachineSessionPipe
{
    private readonly NamedPipeServerStream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    public SafePipeHandle Handle => _stream.SafePipeHandle;
    public async ValueTask WaitForConnectionAsync(CancellationToken cancellationToken) =>
        await _stream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    public void Dispose() => _stream.Dispose();
}

internal sealed class TrustedMachineSessionLauncher : IMachineSessionLauncher
{
    private readonly TrustedHelperLauncher _launcher = new();
    public SafeProcessHandle Launch(HelperImagePin image, string pipeName, Guid operationId) =>
        _launcher.Launch(image, pipeName, operationId);
}

internal sealed class LaunchedMachineSessionIdentity : IMachineSessionIdentity
{
    private readonly LaunchedHelperIdentityVerifier _verifier = new();

    public MachineSessionIdentityEvidence ObserveLaunchedElevatedPeer(SafePipeHandle connectedPipe, SafeProcessHandle launchedProcess)
    {
        var result = _verifier.Verify(connectedPipe, launchedProcess);
        return new MachineSessionIdentityEvidence(
            result.PipeClientMatchesLaunchedProcess,
            result.ElevatedAdministratorToken);
    }
}
