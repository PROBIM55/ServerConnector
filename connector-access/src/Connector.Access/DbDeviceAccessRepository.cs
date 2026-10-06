using System.Data;
using System.Data.Common;
using System.Security.Cryptography;

namespace Connector.Access;

internal sealed record EnrollmentTokenBinding(
    Guid TokenId,
    string UserId,
    string CompanyId,
    DateTimeOffset ExpiresAtUtc);

internal enum EnrollmentClaimStatus
{
    Claimed,
    InvalidOrExpiredToken,
    TokenAlreadyUsed,
    DeviceAlreadyExists,
}

internal sealed record EnrollmentClaim(
    EnrollmentClaimStatus Status,
    string? DeviceId,
    string? UserId,
    string? CompanyId);

internal sealed record StoredDevice(
    string DeviceId,
    Guid EnrollmentRequestId,
    string UserId,
    string CompanyId,
    string PublicKeySha256,
    byte[]? OriginalCsrDer,
    string? CertificateSha256,
    string? CertificatePem,
    string? IssuerCertificatePem,
    DateTimeOffset? CertificateExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc,
    long DesiredRevision,
    long AppliedRevision,
    string EnrollmentStatus);

internal sealed record StoredAdminDevice(
    StoredDevice Device,
    string DisplayName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

internal sealed class DbDeviceAccessRepository
{
    private readonly IDeviceAccessDbConnectionFactory _connections;

    public DbDeviceAccessRepository(IDeviceAccessDbConnectionFactory connections)
    {
        _connections = connections;
    }

    public async ValueTask InsertTokenAsync(
        Guid tokenId,
        byte[] tokenHash,
        string userId,
        string companyId,
        string requestedByUserId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO connector_access_enrollment_tokens
                (token_id, token_hash, user_id, company_id, requested_by_user_id,
                 created_at_utc, expires_at_utc)
            VALUES
                (@token_id, @token_hash, @user_id, @company_id, @requested_by_user_id,
                 @created_at_utc, @expires_at_utc)
            """;
        Add(command, "@token_id", tokenId.ToString("D"));
        Add(command, "@token_hash", tokenHash, DbType.Binary);
        Add(command, "@user_id", userId);
        Add(command, "@company_id", companyId);
        Add(command, "@requested_by_user_id", requestedByUserId);
        Add(command, "@created_at_utc", createdAtUtc, DbType.DateTimeOffset);
        Add(command, "@expires_at_utc", expiresAtUtc, DbType.DateTimeOffset);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<EnrollmentTokenBinding?> ResolveUsableTokenAsync(
        ParsedEnrollmentToken token,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT token_hash, user_id, company_id, expires_at_utc, consumed_at_utc
            FROM connector_access_enrollment_tokens
            WHERE token_id = @token_id
            """;
        Add(command, "@token_id", token.TokenId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0))
        {
            return null;
        }

        var storedHash = (byte[])reader.GetValue(0);
        if (storedHash.Length != token.Hash.Length ||
            !CryptographicOperations.FixedTimeEquals(storedHash, token.Hash) ||
            !reader.IsDBNull(4))
        {
            return null;
        }

        var expiresAt = ReadDateTimeOffset(reader, 3);
        return expiresAt <= now
            ? null
            : new EnrollmentTokenBinding(token.TokenId, reader.GetString(1), reader.GetString(2), expiresAt);
    }

    public async ValueTask<EnrollmentClaim> ClaimEnrollmentAsync(
        ParsedEnrollmentToken token,
        EnrollmentTokenBinding expectedBinding,
        Guid requestId,
        string deviceId,
        string deviceDisplayName,
        string csrSha256,
        byte[] originalCsrDer,
        string publicKeySha256,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var current = await ReadTokenForClaimAsync(connection, transaction, token.TokenId, cancellationToken);
        if (current is null || current.Value.Hash.Length != token.Hash.Length ||
            !CryptographicOperations.FixedTimeEquals(current.Value.Hash, token.Hash) ||
            current.Value.ExpiresAtUtc <= now ||
            current.Value.UserId != expectedBinding.UserId ||
            current.Value.CompanyId != expectedBinding.CompanyId)
        {
            return new EnrollmentClaim(EnrollmentClaimStatus.InvalidOrExpiredToken, null, null, null);
        }

        if (current.Value.ConsumedAtUtc is not null)
        {
            return new EnrollmentClaim(EnrollmentClaimStatus.TokenAlreadyUsed, null, null, null);
        }

        if (await DeviceExistsForPublicKeyAsync(
                connection,
                transaction,
                expectedBinding.UserId,
                expectedBinding.CompanyId,
                publicKeySha256,
                cancellationToken))
        {
            return new EnrollmentClaim(EnrollmentClaimStatus.DeviceAlreadyExists, null, null, null);
        }

        await using (var consume = connection.CreateCommand())
        {
            consume.Transaction = transaction;
            consume.CommandText = """
                UPDATE connector_access_enrollment_tokens
                SET consumed_at_utc = @now,
                    consumed_request_id = @request_id,
                    consumed_device_id = @device_id,
                    consumed_csr_sha256 = @csr_sha256
                WHERE token_id = @token_id
                  AND consumed_at_utc IS NULL
                  AND expires_at_utc > @now
                """;
            Add(consume, "@now", now, DbType.DateTimeOffset);
            Add(consume, "@request_id", requestId.ToString("D"));
            Add(consume, "@device_id", deviceId);
            Add(consume, "@csr_sha256", csrSha256);
            Add(consume, "@token_id", token.TokenId.ToString("D"));
            if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return new EnrollmentClaim(EnrollmentClaimStatus.TokenAlreadyUsed, null, null, null);
            }
        }

        try
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO connector_access_devices
                    (device_id, user_id, company_id, display_name, public_key_sha256, original_csr_der,
                     enrollment_request_id, enrollment_status, desired_revision,
                     applied_revision, created_at_utc, updated_at_utc)
                VALUES
                    (@device_id, @user_id, @company_id, @display_name, @public_key_sha256, @original_csr_der,
                     @request_id, 'pending_certificate', 0, 0, @now, @now)
                """;
            Add(insert, "@device_id", deviceId);
            Add(insert, "@user_id", expectedBinding.UserId);
            Add(insert, "@company_id", expectedBinding.CompanyId);
            Add(insert, "@display_name", deviceDisplayName);
            Add(insert, "@public_key_sha256", publicKeySha256);
            Add(insert, "@original_csr_der", originalCsrDer, DbType.Binary);
            Add(insert, "@request_id", requestId.ToString("D"));
            Add(insert, "@now", now, DbType.DateTimeOffset);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (DbException)
        {
            return new EnrollmentClaim(EnrollmentClaimStatus.DeviceAlreadyExists, null, null, null);
        }

        await transaction.CommitAsync(cancellationToken);
        return new EnrollmentClaim(
            EnrollmentClaimStatus.Claimed,
            deviceId,
            expectedBinding.UserId,
            expectedBinding.CompanyId);
    }

    public async ValueTask<bool> CompleteEnrollmentAsync(
        string deviceId,
        string issuanceAttemptId,
        string certificateSha256,
        string certificatePem,
        string issuerCertificatePem,
        DateTimeOffset certificateExpiresAtUtc,
        long accessRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE connector_access_devices
            SET certificate_sha256 = @certificate_sha256,
                certificate_pem = @certificate_pem,
                issuer_certificate_pem = @issuer_certificate_pem,
                certificate_expires_at_utc = @certificate_expires_at_utc,
                enrollment_status = 'active',
                issuance_attempt_id = NULL,
                issuance_lease_until_utc = NULL,
                issuance_last_error = NULL,
                desired_revision = @access_revision,
                updated_at_utc = @now
            WHERE device_id = @device_id
              AND enrollment_status = 'pending_certificate'
              AND certificate_sha256 IS NULL
              AND issuance_attempt_id = @issuance_attempt_id
            """;
        Add(command, "@certificate_sha256", certificateSha256);
        Add(command, "@certificate_pem", certificatePem);
        Add(command, "@issuer_certificate_pem", issuerCertificatePem);
        Add(command, "@certificate_expires_at_utc", certificateExpiresAtUtc, DbType.DateTimeOffset);
        Add(command, "@access_revision", accessRevision, DbType.Int64);
        Add(command, "@now", now, DbType.DateTimeOffset);
        Add(command, "@device_id", deviceId);
        Add(command, "@issuance_attempt_id", issuanceAttemptId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public ValueTask<StoredDevice?> FindByEnrollmentRequestAsync(
        Guid enrollmentRequestId,
        CancellationToken cancellationToken) =>
        FindDeviceAsync("enrollment_request_id", enrollmentRequestId.ToString("D"), cancellationToken);

    public async ValueTask<StoredDevice?> TryAcquireIssuanceLeaseAsync(
        string deviceId,
        Guid enrollmentRequestId,
        string attemptId,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE connector_access_devices
            SET issuance_attempt_id = @attempt_id,
                issuance_lease_until_utc = @lease_until,
                issuance_attempts = issuance_attempts + 1,
                updated_at_utc = @now
            WHERE device_id = @device_id
              AND enrollment_request_id = @request_id
              AND enrollment_status = 'pending_certificate'
              AND certificate_sha256 IS NULL
              AND (issuance_lease_until_utc IS NULL OR issuance_lease_until_utc <= @now)
            """;
        Add(command, "@attempt_id", attemptId);
        Add(command, "@lease_until", leaseUntil, DbType.DateTimeOffset);
        Add(command, "@now", now, DbType.DateTimeOffset);
        Add(command, "@device_id", deviceId);
        Add(command, "@request_id", enrollmentRequestId.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            return null;
        }

        return await FindByDeviceIdAsync(deviceId, cancellationToken);
    }

    public async ValueTask RecordIssuanceFailureAsync(
        string deviceId,
        string attemptId,
        string error,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE connector_access_devices
            SET issuance_attempt_id = NULL,
                issuance_lease_until_utc = NULL,
                issuance_last_error = @error,
                updated_at_utc = @now
            WHERE device_id = @device_id
              AND enrollment_status = 'pending_certificate'
              AND issuance_attempt_id = @attempt_id
            """;
        Add(command, "@error", error.Length <= 512 ? error : error[..512]);
        Add(command, "@now", now, DbType.DateTimeOffset);
        Add(command, "@device_id", deviceId);
        Add(command, "@attempt_id", attemptId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public ValueTask<StoredDevice?> FindByCertificateAsync(
        string certificateSha256,
        CancellationToken cancellationToken) =>
        FindDeviceAsync("certificate_sha256", certificateSha256, cancellationToken);

    public ValueTask<StoredDevice?> FindByDeviceIdAsync(
        string deviceId,
        CancellationToken cancellationToken) =>
        FindDeviceAsync("device_id", deviceId, cancellationToken);

    public async ValueTask<IReadOnlyList<StoredAdminDevice>> ListByCompanyAsync(
        string companyId,
        CancellationToken cancellationToken)
    {
        var devices = new List<StoredAdminDevice>();
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT device_id, enrollment_request_id, user_id, company_id, public_key_sha256, original_csr_der,
                   certificate_sha256, certificate_pem, issuer_certificate_pem,
                   certificate_expires_at_utc, revoked_at_utc,
                   desired_revision, applied_revision, enrollment_status,
                   display_name, created_at_utc, updated_at_utc
            FROM connector_access_devices
            WHERE company_id = @company_id
            ORDER BY created_at_utc DESC, device_id
            """;
        Add(command, "@company_id", companyId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            devices.Add(new StoredAdminDevice(
                new StoredDevice(
                    reader.GetString(0), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5),
                    ReadNullableString(reader, 6), ReadNullableString(reader, 7), ReadNullableString(reader, 8),
                    ReadNullableDateTimeOffset(reader, 9), ReadNullableDateTimeOffset(reader, 10),
                    reader.GetInt64(11), reader.GetInt64(12), reader.GetString(13)),
                reader.GetString(14),
                ReadDateTimeOffset(reader, 15),
                ReadDateTimeOffset(reader, 16)));
        }

        return devices;
    }

    public async ValueTask<bool> RevokeAsync(
        string deviceId,
        string companyId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE connector_access_devices
            SET revoked_at_utc = @now,
                enrollment_status = 'revoked',
                updated_at_utc = @now
            WHERE device_id = @device_id
              AND company_id = @company_id
              AND revoked_at_utc IS NULL
            """;
        Add(command, "@now", now, DbType.DateTimeOffset);
        Add(command, "@device_id", deviceId);
        Add(command, "@company_id", companyId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async ValueTask SetAccessRevisionsAsync(
        string deviceId,
        long desiredRevision,
        long appliedRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE connector_access_devices
            SET desired_revision = @desired_revision,
                applied_revision = @applied_revision,
                updated_at_utc = @now
            WHERE device_id = @device_id AND revoked_at_utc IS NULL
            """;
        Add(command, "@desired_revision", desiredRevision, DbType.Int64);
        Add(command, "@applied_revision", appliedRevision, DbType.Int64);
        Add(command, "@now", now, DbType.DateTimeOffset);
        Add(command, "@device_id", deviceId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("The device was revoked or removed while updating access state.");
        }
    }

    private async ValueTask<StoredDevice?> FindDeviceAsync(
        string indexedColumn,
        string value,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connections.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT device_id, enrollment_request_id, user_id, company_id, public_key_sha256, original_csr_der,
                   certificate_sha256, certificate_pem, issuer_certificate_pem,
                   certificate_expires_at_utc, revoked_at_utc,
                   desired_revision, applied_revision, enrollment_status
            FROM connector_access_devices
            WHERE {indexedColumn} = @value
            """;
        Add(command, "@value", value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StoredDevice(
            reader.GetString(0),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5),
            ReadNullableString(reader, 6),
            ReadNullableString(reader, 7),
            ReadNullableString(reader, 8),
            ReadNullableDateTimeOffset(reader, 9),
            ReadNullableDateTimeOffset(reader, 10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            reader.GetString(13));
    }

    private static async ValueTask<(
        byte[] Hash,
        string UserId,
        string CompanyId,
        DateTimeOffset ExpiresAtUtc,
        DateTimeOffset? ConsumedAtUtc)?> ReadTokenForClaimAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid tokenId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT token_hash, user_id, company_id, expires_at_utc, consumed_at_utc
            FROM connector_access_enrollment_tokens
            WHERE token_id = @token_id
            """;
        Add(command, "@token_id", tokenId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (
            (byte[])reader.GetValue(0),
            reader.GetString(1),
            reader.GetString(2),
            ReadDateTimeOffset(reader, 3),
            ReadNullableDateTimeOffset(reader, 4));
    }

    private static async ValueTask<bool> DeviceExistsForPublicKeyAsync(
        DbConnection connection,
        DbTransaction transaction,
        string userId,
        string companyId,
        string publicKeySha256,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM connector_access_devices
            WHERE user_id = @user_id
              AND company_id = @company_id
              AND public_key_sha256 = @public_key_sha256
            """;
        Add(command, "@user_id", userId);
        Add(command, "@company_id", companyId);
        Add(command, "@public_key_sha256", publicKeySha256);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
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

    private static DateTimeOffset ReadDateTimeOffset(DbDataReader reader, int ordinal)
    {
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            string text => DateTimeOffset.Parse(text, null, System.Globalization.DateTimeStyles.RoundtripKind),
            _ => throw new DataException($"Unsupported timestamp value {value.GetType().FullName}."),
        };
    }

    private static DateTimeOffset? ReadNullableDateTimeOffset(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ReadDateTimeOffset(reader, ordinal);

    private static string? ReadNullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
