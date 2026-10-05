using System.Data.Common;
using System.Text.Json;
using Connector.Access;
using Connector.Access.AspNetCore;
using Microsoft.Data.Sqlite;

namespace Connector.Access.Tests;

public sealed class ConnectorApiJobDeliveryLedgerTests
{
    [Fact]
    public async Task RecordInTransactionAndFind_PreserveDeliveryIdentityScopeAndStatus()
    {
        await using var fixture = await LedgerFixture.CreateAsync();
        using var transaction = await fixture.Anchor.BeginTransactionAsync();
        var delivery = fixture.CreateDelivery();

        fixture.Ledger.RecordInTransaction(fixture.Anchor, transaction, delivery);
        await transaction.CommitAsync();

        var found = await fixture.Ledger.FindAsync(delivery.RequestId);
        Assert.NotNull(found);
        Assert.Equal(delivery, found);
        Assert.Null(found.LastStatus);
        Assert.Null(found.LastStatusAtUtc);
    }

    [Fact]
    public async Task CompareAndSetProjectsOnceWithExactStatusAndRejectsIdentityMismatch()
    {
        await using var fixture = await LedgerFixture.CreateAsync();
        var delivery = fixture.CreateDelivery();
        await fixture.Ledger.RecordAsync(delivery);
        await fixture.ExecuteAsync("INSERT INTO host_jobs(request_id, status) VALUES(@request_id, 'queued')",
            ("@request_id", delivery.RequestId));
        var status = fixture.CreateStatus(delivery, status: 3, message: "complete");
        var projectionCalls = 0;

        var mismatched = status with { DeviceId = "another-device" };
        Assert.False(await fixture.Ledger.AdvanceStatusAndProjectAsync(delivery, mismatched,
            (_, _, _, _) =>
            {
                projectionCalls++;
                return ValueTask.CompletedTask;
            }));
        Assert.Equal(0, projectionCalls);

        using var projectionCancellation = new CancellationTokenSource();
        Assert.True(await fixture.Ledger.AdvanceStatusAndProjectAsync(delivery, status, async (connection, transaction, projected, cancellationToken) =>
        {
            projectionCalls++;
            Assert.Equal(status, projected);
            Assert.Equal(projectionCancellation.Token, cancellationToken);
            await fixture.ExecuteInTransactionAsync(connection, transaction,
                "INSERT INTO host_events(request_id, status, message) VALUES(@request_id, @status, @message)", cancellationToken,
                ("@request_id", projected.RequestId), ("@status", projected.Status), ("@message", projected.Message));
            await fixture.ExecuteInTransactionAsync(connection, transaction,
                "UPDATE host_jobs SET status = @status WHERE request_id = @request_id",
                cancellationToken, ("@status", "success"), ("@request_id", projected.RequestId));
        }, projectionCancellation.Token));

        Assert.False(await fixture.Ledger.AdvanceStatusAndProjectAsync(delivery,
            fixture.CreateStatus(delivery, status: 4, message: "late conflicting terminal status"),
            (_, _, _, _) =>
            {
                projectionCalls++;
                return ValueTask.CompletedTask;
            }));
        Assert.Equal(1, projectionCalls);
        var found = await fixture.Ledger.FindAsync(delivery.RequestId);
        Assert.Equal(3, found?.LastStatus);
        Assert.Equal(status.UpdatedAtUtc, found?.LastStatusAtUtc?.UtcDateTime);
        Assert.Equal(1L, await fixture.ScalarAsync<long>("SELECT COUNT(*) FROM host_events"));
        Assert.Equal("success", await fixture.ScalarAsync<string>("SELECT status FROM host_jobs WHERE request_id = @request_id",
            ("@request_id", delivery.RequestId)));
    }

    [Fact]
    public async Task ProjectionExceptionRollsBackLedgerAndHostWritesTogether()
    {
        await using var fixture = await LedgerFixture.CreateAsync();
        var delivery = fixture.CreateDelivery();
        await fixture.Ledger.RecordAsync(delivery);
        await fixture.ExecuteAsync("INSERT INTO host_jobs(request_id, status) VALUES(@request_id, 'queued')",
            ("@request_id", delivery.RequestId));
        var status = fixture.CreateStatus(delivery, status: 3, message: "complete");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Ledger.AdvanceStatusAndProjectAsync(delivery, status, async (connection, transaction, projected, cancellationToken) =>
            {
                await fixture.ExecuteInTransactionAsync(connection, transaction,
                    "INSERT INTO host_events(request_id, status, message) VALUES(@request_id, @status, @message)", cancellationToken,
                    ("@request_id", projected.RequestId), ("@status", projected.Status), ("@message", projected.Message));
                await fixture.ExecuteInTransactionAsync(connection, transaction,
                    "UPDATE host_jobs SET status = 'success' WHERE request_id = @request_id",
                    cancellationToken, ("@request_id", projected.RequestId));
                throw new InvalidOperationException("host projection failed");
            }));

