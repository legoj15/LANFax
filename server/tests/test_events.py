"""Tests for faxd.events."""
import queue
import threading
import unittest

from faxd.events import EventBus, utcnow_iso


class EventBusTests(unittest.TestCase):
    def test_two_subscribers_receive(self):
        bus = EventBus()
        a, b = bus.subscribe(), bus.subscribe()
        bus.publish("job", id="x", state="queued")
        for q in (a, b):
            ev = q.get(timeout=1)
            self.assertEqual(ev["type"], "job")
            self.assertEqual(ev["id"], "x")
            self.assertIn("ts", ev)

    def test_unsubscribe_stops_delivery(self):
        bus = EventBus()
        a, b = bus.subscribe(), bus.subscribe()
        bus.unsubscribe(a)
        bus.unsubscribe(a)  # idempotent
        bus.publish("arm", armed=True)
        self.assertTrue(a.empty())
        self.assertEqual(b.get(timeout=1)["type"], "arm")

    def test_publish_from_thread(self):
        bus = EventBus()
        q = bus.subscribe()
        t = threading.Thread(target=lambda: bus.publish("status", n=1))
        t.start()
        t.join()
        self.assertEqual(q.get(timeout=1)["n"], 1)
        with self.assertRaises(queue.Empty):
            q.get_nowait()

    def test_full_subscriber_is_dropped(self):
        bus = EventBus()
        slow = bus.subscribe(maxsize=2)
        fast = bus.subscribe()
        with self.assertLogs("faxd.events", "WARNING"):
            for i in range(3):
                bus.publish("job", n=i)
        self.assertFalse(bus.subscribed(slow))
        self.assertTrue(bus.subscribed(fast))
        self.assertEqual(fast.qsize(), 3)
        bus.publish("job", n=9)  # no error, slow no longer receives
        self.assertEqual(slow.qsize(), 2)

    def test_default_queue_is_bounded(self):
        self.assertEqual(EventBus().subscribe().maxsize, 256)

    def test_utcnow_iso_format(self):
        self.assertRegex(utcnow_iso(), r"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$")


if __name__ == "__main__":
    unittest.main()
