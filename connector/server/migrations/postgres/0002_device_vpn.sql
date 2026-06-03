-- 0002 device_vpn (self-hosted AmneziaWG VPN access per device) — PostgreSQL flavor
-- depends: 0001_baseline_schema
--
-- Mirror of migrations/sqlite/0002_device_vpn.sql. Column types are TEXT/NULL which
-- both backends accept, so the schema is identical. Additive; unused unless
-- cfg["vpn"]["enabled"] is true.

CREATE TABLE IF NOT EXISTS device_vpn (
    device_id TEXT PRIMARY KEY,
    public_key TEXT NOT NULL,
    private_key TEXT NOT NULL,
    vpn_address TEXT NOT NULL UNIQUE,
    config TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
