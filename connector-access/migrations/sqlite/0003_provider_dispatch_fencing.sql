ALTER TABLE connector_access_provider_state ADD COLUMN dispatch_lease_id TEXT NULL;
ALTER TABLE connector_access_provider_state ADD COLUMN dispatch_lease_until_utc TEXT NULL;
ALTER TABLE connector_access_provider_outbox ADD COLUMN available_at_utc TEXT NULL;

CREATE INDEX ix_connector_access_provider_outbox_available
    ON connector_access_provider_outbox (status, available_at_utc, created_at_utc);
