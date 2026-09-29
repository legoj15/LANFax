"""Arm/disarm controller: reads ARM-STATUS.txt and drives fax-arm.sh via sudo."""
import logging
import math
import re
import subprocess
from datetime import datetime

log = logging.getLogger("faxd.arm")


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
        lines = raw.splitlines()
        first = lines[0] if lines else ""
        until = None
        remaining = 0
        m = re.search(r"^Window ends:\s*(\d{4}-\d{2}-\d{2} \d{2}:\d{2})", raw, re.M)
        if m:
            try:
                end = datetime.strptime(m.group(1), "%Y-%m-%d %H:%M")
                until = end.astimezone().isoformat()
                remaining = max(0, math.ceil((end - datetime.now()).total_seconds() / 60))
            except ValueError:
                pass
        armed = "ARMED" in first and "DISARMED" not in first and remaining > 0
        m = re.search(r"on ext (\d+)", first)
        ext = m.group(1) if m else self.cfg.faxin_ext
        changed = None
        m = re.search(r"^Last change:\s*(.+?)\s*$", raw, re.M)
        if m:
            try:
                changed = datetime.strptime(m.group(1), "%a %b %d %H:%M:%S %Z %Y").astimezone().isoformat()
            except ValueError:
                changed = None
        return {"armed": armed, "until": until, "remaining_min": remaining,
                "ext": ext, "changed": changed, "raw": raw}

    def _run(self, arg: str, origin: str) -> dict:
        argv = ["sudo", "-n", self.cfg.arm_script, arg, origin]
        res = self.runner(argv, capture_output=True, text=True, timeout=20)
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
