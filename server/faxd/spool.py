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
from .events import EventBus, utcnow_iso

log = logging.getLogger("faxd.spool")

_OUTBOX_EXTS = (".pdf", ".tif", ".tiff")
_INBOX_RE = re.compile(r"^fax_(\d{8}-\d{6})_from_(.+)\.pdf$", re.IGNORECASE)
_RECV_FAIL_RE = re.compile(r"^recv_(\d{8}-\d{6})_from_(.+)\.txt$", re.IGNORECASE)
_NANP_RE = re.compile(r"^[2-9][0-9]{9}$")
_ACTIVE = ("queued", "converting", "dialing", "sending")


def normalize_number(s: str) -> str:
    """Digits only; a 10-digit NANP number gets a leading 1."""
    digits = re.sub(r"\D", "", s or "")
    return "1" + digits if _NANP_RE.match(digits) else digits


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
                "to_number": normalize_number(base), "origin": "share", "pdf_path": path,
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
            to = "000" if selftest else normalize_number(env.get("TO", ""))
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
            self._apply(row, state, ast_jobid=jobid, to_number=to or None,
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
                jobid = name[:-4]
                rec = parse_receipt(os.path.join(directory, name))
                row = self.reg.find_by_ast_jobid(jobid)
                if row is None:
                    row = self._match_receipt_by_file(directory, rec, jobid)
                if row is None:
                    continue
                if row["state"] in ("sent", "failed") and row["state"] != target:
                    continue
                pdf = os.path.join(directory, row["filename"]) if row["filename"] else None
                if target == "sent":
                    self._apply(row, "sent", pages=_to_int(rec.get("pages")), ast_jobid=jobid,
                                pdf_path=pdf if pdf and _isfile(pdf) else None)
                else:
                    err = rec.get("error") or rec.get("status") or "failed"
                    self._apply(row, "failed", pages=_to_int(rec.get("pages")), error=err,
                                ast_jobid=jobid, pdf_path=pdf if pdf and _isfile(pdf) else None)

    def _match_receipt_by_file(self, directory: str, rec: dict[str, str],
                               jobid: str) -> Optional[dict]:
        """Fallback: the one unmatched active row whose file sits beside the receipt."""
        to = normalize_number(rec.get("to", "")) if rec.get("to") else None
        cands = []
        for row in self.reg.list_jobs(direction="out", states=_ACTIVE, limit=1000):
            if row["ast_jobid"] or not row["filename"]:
                continue
            if not _isfile(os.path.join(directory, row["filename"])):
                continue
            if to and row["to_number"] and row["to_number"] != to:
                continue
            cands.append(row)
        if len(cands) == 1:
            return cands[0]
        if cands:
            log.warning("receipt %s ambiguous between %d rows; skipped", jobid, len(cands))
        return None

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
                        "to_number": normalize_number(orig.split("_", 1)[0]) or None,
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
        """Parse ARM-STATUS.txt (ARMED and DISARMED formats)."""
        state: dict[str, Any] = {"armed": False, "until": None, "remaining_min": 0,
                                 "ext": self.cfg.faxin_ext, "changed": None, "raw": ""}
        text = _read_text(self.cfg.arm_status_file)
        if text is None:
            return state
        state["raw"] = text.strip()
        m = re.search(r"^Fax receiver:\s*(\S+)", text, re.MULTILINE)
        armed = bool(m and m.group(1).upper().rstrip(":-") == "ARMED")
        state["armed"] = armed
        m = re.search(r"\bext\s+(\d+)", text)
        if m:
            state["ext"] = m.group(1)
        m = re.search(r"^Window ends:\s*(\d{4}-\d\d-\d\d)\s+(\d\d:\d\d)", text, re.MULTILINE)
        if armed and m:
            try:
                until = datetime.strptime(f"{m.group(1)} {m.group(2)}", "%Y-%m-%d %H:%M").astimezone()
            except ValueError:
                until = None
            if until is not None:
                secs = (until - datetime.now(timezone.utc)).total_seconds()
                state["remaining_min"] = max(0, math.ceil(secs / 60))
                state["until"] = until.astimezone(timezone.utc).isoformat(
                    timespec="seconds").replace("+00:00", "Z")
        m = re.search(r"^Last change:\s*(.+)$", text, re.MULTILINE)
        if m:
            toks = [t for t in m.group(1).split() if not (t.isalpha() and t.isupper())]
            try:
                dt = datetime.strptime(" ".join(toks), "%a %b %d %H:%M:%S %Y").astimezone(timezone.utc)
                state["changed"] = dt.isoformat(timespec="seconds").replace("+00:00", "Z")
            except ValueError:
                pass
        return state
