"""Arm/disarm controller: reads ARM-STATUS.txt and drives fax-arm.sh via sudo."""
import logging
import math
import re
import subprocess
from datetime import datetime

from .timeutil import date_cmd_to_utc_iso, to_utc_iso

log = logging.getLogger("faxd.arm")

ORIGIN_MAX = 80


def sanitize_origin(s) -> str:
    """Keep [A-Za-z0-9 ._:@-], at most 80 chars: the origin reaches a root script's argv."""
    return re.sub(r"[^A-Za-z0-9 ._:@-]", "", str(s or ""))[:ORIGIN_MAX]


def parse_status(text: str, *, now=None, default_ext="300") -> dict:
    """Parse ARM-STATUS.txt text into the arm-state dict.

    `now` is a naive server-local datetime (default: current time); the file's
    "Window ends" stamp is server-local. `armed` is False once the window passed.
    """
    now = now or datetime.now()
    first = text.splitlines()[0] if text.splitlines() else ""
    until = None
    remaining = 0
    m = re.search(r"^Window ends:\s*(\d{4}-\d{2}-\d{2} \d{2}:\d{2})", text, re.M)
    if m:
        try:
            end = datetime.strptime(m.group(1), "%Y-%m-%d %H:%M")
            until = to_utc_iso(end)
            remaining = max(0, math.ceil((end - now).total_seconds() / 60))
        except ValueError:
            pass
    armed = "ARMED" in first and "DISARMED" not in first and remaining > 0
    if not armed:
        until, remaining = None, 0
    m = re.search(r"on ext (\d+)", first)
    ext = m.group(1) if m else default_ext
    m = re.search(r"^Last change:\s*(.+?)\s*$", text, re.M)
    changed = date_cmd_to_utc_iso(m.group(1)) if m else None
    return {"armed": armed, "until": until, "remaining_min": remaining,
            "ext": ext, "changed": changed, "raw": text}


class ArmError(Exception):
    pass


class ArmController:
    def __init__(self, cfg, bus, runner=subprocess.run):
        self.cfg = cfg
        self.bus = bus
        self.runner = runner

    def state(self) -> dict:
        try:
            with open(self.cfg.arm_status_file, encoding="utf-8", errors="replace") as f:
                raw = f.read()
        except OSError:
            raw = ""
        return parse_status(raw, default_ext=self.cfg.faxin_ext)

    def _run(self, arg: str, origin: str) -> dict:
        argv = ["sudo", "-n", self.cfg.arm_script, arg, sanitize_origin(origin)]
        try:
            res = self.runner(argv, capture_output=True, text=True, timeout=20)
        except subprocess.TimeoutExpired:
            log.warning("arm script timed out")
            raise ArmError("arm script timed out after 20 s") from None
        except (subprocess.SubprocessError, OSError) as e:
            log.warning("arm script could not run: %s", e)
            raise ArmError("arm script could not run: %s" % e) from None
        if res.returncode != 0:
            msg = (res.stderr or res.stdout or "").strip() or f"exit {res.returncode}"
            log.warning("arm script failed: %s", msg)
            raise ArmError(msg)
        st = self.state()
        self.bus.publish("arm", **st)
        return st

    def arm(self, minutes: int, origin: str) -> dict:
        return self._run(f"{int(minutes)}M", origin)

    def disarm(self, origin: str) -> dict:
        return self._run("off", origin)
