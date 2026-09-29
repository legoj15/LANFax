"""SQLite job registry (WAL, one connection per thread)."""
from __future__ import annotations

import logging
import os
import sqlite3
import threading
import uuid
from typing import Any, Iterable, Optional

from .events import utcnow_iso

log = logging.getLogger("faxd.db")

JOB_STATES = ("queued", "converting", "dialing", "sending", "sent", "failed", "received")
TERMINAL_STATES = ("sent", "failed", "received")

COLUMNS = (
    "id", "direction", "state", "filename", "to_number", "from_number", "subject",
    "origin", "client", "ast_jobid", "pages", "error", "pdf_path",
    "created_at", "updated_at", "finished_at",
)

_SCHEMA = """
CREATE TABLE IF NOT EXISTS jobs (
    id TEXT PRIMARY KEY,
    direction TEXT NOT NULL DEFAULT 'out',
    state TEXT NOT NULL DEFAULT 'queued',
    filename TEXT,
    to_number TEXT,
    from_number TEXT,
    subject TEXT,
    origin TEXT,
    client TEXT,
    ast_jobid TEXT,
    pages INTEGER,
    error TEXT,
    pdf_path TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    finished_at TEXT
);
CREATE INDEX IF NOT EXISTS jobs_filename ON jobs(filename);
CREATE INDEX IF NOT EXISTS jobs_ast ON jobs(ast_jobid);
CREATE INDEX IF NOT EXISTS jobs_created ON jobs(created_at);
CREATE TABLE IF NOT EXISTS job_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    job_id TEXT NOT NULL,
    ts TEXT NOT NULL,
    state TEXT,
    detail TEXT
);
CREATE INDEX IF NOT EXISTS job_events_job ON job_events(job_id);
"""


def new_id(prefix: str) -> str:
    """Job id: 2-letter prefix (lf/sh/in) plus 12 hex chars."""
    return prefix + uuid.uuid4().hex[:12]


