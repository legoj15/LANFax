"""Server-local time strings (from the bash pipeline) -> UTC ISO 8601 with a Z."""
from __future__ import annotations

from datetime import datetime, timezone
from typing import Optional


def to_utc_iso(dt: datetime) -> str:
    """A naive datetime is taken as server local time."""
    return dt.astimezone(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def local_to_utc_iso(text: str, fmt: str) -> Optional[str]:
    """Parse `text` with strptime `fmt` as server local time; None if it does not parse."""
    try:
        return to_utc_iso(datetime.strptime(text.strip(), fmt))
    except ValueError:
        return None


def date_cmd_to_utc_iso(text: str) -> Optional[str]:
    """`date` output such as `Tue Sep 29 11:46:24 EDT 2026` (zone name dropped, read as
    server local time) -> UTC ISO; None if it does not parse."""
    toks = [t for t in (text or "").split() if not (t.isalpha() and t.isupper())]
    return local_to_utc_iso(" ".join(toks), "%a %b %d %H:%M:%S %Y")
