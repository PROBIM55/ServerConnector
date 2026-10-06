using System.IO.Pipes;
using System.Security;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Connector.Upgrade.MutualMachineChannel;

/// <summary>
/// One-shot mutual transport handshake for a caller and its elevated helper. It grants no
/// machine-command authority. The caller must keep the launched helper handle alive until this
/// method returns; the helper must retain its caller handle and a trusted image-pin source.
/// </summary>
public sealed class MutualMachineChannel
{
    public static readonly TimeSpan MaximumTimeout = TimeSpan.FromMinutes(2);
    private static readonly Regex SidPattern = new("^S-1-(?:[0-9]+-){1,14}[0-9]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly IMutualMachineChannelPlatform _platform;
    private readonly ITrustedCallerImagePinSource _callerTrust;

    /// <summary>The default production path fails closed until a protected caller-image pin source is configured.</summary>
    public MutualMachineChannel() : this(WindowsMutualMachineChannelPlatform.Instance, UnavailableCallerImagePinSource.Instance) { }

    /// <summary>Uses the Windows transport with an application-supplied, protected caller-image pin source.</summary>
    public MutualMachineChannel(ITrustedCallerImagePinSource trustedCallerImagePinSource)
        : this(WindowsMutualMachineChannelPlatform.Instance, trustedCallerImagePinSource) { }

    internal MutualMachineChannel(IMutualMachineChannelPlatform platform, ITrustedCallerImagePinSource callerTrust)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _callerTrust = callerTrust ?? throw new ArgumentNullException(nameof(callerTrust));
    }

    /// <summary>Creates caller-side A listener before the elevated helper is launched.</summary>
    public MutualChannelPipe CreateCallerPipe(Guid operationId, SecurityIdentifier callerSid)
    {
        ValidateOperation(operationId, callerSid);
        return new MutualChannelPipe(_platform.CreateServer(GetPipeName("A", operationId, callerSid), callerSid));
    }