class Registry:
    """Job registry. Safe to use from many threads."""

    def __init__(self, path: str) -> None:
        self.path = path
        parent = os.path.dirname(os.path.abspath(path))
        os.makedirs(parent, exist_ok=True)
        self._local = threading.local()
        self._conns: list[sqlite3.Connection] = []
        self._conns_lock = threading.Lock()
        conn = self._conn()
        conn.executescript(_SCHEMA)
        conn.commit()

    def _conn(self) -> sqlite3.Connection:
        conn = getattr(self._local, "conn", None)
        if conn is None:
            conn = sqlite3.connect(self.path, timeout=30.0, isolation_level=None,
                                   check_same_thread=False)
            conn.row_factory = sqlite3.Row
            conn.execute("PRAGMA journal_mode=WAL")
            conn.execute("PRAGMA synchronous=NORMAL")
            conn.execute("PRAGMA busy_timeout=30000")
            self._local.conn = conn
            with self._conns_lock:
                self._conns.append(conn)
        return conn

    @staticmethod
    def _row(r: Optional[sqlite3.Row]) -> Optional[dict]:
        return dict(r) if r is not None else None

    def get_job(self, job_id: str) -> Optional[dict]:
        return self._row(self._conn().execute("SELECT * FROM jobs WHERE id=?", (job_id,)).fetchone())

    def upsert_job(self, job: dict) -> dict:
        """Insert or partially update by id; unspecified fields keep their value."""
        if not job.get("id"):
            raise ValueError("job needs an id")
        fields = {k: v for k, v in job.items() if k in COLUMNS and k not in ("id", "updated_at")}
        now = utcnow_iso()
        conn = self._conn()
        conn.execute("BEGIN IMMEDIATE")
        try:
            existing = conn.execute("SELECT 1 FROM jobs WHERE id=?", (job["id"],)).fetchone()
            if existing is None:
                fields.setdefault("created_at", now)
                cols = ["id", "updated_at", *fields]
                vals = [job["id"], now, *fields.values()]
                conn.execute(
                    f"INSERT INTO jobs ({','.join(cols)}) VALUES ({','.join('?' * len(cols))})", vals)
            else:
                sets = ["updated_at=?", *[f"{k}=?" for k in fields]]
                conn.execute(f"UPDATE jobs SET {','.join(sets)} WHERE id=?",
                             [now, *fields.values(), job["id"]])
            conn.execute("COMMIT")
        except BaseException:
            conn.execute("ROLLBACK")
            raise
        return self.get_job(job["id"])  # type: ignore[return-value]

    def set_state(self, job_id: str, state: str, *, pages: Optional[int] = None,
                  error: Optional[str] = None, detail: Optional[str] = None) -> Optional[dict]:
        """Change state, append a job_events row, stamp finished_at on terminal states."""
        if state not in JOB_STATES:
            raise ValueError(f"unknown state {state!r}")
        now = utcnow_iso()
        conn = self._conn()
        conn.execute("BEGIN IMMEDIATE")
        try:
            if conn.execute("SELECT 1 FROM jobs WHERE id=?", (job_id,)).fetchone() is None:
                conn.execute("ROLLBACK")
                return None
            sets = ["state=?", "updated_at=?"]
            vals: list[Any] = [state, now]
            if pages is not None:
                sets.append("pages=?")
                vals.append(pages)
            if error is not None:
                sets.append("error=?")
                vals.append(error)
            if state in TERMINAL_STATES:
                sets.append("finished_at=?")
                vals.append(now)
            conn.execute(f"UPDATE jobs SET {','.join(sets)} WHERE id=?", [*vals, job_id])
            conn.execute("INSERT INTO job_events (job_id, ts, state, detail) VALUES (?,?,?,?)",
                         (job_id, now, state, detail))
            conn.execute("COMMIT")
        except BaseException:
            conn.execute("ROLLBACK")
            raise
        return self.get_job(job_id)

    def find_by_filename(self, filename: Optional[str]) -> Optional[dict]:
        if not filename:
            return None
        return self._row(self._conn().execute(
            "SELECT * FROM jobs WHERE filename=? ORDER BY created_at DESC, rowid DESC LIMIT 1",
            (filename,)).fetchone())

    def find_by_ast_jobid(self, ast_jobid: Optional[str]) -> Optional[dict]:
        if not ast_jobid:
            return None
        return self._row(self._conn().execute(
            "SELECT * FROM jobs WHERE ast_jobid=? ORDER BY created_at DESC, rowid DESC LIMIT 1",
            (ast_jobid,)).fetchone())

    def list_jobs(self, *, direction: Optional[str] = None,
                  states: Optional[Iterable[str]] = None, limit: int = 100,
                  since: Optional[str] = None) -> list[dict]:
        """Newest first. `states=[]` matches nothing; None means any."""
        where: list[str] = []
        vals: list[Any] = []
        if direction:
            where.append("direction=?")
            vals.append(direction)
        if states is not None:
            st = list(states)
            if not st:
                return []
            where.append(f"state IN ({','.join('?' * len(st))})")
            vals.extend(st)
        if since:
            where.append("created_at>=?")
            vals.append(since)
        sql = "SELECT * FROM jobs"
        if where:
            sql += " WHERE " + " AND ".join(where)
        sql += " ORDER BY created_at DESC, rowid DESC LIMIT ?"
        vals.append(int(limit))
        return [dict(r) for r in self._conn().execute(sql, vals).fetchall()]

    def job_events(self, job_id: str) -> list[dict]:
        rows = self._conn().execute(
            "SELECT * FROM job_events WHERE job_id=? ORDER BY id", (job_id,)).fetchall()
        return [dict(r) for r in rows]

    def close(self) -> None:
        """Close every per-thread connection (call when no other thread is using it)."""
        with self._conns_lock:
            conns, self._conns = self._conns, []
        for c in conns:
            try:
                c.close()
            except sqlite3.Error:
                pass
        self._local = threading.local()