        Assert.Equal("host projection failed", error.Message);
        var found = await fixture.Ledger.FindAsync(delivery.RequestId);
        Assert.Null(found?.LastStatus);
        Assert.Null(found?.LastStatusAtUtc);
        Assert.Equal(0L, await fixture.ScalarAsync<long>("SELECT COUNT(*) FROM host_events"));
        Assert.Equal("queued", await fixture.ScalarAsync<string>("SELECT status FROM host_jobs WHERE request_id = @request_id",
            ("@request_id", delivery.RequestId)));
    }

    [Fact]
    public async Task NewerStatusAdvancesLegacySqliteTextTimestamp()
    {
        await using var fixture = await LedgerFixture.CreateAsync();
        var delivery = fixture.CreateDelivery();
        await fixture.Ledger.RecordAsync(delivery);
        var oldTime = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
        await fixture.ExecuteAsync("""
            UPDATE connector_access_api_job_deliveries
            SET last_status = 1, last_status_at_utc = @old_time
            WHERE request_id = @request_id
            """, ("@old_time", oldTime), ("@request_id", delivery.RequestId));
        var status = fixture.CreateStatus(delivery, status: 3, message: "complete");

        Assert.True(await fixture.Ledger.AdvanceStatusAndProjectAsync(delivery, status,
            (_, _, _, _) => ValueTask.CompletedTask));
        Assert.Equal(3, (await fixture.Ledger.FindAsync(delivery.RequestId))?.LastStatus);
    }

    private sealed class LedgerFixture : IAsyncDisposable
    {
        private readonly string _connectionString;
        public SqliteConnection Anchor { get; }
        public ConnectorApiJobDeliveryLedger Ledger { get; }

        private LedgerFixture(string connectionString, SqliteConnection anchor)
        {
            _connectionString = connectionString;
            Anchor = anchor;
            Ledger = new ConnectorApiJobDeliveryLedger(new SqliteFactory(connectionString),
                (_, value) => value.ToUniversalTime().ToString("O"));
        }

        public static async Task<LedgerFixture> CreateAsync()
        {
            var connectionString = $"Data Source=delivery-ledger-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            await using var command = anchor.CreateCommand();
            command.CommandText = """
                CREATE TABLE connector_access_api_job_deliveries(
                    request_id TEXT PRIMARY KEY, device_id TEXT NOT NULL, user_id TEXT NOT NULL, company_id TEXT NOT NULL,
                    access_revision INTEGER NOT NULL, session_id TEXT NOT NULL, schema_version INTEGER NOT NULL,
                    module_id TEXT NOT NULL, provider INTEGER NOT NULL, operation INTEGER NOT NULL,
                    executor_id TEXT NULL, correlation_id TEXT NULL, scope_json TEXT NOT NULL,
                    delivered_at_utc TEXT NOT NULL, last_status INTEGER NULL, last_status_at_utc TEXT NULL);
                CREATE TABLE host_jobs(request_id TEXT PRIMARY KEY, status TEXT NOT NULL);
                CREATE TABLE host_events(request_id TEXT NOT NULL, status INTEGER NOT NULL, message TEXT NULL);
                """;
            await command.ExecuteNonQueryAsync();
            return new LedgerFixture(connectionString, anchor);
        }

        public ConnectorApiJobDelivery CreateDelivery()
        {
            return new ConnectorApiJobDelivery(
                "job-ledger-test", "device-a", "user-a", "company-a", 17, "session-a", 2,
                "bridge", 4, 6, "executor-a", "correlation-a",
                new ConnectorApiJobExecutionScope("platform", "company-a", "project", "project-a"),
                DateTimeOffset.UtcNow.AddMinutes(-1), null, null);
        }

        public ConnectorApiJobStatusEnvelope CreateStatus(ConnectorApiJobDelivery delivery, int status, string message) =>
            new(delivery.SchemaVersion, delivery.RequestId, delivery.DeviceId, delivery.ModuleId, delivery.Provider,
                status, DateTime.UtcNow.AddSeconds(status).ToUniversalTime(), message, 100, null, null,
                delivery.CorrelationId, delivery.ExecutorId);

        public async ValueTask ExecuteInTransactionAsync(DbConnection connection, DbTransaction transaction, string sql,
            CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            AddParameters(command, parameters);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameters(command, parameters);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameters(command, parameters);
            return (T)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Expected a scalar result."));
        }

        private static void AddParameters(DbCommand command, IEnumerable<(string Name, object? Value)> parameters)
        {
            foreach (var (name, value) in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }
        }

        public async ValueTask DisposeAsync() => await Anchor.DisposeAsync();

        private sealed class SqliteFactory(string connectionString) : IDeviceAccessDbConnectionFactory
        {
            public async ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
            {
                var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                return connection;
            }
        }
    }
}
