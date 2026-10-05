using System.Security.Principal;
using Connector.Upgrade.Core;
using Connector.Upgrade.WindowsHost;
using Connector.Upgrade.WindowsUserJournal;

namespace Connector.Upgrade.OriginalUserSession;

/// <summary>Creates an upgrade executor bound to the initiating interactive Windows identity.</summary>
public static class WindowsOriginalUserUpgradeSessionFactory
{
    public static WindowsOriginalUserUpgradeSession Create(
        WindowsHostSession originalUser,
        IUpgradePorts ports)
    {
        ArgumentNullException.ThrowIfNull(originalUser);
        ArgumentNullException.ThrowIfNull(ports);
        originalUser.Validate();
        if (ports is not IUpgradeSessionBoundPorts sessionBoundPorts)
            throw new UnauthorizedAccessException("The upgrade adapter is not bound to the trusted original-user session.");
        sessionBoundPorts.AssertSessionMatches(originalUser.OperationId, originalUser.InitiatingUserSid);
        var identity = ReadCurrentIdentity();
        EnsureMatches(originalUser.InitiatingUserSid, identity);

        // Identity validation deliberately precedes construction: the store constructor probes
        // LocalAppData and creates its protected journal directory.
        var store = new WindowsUserUpgradeJournalStore(originalUser.InitiatingUserSid);
        try
        {
            return new WindowsOriginalUserUpgradeSession(
                new UpgradeOrchestrator(ports, store, boundRunId: Guid.ParseExact(originalUser.OperationId, "N")), store,
                originalUser.InitiatingUserSid, identity.AccountName, ReadCurrentIdentity);
        }
        catch
        {
            store.Dispose();
            throw;
        }
    }

    internal static WindowsOriginalUserUpgradeSession CreateForTesting(
        string originalUserSid,
        IUpgradePorts ports,
        Func<WindowsOriginalUserIdentity> identityProvider,
        Func<IUpgradeJournalStore> journalFactory)
    {
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(identityProvider);
        ArgumentNullException.ThrowIfNull(journalFactory);
        var identity = identityProvider();
        EnsureMatches(originalUserSid, identity);
        var store = journalFactory();
        try
        {
            return new WindowsOriginalUserUpgradeSession(
                new UpgradeOrchestrator(ports, store), store as IDisposable,
                originalUserSid, identity.AccountName, identityProvider);
        }
        catch
        {
            (store as IDisposable)?.Dispose();
            throw;
        }
    }

    private static WindowsOriginalUserIdentity ReadCurrentIdentity()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Original-user upgrade composition requires Windows.");
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var sid = identity.User?.Value;
        var account = identity.Name;
        if (string.IsNullOrWhiteSpace(sid) || string.IsNullOrWhiteSpace(account))
            throw new UnauthorizedAccessException("The current Windows identity has no SID or account name.");
        return new WindowsOriginalUserIdentity(sid, account);
    }

    private static void EnsureMatches(string expectedSid, WindowsOriginalUserIdentity actual)
    {
        if (string.IsNullOrWhiteSpace(expectedSid) ||
            !StringComparer.OrdinalIgnoreCase.Equals(expectedSid, actual.Sid))
            throw new UnauthorizedAccessException("The current Windows account is not the original upgrade user.");
    }
}

internal sealed record WindowsOriginalUserIdentity(string Sid, string AccountName);

/// <summary>Owns the user-scoped journal and serializes execution, recovery, and disposal.</summary>
public sealed class WindowsOriginalUserUpgradeSession : IDisposable
{
    private readonly UpgradeOrchestrator _orchestrator;
    private readonly IDisposable? _journalLifetime;
    private readonly string _sid;
    private readonly string _accountName;
    private readonly Func<WindowsOriginalUserIdentity> _identityProvider;
    private readonly object _lifecycle = new();
    private int _active;
    private bool _disposed;

    internal WindowsOriginalUserUpgradeSession(
        UpgradeOrchestrator orchestrator,
        IDisposable? journalLifetime,
        string sid,
        string accountName,
        Func<WindowsOriginalUserIdentity> identityProvider)
    {
        _orchestrator = orchestrator;
        _journalLifetime = journalLifetime;
        _sid = sid;
        _accountName = accountName;
        _identityProvider = identityProvider;
    }

    public ValueTask<UpgradeExecutionResult> ExecuteAsync(
        OneTimePlatformToken token,
        CancellationToken cancellationToken = default) =>
        RunAsync(() => _orchestrator.ExecuteAsync(token, cancellationToken));

    public ValueTask<UpgradeExecutionResult> RecoverInterruptedAsync(
        CancellationToken cancellationToken = default) =>
        RunAsync(() => _orchestrator.RecoverInterruptedAsync(cancellationToken));

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            while (_active != 0) Monitor.Wait(_lifecycle);
        }
        _journalLifetime?.Dispose();
    }

    private async ValueTask<UpgradeExecutionResult> RunAsync(
        Func<ValueTask<UpgradeExecutionResult>> operation)
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var current = _identityProvider();
            if (!StringComparer.OrdinalIgnoreCase.Equals(_sid, current.Sid) ||
                !StringComparer.OrdinalIgnoreCase.Equals(_accountName, current.AccountName))
                throw new UnauthorizedAccessException("The current Windows account differs from the original upgrade user.");
            _active++;
        }

        try { return await operation().ConfigureAwait(false); }
        finally
        {
            lock (_lifecycle)
            {
                _active--;
                if (_active == 0) Monitor.PulseAll(_lifecycle);
            }
        }
    }
}
