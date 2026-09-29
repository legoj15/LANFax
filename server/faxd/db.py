"""SQLite job registry (WAL, one shared connection guarded by an RLock)."""
from __future__ import annotations

import logging
import os
import sqlite3
import threading
import uuid
from typing import Any, Iterable, Optional

from .events import utcnow_iso

log = logging.getLogger("faxd.db")

# No `converting`: phase 1 cannot observe it (the bash pipeline converts); phase 4 adds it
# back when faxd converts itself.
JOB_STATES = ("queued", "dialing", "sending", "sent", "failed", "received", "cancelled")
TERMINAL_STATES = ("sent", "failed", "received", "cancelled")

COLUMNS = (
    "id", "direction", "state", "filename", "to_number", "from_number", "subject",
    "origin", "client", "ast_jobid", "pages", "error", "pdf_path",
    "created_at", "updated_at", "finished_at", "client_key", "detail",
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
    finished_at TEXT,
    client_key TEXT,
    detail TEXT
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
CREATE INDEX IF NOT EXISTS job_events_ts ON job_events(ts);
"""

# Run after the column migration: an existing db gains the columns first. An Idempotency-Key
# is unique per caller: the old global index `jobs_client_key` is dropped (a no-op once gone).
# ifnull() so a row without a client still collides with another one without a client.
_INDEXES = """
DROP INDEX IF EXISTS jobs_client_key;
CREATE UNIQUE INDEX IF NOT EXISTS jobs_client_scoped_key ON jobs(ifnull(client, ''), client_key);
CREATE INDEX IF NOT EXISTS jobs_updated ON jobs(updated_at);
"""

# Columns added after the first deployment: (name, type). SQLite cannot ADD a UNIQUE
# column, so uniqueness comes from the jobs_client_scoped_key index (NULL keys never collide).
# `detail` is the latest progress note (AMI Status text, "dial attempt failed", ...).
_ADDED_COLUMNS = (("client_key", "TEXT"), ("detail", "TEXT"))

_NOT_TERMINAL_SQL = "state NOT IN (%s)" % ",".join("?" * len(TERMINAL_STATES))


def new_id(prefix: str) -> str:
    """Job id: 2-letter prefix (lf/sh/in) plus 12 hex chars."""
    return prefix + uuid.uuid4().hex[:12]


class Registry:
    """Job registry. Safe to use from many threads.

    One sqlite connection for the whole process, serialised by an RLock. The
    HTTP server runs a thread per request, so a per-thread connection leaked one
    handle per request; the daemon's write rate is tiny, so one lock is plenty.
    """

    def __init__(self, path: str) -> None:
        self.path = path
        parent = os.path.dirname(os.path.abspath(path))
        os.makedirs(parent, exist_ok=True)
        self._lock = threading.RLock()
        db = sqlite3.connect(path, timeout=30.0, isolation_level=None, check_same_thread=False)
        db.row_factory = sqlite3.Row
        db.execute("PRAGMA journal_mode=WAL")
        db.execute("PRAGMA synchronous=NORMAL")
        db.execute("PRAGMA busy_timeout=30000")
        db.executescript(_SCHEMA)
        have = {r["name"] for r in db.execute("PRAGMA table_info(jobs)")}
        for name, typ in _ADDED_COLUMNS:
            if name not in have:
                db.execute(f"ALTER TABLE jobs ADD COLUMN {name} {typ}")
                log.info("migrated jobs table: added column %s", name)
        db.executescript(_INDEXES)
        self._db: Optional[sqlite3.Connection] = db

    def _conn(self) -> sqlite3.Connection:
        if self._db is None:
            raise sqlite3.ProgrammingError("registry is closed")
        return self._db

    def connection_count(self) -> int:
        """Open sqlite connections held by this registry (0 or 1)."""
        return 0 if self._db is None else 1

    @staticmethod
    def _row(r: Optional[sqlite3.Row]) -> Optional[dict]:
        return dict(r) if r is not None else None

    def _one(self, sql: str, args: Iterable[Any]) -> Optional[dict]:
        with self._lock:
            return self._row(self._conn().execute(sql, tuple(args)).fetchone())

    def get_job(self, job_id: str) -> Optional[dict]:
        return self._one("SELECT * FROM jobs WHERE id=?", (job_id,))

    def upsert_job(self, job: dict) -> dict:
        """Insert or partially update by id; unspecified fields keep their value."""
        if not job.get("id"):
            raise ValueError("job needs an id")
        fields = {k: v for k, v in job.items() if k in COLUMNS and k not in ("id", "updated_at")}
        now = utcnow_iso()
        with self._lock:
            conn = self._conn()
            conn.execute("BEGIN IMMEDIATE")
            try:
                existing = conn.execute("SELECT 1 FROM jobs WHERE id=?", (job["id"],)).fetchone()
                if existing is None:
                    fields.setdefault("created_at", now)
                    cols = ["id", "updated_at", *fields]
                    vals = [job["id"], now, *fields.values()]
                    conn.execute(
                        f"INSERT INTO jobs ({','.join(cols)}) VALUES ({','.join('?' * len(cols))})",
                        vals)
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
        return self.set_state_if(job_id, None, state, pages=pages, error=error, detail=detail)

    def set_state_if(self, job_id: str, expected_states: Optional[Iterable[str]], state: str, *,
                     pages: Optional[int] = None, error: Optional[str] = None,
                     detail: Optional[str] = None) -> Optional[dict]:
        """set_state only when the current state is in `expected_states` (None = any).

        Returns the new row plus `event_id` (the job_events rowid of this transition, used
        as the SSE event id), or None when the job is missing or in another state.
        Check and write happen in one transaction, so a concurrent `sent` wins.
        """
        if state not in JOB_STATES:
            raise ValueError(f"unknown state {state!r}")
        expected = None if expected_states is None else tuple(expected_states)
        now = utcnow_iso()
        with self._lock:
            conn = self._conn()
            conn.execute("BEGIN IMMEDIATE")
            try:
                cur = conn.execute("SELECT state FROM jobs WHERE id=?", (job_id,)).fetchone()
                if cur is None or (expected is not None and cur["state"] not in expected):
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
                if detail is not None:
                    sets.append("detail=?")
                    vals.append(detail)
                if state in TERMINAL_STATES:
                    sets.append("finished_at=?")
                    vals.append(now)
                conn.execute(f"UPDATE jobs SET {','.join(sets)} WHERE id=?", [*vals, job_id])
                event_id = conn.execute(
                    "INSERT INTO job_events (job_id, ts, state, detail) VALUES (?,?,?,?)",
                    (job_id, now, state, detail)).lastrowid
                conn.execute("COMMIT")
            except BaseException:
                conn.execute("ROLLBACK")
                raise
            row = self.get_job(job_id)
        if row is not None:
            row["event_id"] = event_id
        return row

    def annotate(self, job_id: str, *, pages: Optional[int] = None,
                 detail: Optional[str] = None) -> Optional[dict]:
        """Record pages/detail on an unfinished job without a state change: no job_events row
        (those are transitions only). None when the job is missing, finished, or nothing
        changed; otherwise the updated row (no `event_id`)."""
        with self._lock:
            conn = self._conn()
            conn.execute("BEGIN IMMEDIATE")
            try:
                cur = conn.execute("SELECT state, pages, detail FROM jobs WHERE id=?",
                                   (job_id,)).fetchone()
                sets: list[str] = []
                vals: list[Any] = []
                if cur is not None and cur["state"] not in TERMINAL_STATES:
                    if pages is not None and cur["pages"] != pages:
                        sets.append("pages=?")
                        vals.append(pages)
                    if detail is not None and cur["detail"] != detail:
                        sets.append("detail=?")
                        vals.append(detail)
                if not sets:
                    conn.execute("ROLLBACK")
                    return None
                conn.execute(f"UPDATE jobs SET {','.join(sets)},updated_at=? WHERE id=?",
                             [*vals, utcnow_iso(), job_id])
                conn.execute("COMMIT")
            except BaseException:
                conn.execute("ROLLBACK")
                raise
            return self.get_job(job_id)

    def get_jobs(self, ids: Iterable[str]) -> list[dict]:
        """Rows for these ids in one query per 500 ids (missing ids are skipped; any order)."""
        ids = list(dict.fromkeys(ids))
        out: list[dict] = []
        with self._lock:
            for i in range(0, len(ids), 500):
                chunk = ids[i:i + 500]
                out.extend(dict(r) for r in self._conn().execute(
                    f"SELECT * FROM jobs WHERE id IN ({','.join('?' * len(chunk))})", chunk))
        return out

    def find_by_filename(self, filename: Optional[str]) -> Optional[dict]:
        """Newest row with this share filename, in any state."""
        if not filename:
            return None
        return self._one(
            "SELECT * FROM jobs WHERE filename=? ORDER BY created_at DESC, rowid DESC LIMIT 1",
            (filename,))

    def find_active_by_filename(self, filename: Optional[str]) -> Optional[dict]:
        """Newest non-terminal row with this filename (names are reused after a job ends)."""
        if not filename:
            return None
        return self._one(
            f"SELECT * FROM jobs WHERE filename=? AND {_NOT_TERMINAL_SQL} "
            "ORDER BY created_at DESC, rowid DESC LIMIT 1",
            (filename, *TERMINAL_STATES))

    def find_by_client_key(self, client_key: Optional[str],
                           client: Optional[str]) -> Optional[dict]:
        """The row this caller (`client`) created with this Idempotency-Key, if any. Keys are
        scoped per caller: another client's identical key is a different request."""
        if not client_key:
            return None
        return self._one("SELECT * FROM jobs WHERE ifnull(client, '')=? AND client_key=?",
                         (client or "", client_key))

    def find_by_ast_jobid(self, ast_jobid: Optional[str]) -> Optional[dict]:
        if not ast_jobid:
            return None
        return self._one(
            "SELECT * FROM jobs WHERE ast_jobid=? ORDER BY created_at DESC, rowid DESC LIMIT 1",
            (ast_jobid,))

    def count_by_state(self, *, direction: Optional[str] = None) -> dict[str, int]:
        """{state: count} in one query; states with no rows are absent."""
        sql, args = "SELECT state, COUNT(*) AS n FROM jobs", ()
        if direction:
            sql, args = sql + " WHERE direction=?", (direction,)
        with self._lock:
            rows = self._conn().execute(sql + " GROUP BY state", args).fetchall()
        return {r["state"]: r["n"] for r in rows}

    def list_jobs(self, *, direction: Optional[str] = None,
                  states: Optional[Iterable[str]] = None, limit: int = 100,
                  since: Optional[str] = None,
                  updated_since: Optional[str] = None) -> list[dict]:
        """Newest first. `states=[]` matches nothing; None means any.

        `since` filters on created_at, `updated_since` on updated_at (both inclusive,
        ISO-8601 UTC strings compared as text)."""
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
        if updated_since:
            where.append("updated_at>=?")
            vals.append(updated_since)
        sql = "SELECT * FROM jobs"
        if where:
            sql += " WHERE " + " AND ".join(where)
        sql += " ORDER BY created_at DESC, rowid DESC LIMIT ?"
        vals.append(int(limit))
        with self._lock:
            return [dict(r) for r in self._conn().execute(sql, vals).fetchall()]

    def job_events(self, job_id: str) -> list[dict]:
        with self._lock:
            rows = self._conn().execute(
                "SELECT * FROM job_events WHERE job_id=? ORDER BY id", (job_id,)).fetchall()
        return [dict(r) for r in rows]

    def last_event_id(self, job_id: str) -> Optional[int]:
        """Rowid of the job's newest job_events row (None if it has none)."""
        with self._lock:
            r = self._conn().execute("SELECT MAX(id) AS m FROM job_events WHERE job_id=?",
                                     (job_id,)).fetchone()
        return r["m"] if r is not None else None

    def max_event_id(self) -> Optional[int]:
        """Newest job_events rowid overall (None while there are none)."""
        with self._lock:
            r = self._conn().execute("SELECT MAX(id) AS m FROM job_events").fetchone()
        return r["m"] if r is not None else None

    def prune_events(self, before_iso: str) -> int:
        """Delete job_events rows older than `before_iso` (UTC ISO text); jobs are kept.
        Returns the number of rows deleted."""
        with self._lock:
            return self._conn().execute("DELETE FROM job_events WHERE ts<?",
                                        (before_iso,)).rowcount

    def job_events_since(self, rowid: int, limit: int = 500) -> list[dict]:
        """job_events rows with id > rowid, oldest first (SSE Last-Event-ID replay)."""
        with self._lock:
            rows = self._conn().execute(
                "SELECT * FROM job_events WHERE id>? ORDER BY id LIMIT ?",
                (int(rowid), int(limit))).fetchall()
        return [dict(r) for r in rows]

    def close(self) -> None:
        """Close the shared connection; later calls raise sqlite3.ProgrammingError."""
        with self._lock:
            db, self._db = self._db, None
        if db is not None:
            try:
                db.close()
            except sqlite3.Error:
                pass
