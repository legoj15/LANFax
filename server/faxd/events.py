"""In-process publish/subscribe bus plus the shared UTC timestamp helper."""
from __future__ import annotations

import logging
import queue
import threading
from datetime import datetime, timezone
from typing import Any

log = logging.getLogger("faxd.events")

SUBSCRIBER_QUEUE = 256


def utcnow_iso() -> str:
    """Current UTC time as ISO 8601 with a trailing Z, second precision."""
    return datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


class EventBus:
    """Fan-out bus. Each subscriber owns a bounded queue; a subscriber whose queue
    fills up (a stalled SSE client) is dropped instead of growing without limit."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._subs: list["queue.Queue[dict]"] = []

    def subscribe(self, maxsize: int = SUBSCRIBER_QUEUE) -> "queue.Queue[dict]":
        q: "queue.Queue[dict]" = queue.Queue(maxsize=maxsize)
        with self._lock:
            self._subs.append(q)
        return q

    def unsubscribe(self, q: "queue.Queue[dict]") -> None:
        with self._lock:
            try:
                self._subs.remove(q)
            except ValueError:
                pass

    def subscribed(self, q: "queue.Queue[dict]") -> bool:
        """False once `q` was unsubscribed or dropped for falling behind."""
        with self._lock:
            return q in self._subs

    def publish(self, type: str, **data: Any) -> None:
        """Deliver {"type", "ts", **data} to every subscriber; never blocks."""
        ev = {"type": type, "ts": utcnow_iso(), **data}
        with self._lock:
            subs = list(self._subs)
        for q in subs:
            try:
                q.put_nowait(dict(ev))
            except queue.Full:
                self.unsubscribe(q)
                log.warning("event subscriber fell %d events behind; dropped", q.maxsize)
