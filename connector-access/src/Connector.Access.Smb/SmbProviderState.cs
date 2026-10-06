using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connector.Access;
using Microsoft.AspNetCore.DataProtection;

namespace Connector.Access.Smb;

internal sealed class SmbProviderState
{
    public int SchemaVersion { get; init; } = 1;
    public required string DeviceId { get; init; }
    public required string UserId { get; init; }
    public required string CompanyId { get; init; }
    public required string LocalUserName { get; init; }
    public string? LocalAccountAuthority { get; set; }
    public string? ProtectedPassword { get; set; }
    public string? LocalUserSid { get; set; }
    public required string HighestCommandId { get; set; }
    public long HighestRevision { get; set; }
    public DeviceAccessProviderCommandKind HighestKind { get; set; }
    public IReadOnlyList<SmbHelperGrant> DesiredGrants { get; set; } = [];
    public long AppliedRevision { get; set; }
    public DeviceAccessProviderCommandKind? AppliedKind { get; set; }
    public IReadOnlyList<SmbHelperResourceObservation> AppliedResources { get; set; } = [];
}

internal sealed class FileSmbProviderStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _directory;
    private readonly IDataProtector _protector;

    public FileSmbProviderStateStore(ValidatedSmbProviderOptions options, IDataProtectionProvider protectionProvider)
    {
        _directory = options.StateDirectory;
        Directory.CreateDirectory(_directory);
        _protector = protectionProvider.CreateProtector("Connector.Access.Smb.Password.v1");
    }

    public string Protect(string password) => _protector.Protect(password);
    public string Unprotect(string protectedPassword) => _protector.Unprotect(protectedPassword);

    public async ValueTask<LockedSmbState> LockAsync(string deviceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stem = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId))).ToLowerInvariant();
        var statePath = Path.Combine(_directory, stem + ".json");
        FileStream gate;
        try
        {
            gate = new FileStream(statePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
        }
        catch (IOException exception)
        {
            throw new SmbProviderBusyException("SMB state is being reconciled by another dispatcher.", exception);
        }
        try
        {
            SmbProviderState? state = null;
            if (File.Exists(statePath))
            {
                await using var input = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
                state = await JsonSerializer.DeserializeAsync<SmbProviderState>(input, JsonOptions, cancellationToken)
                    ?? throw new InvalidDataException("SMB provider state is empty.");
                if (state.SchemaVersion != 1 || !string.Equals(state.DeviceId, deviceId, StringComparison.Ordinal))
                    throw new InvalidDataException("SMB provider state identity or schema is invalid.");
            }
            return new LockedSmbState(statePath, gate, state);
        }
        catch
        {
            await gate.DisposeAsync();
            throw;
        }
    }

    internal sealed class LockedSmbState(string path, FileStream gate, SmbProviderState? state) : IAsyncDisposable
    {
        public SmbProviderState? State { get; set; } = state;

        public async ValueTask SaveAsync(CancellationToken cancellationToken)
        {
            if (State is null) throw new InvalidOperationException("SMB provider state is absent.");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(output, State, JsonOptions, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        public ValueTask DisposeAsync() => gate.DisposeAsync();
    }
}

public sealed class SmbProviderBusyException(string message, Exception? innerException = null) : Exception(message, innerException);
public sealed class SmbRevisionRejectedException(string message) : Exception(message);
public sealed class SmbHelperProtocolException : Exception
{
    public SmbHelperProtocolException(string message) : base(message) { }
    public SmbHelperProtocolException(string message, Exception innerException) : base(message, innerException) { }
}
