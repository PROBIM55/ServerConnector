-- Connector Access v1. Apply through the existing Platform database migration runner.
-- This artifact is intentionally not executed by the library at runtime.
CREATE TABLE connector_access_enrollment_tokens (
    token_id TEXT PRIMARY KEY,
    token_hash BYTEA NOT NULL CHECK (octet_length(token_hash) = 32),
    user_id TEXT NOT NULL,
    company_id TEXT NOT NULL,
    requested_by_user_id TEXT NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL,
    expires_at_utc TIMESTAMPTZ NOT NULL,
    consumed_at_utc TIMESTAMPTZ NULL,
    consumed_request_id TEXT NULL,
    consumed_device_id TEXT NULL,
    consumed_csr_sha256 TEXT NULL,
    CHECK (expires_at_utc > created_at_utc),
    CHECK ((consumed_at_utc IS NULL) = (consumed_device_id IS NULL))
);

CREATE INDEX ix_connector_access_tokens_expiry
    ON connector_access_enrollment_tokens (expires_at_utc)
    WHERE consumed_at_utc IS NULL;

CREATE TABLE connector_access_devices (
    device_id TEXT PRIMARY KEY,
    user_id TEXT NOT NULL,
    company_id TEXT NOT NULL,
    display_name TEXT NOT NULL CHECK (char_length(display_name) BETWEEN 1 AND 128),
    public_key_sha256 TEXT NOT NULL CHECK (char_length(public_key_sha256) = 64),
    enrollment_request_id TEXT NOT NULL,
    enrollment_status TEXT NOT NULL CHECK (enrollment_status IN ('pending_certificate', 'active', 'revoked')),
    certificate_sha256 TEXT NULL CHECK (certificate_sha256 IS NULL OR char_length(certificate_sha256) = 64),
    certificate_pem TEXT NULL,
    issuer_certificate_pem TEXT NULL,
    certificate_expires_at_utc TIMESTAMPTZ NULL,
    revoked_at_utc TIMESTAMPTZ NULL,
    desired_revision BIGINT NOT NULL DEFAULT 0 CHECK (desired_revision >= 0),
    applied_revision BIGINT NOT NULL DEFAULT 0 CHECK (applied_revision >= 0),
    created_at_utc TIMESTAMPTZ NOT NULL,
    updated_at_utc TIMESTAMPTZ NOT NULL,
    UNIQUE (company_id, user_id, public_key_sha256),
    UNIQUE (certificate_sha256)
);

CREATE INDEX ix_connector_access_devices_tenant
    ON connector_access_devices (company_id, user_id);

CREATE INDEX ix_connector_access_devices_active
    ON connector_access_devices (company_id, enrollment_status)
    WHERE revoked_at_utc IS NULL;

ALTER TABLE connector_access_enrollment_tokens
    ADD CONSTRAINT fk_connector_access_token_device
    FOREIGN KEY (consumed_device_id) REFERENCES connector_access_devices (device_id)
    DEFERRABLE INITIALLY DEFERRED;
