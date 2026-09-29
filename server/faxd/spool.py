"""Read-only observer: derives job state from the share, outqueue and ARM-STATUS.txt.

Never writes, moves or deletes anything under the share or the outqueue.
"""
from __future__ import annotations

import logging
import math
import os
import re
import threading
from datetime import datetime, timezone
from typing import Any, Optional

from .config import Config
from .db import Registry, TERMINAL_STATES, new_id
from .arm import parse_status
from .enqueue import normalize_number
from .events import EventBus, utcnow_iso

log = logging.getLogger("faxd.spool")

_OUTBOX_EXTS = (".pdf", ".tif", ".tiff")
_INBOX_RE = re.compile(r"^fax_(\d{8}-\d{6})_from_(.+)\.pdf$", re.IGNORECASE)
_RECV_FAIL_RE = re.compile(r"^recv_(\d{8}-\d{6})_from_(.+)\.txt$", re.IGNORECASE)
_JOBID_RE = re.compile(r"^j\d{8}-\d{6}-")
_ACTIVE = ("queued", "converting", "dialing", "sending")


def _norm(s: str) -> str:
    """Lenient wrapper over enqueue.normalize_number: never raises (observer input is untrusted)."""
    try:
        return normalize_number(s)
    except ValueError:
        return re.sub(r"\D", "", s or "")


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


def _local_stamp_to_utc_iso(stamp: str) -> Optional[str]:
    """`YYYYMMDD-HHMMSS` in server local time -> UTC ISO string with Z."""
    try:
        dt = datetime.strptime(stamp, "%Y%m%d-%H%M%S").astimezone(timezone.utc)
    except ValueError:
        return None
    return dt.isoformat(timespec="seconds").replace("+00:00", "Z")


