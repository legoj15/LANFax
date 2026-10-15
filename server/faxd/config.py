"""faxd configuration: defaults, env-file loading, environment overlay."""
from __future__ import annotations

import logging
import math
import os
from dataclasses import dataclass
from typing import Mapping

log = logging.getLogger("faxd.config")

DEFAULT_ENV_FILE = "/etc/faxserver/settings.env"


@dataclass
class Config:
    """All paths, ports and credentials used by faxd."""

    share_root: str = "/srv/fax"
    outqueue: str = "/var/spool/asterisk/outqueue"
    db_path: str = "/var/lib/faxd/faxd.db"
    http_bind: str = "0.0.0.0"
    http_port: int = 8080
    token: str = ""
    ami_host: str = "127.0.0.1"
    ami_port: int = 5038
    ami_user: str = "faxd"
    ami_secret: str = ""
    arm_script: str = "/usr/local/bin/fax-arm.sh"
    poll_interval: float = 2.0
    station_id: str = "LANFax"
    faxin_ext: str = "300"
    allowed_numbers: str = "nanp,000,ext"  # enqueue.check_number rule names
    event_retention_days: int = 30          # job_events rows older than this are pruned; 0 = keep
    gs_bin: str = "gs"                      # ghostscript binary for merge/stamp/blank analysis
    fax_header: bool = False                # stamp sender/recipient/date and page numbers on every page
    confirmations_dir: str = "/var/lib/faxd/confirmations"   # cached fax confirmation sheets

    @property
    def outbox(self) -> str:
        return os.path.join(self.share_root, "outbox")

    @property
    def sending(self) -> str:
        return os.path.join(self.share_root, "sending")

    @property
    def sent(self) -> str:
        return os.path.join(self.share_root, "sent")

    @property
    def failed(self) -> str:
        return os.path.join(self.share_root, "failed")

    @property
    def inbox(self) -> str:
        return os.path.join(self.share_root, "inbox")

    @property
    def queue_meta(self) -> str:
        return os.path.join(self.share_root, "queue-meta")

    @property
    def arm_status_file(self) -> str:
        return os.path.join(self.share_root, "ARM-STATUS.txt")


# env key -> (Config field, type)
_KEYS: dict[str, tuple[str, type]] = {
    "FAXD_SHARE_ROOT": ("share_root", str),
    "FAXD_OUTQUEUE": ("outqueue", str),
    "FAXD_DB_PATH": ("db_path", str),
    "FAXD_HTTP_BIND": ("http_bind", str),
    "FAXD_HTTP_PORT": ("http_port", int),
    "FAXD_TOKEN": ("token", str),
    "FAXD_AMI_HOST": ("ami_host", str),
    "FAXD_AMI_PORT": ("ami_port", int),
    "FAXD_AMI_USER": ("ami_user", str),
    "FAXD_AMI_SECRET": ("ami_secret", str),
    "FAXD_ARM_SCRIPT": ("arm_script", str),
    "FAXD_POLL_INTERVAL": ("poll_interval", float),
    "FAXD_STATION_ID": ("station_id", str),
    "FAXIN_EXT": ("faxin_ext", str),
    "FAXD_ALLOWED_NUMBERS": ("allowed_numbers", str),
    "FAXD_EVENT_RETENTION_DAYS": ("event_retention_days", int),
    "FAXD_GS_BIN": ("gs_bin", str),
    "FAXD_FAX_HEADER": ("fax_header", bool),
    "FAXD_CONFIRMATIONS_DIR": ("confirmations_dir", str),
}


def _parse_env_file(path: str) -> dict[str, str]:
    """Read KEY=value lines; ignore blanks/comments; strip one pair of quotes."""
    out: dict[str, str] = {}
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            lines = f.read().splitlines()
    except OSError:
        return out
    for line in lines:
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        if line.startswith("export "):
            line = line[7:].lstrip()
        key, _, val = line.partition("=")
        key, val = key.strip(), val.strip()
        if len(val) >= 2 and val[0] == val[-1] and val[0] in "'\"":
            val = val[1:-1]
        if key:
            out[key] = val
    return out


def _parse_bool(raw: str) -> bool:
    """1/true/yes/on (any case) are true; 0/false/no/off and empty are false."""
    if raw.strip().lower() in ("1", "true", "yes", "on"):
        return True
    if raw.strip().lower() in ("", "0", "false", "no", "off"):
        return False
    raise ValueError("not a boolean")


def load(env_file: str = DEFAULT_ENV_FILE, environ: Mapping[str, str] = os.environ) -> Config:
    """Build a Config from the env file, overlaid by `environ`."""
    values = _parse_env_file(env_file)
    for key in _KEYS:
        if key in environ:
            values[key] = environ[key]
    cfg = Config()
    for key, (field, typ) in _KEYS.items():
        if key not in values:
            continue
        raw = values[key]
        try:
            val = _parse_bool(raw) if typ is bool else typ(raw)
            if typ is float and not math.isfinite(val):
                raise ValueError("not finite")
            setattr(cfg, field, val)
        except ValueError:
            log.warning("bad value for %s: %r; using default", key, raw)
    return cfg
