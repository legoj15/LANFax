"""Ingress: validate a PDF and drop it atomically into the outbox."""
import json
import logging
import os
import re
import secrets
from datetime import datetime, timezone

log = logging.getLogger("faxd.enqueue")


def safe_slug(s: str, maxlen=24) -> str:
    return re.sub(r"[^A-Za-z0-9-]", "", s or "")[:maxlen]


def normalize_number(s: str) -> str:
    digits = re.sub(r"\D", "", s or "")
    if len(digits) < 2:
        raise ValueError("number must have at least 2 digits")
    if re.fullmatch(r"[2-9][0-9]{9}", digits):
        digits = "1" + digits
    return digits


def enqueue_pdf(cfg, reg, bus, pdf_bytes: bytes, to: str, *, subject="", origin="api", client="") -> dict:
    if not pdf_bytes.startswith(b"%PDF"):
        raise ValueError("body is not a PDF")
    number = normalize_number(to)
    job_id = "lf" + secrets.token_hex(6)
    name = f"{number}_{safe_slug(subject) or 'fax'}-{job_id}.pdf"
    now = datetime.now(timezone.utc).isoformat()
    os.makedirs(cfg.outbox, exist_ok=True)
    os.makedirs(cfg.queue_meta, exist_ok=True)
    final = os.path.join(cfg.outbox, name)
    part = os.path.join(cfg.outbox, "." + name + ".part")
    with open(part, "wb") as f:
        f.write(pdf_bytes)
    os.replace(part, final)
    meta = {"id": job_id, "to": number, "subject": subject, "origin": origin,
            "client": client, "created_at": now}
    with open(os.path.join(cfg.queue_meta, name + ".json"), "w", encoding="utf-8") as f:
        json.dump(meta, f)
    row = reg.upsert_job({
        "id": job_id, "direction": "out", "state": "queued", "filename": name,
        "to_number": number, "subject": subject, "origin": origin, "client": client,
        "pdf_path": final, "created_at": now, "updated_at": now,
    })
    bus.publish("job", **row)
    log.info("enqueued %s -> %s", job_id, name)
    return row
