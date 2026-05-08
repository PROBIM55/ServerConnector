-- 0001 baseline schema (PostgreSQL flavor)
-- depends:
--
-- Mirror of migrations/sqlite/0001_baseline_schema.sql adapted for PostgreSQL.
-- Differences from SQLite version:
--   * audit_events.id: BIGSERIAL instead of INTEGER PRIMARY KEY AUTOINCREMENT
--   * Other column types stay TEXT/INTEGER/NULL — both backends accept them.
--
-- На пустой connector_prod создаст полную схему с нуля. Первый раз должен
-- запускаться после migrate_sqlite_to_postgres.py, который УЖЕ создаст
-- таблицы и зальёт данные — yoyo тут будет no-op (CREATE TABLE IF NOT EXISTS),
-- но мы всё равно отметим миграцию как applied для согласованности с SQLite-веткой.

CREATE TABLE IF NOT EXISTS admin_user_roles (
    username TEXT PRIMARY KEY,
    is_system_admin INTEGER NOT NULL DEFAULT 0,
    is_firm_admin INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS audit_events (
    id BIGSERIAL PRIMARY KEY,
    event_type TEXT NOT NULL,
    device_id TEXT,
    actor TEXT,
    details TEXT,
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS device_access (
    device_id TEXT PRIMARY KEY,
    smb_login TEXT NOT NULL,
    smb_username TEXT NOT NULL,
    smb_password TEXT NOT NULL,
    smb_share_unc TEXT NOT NULL,
    smb_share_path TEXT,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS device_sessions (
    device_id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL,
    hostname TEXT,
    public_ip TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS device_tokens (
    device_id TEXT PRIMARY KEY,
    token_hash TEXT NOT NULL,
    issued_to TEXT,
    created_at TEXT NOT NULL,
    last_used_at TEXT,
    revoked_at TEXT,
    token_value TEXT
);

CREATE TABLE IF NOT EXISTS device_web_access (
    device_id TEXT PRIMARY KEY,
    speckle_url TEXT,
    speckle_login TEXT,
    speckle_password TEXT,
    nextcloud_url TEXT,
    nextcloud_login TEXT,
    nextcloud_password TEXT,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS devices (
    device_id TEXT PRIMARY KEY,
    public_ip TEXT NOT NULL,
    hostname TEXT,
    agent_version TEXT,
    updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS tekla_client_state (
    device_id TEXT PRIMARY KEY,
    installed_revision TEXT,
    target_revision TEXT,
    pending_after_close INTEGER,
    tekla_running INTEGER,
    last_check_utc TEXT,
    last_success_utc TEXT,
    updated_at TEXT NOT NULL,
    last_error TEXT,
    installed_version TEXT,
    target_version TEXT
);
