-- SQLite parity fixture/development migration for Connector Access v1.
-- Production Platform uses the PostgreSQL artifact with the same semantic constraints.
PRAGMA foreign_keys = ON;

CREATE TABLE connector_access_enrollment_tokens (
    token_id TEXT PRIMARY KEY,
    token_hash BLOB NOT NULL CHECK (length(token_hash) = 32),
    user_id TEXT NOT NULL,
    company_id TEXT NOT NULL,
    requested_by_user_id TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    expires_at_utc TEXT NOT NULL,
    consumed_at_utc TEXT NULL,
    consumed_request_id TEXT NULL,
    consumed_device_id TEXT NULL,
    consumed_csr_sha256 TEXT NULL,
    CHECK (expires_at_utc > created_at_utc),
    CHECK ((consumed_at_utc IS NULL) = (consumed_device_id IS NULL)),
    FOREIGN KEY (consumed_device_id) REFERENCES connector_access_devices (device_id) DEFERRABLE INITIALLY DEFERRED
);

CREATE INDEX ix_connector_access_tokens_expiry
    ON connector_access_enrollment_tokens (expires_at_utc)
    WHERE consumed_at_utc IS NULL;

CREATE TABLE connector_access_devices (
    device_id TEXT PRIMARY KEY,
    user_id TEXT NOT NULL,
    company_id TEXT NOT NULL,
    display_name TEXT NOT NULL CHECK (length(display_name) BETWEEN 1 AND 128),
    public_key_sha256 TEXT NOT NULL CHECK (length(public_key_sha256) = 64),
    enrollment_request_id TEXT NOT NULL,
    enrollment_status TEXT NOT NULL CHECK (enrollment_status IN ('pending_certificate', 'active', 'revoked')),
    certificate_sha256 TEXT NULL CHECK (certificate_sha256 IS NULL OR length(certificate_sha256) = 64),
    certificate_pem TEXT NULL,
    issuer_certificate_pem TEXT NULL,
    certificate_expires_at_utc TEXT NULL,
    revoked_at_utc TEXT NULL,
    desired_revision INTEGER NOT NULL DEFAULT 0 CHECK (desired_revision >= 0),
    applied_revision INTEGER NOT NULL DEFAULT 0 CHECK (applied_revision >= 0),
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    UNIQUE (company_id, user_id, public_key_sha256),
    UNIQUE (certificate_sha256)
);

CREATE INDEX ix_connector_access_devices_tenant
    ON connector_access_devices (company_id, user_id);

CREATE INDEX ix_connector_access_devices_active
    ON connector_access_devices (company_id, enrollment_status)
    WHERE revoked_at_utc IS NULL;
