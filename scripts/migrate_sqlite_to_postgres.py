"""One-shot migration: copy all Connector data from SQLite into PostgreSQL.

Usage:
    python migrate_sqlite_to_postgres.py \
        --sqlite C:\\Connector\\runtime\\connector.db \
        --pg-url postgresql://connector_user:PASS@127.0.0.1:5432/connector_prod \
        [--force]

Steps:
    1. Connect to source SQLite (read-only).
    2. Connect to destination PostgreSQL.
    3. Apply yoyo migrations on PG (idempotent — no-ops if already present).
    4. For each table: count source rows; abort if dest non-empty (unless --force).
    5. INSERT rows from SQLite into PG, preserving primary key ids (including
       audit_events.id which is BIGSERIAL — sequence bumped at the end).
    6. Verify dest counts match source. Abort with non-zero exit if mismatch.

Designed to be idempotent in the failure case: if step 5 fails partway, the
PG transaction rolls back, so re-running starts clean.

Read-only on SQLite — safe to run while SQLite is in use, but recommended to
stop the server first to ensure consistent snapshot.
"""

from __future__ import annotations

import argparse
import logging
import sqlite3
import sys
from pathlib import Path

import psycopg2
import psycopg2.extras


LOG = logging.getLogger("migrate_sqlite_to_postgres")

# Order matters only if we had FKs — current Connector schema has no FK
# constraints, but sticking with a deterministic order helps debugging.
TABLES_IN_ORDER = [
    "admin_user_roles",
    "devices",
    "device_tokens",
    "device_access",
    "device_sessions",
    "device_web_access",
    "tekla_client_state",
    "audit_events",  # has BIGSERIAL id — handled specially
]

# audit_events has the only autogen id column. After we INSERT preserved ids,
# we need to advance the sequence so future inserts on PG don't collide.
TABLES_WITH_AUTOGEN_ID = {
    "audit_events": ("audit_events_id_seq", "id"),
}


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--sqlite", required=True, help="Path to source SQLite DB")
    p.add_argument("--pg-url", required=True, help="postgresql:// URL of destination")
    p.add_argument("--force", action="store_true", help="Truncate non-empty dest tables before insert")
    p.add_argument("--dry-run", action="store_true", help="Connect, count rows, but don't write")
    return p.parse_args()


def get_columns(sqlite_conn: sqlite3.Connection, table: str) -> list[str]:
    rows = sqlite_conn.execute(f"PRAGMA table_info({table})").fetchall()
    return [r[1] for r in rows]


def count_rows(conn, table: str) -> int:
    cur = conn.cursor()
    cur.execute(f"SELECT count(*) FROM {table}")
    n = cur.fetchone()[0]
    cur.close()
    return int(n)


def apply_pg_migrations(pg_url: str) -> None:
    from yoyo import get_backend, read_migrations

    server_dir = Path(__file__).resolve().parent.parent / "connector" / "server"
    migrations_dir = server_dir / "migrations" / "postgres"
    if not migrations_dir.is_dir():
        raise FileNotFoundError(f"PG migrations dir not found: {migrations_dir}")

    backend = get_backend(pg_url)
    migrations = read_migrations(str(migrations_dir))
    with backend.lock():
        pending = backend.to_apply(migrations)
        if not pending:
            LOG.info("PG migrations: no pending")
            return
        LOG.info("PG migrations pending: %s", ", ".join(m.id for m in pending))
        backend.apply_migrations(pending)
        LOG.info("PG migrations applied")


def copy_table(
    sqlite_conn: sqlite3.Connection,
    pg_conn,
    table: str,
    force: bool,
    dry_run: bool,
) -> tuple[int, int]:
    src_count = count_rows(sqlite_conn, table)

    pg_cur = pg_conn.cursor()
    pg_cur.execute(f"SELECT count(*) FROM {table}")
    dst_count_before = int(pg_cur.fetchone()[0])

    if dst_count_before > 0:
        if not force:
            raise SystemExit(
                f"Destination table {table} already has {dst_count_before} rows. "
                f"Use --force to truncate before insert."
            )
        LOG.warning("Truncating %s (had %d rows)", table, dst_count_before)
        if not dry_run:
            pg_cur.execute(f"TRUNCATE TABLE {table} RESTART IDENTITY CASCADE")

    if src_count == 0:
        LOG.info("%-22s src=0 dst=%d (skip)", table, dst_count_before)
        pg_cur.close()
        return src_count, dst_count_before

    cols = get_columns(sqlite_conn, table)
    cols_csv = ", ".join(cols)
    placeholders = ", ".join(["%s"] * len(cols))
    insert_sql = f"INSERT INTO {table} ({cols_csv}) VALUES ({placeholders})"

    rows = sqlite_conn.execute(f"SELECT {cols_csv} FROM {table}").fetchall()

    if dry_run:
        LOG.info("%-22s src=%d (DRY-RUN: would insert)", table, src_count)
        pg_cur.close()
        return src_count, dst_count_before

    psycopg2.extras.execute_batch(pg_cur, insert_sql, rows, page_size=500)

    # Bump sequence for autogen-id tables to max(id)+1
    if table in TABLES_WITH_AUTOGEN_ID:
        seq_name, id_col = TABLES_WITH_AUTOGEN_ID[table]
        pg_cur.execute(
            f"SELECT setval(%s, COALESCE((SELECT max({id_col}) FROM {table}), 1), true)",
            (seq_name,),
        )

    pg_cur.execute(f"SELECT count(*) FROM {table}")
    dst_count_after = int(pg_cur.fetchone()[0])
    pg_cur.close()

    LOG.info("%-22s src=%d dst=%d %s", table, src_count, dst_count_after, "OK" if src_count == dst_count_after else "MISMATCH!")
    return src_count, dst_count_after


def main() -> int:
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    args = parse_args()

    sqlite_path = Path(args.sqlite).resolve()
    if not sqlite_path.exists():
        LOG.error("SQLite source not found: %s", sqlite_path)
        return 2

    LOG.info("Source SQLite: %s", sqlite_path)
    LOG.info("Destination PG: %s", _redact(args.pg_url))
    LOG.info("Mode: %s", "DRY-RUN" if args.dry_run else ("FORCE" if args.force else "STRICT (abort if dest non-empty)"))

    if not args.dry_run:
        apply_pg_migrations(args.pg_url)

    sqlite_conn = sqlite3.connect(f"file:{sqlite_path.as_posix()}?mode=ro", uri=True)
    try:
        with psycopg2.connect(args.pg_url) as pg_conn:
            results: dict[str, tuple[int, int]] = {}
            for table in TABLES_IN_ORDER:
                src, dst = copy_table(sqlite_conn, pg_conn, table, args.force, args.dry_run)
                results[table] = (src, dst)
            if args.dry_run:
                pg_conn.rollback()

        mismatches = [
            (t, s, d) for t, (s, d) in results.items() if not args.dry_run and s != d
        ]
        if mismatches:
            for t, s, d in mismatches:
                LOG.error("MISMATCH %s: src=%d dst=%d", t, s, d)
            return 3

        total_src = sum(s for s, _ in results.values())
        total_dst = sum(d for _, d in results.values())
        LOG.info("DONE: total_src=%d total_dst=%d", total_src, total_dst)
        return 0
    finally:
        sqlite_conn.close()


def _redact(url: str) -> str:
    import re

    return re.sub(r"://([^:/@]+):([^@]+)@", r"://\1:***@", url)


if __name__ == "__main__":
    sys.exit(main())
