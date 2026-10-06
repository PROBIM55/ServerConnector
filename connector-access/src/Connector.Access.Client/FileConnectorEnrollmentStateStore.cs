using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Connector.Access.Client;

public sealed class FileConnectorEnrollmentStateStore
{
    private const long MaximumStateBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileConnectorEnrollmentStateStore(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        DirectoryPath = Path.GetFullPath(directoryPath);
        StateFilePath = Path.Combine(DirectoryPath, "connector-enrollment-v1.json");
    }

    public string DirectoryPath { get; }
    public string StateFilePath { get; }

    internal async ValueTask<StoredEnrollmentState?> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(StateFilePath))
            {
                return null;
            }

            var file = new FileInfo(StateFilePath);
            if (file.Length <= 0 || file.Length > MaximumStateBytes)
            {
                throw new ConnectorEnrollmentStateException("The connector enrollment state file has an invalid size.");
            }

            PersistedEnrollmentState persisted;
            try
            {
                await using var stream = new FileStream(
                    StateFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                persisted = await JsonSerializer.DeserializeAsync<PersistedEnrollmentState>(stream, JsonOptions, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new ConnectorEnrollmentStateException("The connector enrollment state file is empty.");
            }
            catch (ConnectorEnrollmentStateException)
            {
                throw;
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                throw new ConnectorEnrollmentStateException("The connector enrollment state file cannot be read safely.", exception);
            }

            ValidatePersisted(persisted);
            byte[] protectedKey;
            try
            {
                protectedKey = Convert.FromBase64String(persisted.ProtectedPrivateKeyPkcs8);
            }
            catch (FormatException exception)
            {
                throw new ConnectorEnrollmentStateException("The protected connector key is corrupt.", exception);
            }

            try
            {
                var entropy = GetEntropy(persisted.RequestId, persisted.ServiceBaseUri);
                try
                {
                    var privateKey = WindowsDpapi.UnprotectCurrentUser(protectedKey, entropy);
                    return new StoredEnrollmentState
                    {
                        SchemaVersion = persisted.SchemaVersion,
                        RequestId = persisted.RequestId,
                        ServiceBaseUri = persisted.ServiceBaseUri,
                        IssuerPinSha256 = persisted.IssuerPinSha256,
                        KeyAlgorithm = persisted.KeyAlgorithm,
                        PrivateKeyPkcs8 = privateKey,
                        CertificateSigningRequestPem = persisted.CertificateSigningRequestPem,
                        PublicKeySha256 = persisted.PublicKeySha256,
                        DeviceDisplayName = persisted.DeviceDisplayName,
                        Status = persisted.Status,
                        Response = persisted.Response,
                    };
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(entropy);
                }
            }
            catch (Exception exception) when (exception is CryptographicException or System.ComponentModel.Win32Exception)
            {
                throw new ConnectorEnrollmentStateException(
                    "The connector private key is corrupt or belongs to another Windows user.",
                    exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedKey);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask SaveAsync(StoredEnrollmentState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateRuntime(state);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var entropy = GetEntropy(state.RequestId, state.ServiceBaseUri);
            byte[] protectedKey;
            try
            {
                protectedKey = WindowsDpapi.ProtectCurrentUser(state.PrivateKeyPkcs8, entropy);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(entropy);
            }

            byte[] json;
            try
            {
                json = JsonSerializer.SerializeToUtf8Bytes(new PersistedEnrollmentState
                {
                    SchemaVersion = state.SchemaVersion,
                    RequestId = state.RequestId,
                    ServiceBaseUri = state.ServiceBaseUri,
                    IssuerPinSha256 = state.IssuerPinSha256,
                    KeyAlgorithm = state.KeyAlgorithm,
                    ProtectedPrivateKeyPkcs8 = Convert.ToBase64String(protectedKey),
                    CertificateSigningRequestPem = state.CertificateSigningRequestPem,
                    PublicKeySha256 = state.PublicKeySha256,
                    DeviceDisplayName = state.DeviceDisplayName,
                    Status = state.Status,
                    Response = state.Response,
                }, JsonOptions);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedKey);
            }

            temporaryPath = Path.Combine(DirectoryPath, $".{Path.GetFileName(StateFilePath)}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(StateFilePath))
            {
                File.Replace(temporaryPath, StateFilePath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, StateFilePath);
            }

            temporaryPath = null;
        }
        catch (ConnectorEnrollmentStateException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or System.ComponentModel.Win32Exception)
        {
            throw new ConnectorEnrollmentStateException("The connector enrollment state could not be saved atomically.", exception);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            _gate.Release();
        }
    }

    private static byte[] GetEntropy(Guid requestId, string serviceBaseUri) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"Connector.Access.Client|v1|{requestId:N}|{serviceBaseUri}"));

    private static void ValidatePersisted(PersistedEnrollmentState state)
    {
        if (state.SchemaVersion != StoredEnrollmentState.CurrentSchemaVersion ||
            state.RequestId == Guid.Empty ||
            !Uri.TryCreate(state.ServiceBaseUri, UriKind.Absolute, out var serviceUri) ||
            serviceUri.Scheme != Uri.UriSchemeHttps ||
            state.IssuerPinSha256.Length != 64 ||
            state.ProtectedPrivateKeyPkcs8.Length == 0 ||
            state.CertificateSigningRequestPem.Length is < 32 or > 32768 ||
            state.PublicKeySha256.Length != 64 ||
            state.DeviceDisplayName.Length is < 1 or > 128 ||
            !Enum.IsDefined(state.Status) ||
            (state.Status == StoredEnrollmentStatus.Completed) != (state.Response is not null))
        {
            throw new ConnectorEnrollmentStateException("The connector enrollment state violates its schema invariants.");
        }
    }

    private static void ValidateRuntime(StoredEnrollmentState state)
    {
        if (state.SchemaVersion != StoredEnrollmentState.CurrentSchemaVersion ||
            state.RequestId == Guid.Empty ||
            state.PrivateKeyPkcs8.Length == 0 ||
            (state.Status == StoredEnrollmentStatus.Completed) != (state.Response is not null))
        {
            throw new ConnectorEnrollmentStateException("Refusing to persist an invalid connector enrollment state.");
        }
    }

    private sealed class PersistedEnrollmentState
    {
        public int SchemaVersion { get; init; }
        public Guid RequestId { get; init; }
        public string ServiceBaseUri { get; init; } = string.Empty;
        public string IssuerPinSha256 { get; init; } = string.Empty;
        public ConnectorEnrollmentKeyAlgorithm KeyAlgorithm { get; init; }
        public string ProtectedPrivateKeyPkcs8 { get; init; } = string.Empty;
        public string CertificateSigningRequestPem { get; init; } = string.Empty;
        public string PublicKeySha256 { get; init; } = string.Empty;
        public string DeviceDisplayName { get; init; } = string.Empty;
        public StoredEnrollmentStatus Status { get; init; }
        public Connector.Access.Contracts.DeviceEnrollmentResponse? Response { get; init; }
    }
}
