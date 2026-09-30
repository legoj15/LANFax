"""Ingress: validate a PDF and drop it atomically into the outbox."""
import json
import logging
import os
import re
import secrets

from .events import utcnow_iso

log = logging.getLogger("faxd.enqueue")

DEFAULT_ALLOWED = "nanp,000,ext"
_NANP = re.compile(r"1[2-9]\d{2}[2-9]\d{6}")
_PREMIUM_AREAS = ("900", "976")


def safe_slug(s: str, maxlen=24) -> str:
    return re.sub(r"[^A-Za-z0-9-]", "", s or "")[:maxlen]


def normalize_number(s: str) -> str:
    digits = re.sub(r"[^0-9]", "", s or "")
    if len(digits) < 2:
        raise ValueError("number must have at least 2 digits")
    if re.fullmatch(r"[2-9][0-9]{9}", digits):
        digits = "1" + digits
    return digits


def check_number(number: str, allowed: str = DEFAULT_ALLOWED) -> str:
    """Apply the FAXD_ALLOWED_NUMBERS rules to a normalized number; return it or raise ValueError.

    `000` (offline self-test) is always allowed. `ext`: 2-4 digit internal extensions,
    except emergency codes (anything starting 911, and 933/988). `nanp`: 11-digit
    1NXXNXXXXXX, except premium area codes 900 and 976. Anything else is rejected.
    """
    rules = {r.strip().lower() for r in (allowed or "").split(",") if r.strip()}
    if number == "000":
        return number
    if 2 <= len(number) <= 4:
        if number.startswith("911") or number in ("933", "988"):
            raise ValueError(f"number {number} not allowed: emergency code")
        if "ext" not in rules:
            raise ValueError(f"number {number} not allowed: internal extensions need 'ext' "
                             "in FAXD_ALLOWED_NUMBERS")
        return number
    if _NANP.fullmatch(number):
        if number[1:4] in _PREMIUM_AREAS:
            raise ValueError(f"number {number} not allowed: premium-rate area code {number[1:4]}")
        if "nanp" not in rules:
            raise ValueError(f"number {number} not allowed: NANP numbers need 'nanp' "
                             "in FAXD_ALLOWED_NUMBERS")
        return number
    raise ValueError(f"number {number} not allowed: only 10/11-digit NANP numbers, "
                     "2-4 digit extensions and 000 are accepted")


class ExistingJob(Exception):
    """The Idempotency-Key was used before: `job` is the row it created. Nothing was written."""

    def __init__(self, job: dict) -> None:
        super().__init__("idempotency key already used by " + str(job.get("id")))
        self.job = job


def _unlink(path: str) -> None:
    try:
        os.unlink(path)
    except OSError:
        pass


def enqueue_pdf(cfg, reg, bus, pdf_bytes: bytes, to: str, *, subject="", origin="api", client="",
                client_key=None) -> dict:
    """Queue a PDF (bytes or bytearray). The row and queue-meta exist before the final
    outbox name appears, so the observer never sees a file without its row.

    `client_key` (the Idempotency-Key) is stored on the row, globally unique. A key used
    before (from any address) raises ExistingJob with that row, also when two requests race
    to insert it; the caller decides whether the number matches."""
    if not pdf_bytes.startswith(b"%PDF"):
        raise ValueError("body is not a PDF")
    number = check_number(normalize_number(to), getattr(cfg, "allowed_numbers", DEFAULT_ALLOWED))
    client_key = client_key or None
    if client_key:
        prior = reg.find_by_client_key(client_key)
        if prior is not None:
            raise ExistingJob(prior)
    job_id = "lf" + secrets.token_hex(6)
    name = f"{number}_{safe_slug(subject) or 'fax'}-{job_id}.pdf"
    now = utcnow_iso()
    os.makedirs(cfg.outbox, exist_ok=True)
    os.makedirs(cfg.queue_meta, exist_ok=True)
    final = os.path.join(cfg.outbox, name)
    part = os.path.join(cfg.outbox, "." + name + ".part")  # dotfiles: ignored by observer and bash glob
    meta_path = os.path.join(cfg.queue_meta, name + ".json")
    row = None
    try:
        with open(part, "wb") as f:
            f.write(pdf_bytes)
        meta = {"id": job_id, "to": number, "subject": subject, "origin": origin,
                "client": client, "client_key": client_key, "created_at": now}
        with open(meta_path, "w", encoding="utf-8") as f:
            json.dump(meta, f)
        row = reg.upsert_job({
            "id": job_id, "direction": "out", "state": "queued", "filename": name,
            "to_number": number, "subject": subject, "origin": origin, "client": client,
            "pdf_path": final, "created_at": now, "updated_at": now, "client_key": client_key,
        })
        os.replace(part, final)
    except BaseException:
        _unlink(part)
        _unlink(meta_path)
        if row is None and client_key:
            winner = None
            try:
                winner = reg.find_by_client_key(client_key)
            except Exception:  # noqa: BLE001
                log.exception("idempotency lookup failed for a collided insert")
            if winner is not None:  # a concurrent request with the same key inserted first
                raise ExistingJob(winner) from None
        if row is not None:  # the final rename failed: do not leave a queued row without a file
            try:
                reg.set_state(job_id, "failed", error="enqueue failed")
            except Exception:  # noqa: BLE001
                log.exception("could not mark %s failed", job_id)
        raise
    # the fax is now live for the bash pipeline: from here on, never fail the request
    try:
        row = reg.set_state(job_id, "queued", detail="enqueued via " + origin) or row
        bus.publish("job", **row)
    except Exception:  # noqa: BLE001
        log.exception("post-enqueue bookkeeping failed for %s", job_id)
    log.info("enqueued %s -> %s", job_id, name)
    return row
