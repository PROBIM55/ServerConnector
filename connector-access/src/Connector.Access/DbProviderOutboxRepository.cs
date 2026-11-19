using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connector.Access.Contracts;

namespace Connector.Access;

internal sealed record StoredProviderState(
    string ProviderName,
    long DesiredRevision,
    long AppliedRevision,
    string DesiredAction,
    string Status,
    string? LastError);

internal sealed record StoredProviderCommand(
    string CommandId,
    string LeaseId,
    string ProviderName,
    DeviceAccessProviderCommandKind Kind,
    string DeviceId,
    string UserId,
    string CompanyId,
    long DesiredRevision,
    IReadOnlyList<ModuleGrant> Modules,
    IReadOnlyList<ResourceGrant> Resources,
    int AttemptCount = 0,
    DateTimeOffset CreatedAtUtc = default);

internal sealed record ProviderPayload(
    string UserId,
    string CompanyId,
    IReadOnlyList<ModuleGrant> Modules,
    IReadOnlyList<ResourceGrant> Resources);

internal enum DispatchFenceStatus { Ready, Superseded, Busy }

internal sealed class DbProviderOutboxRepository
{
    private const int SerializationRetries = 3;
    private readonly IDeviceAccessDbConnectionFactory _connections;
    private readonly ConnectorAccessDispatchSignal? _signal;
    private readonly DeviceAccessOptions _options;

    public DbProviderOutboxRepository(
        IDeviceAccessDbConnectionFactory connections,
        ConnectorAccessDispatchSignal? signal = null,
        DeviceAccessOptions? options = null)
    {
        _connections = connections;
        _signal = signal;
        _options = options ?? new DeviceAccessOptions();
    }

    public async ValueTask<IReadOnlyList<StoredProviderState>> EnsureApplyDesiredAsync(
        StoredDevice device,
        PlatformAccessSnapshot access,
        IReadOnlyCollection<string> providerNames,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new ProviderPayload(
            device.UserId,
            device.CompanyId,
            access.Modules,
            access.Resources));
        var (enqueued, rearmed) = await RetrySerializationAsync(async () =>
        {
            await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            await UpdateDeviceDesiredRevisionAsync(connection, transaction, device.DeviceId, access.AccessRevision, now, cancellationToken);
            var enqueuedNow = new List<string>();
            foreach (var providerName in providerNames)
            {
                if (await UpsertStateAndCommandAsync(
                        connection,
                        transaction,
                        device.DeviceId,
                        providerName,
                        DeviceAccessProviderCommandKind.Apply,
                        access.AccessRevision,
                        payload,
                        now,
                        cancellationToken))
                {
                    enqueuedNow.Add(providerName);
                }
            }

            var rearmedNow = await RearmAppliesAsync(connection, transaction, device.DeviceId, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (enqueuedNow, rearmedNow);
        }, cancellationToken);
        _signal?.Notify(rearmed > 0 ? providerNames : (IEnumerable<string>)enqueued);
        return await ReadStatesAsync(device.DeviceId, cancellationToken);
    }