    /// <summary>
    /// Authenticates A against the retained launched-helper handle, sends one fresh nonce, then
    /// accepts B only when it echoes that nonce. Any failure closes both pipe handles.
    /// </summary>
    public async ValueTask AuthenticateCallerAsync(
        Guid operationId,
        SecurityIdentifier callerSid,
        MutualChannelPipe aServer,
        SafeProcessHandle launchedHelper,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aServer);
        IMutualChannelPipe? bClient = null;
        CancellationTokenSource? deadline = null;
        try
        {
            ValidateOperation(operationId, callerSid);
            ArgumentNullException.ThrowIfNull(launchedHelper);
            ValidateTimeout(timeout);
            deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            await RunWhilePeerLivesAsync(async token =>
            {
                await aServer.Inner.WaitForConnectionAsync(token).ConfigureAwait(false);
                _platform.AssertConnectedHelper(aServer.Inner.Handle, launchedHelper);
                var nonce = RandomNumberGenerator.GetBytes(32);
                await aServer.Inner.WriteExactlyAsync(nonce, token).ConfigureAwait(false);
                bClient = await _platform.ConnectClientAsync(GetPipeName("B", operationId, callerSid), token).ConfigureAwait(false);
                var echo = new byte[32];
                await bClient.ReadExactlyAsync(echo, token).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(nonce, echo))
                    throw new SecurityException("Mutual machine channel B did not echo the fresh A nonce.");
            }, launchedHelper, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline?.IsCancellationRequested == true)
        {
            throw new TimeoutException("Timed out during the one-shot mutual machine channel handshake.");
        }
        finally
        {
            deadline?.Dispose();
            bClient?.Dispose();
            aServer.Dispose();
        }
    }

    /// <summary>Opt-in command-capable handshake. The returned session owns A and retains the launched process handle.</summary>
    public async ValueTask<MutualMachineAuthenticatedSession> AuthenticateCallerSessionAsync(
        Guid operationId, SecurityIdentifier callerSid, MutualChannelPipe aServer,
        SafeProcessHandle launchedHelper, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aServer);
        ArgumentNullException.ThrowIfNull(launchedHelper);
        try
        {
            ValidateOperation(operationId, callerSid);
            ValidateTimeout(timeout);
            using var currentProcess = WindowsMutualNative.OpenCurrentProcessHandle();
            var actualCallerSid = WindowsMutualNative.GetProcessUserSid(currentProcess);
            if (!actualCallerSid.Equals(callerSid))
                throw new SecurityException("Caller SID does not match the current process token.");
            _callerTrust.AssertTrustedCaller(currentProcess, actualCallerSid);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            await AuthenticateCallerCoreAsync(operationId, callerSid, aServer.Inner, launchedHelper, deadline.Token).ConfigureAwait(false);
            return CreateAuthenticatedSession(aServer.Inner, launchedHelper);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { aServer.Dispose(); throw new TimeoutException("Timed out during the mutual machine command handshake."); }
        catch { aServer.Dispose(); throw; }
    }

    /// <summary>
    /// Helper-side handshake. Creates restrictive, one-instance B first, then reads the nonce from
    /// A and echoes it only after B's connected client PID, process handle, SID and image pin pass.
    /// The caller handle must come from a trusted bootstrap; PID text or SID text is never accepted.
    /// </summary>
    public async ValueTask RunHelperAsync(
        Guid operationId,
        SecurityIdentifier originalCallerSid,
        SafeProcessHandle retainedOriginalCaller,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateOperation(operationId, originalCallerSid);
        ArgumentNullException.ThrowIfNull(retainedOriginalCaller);
        ValidateTimeout(timeout);
        // A missing, unavailable, or mismatched pin source aborts before either endpoint is used.
        _callerTrust.AssertTrustedCaller(retainedOriginalCaller, originalCallerSid);
        using var bServer = _platform.CreateServer(GetPipeName("B", operationId, originalCallerSid), originalCallerSid);
        IMutualChannelPipe? aClient = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await RunWhilePeerLivesAsync(async token =>
            {
                aClient = await _platform.ConnectClientAsync(GetPipeName("A", operationId, originalCallerSid), token).ConfigureAwait(false);
                var nonce = new byte[32];
                await aClient.ReadExactlyAsync(nonce, token).ConfigureAwait(false);
                await bServer.WaitForConnectionAsync(token).ConfigureAwait(false);
                _platform.AssertConnectedCaller(bServer.Handle, retainedOriginalCaller, originalCallerSid, _callerTrust);
                await bServer.WriteExactlyAsync(nonce, token).ConfigureAwait(false);
            }, retainedOriginalCaller, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out during the one-shot mutual machine channel handshake.");
        }
        finally
        {
            aClient?.Dispose();
        }
    }

    /// <summary>Opt-in helper handshake retaining A, the trusted caller handle, and caller pin decision.</summary>
    public async ValueTask<MutualMachineAuthenticatedSession> RunHelperSessionAsync(
        Guid operationId, SecurityIdentifier originalCallerSid, SafeProcessHandle retainedOriginalCaller,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ValidateOperation(operationId, originalCallerSid);
        ArgumentNullException.ThrowIfNull(retainedOriginalCaller);
        ValidateTimeout(timeout);
        _callerTrust.AssertTrustedCaller(retainedOriginalCaller, originalCallerSid);
        var aClient = await AuthenticateHelperCoreAsync(operationId, originalCallerSid, retainedOriginalCaller, timeout, cancellationToken).ConfigureAwait(false);
        try { return CreateAuthenticatedSession(aClient, retainedOriginalCaller); }
        catch { aClient.Dispose(); throw; }
    }

    private MutualMachineAuthenticatedSession CreateAuthenticatedSession(IMutualChannelPipe pipe, SafeProcessHandle peer) =>
        new(pipe, peer, (operation, token) => RunWhilePeerLivesAsync(operation, peer, token));

    private async ValueTask<IMutualChannelPipe> AuthenticateCallerCoreAsync(Guid operationId, SecurityIdentifier callerSid,
        IMutualChannelPipe aServer, SafeProcessHandle launchedHelper, CancellationToken token)
    {
        IMutualChannelPipe? bClient = null;
        try
        {
            await RunWhilePeerLivesAsync(async ct =>
            {
                await aServer.WaitForConnectionAsync(ct).ConfigureAwait(false);
                _platform.AssertConnectedHelper(aServer.Handle, launchedHelper);
                var nonce = RandomNumberGenerator.GetBytes(32);
                await aServer.WriteExactlyAsync(nonce, ct).ConfigureAwait(false);
                bClient = await _platform.ConnectClientAsync(GetPipeName("B", operationId, callerSid), ct).ConfigureAwait(false);
                var echo = new byte[32]; await bClient.ReadExactlyAsync(echo, ct).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(nonce, echo)) throw new SecurityException("Mutual machine channel B did not echo the fresh A nonce.");
            }, launchedHelper, token).ConfigureAwait(false);
            bClient?.Dispose();
            return aServer;
        }
        catch { bClient?.Dispose(); throw; }
    }

    private async ValueTask<IMutualChannelPipe> AuthenticateHelperCoreAsync(Guid operationId, SecurityIdentifier sid,
        SafeProcessHandle caller, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var bServer = _platform.CreateServer(GetPipeName("B", operationId, sid), sid);
        IMutualChannelPipe? aClient = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await RunWhilePeerLivesAsync(async ct =>
            {
                aClient = await _platform.ConnectClientAsync(GetPipeName("A", operationId, sid), ct).ConfigureAwait(false);
                var nonce = new byte[32]; await aClient.ReadExactlyAsync(nonce, ct).ConfigureAwait(false);
                await bServer.WaitForConnectionAsync(ct).ConfigureAwait(false);
                _platform.AssertConnectedCaller(bServer.Handle, caller, sid, _callerTrust);
                await bServer.WriteExactlyAsync(nonce, ct).ConfigureAwait(false);
            }, caller, deadline.Token).ConfigureAwait(false);
            bServer.Dispose();
            return aClient!;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { aClient?.Dispose(); bServer.Dispose(); throw new TimeoutException("Timed out during the mutual machine command handshake."); }
        catch { aClient?.Dispose(); bServer.Dispose(); throw; }
    }


    public static string GetPipeName(string lane, Guid operationId, SecurityIdentifier sid)
    {
        if (lane is not ("A" or "B")) throw new ArgumentOutOfRangeException(nameof(lane));
        ValidateInitiatingSid(sid);
        if (operationId == Guid.Empty) throw new ArgumentException("Operation id must not be empty.", nameof(operationId));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid.Value!)))[..24];
        return $"Structura.Connector.Upgrade.Mutual.{lane}.{operationId:N}.{digest}";
    }

    private static void ValidateOperation(Guid operationId, SecurityIdentifier sid)
    {
        ValidateInitiatingSid(sid);
        if (operationId == Guid.Empty) throw new ArgumentException("Operation id must not be empty.", nameof(operationId));
    }

    internal static void ValidateInitiatingSid(SecurityIdentifier sid)
    {
        ArgumentNullException.ThrowIfNull(sid);
        var value = sid.Value;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Initiating account SID validation requires Windows.");
        if (string.IsNullOrEmpty(value) || !SidPattern.IsMatch(value) ||
            sid.IsWellKnown(WellKnownSidType.WorldSid) ||
            sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid) ||
            sid.IsWellKnown(WellKnownSidType.AnonymousSid) ||
            sid.IsWellKnown(WellKnownSidType.NetworkSid) ||
            sid.IsWellKnown(WellKnownSidType.BuiltinGuestsSid) ||
            IsGuestSid(sid) ||
            value.StartsWith("S-1-5-80-", StringComparison.Ordinal) ||
            value.StartsWith("S-1-5-82-", StringComparison.Ordinal) ||
            !IsUserAccountSid(sid))
            throw new ArgumentException("Initiating SID must be a specific, non-anonymous user account SID.", nameof(sid));
    }

    private static bool IsGuestSid(SecurityIdentifier sid) =>
        sid.Value?.EndsWith("-501", StringComparison.Ordinal) == true;

    private static bool IsUserAccountSid(SecurityIdentifier sid)
    {
        var sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);
        var name = new StringBuilder(1024);
        var domain = new StringBuilder(1024);
        uint nameLength = (uint)name.Capacity;
        uint domainLength = (uint)domain.Capacity;
        return LookupAccountSid(IntPtr.Zero, sidBytes, name, ref nameLength, domain, ref domainLength, out var sidNameUse) &&
            sidNameUse == 1;
    }

    [DllImport("advapi32.dll", EntryPoint = "LookupAccountSidW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupAccountSid(IntPtr systemName, byte[] sid, StringBuilder name, ref uint nameLength,
        StringBuilder domain, ref uint domainLength, out int sidNameUse);

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > MaximumTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout), $"Timeout must be positive and no greater than {MaximumTimeout}.");
    }

    private async Task RunWhilePeerLivesAsync(Func<CancellationToken, Task> handshake, SafeProcessHandle peer,
        CancellationToken deadlineToken)
    {
        using var workDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadlineToken);
        using var monitorDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadlineToken);
        var handshakeTask = handshake(workDeadline.Token);
        Task exitTask;
        try { exitTask = _platform.WaitForProcessExitAsync(peer, monitorDeadline.Token).AsTask(); }
        catch
        {
            workDeadline.Cancel();
            try { await handshakeTask.ConfigureAwait(false); } catch { }
            throw;
        }
        var completed = await Task.WhenAny(handshakeTask, exitTask).ConfigureAwait(false);
        if (completed == exitTask)
        {
            try { await exitTask.ConfigureAwait(false); } // Propagates timeout, cancellation, or monitor faults.
            finally
            {
                workDeadline.Cancel();
                try { await handshakeTask.ConfigureAwait(false); } catch { }
            }
            throw new SecurityException("The peer process exited during the one-shot mutual channel handshake.");
        }

        monitorDeadline.Cancel();
        try { await exitTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        await handshakeTask.ConfigureAwait(false);
    }
}

