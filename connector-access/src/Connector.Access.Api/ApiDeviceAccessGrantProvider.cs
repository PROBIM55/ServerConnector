using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connector.Access.Contracts;

namespace Connector.Access.Api;

public sealed class ApiDeviceAccessGrantProvider : IDeviceAccessGrantProvider, IConnectorApiAccessPolicyReader
{
    public const string Name = "api";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ApiDeviceAccessGrantProvider(ApiAccessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _directory = options.Validate();
        Directory.CreateDirectory(_directory);
    }

    public string ProviderName => Name;

    public ValueTask<DeviceAccessProviderReceipt> ApplyAsync(
        DeviceAccessProviderCommand command,
        CancellationToken cancellationToken) =>
        ExecuteAsync(Validate(command, DeviceAccessProviderCommandKind.Apply), cancellationToken);

    public ValueTask<DeviceAccessProviderReceipt> RevokeAsync(
        DeviceAccessProviderCommand command,
        CancellationToken cancellationToken) =>
        ExecuteAsync(Validate(command, DeviceAccessProviderCommandKind.Revoke), cancellationToken);

    public async ValueTask<DeviceAccessResult<ConnectorApiAccessPolicy>> GetCurrentAsync(
        AuthenticatedDevice device,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await ReadAsync(device.DeviceId, cancellationToken).ConfigureAwait(false);
            if (state is null || state.Kind != DeviceAccessProviderCommandKind.Apply ||
                state.Revision != device.AccessRevision ||
                !string.Equals(state.UserId, device.UserId, StringComparison.Ordinal) ||
                !string.Equals(state.CompanyId, device.CompanyId, StringComparison.Ordinal))
            {
                return DeviceAccessResult<ConnectorApiAccessPolicy>.Fail(
                    "api_policy_unavailable", "No current API policy is installed for this device.");
            }

            return DeviceAccessResult<ConnectorApiAccessPolicy>.Success(new ConnectorApiAccessPolicy(
                state.DeviceId, state.UserId, state.CompanyId, state.Revision,
                state.Modules, state.Resources));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return DeviceAccessResult<ConnectorApiAccessPolicy>.Fail(
                "api_policy_unavailable", "The installed API policy cannot be verified.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<DeviceAccessProviderReceipt> ExecuteAsync(
        DeviceAccessProviderCommand command,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await ReadAsync(command.DeviceId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (command.DesiredRevision < existing.Revision)
                    throw new ApiAccessRevisionRejectedException("A newer API policy is already installed.");
                if (command.DesiredRevision == existing.Revision)
                {
                    if (command.Kind != existing.Kind || !Matches(existing, command))
                        throw new ApiAccessRevisionRejectedException("The API policy revision is already fenced by different content or action.");
                    return Receipt(command);
                }
            }

            var state = new StoredApiPolicy(
                command.DeviceId, command.UserId, command.CompanyId, command.DesiredRevision,
                command.Kind,
                command.Kind == DeviceAccessProviderCommandKind.Apply ? command.Modules : [],
                command.Kind == DeviceAccessProviderCommandKind.Apply ? command.Resources : []);
            await WriteAsync(state, cancellationToken).ConfigureAwait(false);
            var readback = await ReadAsync(command.DeviceId, cancellationToken).ConfigureAwait(false);
            if (readback is null || !Matches(readback, command))
                throw new IOException("The API policy durable readback did not match the fenced command.");
            return Receipt(command);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<StoredApiPolicy?> ReadAsync(string deviceId, CancellationToken cancellationToken)
    {
        var path = StatePath(deviceId);
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        return await JsonSerializer.DeserializeAsync<StoredApiPolicy>(stream, Json, cancellationToken).ConfigureAwait(false)
               ?? throw new JsonException("API policy state is empty.");
    }

    private async ValueTask WriteAsync(StoredApiPolicy state, CancellationToken cancellationToken)
    {
        var path = StatePath(state.DeviceId);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, Json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string StatePath(string deviceId)
    {
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId))).ToLowerInvariant();
        return Path.Combine(_directory, name + ".json");
    }

    private static DeviceAccessProviderCommand Validate(DeviceAccessProviderCommand command, DeviceAccessProviderCommandKind kind)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!string.Equals(command.ProviderName, Name, StringComparison.OrdinalIgnoreCase) || command.Kind != kind ||
            string.IsNullOrWhiteSpace(command.CommandId) || string.IsNullOrWhiteSpace(command.DeviceId) ||
            string.IsNullOrWhiteSpace(command.UserId) || string.IsNullOrWhiteSpace(command.CompanyId) ||
            command.DesiredRevision <= 0 || command.Modules is null || command.Resources is null)
            throw new ArgumentException("The API provider command is invalid.");
        return command;
    }

    private static bool Matches(StoredApiPolicy state, DeviceAccessProviderCommand command) =>
        state.DeviceId == command.DeviceId && state.UserId == command.UserId && state.CompanyId == command.CompanyId &&
        state.Revision == command.DesiredRevision && state.Kind == command.Kind &&
        JsonSerializer.Serialize(state.Modules, Json) == JsonSerializer.Serialize(
            command.Kind == DeviceAccessProviderCommandKind.Apply ? command.Modules : [], Json) &&
        JsonSerializer.Serialize(state.Resources, Json) == JsonSerializer.Serialize(
            command.Kind == DeviceAccessProviderCommandKind.Apply ? command.Resources : [], Json);

    private static DeviceAccessProviderReceipt Receipt(DeviceAccessProviderCommand command) =>
        new(command.CommandId, command.DesiredRevision, command.Kind);

    private sealed record StoredApiPolicy(
        string DeviceId, string UserId, string CompanyId, long Revision,
        DeviceAccessProviderCommandKind Kind,
        IReadOnlyList<ModuleGrant> Modules,
        IReadOnlyList<ResourceGrant> Resources);
}

public sealed class ApiAccessRevisionRejectedException(string message) : InvalidOperationException(message);
