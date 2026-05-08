"""Backend-agnostic DB connection helper.

Resolves `CONNECTOR_DB_URL` environment variable:
* `postgresql://user:pass@host:port/database` → psycopg2-backed connection
* `sqlite:///abs/path/to.db`                  → sqlite3 connection
* unset                                       → sqlite at the legacy DB_PATH

For PostgreSQL, the wrapper translates `?` placeholders to `%s` on the fly so
the rest of the codebase stays sqlite-style. We use a regex that skips
single-quoted string literals to avoid mangling SQL like `WHERE col = '?'`.

The wrapper exposes the subset of `sqlite3.Connection` API used by `app.py`:
`execute`, `executemany`, `commit`, `rollback`, `close`, plus context-manager
semantics that match sqlite3 (commit on success, rollback on error, do NOT
close the connection automatically).
"""

from __future__ import annotations

import os
import re
import sqlite3
from pathlib import Path


def get_db_url(default_sqlite_path: Path) -> str:
    raw = os.environ.get("CONNECTOR_DB_URL", "").strip()
    if raw:
        return raw
    return f"sqlite:///{default_sqlite_path.as_posix()}"


def is_postgres_url(url: str) -> bool:
    return url.startswith("postgresql://") or url.startswith("postgres://")


def is_sqlite_url(url: str) -> bool:
    return url.startswith("sqlite:///")


def sqlite_path_from_url(url: str) -> str:
    if not is_sqlite_url(url):
        raise ValueError(f"Not a sqlite URL: {url}")
    return url[len("sqlite:///") :]


def connect(url: str):
    """Open a DB connection.  Caller controls lifecycle."""
    if is_postgres_url(url):
        import psycopg2

        return _PgConnectionWrapper(psycopg2.connect(url))

    if is_sqlite_url(url):
        return sqlite3.connect(sqlite_path_from_url(url))

    raise ValueError(f"Unsupported DB URL: {url[:32]}...")


# ---- PostgreSQL adapter ---------------------------------------------------

_QMARK_PATTERN = re.compile(r"(\?)|('(?:[^']|'')*')", re.DOTALL)


def _translate_qmark(sql: str) -> str:
    """Convert ? placeholders to %s, leaving single-quoted strings intact."""

    def replace(match: re.Match) -> str:
        if match.group(1):
            return "%s"
        return match.group(0)

    return _QMARK_PATTERN.sub(replace, sql)


class _PgCursorWrapper:
    """Thin wrapper exposing the cursor methods that app.py uses."""

    def __init__(self, cursor) -> None:
        self._cursor = cursor

    def fetchone(self):
        return self._cursor.fetchone()

    def fetchall(self):
        return self._cursor.fetchall()

    def fetchmany(self, size=None):
        if size is None:
            return self._cursor.fetchmany()
        return self._cursor.fetchmany(size)

    def __iter__(self):
        return iter(self._cursor)

    @property
    def rowcount(self):
        return self._cursor.rowcount

    def close(self):
        self._cursor.close()


class _PgConnectionWrapper:
    """psycopg2 connection wrapped to look like sqlite3.Connection.

    The `with` semantics mirror sqlite3: commit on clean exit, rollback on
    error, do NOT close the connection.  Closing is the caller's job.
    """

    def __init__(self, conn) -> None:
        self._conn = conn

    def execute(self, sql, params=None):
        cursor = self._conn.cursor()
        cursor.execute(_translate_qmark(sql), params or ())
        return _PgCursorWrapper(cursor)

    def executemany(self, sql, params):
        cursor = self._conn.cursor()
        cursor.executemany(_translate_qmark(sql), params)
        return _PgCursorWrapper(cursor)

    def commit(self):
        self._conn.commit()

    def rollback(self):
        self._conn.rollback()

    def close(self):
        self._conn.close()

    def cursor(self):
        return self._conn.cursor()

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        if exc_type is not None:
            self._conn.rollback()
        else:
            self._conn.commit()
        return False