/// <summary>Only a protected, deployment-controlled source may implement this trust decision.</summary>
public interface ITrustedCallerImagePinSource
{
    void AssertTrustedCaller(SafeProcessHandle retainedCaller, SecurityIdentifier expectedUserSid);
}

internal sealed class UnavailableCallerImagePinSource : ITrustedCallerImagePinSource
{
    internal static readonly UnavailableCallerImagePinSource Instance = new();
    private UnavailableCallerImagePinSource() { }
    public void AssertTrustedCaller(SafeProcessHandle retainedCaller, SecurityIdentifier expectedUserSid) =>
        throw new SecurityException("Trusted caller image pin source is unavailable; mutual channel fails closed.");
}

internal interface IMutualMachineChannelPlatform
{
    IMutualChannelPipe CreateServer(string name, SecurityIdentifier userSid);
    ValueTask<IMutualChannelPipe> ConnectClientAsync(string name, CancellationToken cancellationToken);
    ValueTask WaitForProcessExitAsync(SafeProcessHandle process, CancellationToken cancellationToken);
    void AssertConnectedHelper(SafePipeHandle connectedPipe, SafeProcessHandle launchedHelper);
    void AssertConnectedCaller(SafePipeHandle connectedPipe, SafeProcessHandle retainedCaller,
        SecurityIdentifier expectedUserSid, ITrustedCallerImagePinSource callerTrust);
}

