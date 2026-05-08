"""Тест что run_migrations.py применяет baseline и идемпотентен."""

import os
import sqlite3
import subprocess
import sys
from pathlib import Path

SERVER_DIR = Path(__file__).resolve().parent.parent


def _run_migrations(db_path: Path) -> subprocess.CompletedProcess:
    env = os.environ.copy()
    env["CONNECTOR_DB_PATH"] = str(db_path)
    return subprocess.run(
        [sys.executable, str(SERVER_DIR / "run_migrations.py")],
        capture_output=True,
        text=True,
        env=env,
        timeout=30,
    )


def test_migrations_apply_baseline_to_fresh_db(tmp_path):
    db = tmp_path / "fresh.db"

    result = _run_migrations(db)
    assert result.returncode == 0, f"first run failed: {result.stderr}"
    assert db.exists()

    # baseline должен создать 8 таблиц (плюс _yoyo_*)
    conn = sqlite3.connect(db)
    try:
        rows = conn.execute(
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '\\_yoyo%' ESCAPE '\\'"
        ).fetchall()
    finally:
        conn.close()
    table_names = {r[0] for r in rows}
    expected = {
        "admin_user_roles",
        "audit_events",
        "device_access",
        "device_sessions",
        "device_tokens",
        "device_web_access",
        "devices",
        "tekla_client_state",
    }
    assert expected.issubset(table_names), f"missing tables: {expected - table_names}"


def test_migrations_are_idempotent(tmp_path):
    db = tmp_path / "idempotent.db"

    first = _run_migrations(db)
    assert first.returncode == 0

    second = _run_migrations(db)
    assert second.returncode == 0
    assert "No pending migrations" in (second.stdout + second.stderr)
