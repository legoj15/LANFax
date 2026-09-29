"""Fakes for Registry / EventBus / ArmController used by test_api (lane C only)."""
import queue
import threading
from datetime import datetime, timezone


class FakeRegistry:
    def __init__(self, jobs=None, events=None):
        self.jobs = dict(jobs or {})          # insertion order == newest first
        self.events = dict(events or {})
        self.list_calls = []

    def list_jobs(self, direction=None, states=None, limit=100, since=None, updated_since=None):
        self.list_calls.append(dict(direction=direction, states=states, limit=limit, since=since,
                                    updated_since=updated_since))
        out = [j for j in self.jobs.values()
               if (direction is None or j.get("direction") == direction)
               and (not states or j.get("state") in states)]
        return out[:limit]

    def get_job(self, job_id):
        return self.jobs.get(job_id)

    def job_events(self, job_id):
        return list(self.events.get(job_id, []))

    def find_by_client_key(self, key):
        return None

    def last_event_id(self, job_id):
        evs = self.events.get(job_id) or []
        return evs[-1]["id"] if evs else None

    def job_events_since(self, rowid, limit=500):
        evs = sorted((e for es in self.events.values() for e in es), key=lambda e: e["id"])
        return [e for e in evs if e["id"] > rowid][:limit]


class FakeBus:
    def __init__(self):
        self._lock = threading.Lock()
        self.subs = []

    def subscribe(self):
        q = queue.Queue()
        with self._lock:
            self.subs.append(q)
        return q

    def unsubscribe(self, q):
        with self._lock:
            if q in self.subs:
                self.subs.remove(q)

    def publish(self, type, **data):
        ev = {"type": type, "ts": datetime.now(timezone.utc).isoformat(), **data}
        with self._lock:
            for q in list(self.subs):
                q.put(ev)

    def subscribed(self, q):
        with self._lock:
            return q in self.subs

    def count(self):
        with self._lock:
            return len(self.subs)


class FakeArm:
    def __init__(self):
        self.calls = []
        self._state = {"armed": False, "until": None, "remaining_min": 0, "ext": "300",
                       "changed": None, "raw": ""}

    def state(self):
        return dict(self._state)

    def arm(self, minutes, origin):
        self.calls.append(("arm", minutes, origin))
        self._state = dict(self._state, armed=True, remaining_min=minutes,
                           until="2026-09-29T12:00:00+00:00")
        return self.state()

    def disarm(self, origin):
        self.calls.append(("disarm", origin))
        self._state = dict(self._state, armed=False, remaining_min=0, until=None)
        return self.state()
