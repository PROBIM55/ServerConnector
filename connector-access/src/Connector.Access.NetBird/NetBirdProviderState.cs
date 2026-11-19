using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connector.Access;
using Microsoft.AspNetCore.DataProtection;

namespace Connector.Access.NetBird;

internal sealed class NetBirdProviderState
{
    public int SchemaVersion { get; init; } = 1;
    public required string DeviceId { get; init; }
    public required string UserId { get; set; }
    public required string CompanyId { get; set; }
    public long HighestRevision { get; set; }
    public DeviceAccessProviderCommandKind HighestKind { get; set; }
    public long AppliedRevision { get; set; }
    public DeviceAccessProviderCommandKind? AppliedKind { get; set; }
    public string? BootstrapGroupId { get; set; }
    public string? SetupKeyId { get; set; }
    public string? ProtectedSetupKey { get; set; }
    public DateTimeOffset? SetupKeyExpiresAtUtc { get; set; }
    public string? PeerId { get; set; }
    public string? PeerIpv4 { get; set; }
    public string? PeerIpv6 { get; set; }
    public string? PolicyId { get; set; }
    public NetBirdPolicyWrite? AppliedPolicy { get; set; }
}

internal sealed class FileNetBirdProviderStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly TimeSpan MaximumLockPollInterval = TimeSpan.FromMilliseconds(200);
    private const int SnapshotReadAttempts = 20;
    private readonly string _directory;
    private readonly IDataProtector _protector;
    private readonly TimeSpan _lockTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _localLocks = new(StringComparer.Ordinal);

    public FileNetBirdProviderStateStore(
        ValidatedNetBirdOptions options,
        IDataProtectionProvider protectionProvider,
        TimeProvider? timeProvider = null)
    {
        _directory = options.StateDirectory;
        _protector = protectionProvider.CreateProtector("Connector.Access.NetBird.SetupKey.v1");
        _lockTimeout = options.StateLockTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Directory.CreateDirectory(_directory);
    }

    // Exclusive per-device lock for reconcilers. Waits up to the configured
    // timeout: in-process waiters queue on a semaphore, another process holding
    // the lock file is polled with a short backoff.
    public async ValueTask<LockedNetBirdState> LockAsync(string deviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (statePath, lockPath) = Paths(deviceId);
        var started = _timeProvider.GetTimestamp();
        var local = _localLocks.GetOrAdd(deviceId, static _ => new SemaphoreSlim(1, 1));
        if (!await local.WaitAsync(_lockTimeout, cancellationToken))
        {
            throw new NetBirdProviderBusyException("NetBird device state is being reconciled by another dispatcher.");
        }

        FileStream? lockStream = null;
        try
        {
            var delay = TimeSpan.FromMilliseconds(10);
            while (true)
            {
                try
                {
                    lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
                    break;
                }
                catch (IOException exception)
                {
                    if (_timeProvider.GetElapsedTime(started) + delay > _lockTimeout)
                    {
                        throw new NetBirdProviderBusyException("NetBird device state is being reconciled by another dispatcher.", exception);
                    }
                }

                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaximumLockPollInterval.Ticks));
            }

            var state = await ReadStateAsync(statePath, deviceId, FileShare.Read, cancellationToken);
            var locked = new LockedNetBirdState(statePath, lockStream, state, JsonOptions, () => ReleaseLocal(deviceId, local));
            lockStream = null;
            return locked;
        }
        catch
        {
            if (lockStream is not null) await lockStream.DisposeAsync();
            ReleaseLocal(deviceId, local);
            throw;
        }
    }

    internal int LocalLockCount => _localLocks.Count;

    // Idle per-device semaphores are dropped so the map does not grow with the
    // number of devices ever seen. A waiter that raced the removal may create
    // a fresh semaphore; the exclusive lock file still serializes reconcilers.
    private void ReleaseLocal(string deviceId, SemaphoreSlim local)
    {
        local.Release();
        if (local.CurrentCount == 1)
        {
            _localLocks.TryRemove(new KeyValuePair<string, SemaphoreSlim>(deviceId, local));
        }
    }

    // Lock-free read for HTTP readers. Every save atomically replaces the
    // whole file, so a reader sees either the previous or the next committed
    // state and never waits for a reconciler that is calling NetBird.
    public async ValueTask<NetBirdProviderState?> ReadSnapshotAsync(string deviceId, CancellationToken cancellationToken)
    {
        var (statePath, _) = Paths(deviceId);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ReadStateAsync(statePath, deviceId, FileShare.ReadWrite | FileShare.Delete, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException &&
                                              exception is not FileNotFoundException and not DirectoryNotFoundException)
            {
                if (attempt >= SnapshotReadAttempts)
                {
                    throw new NetBirdProviderBusyException("NetBird device state is being replaced; retry shortly.", exception);
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5 * attempt), cancellationToken);
        }
    }

    private static async ValueTask<NetBirdProviderState?> ReadStateAsync(
        string statePath,
        string deviceId,
        FileShare share,
        CancellationToken cancellationToken)
    {
        NetBirdProviderState state;
        try
        {
            await using var input = new FileStream(statePath, FileMode.Open, FileAccess.Read, share, 8192, FileOptions.Asynchronous);
            state = await JsonSerializer.DeserializeAsync<NetBirdProviderState>(input, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("NetBird provider state is empty.");
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        if (state.SchemaVersion != 1 || !string.Equals(state.DeviceId, deviceId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("NetBird provider state identity or schema is invalid.");
        }
        return state;
    }

    private (string StatePath, string LockPath) Paths(string deviceId)
    {
        var stem = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId))).ToLowerInvariant();
        return (Path.Combine(_directory, stem + ".json"), Path.Combine(_directory, stem + ".lock"));
    }

    public string ProtectSetupKey(string setupKey) => _protector.Protect(setupKey);
    public string UnprotectSetupKey(string protectedSetupKey) => _protector.Unprotect(protectedSetupKey);
}

internal sealed class LockedNetBirdState : IAsyncDisposable
{
    private const int ReplaceAttempts = 40;
    private readonly string _path;
    private readonly FileStream _lockStream;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly Action? _release;
    private int _disposed;

    public LockedNetBirdState(
        string path,
        FileStream lockStream,
        NetBirdProviderState? state,
        JsonSerializerOptions jsonOptions,
        Action? release = null)
    {
        _path = path;
        _lockStream = lockStream;
        State = state;
        _jsonOptions = jsonOptions;
        _release = release;
    }

    public NetBirdProviderState? State { get; set; }

    public async ValueTask SaveAsync(CancellationToken cancellationToken)
    {
        if (State is null) throw new InvalidOperationException("There is no NetBird state to save.");
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, State, _jsonOptions, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            // A lock-free reader may hold the previous file open for a few
            // milliseconds; retry the atomic replace instead of failing.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temporary, _path, true);
                    break;
                }
                catch (Exception exception) when (attempt < ReplaceAttempts &&
                                                  exception is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(5 * Math.Min(attempt, 10)), cancellationToken);
                }
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await _lockStream.DisposeAsync();
        }
        finally
        {
            _release?.Invoke();
        }
    }
}

public sealed class NetBirdProviderBusyException : DeviceAccessProviderBusyException
{
    public NetBirdProviderBusyException(string message, Exception? innerException = null) : base(message, innerException) { }
}

public sealed class NetBirdPeerNotReadyException : DeviceAccessProviderNotReadyException
{
    public NetBirdPeerNotReadyException(string message) : base(message) { }
}

public sealed class NetBirdRevisionRejectedException : Exception
{
    public NetBirdRevisionRejectedException(string message) : base(message) { }
}
