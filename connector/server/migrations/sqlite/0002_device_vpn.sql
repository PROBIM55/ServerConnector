-- 0002 device_vpn (self-hosted AmneziaWG VPN access per device)
-- depends: 0001_baseline_schema
--
-- One row per device: the server-generated AmneziaWG keypair, the allocated VPN
-- address, and the full client .conf (so re-bootstrap returns the same config).
-- Mirrors device_access. Additive; unused unless cfg["vpn"]["enabled"] is true.

CREATE TABLE IF NOT EXISTS device_vpn (
    device_id TEXT PRIMARY KEY,
    public_key TEXT NOT NULL,
    private_key TEXT NOT NULL,
    vpn_address TEXT NOT NULL UNIQUE,
    config TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