    // Called for the device's own authenticated vpn/state and vpn/bootstrap
    // reads: the client waits there (not on /profile) while its VPN grant is
    // being applied, so these reads also re-arm the current apply.
    public async ValueTask RearmAppliesAsync(
        string deviceId,
        IReadOnlyCollection<string> providerNames,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var rearmed = await RetrySerializationAsync(async () =>
        {
            await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            var count = await RearmAppliesAsync(connection, transaction, deviceId, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return count;
        }, cancellationToken);
        if (rearmed > 0) _signal?.Notify(providerNames);
    }

    // Re-arms only the device's current-revision apply commands that are not
    // leased: a parked one after it has been parked for the revive interval
    // (fresh failure count and age), a waiting one (retry scheduled in the
    // future) at most once per WaitRearmGap since its last attempt. A waiting
    // one keeps created_at_utc, so its pause keeps growing with its age.
    private async ValueTask<int> RearmAppliesAsync(
        DbConnection connection,
        DbTransaction transaction,
        string deviceId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE connector_access_provider_outbox
            SET status = 'pending',
                attempt_count = CASE WHEN last_error LIKE 'dead_letter:%' THEN 0 ELSE attempt_count END,
                last_error = CASE WHEN last_error LIKE 'dead_letter:%' THEN NULL ELSE last_error END,
                available_at_utc = NULL,
                dispatch_lease_id = NULL,
                lease_until_utc = NULL,
                created_at_utc = CASE WHEN last_error LIKE 'dead_letter:%' THEN @now ELSE created_at_utc END,
                updated_at_utc = @now
            WHERE device_id = @device_id
              AND command_kind = 'apply'
              AND status IN ('pending', 'error')
              AND (lease_until_utc IS NULL OR lease_until_utc <= @now)
              AND EXISTS (
                  SELECT 1 FROM connector_access_provider_state AS state
                  WHERE state.device_id = connector_access_provider_outbox.device_id
                    AND state.provider_name = connector_access_provider_outbox.provider_name
                    AND state.desired_revision = connector_access_provider_outbox.desired_revision
                    AND state.desired_action = 'apply')
              AND ((last_error LIKE 'dead_letter:%' AND updated_at_utc <= @revive_before)
                   OR ((last_error IS NULL OR last_error NOT LIKE 'dead_letter:%')
                       AND available_at_utc > @now
                       AND updated_at_utc <= @wait_rearm_before))
            """;
        Add(command, "@device_id", deviceId);
        Add(command, "@now", now, DbType.DateTimeOffset);
        Add(command, "@revive_before", now.Subtract(_options.ProviderDeadLetterReviveInterval), DbType.DateTimeOffset);
        Add(command, "@wait_rearm_before", now.Subtract(ProviderRetryPolicy.WaitRearmGap), DbType.DateTimeOffset);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<bool> RevokeAndEnqueueAsync(
        StoredDevice device,
        IReadOnlyCollection<string> providerNames,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using (var revoke = connection.CreateCommand())
        {
            revoke.Transaction = transaction;
            revoke.CommandText = """
                UPDATE connector_access_devices
                SET revoked_at_utc = @now,
                    enrollment_status = 'revoked',
                    desired_revision = desired_revision + 1,
                    updated_at_utc = @now
                WHERE device_id = @device_id
                  AND company_id = @company_id
                  AND revoked_at_utc IS NULL
                """;
            Add(revoke, "@now", now, DbType.DateTimeOffset);
            Add(revoke, "@device_id", device.DeviceId);
            Add(revoke, "@company_id", device.CompanyId);
            if (await revoke.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return false;
            }
        }

        long revokeRevision;
        await using (var revision = connection.CreateCommand())
        {
            revision.Transaction = transaction;
            revision.CommandText = "SELECT desired_revision FROM connector_access_devices WHERE device_id = @device_id";
            Add(revision, "@device_id", device.DeviceId);
            revokeRevision = Convert.ToInt64(await revision.ExecuteScalarAsync(cancellationToken));
        }
        var payload = JsonSerializer.Serialize(new ProviderPayload(
            device.UserId,
            device.CompanyId,
            Array.Empty<ModuleGrant>(),
            Array.Empty<ResourceGrant>()));
        foreach (var providerName in providerNames)
        {
            await UpsertStateAndCommandAsync(
                connection,
                transaction,
                device.DeviceId,
                providerName,
                DeviceAccessProviderCommandKind.Revoke,
                revokeRevision,
                payload,
                now,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        _signal?.Notify(providerNames);
        return true;
    }

    public ValueTask<StoredProviderCommand?> TryClaimNextAsync(
        string leaseId,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        CancellationToken cancellationToken,
        string? providerName = null) =>
        RetrySerializationAsync(
            () => TryClaimNextOnceAsync(leaseId, now, leaseUntil, providerName, cancellationToken),
            cancellationToken);

    private async ValueTask<StoredProviderCommand?> TryClaimNextOnceAsync(
        string leaseId,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        string? providerName,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        string? commandId;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            // Only the current desired revision of a (device, provider) pair is
            // claimable, and the state lease admits one dispatch per pair, so
            // per-provider lanes keep per-device revision ordering. Revokes go
            // first, then commands that never failed; parked applies are skipped.
            var providerFilter = providerName is null ? string.Empty : "AND outbox.provider_name = @provider_name";
            select.CommandText = $"""
                SELECT outbox.command_id
                FROM connector_access_provider_outbox AS outbox
                INNER JOIN connector_access_provider_state AS state
                    ON state.device_id = outbox.device_id
                   AND state.provider_name = outbox.provider_name
                   AND state.desired_revision = outbox.desired_revision
                   AND state.desired_action = outbox.command_kind
                WHERE outbox.status IN ('pending', 'error', 'processing')
                  {providerFilter}
                  AND (outbox.last_error IS NULL OR outbox.last_error NOT LIKE 'dead_letter:%')
                  AND (outbox.available_at_utc IS NULL OR outbox.available_at_utc <= @now)
                  AND (outbox.lease_until_utc IS NULL OR outbox.lease_until_utc <= @now)
                  AND (state.dispatch_lease_until_utc IS NULL OR state.dispatch_lease_until_utc <= @now)
                ORDER BY CASE
                             WHEN outbox.command_kind = 'revoke' THEN 0
                             WHEN outbox.attempt_count = 0 AND outbox.last_error IS NULL THEN 1
                             ELSE 2 END,
                         COALESCE(outbox.available_at_utc, outbox.created_at_utc),
                         outbox.created_at_utc,
                         outbox.command_id
                LIMIT 1
                """;
            Add(select, "@now", now, DbType.DateTimeOffset);
            if (providerName is not null)
            {
                Add(select, "@provider_name", providerName);
            }
            commandId = await select.ExecuteScalarAsync(cancellationToken) as string;
        }

        if (commandId is null)
        {
            return null;
        }

        await using (var claim = connection.CreateCommand())
        {
            claim.Transaction = transaction;
            claim.CommandText = """
                UPDATE connector_access_provider_outbox
                SET status = 'processing',
                    dispatch_lease_id = @lease_id,
                    lease_until_utc = @lease_until,
                    last_error = NULL,
                    attempt_count = CASE
                        WHEN @dead_letter_attempts > 0 AND attempt_count >= @dead_letter_attempts THEN 1
                        ELSE attempt_count + 1 END,
                    updated_at_utc = @now
                WHERE command_id = @command_id
                  AND status IN ('pending', 'error', 'processing')
                  AND (lease_until_utc IS NULL OR lease_until_utc <= @now)
                """;
            Add(claim, "@lease_until", leaseUntil, DbType.DateTimeOffset);
            // A row reaching a claim with a count at the limit was never parked
            // (parking is marked in last_error): it was counted by an older
            // release that retried forever, so its failure count starts over.
            Add(claim, "@dead_letter_attempts", _options.ProviderDeadLetterAttempts, DbType.Int32);
            Add(claim, "@lease_id", leaseId);
            Add(claim, "@now", now, DbType.DateTimeOffset);
            Add(claim, "@command_id", commandId);
            if (await claim.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return null;
            }
        }

        StoredProviderCommand command;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT command_id, provider_name, command_kind, device_id,
                       desired_revision, payload_json, attempt_count, created_at_utc
                FROM connector_access_provider_outbox
                WHERE command_id = @command_id
                """;
            Add(read, "@command_id", commandId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var payload = JsonSerializer.Deserialize<ProviderPayload>(reader.GetString(5))
                ?? throw new DataException("Provider outbox payload is empty.");
            command = new StoredProviderCommand(
                reader.GetString(0),
                leaseId,
                reader.GetString(1),
                reader.GetString(2) == "apply" ? DeviceAccessProviderCommandKind.Apply : DeviceAccessProviderCommandKind.Revoke,
                reader.GetString(3),
                payload.UserId,
                payload.CompanyId,
                reader.GetInt64(4),
                payload.Modules,
                payload.Resources,
                Convert.ToInt32(reader.GetValue(6), System.Globalization.CultureInfo.InvariantCulture),
                DbDeviceAccessRepository.ReadDateTimeOffset(reader, 7));
        }

        await transaction.CommitAsync(cancellationToken);
        return command;
    }

    public ValueTask<DispatchFenceStatus> TryBeginDispatchAsync(
        StoredProviderCommand command,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        CancellationToken cancellationToken) =>
        RetrySerializationAsync(
            () => TryBeginDispatchOnceAsync(command, now, leaseUntil, cancellationToken),
            cancellationToken);

    private async ValueTask<DispatchFenceStatus> TryBeginDispatchOnceAsync(
        StoredProviderCommand command,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using (var fence = connection.CreateCommand())
        {
            fence.Transaction = transaction;
            fence.CommandText = """
                UPDATE connector_access_provider_state
                SET dispatch_lease_id = @lease_id,
                    dispatch_lease_until_utc = @lease_until,
                    updated_at_utc = @now
                WHERE device_id = @device_id
                  AND provider_name = @provider_name
                  AND desired_revision = @revision
                  AND desired_action = @action
                  AND (dispatch_lease_until_utc IS NULL OR dispatch_lease_until_utc <= @now)
                """;
            Add(fence, "@lease_id", command.LeaseId);
            Add(fence, "@lease_until", leaseUntil, DbType.DateTimeOffset);
            Add(fence, "@now", now, DbType.DateTimeOffset);
            Add(fence, "@device_id", command.DeviceId);
            Add(fence, "@provider_name", command.ProviderName);
            Add(fence, "@revision", command.DesiredRevision, DbType.Int64);
            Add(fence, "@action", command.Kind == DeviceAccessProviderCommandKind.Apply ? "apply" : "revoke");
            if (await fence.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                await transaction.CommitAsync(cancellationToken);
                return DispatchFenceStatus.Ready;
            }
        }

        var isCurrent = false;
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = """
                SELECT 1 FROM connector_access_provider_state
                WHERE device_id = @device_id
                  AND provider_name = @provider_name
                  AND desired_revision = @revision
                  AND desired_action = @action
                """;
            Add(current, "@device_id", command.DeviceId);
            Add(current, "@provider_name", command.ProviderName);
            Add(current, "@revision", command.DesiredRevision, DbType.Int64);
            Add(current, "@action", command.Kind == DeviceAccessProviderCommandKind.Apply ? "apply" : "revoke");
            isCurrent = await current.ExecuteScalarAsync(cancellationToken) is not null;
        }

        await using (var releaseClaim = connection.CreateCommand())
        {
            releaseClaim.Transaction = transaction;
            releaseClaim.CommandText = """
                UPDATE connector_access_provider_outbox
                SET status = @status,
                    dispatch_lease_id = NULL,
                    lease_until_utc = NULL,
                    available_at_utc = @available_at,
                    last_error = @error,
                    attempt_count = CASE WHEN attempt_count > 0 THEN attempt_count - 1 ELSE 0 END,
                    updated_at_utc = @now
                WHERE command_id = @command_id
                  AND status = 'processing'
                  AND dispatch_lease_id = @lease_id
                """;
            Add(releaseClaim, "@status", isCurrent ? "pending" : "error");
            Add(releaseClaim, "@available_at", isCurrent ? leaseUntil : DBNull.Value, DbType.DateTimeOffset);
            Add(releaseClaim, "@error", isCurrent ? "dispatch_busy" : "superseded");
            Add(releaseClaim, "@now", now, DbType.DateTimeOffset);
            Add(releaseClaim, "@command_id", command.CommandId);
            Add(releaseClaim, "@lease_id", command.LeaseId);
            await releaseClaim.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return isCurrent ? DispatchFenceStatus.Busy : DispatchFenceStatus.Superseded;
    }

    public async ValueTask CompleteAsync(
        StoredProviderCommand command,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await RetrySerializationAsync(async () =>
        {
            await CompleteOnceAsync(command, now, cancellationToken);
            return true;
        }, cancellationToken);

    private async ValueTask CompleteOnceAsync(
        StoredProviderCommand command,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using (var outbox = connection.CreateCommand())
        {
            outbox.Transaction = transaction;
            outbox.CommandText = """
                UPDATE connector_access_provider_outbox
                SET status = 'applied', dispatch_lease_id = NULL, lease_until_utc = NULL,
                    last_error = NULL, updated_at_utc = @now
                WHERE command_id = @command_id AND status = 'processing'
                  AND dispatch_lease_id = @lease_id
                """;
            Add(outbox, "@now", now, DbType.DateTimeOffset);
            Add(outbox, "@command_id", command.CommandId);
            Add(outbox, "@lease_id", command.LeaseId);
            if (await outbox.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return;
            }
        }

        await using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = """
                UPDATE connector_access_provider_state
                SET applied_revision = @revision,
                    status = @status,
                    last_error = NULL,
                    dispatch_lease_id = NULL,
                    dispatch_lease_until_utc = NULL,
                    updated_at_utc = @now
                WHERE device_id = @device_id
                  AND provider_name = @provider_name
                  AND desired_revision = @revision
                  AND desired_action = @action
                  AND dispatch_lease_id = @lease_id
                """;
            Add(state, "@revision", command.DesiredRevision, DbType.Int64);
            Add(state, "@status", command.Kind == DeviceAccessProviderCommandKind.Apply ? "applied" : "revoked");
            Add(state, "@now", now, DbType.DateTimeOffset);
            Add(state, "@device_id", command.DeviceId);
            Add(state, "@provider_name", command.ProviderName);
            Add(state, "@action", command.Kind == DeviceAccessProviderCommandKind.Apply ? "apply" : "revoke");
            Add(state, "@lease_id", command.LeaseId);
            await state.ExecuteNonQueryAsync(cancellationToken);
        }

        await ReleaseStateLeaseAsync(connection, transaction, command, now, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask FailAsync(
        StoredProviderCommand command,
        string error,
        DateTimeOffset now,
        DateTimeOffset availableAt,
        CancellationToken cancellationToken,
        bool refundAttempt = false) =>
        await RetrySerializationAsync(async () =>
        {
            await FailOnceAsync(command, error, now, availableAt, refundAttempt, cancellationToken);
            return true;
        }, cancellationToken);

    private async ValueTask FailOnceAsync(
        StoredProviderCommand command,
        string error,
        DateTimeOffset now,
        DateTimeOffset availableAt,
        bool refundAttempt,
        CancellationToken cancellationToken)
    {
        error = error.Length <= 512 ? error : error[..512];
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using (var outbox = connection.CreateCommand())
        {
            outbox.Transaction = transaction;
            outbox.CommandText = """
                UPDATE connector_access_provider_outbox
                SET status = 'error', dispatch_lease_id = NULL, lease_until_utc = NULL,
                    available_at_utc = @available_at, last_error = @error, updated_at_utc = @now,
                    attempt_count = CASE
                        WHEN @refund = 1 AND attempt_count > 0 THEN attempt_count - 1
                        ELSE attempt_count END
                WHERE command_id = @command_id AND status = 'processing'
                  AND dispatch_lease_id = @lease_id
                """;
            Add(outbox, "@error", error);
            Add(outbox, "@refund", refundAttempt ? 1 : 0, DbType.Int32);
            Add(outbox, "@now", now, DbType.DateTimeOffset);
            Add(outbox, "@available_at", availableAt, DbType.DateTimeOffset);
            Add(outbox, "@command_id", command.CommandId);
            Add(outbox, "@lease_id", command.LeaseId);
            if (await outbox.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return;
            }
        }

        await using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = """
                UPDATE connector_access_provider_state
                SET status = 'error', last_error = @error, updated_at_utc = @now
                WHERE device_id = @device_id
                  AND provider_name = @provider_name
                  AND desired_revision = @revision
                  AND desired_action = @action
                  AND dispatch_lease_id = @lease_id
                """;
            Add(state, "@error", error);
            Add(state, "@now", now, DbType.DateTimeOffset);
            Add(state, "@device_id", command.DeviceId);
            Add(state, "@provider_name", command.ProviderName);
            Add(state, "@revision", command.DesiredRevision, DbType.Int64);
            Add(state, "@action", command.Kind == DeviceAccessProviderCommandKind.Apply ? "apply" : "revoke");
            Add(state, "@lease_id", command.LeaseId);
            await state.ExecuteNonQueryAsync(cancellationToken);
        }

        await ReleaseStateLeaseAsync(connection, transaction, command, now, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private static async ValueTask ReleaseStateLeaseAsync(
        DbConnection connection,
        DbTransaction transaction,
        StoredProviderCommand command,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var release = connection.CreateCommand();
        release.Transaction = transaction;
        release.CommandText = """
            UPDATE connector_access_provider_state
            SET dispatch_lease_id = NULL, dispatch_lease_until_utc = NULL, updated_at_utc = @now
            WHERE device_id = @device_id
              AND provider_name = @provider_name
              AND dispatch_lease_id = @lease_id
            """;
        Add(release, "@now", now, DbType.DateTimeOffset);
        Add(release, "@device_id", command.DeviceId);
        Add(release, "@provider_name", command.ProviderName);
        Add(release, "@lease_id", command.LeaseId);
        await release.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async ValueTask<IReadOnlyList<StoredProviderState>> ReadStatesAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        var states = new List<StoredProviderState>();
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT provider_name, desired_revision, applied_revision, desired_action, status, last_error
            FROM connector_access_provider_state
            WHERE device_id = @device_id
            """;
        Add(command, "@device_id", deviceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            states.Add(new StoredProviderState(
                reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2),
                reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return states;
    }

    private static async ValueTask UpdateDeviceDesiredRevisionAsync(
        DbConnection connection,
        DbTransaction transaction,
        string deviceId,
        long revision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE connector_access_devices
            SET desired_revision = @revision, updated_at_utc = @now
            WHERE device_id = @device_id AND revoked_at_utc IS NULL
            """;
        Add(command, "@revision", revision, DbType.Int64);
        Add(command, "@now", now, DbType.DateTimeOffset);
        Add(command, "@device_id", deviceId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Device was revoked while recording desired grants.");
        }
    }

    private async ValueTask<bool> UpsertStateAndCommandAsync(
        DbConnection connection,
        DbTransaction transaction,
        string deviceId,
        string providerName,
        DeviceAccessProviderCommandKind kind,
        long revision,
        string payload,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var action = kind == DeviceAccessProviderCommandKind.Apply ? "apply" : "revoke";
        await using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = """
                INSERT INTO connector_access_provider_state
                    (device_id, provider_name, desired_revision, applied_revision,
                     desired_action, status, updated_at_utc)
                VALUES (@device_id, @provider_name, @revision, 0, @action, 'pending', @now)
                ON CONFLICT (device_id, provider_name) DO UPDATE SET
                    desired_revision = excluded.desired_revision,
                    desired_action = excluded.desired_action,
                    status = CASE
                        WHEN connector_access_provider_state.applied_revision >= excluded.desired_revision
                         AND connector_access_provider_state.desired_action = excluded.desired_action
                        THEN connector_access_provider_state.status ELSE 'pending' END,
                    last_error = CASE
                        WHEN connector_access_provider_state.applied_revision >= excluded.desired_revision
                         AND connector_access_provider_state.desired_action = excluded.desired_action
                        THEN connector_access_provider_state.last_error ELSE NULL END,
                    updated_at_utc = excluded.updated_at_utc
                """;
            Add(state, "@device_id", deviceId);
            Add(state, "@provider_name", providerName);
            Add(state, "@revision", revision, DbType.Int64);
            Add(state, "@action", action);
            Add(state, "@now", now, DbType.DateTimeOffset);
            await state.ExecuteNonQueryAsync(cancellationToken);
        }

        var commandId = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{deviceId}\n{providerName}\n{action}\n{revision}"))).ToLowerInvariant();
        await using var outbox = connection.CreateCommand();
        outbox.Transaction = transaction;
        outbox.CommandText = """
            INSERT INTO connector_access_provider_outbox
                (command_id, device_id, provider_name, command_kind, desired_revision,
                 payload_json, status, created_at_utc, updated_at_utc)
            VALUES
                (@command_id, @device_id, @provider_name, @action, @revision,
                 @payload, 'pending', @now, @now)
            ON CONFLICT (command_id) DO NOTHING
            """;
        Add(outbox, "@command_id", commandId);
        Add(outbox, "@device_id", deviceId);
        Add(outbox, "@provider_name", providerName);
        Add(outbox, "@action", action);
        Add(outbox, "@revision", revision, DbType.Int64);
        Add(outbox, "@payload", payload);
        Add(outbox, "@now", now, DbType.DateTimeOffset);
        return await outbox.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    // Each wrapped operation is one complete transaction on its own
    // connection, so repeating it after a serialization conflict is safe.
    private static async ValueTask<T> RetrySerializationAsync<T>(
        Func<ValueTask<T>> operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception exception) when (attempt < SerializationRetries &&
                                              ProviderRetryPolicy.IsTransientDatabaseConflict(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 50) * attempt), cancellationToken);
            }
        }
    }

    private static void Add(DbCommand command, string name, object value, DbType? type = null)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        if (type is not null)
        {
            parameter.DbType = type.Value;
        }

        command.Parameters.Add(parameter);
    }
}
