ALTER TABLE connector_access_devices ADD COLUMN original_csr_der BLOB NULL;
ALTER TABLE connector_access_devices ADD COLUMN issuance_attempt_id TEXT NULL;
ALTER TABLE connector_access_devices ADD COLUMN issuance_lease_until_utc TEXT NULL;
ALTER TABLE connector_access_devices ADD COLUMN issuance_attempts INTEGER NOT NULL DEFAULT 0;
ALTER TABLE connector_access_devices ADD COLUMN issuance_last_error TEXT NULL;

CREATE UNIQUE INDEX ux_connector_access_devices_enrollment_request
    ON connector_access_devices (enrollment_request_id);

CREATE TABLE connector_access_provider_state (
    device_id TEXT NOT NULL REFERENCES connector_access_devices(device_id),
    provider_name TEXT NOT NULL,
    desired_revision INTEGER NOT NULL CHECK (desired_revision >= 0),
    applied_revision INTEGER NOT NULL DEFAULT 0 CHECK (applied_revision >= 0),
    desired_action TEXT NOT NULL CHECK (desired_action IN ('apply', 'revoke')),
    status TEXT NOT NULL CHECK (status IN ('pending', 'applied', 'revoked', 'error')),
    last_error TEXT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY (device_id, provider_name)
);

CREATE TABLE connector_access_provider_outbox (
    command_id TEXT PRIMARY KEY,
    device_id TEXT NOT NULL REFERENCES connector_access_devices(device_id),
    provider_name TEXT NOT NULL,
    command_kind TEXT NOT NULL CHECK (command_kind IN ('apply', 'revoke')),
    desired_revision INTEGER NOT NULL CHECK (desired_revision >= 0),
    payload_json TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('pending', 'processing', 'applied', 'error')),
    attempt_count INTEGER NOT NULL DEFAULT 0,
    dispatch_lease_id TEXT NULL,
    lease_until_utc TEXT NULL,
    last_error TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    UNIQUE (device_id, provider_name, command_kind, desired_revision)
);

CREATE INDEX ix_connector_access_provider_outbox_pending
    ON connector_access_provider_outbox (status, lease_until_utc, created_at_utc);
