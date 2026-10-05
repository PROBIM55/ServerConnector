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
    private readonly string _directory;
    private readonly IDataProtector _protector;

    public FileNetBirdProviderStateStore(ValidatedNetBirdOptions options, IDataProtectionProvider protectionProvider)
    {
        _directory = options.StateDirectory;
        _protector = protectionProvider.CreateProtector("Connector.Access.NetBird.SetupKey.v1");
        Directory.CreateDirectory(_directory);
    }

    public async ValueTask<LockedNetBirdState> LockAsync(string deviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stem = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId))).ToLowerInvariant();
        var statePath = Path.Combine(_directory, stem + ".json");
        var lockPath = Path.Combine(_directory, stem + ".lock");
        FileStream lockStream;
        try
        {
            lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
        }
        catch (IOException exception)
        {
            throw new NetBirdProviderBusyException("NetBird device state is being reconciled by another dispatcher.", exception);
        }

        try
        {
            NetBirdProviderState? state = null;
            if (File.Exists(statePath))
            {
                await using var input = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
                state = await JsonSerializer.DeserializeAsync<NetBirdProviderState>(input, JsonOptions, cancellationToken)
                    ?? throw new InvalidDataException("NetBird provider state is empty.");
                if (state.SchemaVersion != 1 || !string.Equals(state.DeviceId, deviceId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("NetBird provider state identity or schema is invalid.");
                }
            }
            return new LockedNetBirdState(statePath, lockStream, state, JsonOptions);
        }
        catch
        {
            await lockStream.DisposeAsync();
            throw;
        }
    }

    public string ProtectSetupKey(string setupKey) => _protector.Protect(setupKey);
    public string UnprotectSetupKey(string protectedSetupKey) => _protector.Unprotect(protectedSetupKey);
}

internal sealed class LockedNetBirdState : IAsyncDisposable
{
    private readonly string _path;
    private readonly FileStream _lockStream;
    private readonly JsonSerializerOptions _jsonOptions;

    public LockedNetBirdState(string path, FileStream lockStream, NetBirdProviderState? state, JsonSerializerOptions jsonOptions)
    {
        _path = path;
        _lockStream = lockStream;
        State = state;
        _jsonOptions = jsonOptions;
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
            File.Move(temporary, _path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public ValueTask DisposeAsync() => _lockStream.DisposeAsync();
}

public sealed class NetBirdProviderBusyException : Exception
{
    public NetBirdProviderBusyException(string message, Exception? innerException = null) : base(message, innerException) { }
}

public sealed class NetBirdPeerNotReadyException : Exception
{
    public NetBirdPeerNotReadyException(string message) : base(message) { }
}

public sealed class NetBirdRevisionRejectedException : Exception
{
    public NetBirdRevisionRejectedException(string message) : base(message) { }
}
