"""Read-only observer: derives job state from the share, outqueue and ARM-STATUS.txt.

Never writes, moves or deletes anything under the share or the outqueue.
"""
from __future__ import annotations

import json
import logging
import os
import re
import threading
import time
from datetime import datetime, timedelta, timezone
from typing import Any, Optional

from .config import Config
from .db import Registry, TERMINAL_STATES, new_id
from .arm import parse_status
from .enqueue import normalize_number
from .events import EventBus, utcnow_iso
from .timeutil import date_cmd_to_utc_iso, local_to_utc_iso, to_utc_iso

log = logging.getLogger("faxd.spool")

_OUTBOX_EXTS = (".pdf", ".tif", ".tiff")
_INBOX_RE = re.compile(r"^fax_(\d{8}-\d{6})_from_(.+)\.pdf$", re.IGNORECASE)
_RECV_FAIL_RE = re.compile(r"^recv_(\d{8}-\d{6})_from_(.+)\.txt$", re.IGNORECASE)
_JOBID_RE = re.compile(r"^j\d{8}-\d{6}-")
_API_ID_RE = re.compile(r"^lf[0-9a-f]{12}$")
_ACTIVE = ("queued", "dialing", "sending")
_STAMP = "%Y%m%d-%H%M%S"
MIN_POLL = 0.2              # seconds; a 0/negative FAXD_POLL_INTERVAL must not busy-loop
STUCK_QUEUED_AFTER = 300    # seconds a queued row may point at a missing file
PRUNE_EVERY = 3600          # seconds between job_events retention passes


def _norm(s: str) -> str:
    """Lenient wrapper over enqueue.normalize_number: never raises (observer input is untrusted)."""
    try:
        return normalize_number(s)
    except ValueError:
        return re.sub(r"[^0-9]", "", s or "")


def _unescape(v: str) -> str:
    """Undo bash %q quoting: quotes around the value and backslash escapes."""
    v = v.strip()
    if len(v) >= 2 and v[0] == "'" and v[-1] == "'":
        return v[1:-1]
    if len(v) >= 2 and v[0] == '"' and v[-1] == '"':
        v = v[1:-1]
    return re.sub(r"\\(.)", r"\1", v)


def _read_text(path: str) -> Optional[str]:
    """Read a text file; None if it vanished or is unreadable."""
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            return f.read()
    except FileNotFoundError:
        return None
    except OSError as e:
        log.warning("cannot read %s: %s", path, e)
        return None


def parse_env_file(path: str) -> dict[str, str]:
    """Parse KEY=value lines of a call-file .env (bash %q quoted values)."""
    out: dict[str, str] = {}
    text = _read_text(path)
    for line in (text or "").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, val = line.partition("=")
        key = key.strip()
        if key:
            out[key] = _unescape(val)
    return out


def parse_receipt(path: str) -> dict[str, str]:
    """Parse `Key: value` receipt lines into a dict with lower-cased keys."""
    out: dict[str, str] = {}
    for line in (_read_text(path) or "").splitlines():
        key, sep, val = line.partition(":")
        if sep and key.strip().isalpha():
            out.setdefault(key.strip().lower(), val.strip())
    return out


def _listdir(path: str) -> list[str]:
    try:
        return sorted(os.listdir(path))
    except OSError:
        return []


def _isfile(path: str) -> bool:
    try:
        return os.path.isfile(path)
    except OSError:
        return False


def _sig(path: str) -> Optional[tuple[int, int]]:
    """(mtime_ns, size) of a regular file, None if missing."""
    try:
        st = os.stat(path)
    except OSError:
        return None
    return (st.st_mtime_ns, st.st_size)


def _mtime_iso(path: str) -> Optional[str]:
    try:
        return to_utc_iso(datetime.fromtimestamp(os.stat(path).st_mtime))
    except (OSError, ValueError, OverflowError):
        return None


def _to_int(s: Optional[str]) -> Optional[int]:
    try:
        return int(str(s).strip())
    except (TypeError, ValueError):
        return None


