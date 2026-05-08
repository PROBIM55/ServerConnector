"""Apply pending DB migrations using yoyo-migrations.

Usage (production):
    python run_migrations.py

Reads CONNECTOR_DB_PATH from env (same as app.py); falls back to
BASE_DIR/connector.db for local dev. Migrations directory is
``migrations/`` next to this script. Exits 0 on success, non-zero on
any failure (CI deploy step should treat that as a fatal abort).
"""

from __future__ import annotations

import logging
import os
import sys
from pathlib import Path

from yoyo import get_backend, read_migrations


LOG = logging.getLogger("run_migrations")


def _resolve_db_path() -> Path:
    base_dir = Path(__file__).resolve().parent
    raw = os.environ.get("CONNECTOR_DB_PATH", "").strip()
    return Path(raw).resolve() if raw else base_dir / "connector.db"


def _resolve_migrations_dir() -> Path:
    return Path(__file__).resolve().parent / "migrations"


def main() -> int:
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s %(message)s",
    )

    db_path = _resolve_db_path()
    migrations_dir = _resolve_migrations_dir()

    if not migrations_dir.is_dir():
        LOG.error("Migrations directory not found: %s", migrations_dir)
        return 2

    LOG.info("DB path:         %s", db_path)
    LOG.info("Migrations dir:  %s", migrations_dir)

    if not db_path.parent.exists():
        db_path.parent.mkdir(parents=True, exist_ok=True)

    backend = get_backend(f"sqlite:///{db_path.as_posix()}")
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


if __name__ == "__main__":
    sys.exit(main())
