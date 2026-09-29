"""In-process publish/subscribe bus plus the shared UTC timestamp helper."""
from __future__ import annotations

import queue
import threading
from datetime import datetime, timezone
from typing import Any


def utcnow_iso() -> str:
    """Current UTC time as ISO 8601 with a trailing Z, second precision."""
    return datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


class EventBus:
    """Fan-out bus; every subscriber owns an unbounded queue."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._subs: list["queue.Queue[dict]"] = []

    def subscribe(self) -> "queue.Queue[dict]":
        q: "queue.Queue[dict]" = queue.Queue()
        with self._lock:
            self._subs.append(q)
        return q

    def unsubscribe(self, q: "queue.Queue[dict]") -> None:
        with self._lock:
            try:
                self._subs.remove(q)
            except ValueError:
                pass

    def publish(self, type: str, **data: Any) -> None:
        """Deliver {"type", "ts", **data} to every subscriber."""
        ev = {"type": type, "ts": utcnow_iso(), **data}
        with self._lock:
            subs = list(self._subs)
        for q in subs:
            q.put(dict(ev))