class SpoolObserver:
    """Reconciles the registry with what is on disk."""

    def __init__(self, cfg: Config, reg: Registry, bus: EventBus) -> None:
        self.cfg = cfg
        self.reg = reg
        self.bus = bus
        self._arm_sig: Optional[tuple[int, int]] = None
        self._receipts_done: dict[str, tuple[int, int]] = {}  # receipt path -> sig when settled
        # receipt path -> (sig, folder listing hash) when it matched nothing: not re-read until
        # the receipt or the files beside it change
        self._receipts_skipped: dict[str, tuple] = {}
        self._warned: set[str] = set()                        # ambiguous receipts already logged
        self._last_prune: Optional[float] = None              # time.monotonic() of the last prune

    # ---- helpers ------------------------------------------------------
    def _queue_meta(self, filename: Optional[str]) -> dict[str, Any]:
        """Identity of an API job from queue-meta/<filename>.json (written by enqueue), so a
        rebuilt registry keeps the original id, subject, origin, client and client_key.
        Empty when there is no meta file or its id/key is already taken by another row."""
        if not filename:
            return {}
        path = os.path.join(self.cfg.queue_meta, filename + ".json")
        try:
            with open(path, encoding="utf-8") as f:
                meta = json.load(f)
        except FileNotFoundError:
            return {}
        except (OSError, ValueError) as e:
            log.warning("unreadable queue-meta %s: %s", path, e)
            return {}
        mid = meta.get("id") if isinstance(meta, dict) else None
        if not isinstance(mid, str) or not _API_ID_RE.match(mid) or self.reg.get_job(mid):
            return {}
        out: dict[str, Any] = {"id": mid}
        for key in ("subject", "origin", "client", "client_key", "created_at"):
            if isinstance(meta.get(key), str) and meta[key]:
                out[key] = meta[key]
        if out.get("client_key") and self.reg.find_by_client_key(out["client_key"]):
            del out["client_key"]
        return out

    def _create(self, fields: dict[str, Any]) -> dict:
        """Insert a row, log its first state, publish `job`. An outbound share row whose
        file has queue-meta takes its API identity (id, subject, ...) from it."""
        prefix = fields.pop("_prefix")
        meta = self._queue_meta(fields.get("filename")) if prefix == "sh" else {}
        row = self.reg.upsert_job({"id": new_id(prefix), **fields, **meta})
        row = self.reg.set_state_if(row["id"], (row["state"],), row["state"]) or row
        self.bus.publish("job", **row)
        return row

    def _apply(self, row: dict, state: Optional[str] = None, *,
               expected: tuple[str, ...] = _ACTIVE, pages: Optional[int] = None,
               error: Optional[str] = None, **fields: Any) -> dict:
        """Apply only the differences; publish `job` once if anything changed.

        A state change is conditional on the row still being in one of `expected` (the
        observer's `row` may be stale: AMI moves rows concurrently). If it moved on,
        nothing at all is written and the current row is returned.
        """
        changed = False
        event_id = None
        if state is not None and row["state"] != state:
            new = self.reg.set_state_if(row["id"], expected, state, pages=pages, error=error)
            if new is None:
                log.debug("job %s already moved on; not setting %s", row["id"], state)
                return self.reg.get_job(row["id"]) or row
            row, changed, event_id = new, True, new.get("event_id")
            pages = error = None
        diff = {k: v for k, v in fields.items() if v is not None and row.get(k) != v}
        if pages is not None and row.get("pages") != pages:
            diff["pages"] = pages
        if error is not None and row.get("error") != error:
            diff["error"] = error
        if diff:
            row = self.reg.upsert_job({"id": row["id"], **diff})
            changed = True
        if changed:
            if event_id is not None:
                row = dict(row, event_id=event_id)   # the SSE id of this transition
            self.bus.publish("job", **row)
        return row

    # ---- rules --------------------------------------------------------
    def _rule_outbox(self) -> None:
        for name in _listdir(self.cfg.outbox):
            low = name.lower()
            if name.startswith(".") or low.startswith(("arm-", "disarm")):
                continue
            if not low.endswith(_OUTBOX_EXTS):
                continue
            path = os.path.join(self.cfg.outbox, name)
            # a finished row with the same name is an earlier fax: a re-drop is a new job
            if not _isfile(path) or self.reg.find_active_by_filename(name):
                continue
            base = name.split("_", 1)[0] if "_" in name else os.path.splitext(name)[0]
            self._create({
                "_prefix": "sh", "direction": "out", "state": "queued", "filename": name,
                "to_number": _norm(base) or None, "origin": "share", "pdf_path": path,
            })

    def _rule_env(self) -> None:
        for name in _listdir(self.cfg.outqueue):
            if not name.lower().endswith(".env"):
                continue
            env = parse_env_file(os.path.join(self.cfg.outqueue, name))
            jobid = env.get("JOBID") or name[:-4]
            src = env.get("SRCFILE", "").replace("\\", "/")
            filename = src.rsplit("/", 1)[-1] if src else None
            selftest = name == "SELFTEST.env"
            to = "000" if selftest else _norm(env.get("TO", ""))
            row = self.reg.find_by_ast_jobid(jobid) or self.reg.find_active_by_filename(filename)
            if row is not None and row["state"] in TERMINAL_STATES:
                continue  # .env files outlive their job; never touch a finished row from one
            if row is None:
                if not filename and not selftest:
                    continue
                if selftest and self.reg.find_by_filename(filename):
                    continue  # SELFTEST.env of a self-test that already finished
                row = self._create({
                    "_prefix": "sh", "direction": "out", "state": "queued", "filename": filename,
                    "to_number": to or None, "origin": "share", "ast_jobid": jobid,
                })
            state = None
            if row["state"] == "queued" and not _isfile(
                    os.path.join(self.cfg.outqueue, jobid + ".result")):
                state = "dialing"
            sending = os.path.join(self.cfg.sending, filename) if filename else None
            # SELFTEST.env duplicates the pipeline's own <jobid>.env; never let it steal the id
            ast = None if selftest and row.get("ast_jobid") else jobid
            self._apply(row, state, expected=("queued",), ast_jobid=ast,
                        to_number=to or None,
                        pdf_path=sending if sending and _isfile(sending) else None)

    def _rule_sending(self) -> None:
        for name in _listdir(self.cfg.sending):
            path = os.path.join(self.cfg.sending, name)
            row = self.reg.find_active_by_filename(name)
            if row is None or not _isfile(path):
                continue
            self._apply(row, "dialing" if row["state"] == "queued" else None,
                        expected=("queued",), pdf_path=path)

    def _rule_receipts(self, ls: dict[str, list[str]]) -> None:
        live: set[str] = set()
        for directory, target in ((self.cfg.sent, "sent"), (self.cfg.failed, "failed")):
            for name in ls[directory]:
                if not name.lower().endswith(".txt") or name.lower().startswith("recv_"):
                    continue
                if name.startswith(("control-", "invalid-")):
                    continue  # ARM control files, not receipts
                path = os.path.join(directory, name)
                live.add(path)
                sig = _sig(path)
                if sig is None or self._receipts_done.get(path) == sig:
                    continue  # already applied to a finished row and unchanged since
                skip_key = (sig, hash(tuple(ls[directory])))
                if self._receipts_skipped.get(path) == skip_key:
                    continue  # matched nothing, and neither it nor its folder changed since
                self._receipts_skipped.pop(path, None)
                result = self._receipt(directory, target, name, path, ls)
                if result is True:
                    self._receipts_done[path] = sig
                elif result is None:
                    self._receipts_skipped[path] = skip_key
        for seen in (self._receipts_done, self._receipts_skipped):
            for path in [p for p in seen if p not in live]:
                del seen[path]
        self._warned &= live

    def _receipt(self, directory: str, target: str, name: str, path: str,
                 ls: dict[str, list[str]]) -> Optional[bool]:
        """Apply one receipt. True once it is settled (its row is finished), False to look
        again next scan, None when a SELFTEST.txt matches nothing (skipped until it or the
        files beside it change)."""
        jobid = name[:-4]
        rec = parse_receipt(path)
        row = self.reg.find_by_ast_jobid(jobid)
        selftest = jobid == "SELFTEST"
        if row is None and selftest:
            row, stale = self._selftest_row(path)
            if stale:
                return None   # older than SELFTEST.env: an earlier self-test's receipt
        if row is None:
            cands = self._receipt_candidates(directory, rec, ls,
                                             selftest_receipt=path if selftest else None)
            if len(cands) == 1:
                row = cands[0]
            elif cands:
                if path not in self._warned:
                    self._warned.add(path)
                    log.warning("receipt %s ambiguous between %d rows; skipped", jobid, len(cands))
                return False
            elif selftest:
                return True if self._adopt_selftest(directory, target, path, rec, ls) else None
            else:
                if _JOBID_RE.match(jobid):
                    self._adopt_receipt(directory, target, jobid, rec, ls)
                    return True
                return False
        if row["state"] in ("sent", "failed") and row["state"] != target:
            return True
        pdf = os.path.join(directory, row["filename"]) if row["filename"] else None
        pdf = pdf if row["filename"] and row["filename"] in ls[directory] else None
        # never re-point a finished row at another pipeline job id
        keep_ast = selftest or (row["state"] in TERMINAL_STATES and row.get("ast_jobid"))
        ast = None if keep_ast else jobid
        if target == "sent":
            row = self._apply(row, "sent", expected=_ACTIVE, pages=_to_int(rec.get("pages")),
                              ast_jobid=ast, pdf_path=pdf)
        else:
            err = rec.get("error") or rec.get("status") or "failed"
            row = self._apply(row, "failed", expected=_ACTIVE, pages=_to_int(rec.get("pages")),
                              error=err, ast_jobid=ast, pdf_path=pdf)
        return row["state"] in TERMINAL_STATES

    def _selftest_row(self, receipt_path: str) -> tuple[Optional[dict], bool]:
        """(row, stale) for sent|failed/SELFTEST.txt: the job named by SELFTEST.env when the
        receipt is newer than it; stale=True when the receipt predates SELFTEST.env. With no
        SELFTEST.env (cleaned up) it is (None, False) and the caller searches by file."""
        env_path = os.path.join(self.cfg.outqueue, "SELFTEST.env")
        try:
            env_mtime = os.stat(env_path).st_mtime
        except OSError:
            return None, False
        try:
            if os.stat(receipt_path).st_mtime < env_mtime:
                return None, True
        except OSError:
            return None, False
        src = parse_env_file(env_path).get("SRCFILE", "").replace("\\", "/")
        return (self.reg.find_by_filename(src.rsplit("/", 1)[-1]) if src else None), False

    def _receipt_candidates(self, directory: str, rec: dict[str, str],
                            ls: dict[str, list[str]], *,
                            selftest_receipt: Optional[str] = None) -> list[dict]:
        """Unmatched active rows whose file sits beside the receipt (and match its number).

        For SELFTEST.txt (`selftest_receipt` = its path; `To: SELFTEST` is not a number) the
        candidates are active `000` rows created no later than the receipt; they may carry the
        pipeline's own job id, which never gets a receipt of its own."""
        here = set(ls[directory])
        if selftest_receipt:
            to, done = "000", _mtime_iso(selftest_receipt) or ""
        else:
            to, done = (_norm(rec["to"]) if rec.get("to") else None), ""
        cands = []
        for row in self.reg.list_jobs(direction="out", states=_ACTIVE, limit=1000):
            if not row["filename"] or row["filename"] not in here:
                continue
            if selftest_receipt:
                if row["to_number"] != to or (row["created_at"] or "") > done:
                    continue
            elif row["ast_jobid"] or (to and row["to_number"] and row["to_number"] != to):
                continue
            cands.append(row)
        return cands

    def _adopt_selftest(self, directory: str, target: str, path: str, rec: dict[str, str],
                        ls: dict[str, list[str]]) -> bool:
        """History for a SELFTEST.txt with no job: the newest `000_*` file beside it that is
        not newer than the receipt (2 min slack) becomes a finished row. False when there is
        no such file; True, writing nothing, when that file already has a row."""
        try:
            limit = os.stat(path).st_mtime + 120
        except OSError:
            return False
        best, best_mtime = None, None
        for fn in ls[directory]:
            if not fn.startswith("000_") or os.path.splitext(fn)[1].lower() not in _OUTBOX_EXTS:
                continue
            try:
                m = os.stat(os.path.join(directory, fn)).st_mtime
            except OSError:
                continue
            if m <= limit and (best_mtime is None or m > best_mtime):
                best, best_mtime = fn, m
        if best is None:
            return False
        if self.reg.find_by_filename(best):
            return True   # already known (adopted earlier, or settled while SELFTEST.env existed)
        pdf = os.path.join(directory, best)
        done = date_cmd_to_utc_iso(rec.get("date", "")) or _mtime_iso(path) or utcnow_iso()
        fields: dict[str, Any] = {
            "_prefix": "sh", "direction": "out", "state": target, "filename": best,
            "to_number": "000", "origin": "share", "pdf_path": pdf,
            "pages": _to_int(rec.get("pages")), "created_at": _mtime_iso(pdf) or done,
        }
        if target == "failed":
            fields["error"] = rec.get("error") or rec.get("status") or "failed"
        row = self._create(fields)
        self.reg.upsert_job({"id": row["id"], "finished_at": done})
        return True

    def _adopt_receipt(self, directory: str, target: str, jobid: str, rec: dict[str, str],
                       ls: dict[str, list[str]]) -> None:
        """History: a receipt with no row at all becomes a finished row, dated from its jobid/Date."""
        suffix = jobid[17:]
        filename = pdf = None
        for d in dict.fromkeys((directory, self.cfg.sent, self.cfg.failed)):
            for fn in ls[d]:
                stem, ext = os.path.splitext(fn)
                if ext.lower() not in _OUTBOX_EXTS or fn.startswith(("control-", "invalid-", "convert-")):
                    continue
                if suffix and re.sub(r"[^A-Za-z0-9]", "", stem)[:len(suffix)] == suffix:
                    filename, pdf = fn, os.path.join(d, fn)
                    break
            if filename:
                break
        fields: dict[str, Any] = {
            "_prefix": "sh", "direction": "out", "state": target, "filename": filename,
            "to_number": _norm(rec["to"]) if rec.get("to") else None, "origin": "share",
            "ast_jobid": jobid, "pdf_path": pdf, "pages": _to_int(rec.get("pages")),
            "created_at": local_to_utc_iso(jobid[1:16], _STAMP) or utcnow_iso(),
        }
        if target == "failed":
            fields["error"] = rec.get("error") or rec.get("status") or "failed"
        row = self._create(fields)
        done = date_cmd_to_utc_iso(rec.get("date", ""))
        if done:
            self.reg.upsert_job({"id": row["id"], "finished_at": done})

    def _rule_failed_markers(self, ls: dict[str, list[str]]) -> None:
        for prefix, msg in (("invalid-number-", "invalid number"),
                            ("convert-failed-", "conversion failed")):
            for name in ls[self.cfg.failed]:
                if not name.startswith(prefix):
                    continue
                orig = name[len(prefix):]
                path = os.path.join(self.cfg.failed, name)
                if not orig or not _isfile(path):
                    continue
                row = self.reg.find_active_by_filename(orig)
                if row is not None:
                    self._apply(row, "failed", expected=_ACTIVE, error=msg, pdf_path=path)
                    continue
                last = self.reg.find_by_filename(orig)
                if last is not None:
                    if last.get("pdf_path") == path:
                        continue  # this marker already failed that row
                    done = last.get("finished_at") or last.get("updated_at") or ""
                    marker = _mtime_iso(path) or ""
                    if done >= marker:
                        continue  # the marker predates the finished row: nothing new
                self._create({
                    "_prefix": "sh", "direction": "out", "state": "failed", "filename": orig,
                    "to_number": _norm(orig.split("_", 1)[0]) or None,
                    "origin": "share", "error": msg, "pdf_path": path,
                })

    def _rule_inbox(self, ls: dict[str, list[str]]) -> None:
        for name in ls[self.cfg.inbox]:
            m = _INBOX_RE.match(name)
            path = os.path.join(self.cfg.inbox, name)
            if not m or not _isfile(path) or self.reg.find_by_filename(name):
                continue
            created = local_to_utc_iso(m.group(1), _STAMP) or utcnow_iso()
            row = self._create({
                "_prefix": "in", "direction": "in", "state": "received", "filename": name,
                "from_number": m.group(2), "origin": "inbound", "pdf_path": path,
                "created_at": created,
            })
            row = self.reg.upsert_job({"id": row["id"], "finished_at": created})
            self.bus.publish("received", **row)

    def _rule_failed_recv(self, ls: dict[str, list[str]]) -> None:
        for name in ls[self.cfg.failed]:
            m = _RECV_FAIL_RE.match(name)
            path = os.path.join(self.cfg.failed, name)
            if not m or not _isfile(path) or self.reg.find_by_filename(name):
                continue
            rec = parse_receipt(path)
            self._create({
                "_prefix": "in", "direction": "in", "state": "failed", "filename": name,
                "from_number": m.group(2), "origin": "inbound",
                "error": rec.get("error") or rec.get("status") or "receive failed",
                "created_at": local_to_utc_iso(m.group(1), _STAMP) or utcnow_iso(),
            })

    def _rule_stuck_queued(self) -> None:
        """A `queued` row whose pdf_path is gone and that has not changed for 5 minutes never
        reached the pipeline (the enqueue died before its rename, or the file was removed)."""
        cutoff = (datetime.now(timezone.utc) - timedelta(seconds=STUCK_QUEUED_AFTER)).strftime(
            "%Y-%m-%dT%H:%M:%SZ")
        for row in self.reg.list_jobs(direction="out", states=["queued"], limit=1000):
            path = row["pdf_path"]
            if not path or (row["updated_at"] or "") >= cutoff or _isfile(path):
                continue
            new = self.reg.set_state_if(row["id"], ("queued",), "failed",
                                        error="file never appeared")
            if new is not None:
                log.warning("job %s: %s never appeared; marked failed", row["id"], path)
                self.bus.publish("job", **new)

    def _rule_arm(self) -> None:
        try:
            st = os.stat(self.cfg.arm_status_file)
        except OSError:
            return
        sig = (st.st_mtime_ns, st.st_size)
        if sig == self._arm_sig:
            return
        self._arm_sig = sig
        self.bus.publish("arm", **self.arm_state())

    # ---- public -------------------------------------------------------
    def scan_once(self) -> None:
        """One idempotent reconciliation pass. Each rule is isolated from failures.

        sent/, failed/ and inbox/ are listed once per pass and shared by the rules.
        """
        ls = {d: _listdir(d) for d in (self.cfg.sent, self.cfg.failed, self.cfg.inbox)}
        for rule, args in ((self._rule_outbox, ()), (self._rule_env, ()), (self._rule_sending, ()),
                           (self._rule_receipts, (ls,)), (self._rule_failed_markers, (ls,)),
                           (self._rule_inbox, (ls,)), (self._rule_failed_recv, (ls,)),
                           (self._rule_stuck_queued, ()), (self._rule_arm, ())):
            try:
                rule(*args)
            except Exception:
                log.exception("spool rule %s failed", rule.__name__)

    def maybe_prune(self, now: Optional[float] = None) -> None:
        """Hourly: delete job_events rows older than cfg.event_retention_days (0 = keep all).
        `now` is a time.monotonic() value (tests pass their own)."""
        days = getattr(self.cfg, "event_retention_days", 0) or 0
        if days <= 0:
            return
        now = time.monotonic() if now is None else now
        if self._last_prune is not None and now - self._last_prune < PRUNE_EVERY:
            return
        self._last_prune = now
        before = (datetime.now(timezone.utc) - timedelta(days=days)).strftime("%Y-%m-%dT%H:%M:%SZ")
        n = self.reg.prune_events(before)
        if n:
            log.info("pruned %d job_events rows older than %s", n, before)

    def run(self, stop: threading.Event) -> None:
        """Scan every poll_interval (at least MIN_POLL) until `stop` is set; prune hourly."""
        while not stop.is_set():
            try:
                self.scan_once()
            except Exception:
                log.exception("spool scan failed")
            try:
                self.maybe_prune()
            except Exception:
                log.exception("job_events retention failed")
            stop.wait(max(MIN_POLL, self.cfg.poll_interval))

    def arm_state(self) -> dict:
        """Parse ARM-STATUS.txt via arm.parse_status (same dict shape as ArmController.state)."""
        return parse_status(_read_text(self.cfg.arm_status_file) or "", default_ext=self.cfg.faxin_ext)
