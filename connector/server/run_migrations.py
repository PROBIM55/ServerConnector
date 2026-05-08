"""Apply pending DB migrations using yoyo-migrations.

Usage (production):
    python run_migrations.py

Reads `CONNECTOR_DB_URL` from env (same as app.py); falls back to
`sqlite:///<CONNECTOR_DB_PATH or BASE_DIR/connector.db>`.

Migrations live in `migrations/<backend>/` (postgres or sqlite). The runner
picks the directory based on the URL scheme so each backend has its own
baseline schema (PG uses BIGSERIAL, SQLite uses INTEGER PRIMARY KEY
AUTOINCREMENT).

Exits 0 on success, non-zero on any failure (CI deploy step treats that as a
fatal abort and triggers rollback).
"""

from __future__ import annotations

import logging
import os
import sys
from pathlib import Path

from yoyo import get_backend, read_migrations


LOG = logging.getLogger("run_migrations")
BASE_DIR = Path(__file__).resolve().parent


def _resolve_db_url() -> str:
    raw = os.environ.get("CONNECTOR_DB_URL", "").strip()
    if raw:
        return raw
    raw_path = os.environ.get("CONNECTOR_DB_PATH", "").strip()
    if raw_path:
        return f"sqlite:///{Path(raw_path).resolve().as_posix()}"
    return f"sqlite:///{(BASE_DIR / 'connector.db').as_posix()}"


def _backend_name_from_url(url: str) -> str:
    if url.startswith("postgresql://") or url.startswith("postgres://"):
        return "postgres"
    if url.startswith("sqlite:///"):
        return "sqlite"
    raise ValueError(f"Unsupported DB URL scheme: {url[:32]}...")


def _resolve_migrations_dir(url: str) -> Path:
    return BASE_DIR / "migrations" / _backend_name_from_url(url)


def main() -> int:
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s %(message)s",
    )

    url = _resolve_db_url()
    migrations_dir = _resolve_migrations_dir(url)

    if not migrations_dir.is_dir():
        LOG.error("Migrations directory not found: %s", migrations_dir)
        return 2

    LOG.info("DB URL:          %s", _redact_url(url))
    LOG.info("Migrations dir:  %s", migrations_dir)

    backend = get_backend(url)
    migrations = read_migrations(str(migrations_dir))

    with backend.lock():
        pending = backend.to_apply(migrations)
        if not pending:
            LOG.info("No pending migrations.")
            return 0

        LOG.info("Pending migrations: %s", ", ".join(m.id for m in pending))
        backend.apply_migrations(pending)
        LOG.info("Applied %d migration(s).", len(pending))

    return 0


def _redact_url(url: str) -> str:
    """Strip password from postgres URL for logging."""
    import re

    return re.sub(r"://([^:/@]+):([^@]+)@", r"://\1:***@", url)


if __name__ == "__main__":
    sys.exit(main())