def _receipt_date_to_utc_iso(text: str) -> Optional[str]:
    """`Tue Sep 29 11:46:24 EDT 2026` (server local, zone name dropped) -> UTC ISO with Z."""
    toks = [t for t in text.split() if not (t.isalpha() and t.isupper())]
    try:
        dt = datetime.strptime(" ".join(toks), "%a %b %d %H:%M:%S %Y").astimezone(timezone.utc)
    except ValueError:
        return None
    return dt.isoformat(timespec="seconds").replace("+00:00", "Z")


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

    # ---- helpers ------------------------------------------------------
    def _create(self, fields: dict[str, Any]) -> dict:
        """Insert a row, log its first state, publish `job`."""
        row = self.reg.upsert_job({"id": new_id(fields.pop("_prefix")), **fields})
        row = self.reg.set_state(row["id"], row["state"]) or row
        self.bus.publish("job", **row)
        return row

    def _apply(self, row: dict, state: Optional[str] = None, *, pages: Optional[int] = None,
               error: Optional[str] = None, **fields: Any) -> dict:
        """Apply only the differences; publish `job` once if anything changed."""
        changed = False
        diff = {k: v for k, v in fields.items() if v is not None and row.get(k) != v}
        if diff:
            row = self.reg.upsert_job({"id": row["id"], **diff})
            changed = True
        if state is not None and row["state"] != state:
            new = self.reg.set_state(row["id"], state, pages=pages, error=error)
            row = new or row
            changed = True
        else:
            extra = {}
            if pages is not None and row.get("pages") != pages:
                extra["pages"] = pages
            if error is not None and row.get("error") != error:
                extra["error"] = error
            if extra:
                row = self.reg.upsert_job({"id": row["id"], **extra})
                changed = True
        if changed:
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
            if not _isfile(path) or self.reg.find_by_filename(name):
                continue
            base = name.split("_", 1)[0] if "_" in name else os.path.splitext(name)[0]
            self._create({
                "_prefix": "sh", "direction": "out", "state": "queued", "filename": name,
                "to_number": _norm(base), "origin": "share", "pdf_path": path,
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
            row = self.reg.find_by_ast_jobid(jobid) or self.reg.find_by_filename(filename)
            if row is None:
                if not filename and not selftest:
                    continue
                row = self._create({
                    "_prefix": "sh", "direction": "out", "state": "queued", "filename": filename,
                    "to_number": to or None, "origin": "share", "ast_jobid": jobid,
                })
            state = None
            if row["state"] in ("queued", "converting") and not _isfile(
                    os.path.join(self.cfg.outqueue, jobid + ".result")):
                state = "dialing"
            sending = os.path.join(self.cfg.sending, filename) if filename else None
            # SELFTEST.env duplicates the pipeline's own <jobid>.env; never let it steal the id
            ast = None if selftest and row.get("ast_jobid") else jobid
            self._apply(row, state, ast_jobid=ast, to_number=to or None,
                        pdf_path=sending if sending and _isfile(sending) else None)

    def _rule_sending(self) -> None:
        for name in _listdir(self.cfg.sending):
            path = os.path.join(self.cfg.sending, name)
            row = self.reg.find_by_filename(name)
            if row is None or row["state"] in TERMINAL_STATES or not _isfile(path):
                continue
            self._apply(row, "dialing" if row["state"] == "queued" else None, pdf_path=path)

    def _rule_receipts(self) -> None:
        for directory, target in ((self.cfg.sent, "sent"), (self.cfg.failed, "failed")):
            for name in _listdir(directory):
                if not name.lower().endswith(".txt") or name.lower().startswith("recv_"):
                    continue
                if name.startswith(("control-", "invalid-")):
                    continue  # ARM control files, not receipts
                jobid = name[:-4]
                rec = parse_receipt(os.path.join(directory, name))
                row = self.reg.find_by_ast_jobid(jobid)
                selftest = jobid == "SELFTEST"
                if row is None and selftest:
                    row = self._selftest_row(os.path.join(directory, name))
                    if row is None:
                        continue  # stale receipt from an earlier self-test, or no matching job
                if row is None:
                    cands = self._receipt_candidates(directory, rec)
                    if len(cands) == 1:
                        row = cands[0]
                    elif cands:
                        log.warning("receipt %s ambiguous between %d rows; skipped", jobid, len(cands))
                        continue
                    else:
                        if _JOBID_RE.match(jobid):
                            self._adopt_receipt(directory, target, jobid, rec)
                        continue
                if row["state"] in ("sent", "failed") and row["state"] != target:
                    continue
                pdf = os.path.join(directory, row["filename"]) if row["filename"] else None
                if target == "sent":
                    self._apply(row, "sent", pages=_to_int(rec.get("pages")),
                                ast_jobid=None if selftest else jobid,
                                pdf_path=pdf if pdf and _isfile(pdf) else None)
                else:
                    err = rec.get("error") or rec.get("status") or "failed"
                    self._apply(row, "failed", pages=_to_int(rec.get("pages")), error=err,
                                ast_jobid=None if selftest else jobid,
                                pdf_path=pdf if pdf and _isfile(pdf) else None)

    def _selftest_row(self, receipt_path: str) -> Optional[dict]:
        """Row for sent|failed/SELFTEST.txt: the job named by SELFTEST.env, if the receipt is newer."""
        env_path = os.path.join(self.cfg.outqueue, "SELFTEST.env")
        try:
            if os.stat(receipt_path).st_mtime < os.stat(env_path).st_mtime:
                return None
        except OSError:
            return None
        src = parse_env_file(env_path).get("SRCFILE", "").replace("\\", "/")
        return self.reg.find_by_filename(src.rsplit("/", 1)[-1]) if src else None

    def _receipt_candidates(self, directory: str, rec: dict[str, str]) -> list[dict]:
        """Unmatched active rows whose file sits beside the receipt (and match its number)."""
        to = _norm(rec["to"]) if rec.get("to") else None
        cands = []
        for row in self.reg.list_jobs(direction="out", states=_ACTIVE, limit=1000):
            if row["ast_jobid"] or not row["filename"]:
                continue
            if not _isfile(os.path.join(directory, row["filename"])):
                continue
            if to and row["to_number"] and row["to_number"] != to:
                continue
            cands.append(row)
        return cands

    def _adopt_receipt(self, directory: str, target: str, jobid: str, rec: dict[str, str]) -> None:
        """History: a receipt with no row at all becomes a finished row, dated from its jobid/Date."""
        suffix = jobid[17:]
        filename = pdf = None
        for d in dict.fromkeys((directory, self.cfg.sent, self.cfg.failed)):
            for fn in _listdir(d):
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
            "created_at": _local_stamp_to_utc_iso(jobid[1:16]) or utcnow_iso(),
        }
        if target == "failed":
            fields["error"] = rec.get("error") or rec.get("status") or "failed"
        row = self._create(fields)
        done = _receipt_date_to_utc_iso(rec.get("date", ""))
        if done:
            self.reg.upsert_job({"id": row["id"], "finished_at": done})

    def _rule_failed_markers(self) -> None:
        for prefix, msg in (("invalid-number-", "invalid number"),
                            ("convert-failed-", "conversion failed")):
            for name in _listdir(self.cfg.failed):
                if not name.startswith(prefix):
                    continue
                orig = name[len(prefix):]
                path = os.path.join(self.cfg.failed, name)
                if not orig or not _isfile(path):
                    continue
                row = self.reg.find_by_filename(orig)
                if row is None:
                    self._create({
                        "_prefix": "sh", "direction": "out", "state": "failed", "filename": orig,
                        "to_number": _norm(orig.split("_", 1)[0]) or None,
                        "origin": "share", "error": msg, "pdf_path": path,
                    })
                elif row["state"] in ("sent", "failed") and row["state"] != "failed":
                    continue
                elif row["state"] == "failed" and row.get("ast_jobid"):
                    continue  # already failed via a receipt; keep its error
                else:
                    self._apply(row, "failed", error=msg, pdf_path=path)

    def _rule_inbox(self) -> None:
        for name in _listdir(self.cfg.inbox):
            m = _INBOX_RE.match(name)
            path = os.path.join(self.cfg.inbox, name)
            if not m or not _isfile(path) or self.reg.find_by_filename(name):
                continue
            created = _local_stamp_to_utc_iso(m.group(1)) or utcnow_iso()
            row = self._create({
                "_prefix": "in", "direction": "in", "state": "received", "filename": name,
                "from_number": m.group(2), "origin": "inbound", "pdf_path": path,
                "created_at": created,
            })
            row = self.reg.upsert_job({"id": row["id"], "finished_at": created})
            self.bus.publish("received", **row)

    def _rule_failed_recv(self) -> None:
        for name in _listdir(self.cfg.failed):
            m = _RECV_FAIL_RE.match(name)
            path = os.path.join(self.cfg.failed, name)
            if not m or not _isfile(path) or self.reg.find_by_filename(name):
                continue
            rec = parse_receipt(path)
            self._create({
                "_prefix": "in", "direction": "in", "state": "failed", "filename": name,
                "from_number": m.group(2), "origin": "inbound",
                "error": rec.get("error") or rec.get("status") or "receive failed",
                "created_at": _local_stamp_to_utc_iso(m.group(1)) or utcnow_iso(),
            })

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
        """One idempotent reconciliation pass. Each rule is isolated from failures."""
        for rule in (self._rule_outbox, self._rule_env, self._rule_sending, self._rule_receipts,
                     self._rule_failed_markers, self._rule_inbox, self._rule_failed_recv,
                     self._rule_arm):
            try:
                rule()
            except Exception:
                log.exception("spool rule %s failed", rule.__name__)

    def run(self, stop: threading.Event) -> None:
        """Scan every poll_interval until `stop` is set."""
        while not stop.is_set():
            try:
                self.scan_once()
            except Exception:
                log.exception("spool scan failed")
            stop.wait(self.cfg.poll_interval)

    def arm_state(self) -> dict:
        """Parse ARM-STATUS.txt via arm.parse_status (same dict shape as ArmController.state)."""
        return parse_status(_read_text(self.cfg.arm_status_file) or "", default_ext=self.cfg.faxin_ext)