internal interface IMutualChannelPipe : IDisposable
{
    Stream Stream { get; }
    SafePipeHandle Handle { get; }
    ValueTask WaitForConnectionAsync(CancellationToken cancellationToken);
    ValueTask ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken);
    ValueTask WriteExactlyAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken);
}

/// <summary>An opaque, single-use caller endpoint; disposing it closes the underlying pipe handle.</summary>
public sealed class MutualChannelPipe : IDisposable
{
    internal MutualChannelPipe(IMutualChannelPipe inner) => Inner = inner ?? throw new ArgumentNullException(nameof(inner));
    internal IMutualChannelPipe Inner { get; }
    public void Dispose() => Inner.Dispose();
}

/// <summary>Authenticated transport capability. It carries no command API by itself.</summary>
public sealed class MutualMachineAuthenticatedSession : IDisposable
{
    private IMutualChannelPipe? _pipe;
    private readonly SafeProcessHandle _peer;
    private int _peerRefAdded;
    private int _commandBusy;
    private readonly Func<Func<CancellationToken, Task>, CancellationToken, Task> _runWithPeer;
    internal MutualMachineAuthenticatedSession(IMutualChannelPipe pipe, SafeProcessHandle peer,
        Func<Func<CancellationToken, Task>, CancellationToken, Task> runWithPeer)
    {
        _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
        _peer = peer ?? throw new ArgumentNullException(nameof(peer));
        _runWithPeer = runWithPeer ?? throw new ArgumentNullException(nameof(runWithPeer));
        var success = false;
        _peer.DangerousAddRef(ref success);
        _peerRefAdded = success ? 1 : 0;
    }
    internal SafeProcessHandle Peer => _peer;
    internal Stream Stream => (_pipe ?? throw new ObjectDisposedException(nameof(MutualMachineAuthenticatedSession))).Stream;
    internal IMutualChannelPipe Pipe => _pipe ?? throw new ObjectDisposedException(nameof(MutualMachineAuthenticatedSession));
    internal Task RunWithPeerAsync(Func<CancellationToken, Task> operation, CancellationToken token) => _runWithPeer(operation, token);
    internal void EnterCommandUse()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _pipe) is null, this);
        if (Interlocked.CompareExchange(ref _commandBusy, 1, 0) != 0)
            throw new InvalidOperationException("Concurrent use of an authenticated machine session is rejected.");
    }
    internal void ExitCommandUse() => Volatile.Write(ref _commandBusy, 0);
    public void Dispose()
    {
        Interlocked.Exchange(ref _pipe, null)?.Dispose();
        if (Interlocked.Exchange(ref _peerRefAdded, 0) != 0) _peer.DangerousRelease();
    }
}

