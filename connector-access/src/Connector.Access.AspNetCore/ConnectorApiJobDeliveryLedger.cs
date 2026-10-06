using System.Data.Common;
using System.Text.Json;
using Connector.Access;

namespace Connector.Access.AspNetCore;

/// <summary>
/// Persists the shared API delivery ledger. Host-specific job events and job projections are supplied
/// through a callback and remain in the host's schema and authorization boundary. The host also
/// supplies its existing timestamp encoding so SQLite TEXT CAS remains compatible with old rows.
/// </summary>
public sealed class ConnectorApiJobDeliveryLedger(
    IDeviceAccessDbConnectionFactory connections,
    Func<DbConnection, DateTimeOffset, object> encodeUtcTime)
{
    private readonly Func<DbConnection, DateTimeOffset, object> _encodeUtcTime =
        encodeUtcTime ?? throw new ArgumentNullException(nameof(encodeUtcTime));

    public async ValueTask RecordAsync(ConnectorApiJobDelivery delivery, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        await using var connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        RecordInTransaction(connection, transaction, delivery);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void RecordInTransaction(
        DbConnection connection,
        DbTransaction transaction,
        ConnectorApiJobDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(delivery);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO connector_access_api_job_deliveries(
                request_id, device_id, user_id, company_id, access_revision, session_id,
                schema_version, module_id, provider, operation, executor_id, correlation_id, scope_json, delivered_at_utc)
            VALUES(@request_id, @device_id, @user_id, @company_id, @access_revision, @session_id,
                @schema_version, @module_id, @provider, @operation, @executor_id, @correlation_id, @scope_json, @delivered_at_utc)
            """;
        Add(command, "@request_id", delivery.RequestId);
        Add(command, "@device_id", delivery.DeviceId);
        Add(command, "@user_id", delivery.UserId);
        Add(command, "@company_id", delivery.CompanyId);
        Add(command, "@access_revision", delivery.AccessRevision);
        Add(command, "@session_id", delivery.SessionId);
        Add(command, "@schema_version", delivery.SchemaVersion);
        Add(command, "@module_id", delivery.ModuleId);
        Add(command, "@provider", delivery.Provider);
        Add(command, "@operation", delivery.Operation);
        Add(command, "@executor_id", delivery.ExecutorId);
        Add(command, "@correlation_id", delivery.CorrelationId);
        Add(command, "@scope_json", JsonSerializer.Serialize(delivery.Scope));
        Add(command, "@delivered_at_utc", _encodeUtcTime(connection, delivery.DeliveredAtUtc));
        command.ExecuteNonQuery();
    }

    public async ValueTask<ConnectorApiJobDelivery?> FindAsync(
        string requestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        await using var connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT request_id, device_id, user_id, company_id, access_revision, session_id,
                   schema_version, module_id, provider, operation, executor_id, correlation_id, scope_json,
                   delivered_at_utc, last_status, last_status_at_utc
            FROM connector_access_api_job_deliveries
            WHERE request_id = @request_id
            """;
        Add(command, "@request_id", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var scope = JsonSerializer.Deserialize<ConnectorApiJobExecutionScope>(reader.GetString(12))
                    ?? throw new InvalidOperationException("Stored connector job scope is invalid.");
        return new ConnectorApiJobDelivery(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
            reader.GetString(5), reader.GetInt32(6), reader.GetString(7), reader.GetInt32(8), reader.GetInt32(9),
            reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
            scope, ReadTime(reader, 13), reader.IsDBNull(14) ? null : reader.GetInt32(14),
            reader.IsDBNull(15) ? null : ReadTime(reader, 15));
    }

    /// <summary>
    /// Advances the delivery CAS and invokes host projection work inside the same transaction.
    /// Projection exceptions roll back the ledger and are rethrown unchanged.
    /// </summary>
    public async ValueTask<bool> AdvanceStatusAndProjectAsync(
        ConnectorApiJobDelivery delivery,
        ConnectorApiJobStatusEnvelope status,
        Func<DbConnection, DbTransaction, ConnectorApiJobStatusEnvelope, CancellationToken, ValueTask> projectStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(projectStatus);
        if (!HasMatchingIdentity(delivery, status)) return false;

        await using var connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE connector_access_api_job_deliveries
                SET last_status = @status, last_status_at_utc = @updated_at_utc
                WHERE request_id = @request_id AND device_id = @device_id AND session_id = @session_id
                  AND access_revision = @access_revision
                  AND (last_status IS NULL OR last_status IN (1, 2))
                  AND (last_status_at_utc IS NULL OR last_status_at_utc < @updated_at_utc)
                """;
            Add(command, "@status", status.Status);
            Add(command, "@updated_at_utc", _encodeUtcTime(connection,
                new DateTimeOffset(DateTime.SpecifyKind(status.UpdatedAtUtc, DateTimeKind.Utc))));
            Add(command, "@request_id", delivery.RequestId);
            Add(command, "@device_id", delivery.DeviceId);
            Add(command, "@session_id", delivery.SessionId);
            Add(command, "@access_revision", delivery.AccessRevision);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return false;
            }

            await projectStatus(connection, transaction, status, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* Preserve the CAS or projection exception. */ }
            throw;
        }
    }

    private static bool HasMatchingIdentity(ConnectorApiJobDelivery delivery, ConnectorApiJobStatusEnvelope status) =>
        string.Equals(status.RequestId, delivery.RequestId, StringComparison.Ordinal) &&
        string.Equals(status.DeviceId, delivery.DeviceId, StringComparison.Ordinal) &&
        status.SchemaVersion == delivery.SchemaVersion &&
        string.Equals(status.ModuleId, delivery.ModuleId, StringComparison.Ordinal) &&
        status.Provider == delivery.Provider &&
        string.Equals(status.CorrelationId, delivery.CorrelationId, StringComparison.Ordinal) &&
        string.Equals(status.ExecutorId, delivery.ExecutorId, StringComparison.Ordinal);

    private static DateTimeOffset ReadTime(DbDataReader reader, int ordinal) => reader.GetValue(ordinal) switch
    {
        DateTimeOffset value => value,
        DateTime value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
        string value => DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind),
        var value => DateTimeOffset.Parse(value.ToString()!, null, System.Globalization.DateTimeStyles.RoundtripKind),
    };

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

}