internal sealed class WindowsMutualMachineChannelPlatform : IMutualMachineChannelPlatform
{
    internal static readonly WindowsMutualMachineChannelPlatform Instance = new();
    private WindowsMutualMachineChannelPlatform() { }
    public IMutualChannelPipe CreateServer(string name, SecurityIdentifier userSid) =>
        new WindowsMutualChannelPipe(WindowsMutualPipeFactory.Create(name, userSid));
    public async ValueTask<IMutualChannelPipe> ConnectClientAsync(string name, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Mutual machine channels require Windows.");
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new WindowsMutualChannelPipe(client);
        }
        catch { client.Dispose(); throw; }
    }
    public ValueTask WaitForProcessExitAsync(SafeProcessHandle process, CancellationToken cancellationToken) =>
        WindowsMutualNative.WaitForProcessExitAsync(process, cancellationToken);
    public void AssertConnectedHelper(SafePipeHandle connectedPipe, SafeProcessHandle launchedHelper)
    {
        var clientPid = WindowsMutualNative.GetPipeClientPid(connectedPipe);
        if (!WindowsMutualNative.IsSameRunningProcess(clientPid, launchedHelper) || !WindowsMutualNative.IsElevatedAdministrator(launchedHelper))
            throw new SecurityException("A pipe peer is not the retained elevated helper process.");
    }
    public void AssertConnectedCaller(SafePipeHandle connectedPipe, SafeProcessHandle retainedCaller,
        SecurityIdentifier expectedUserSid, ITrustedCallerImagePinSource callerTrust)
    {
        var clientPid = WindowsMutualNative.GetPipeClientPid(connectedPipe);
        if (!WindowsMutualNative.IsSameRunningProcess(clientPid, retainedCaller))
            throw new SecurityException("B pipe peer is not the retained original caller process.");
        WindowsMutualNative.AssertImpersonatedSid(connectedPipe, expectedUserSid);
        callerTrust.AssertTrustedCaller(retainedCaller, expectedUserSid);
    }
}

internal sealed class WindowsMutualChannelPipe : IMutualChannelPipe
{
    private readonly Stream _stream;
    public Stream Stream => _stream;
    private readonly NamedPipeServerStream? _server;
    public WindowsMutualChannelPipe(NamedPipeServerStream server) { _server = server; _stream = server; }
    public WindowsMutualChannelPipe(NamedPipeClientStream client) => _stream = client;
    public SafePipeHandle Handle => _server?.SafePipeHandle ?? throw new InvalidOperationException("Client handles are not valid for server peer PID queries.");
    public ValueTask WaitForConnectionAsync(CancellationToken cancellationToken) =>
        _server is null ? ValueTask.CompletedTask : new ValueTask(_server.WaitForConnectionAsync(cancellationToken));
    public async ValueTask ReadExactlyAsync(Memory<byte> destination, CancellationToken cancellationToken) =>
        await _stream.ReadExactlyAsync(destination, cancellationToken).ConfigureAwait(false);
    public async ValueTask WriteExactlyAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(source, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    public void Dispose() => _stream.Dispose();
}
