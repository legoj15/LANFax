"""Tests for faxd.db."""
import os
import tempfile
import threading
import unittest

import sqlite3

from faxd.db import Registry, JOB_STATES, TERMINAL_STATES


class RegistryTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.reg = Registry(os.path.join(self.tmp.name, "sub", "t.db"))

    def tearDown(self):
        self.reg.close()
        self.tmp.cleanup()

    def _job(self, i, **kw):
        d = {"id": f"sh{i:012x}", "direction": "out", "state": "queued",
             "filename": f"f{i}.pdf", "to_number": "1555", "origin": "share"}
        d.update(kw)
        return d

    def test_upsert_and_get(self):
        row = self.reg.upsert_job(self._job(1))
        self.assertEqual(row["filename"], "f1.pdf")
        self.assertTrue(row["created_at"].endswith("Z"))
        self.assertTrue(row["updated_at"].endswith("Z"))
        self.assertIsNone(row["finished_at"])
        self.assertEqual(self.reg.get_job(row["id"]), row)
        row2 = self.reg.upsert_job({"id": row["id"], "subject": "hi"})
        self.assertEqual(row2["subject"], "hi")
        self.assertEqual(row2["filename"], "f1.pdf")  # partial update keeps fields
        self.assertIsNone(self.reg.get_job("nope"))

    def test_set_state_events_and_finished(self):
        j = self.reg.upsert_job(self._job(1))["id"]
        r = self.reg.set_state(j, "dialing")
        self.assertEqual(r["state"], "dialing")
        self.assertIsNone(r["finished_at"])
        r = self.reg.set_state(j, "sent", pages=2, detail="ok")
        self.assertEqual(r["pages"], 2)
        self.assertIsNotNone(r["finished_at"])
        r = self.reg.set_state(j, "failed", error="boom")
        self.assertEqual(r["error"], "boom")
        evs = self.reg.job_events(j)
        self.assertEqual([e["state"] for e in evs], ["dialing", "sent", "failed"])
        self.assertEqual(evs[1]["detail"], "ok")
        self.assertIsNone(self.reg.set_state("missing", "sent"))
        with self.assertRaises(ValueError):
            self.reg.set_state(j, "bogus")
        self.assertIn("received", JOB_STATES)

    def test_finders(self):
        self.reg.upsert_job(self._job(1, ast_jobid="j20260101-000000-1"))
        self.assertEqual(self.reg.find_by_filename("f1.pdf")["id"], self._job(1)["id"])
        self.assertEqual(self.reg.find_by_ast_jobid("j20260101-000000-1")["filename"], "f1.pdf")
        self.assertIsNone(self.reg.find_by_filename("zzz"))
        self.assertIsNone(self.reg.find_by_ast_jobid("zzz"))
        self.assertIsNone(self.reg.find_by_filename(""))

    def test_list_jobs(self):
        for i in range(5):
            self.reg.upsert_job(self._job(i, created_at=f"2026-01-0{i+1}T00:00:00Z"))
        self.reg.upsert_job(self._job(9, direction="in", state="received",
                                      created_at="2026-02-01T00:00:00Z"))
        self.reg.set_state(self._job(0)["id"], "sent")
        allj = self.reg.list_jobs()
        self.assertEqual(allj[0]["id"], self._job(9)["id"])  # newest first
        outs = self.reg.list_jobs(direction="out")
        self.assertEqual(len(outs), 5)
        self.assertEqual(outs[0]["filename"], "f4.pdf")
        self.assertEqual(len(self.reg.list_jobs(direction="out", limit=2)), 2)
        sent = self.reg.list_jobs(states=["sent", "failed"])
        self.assertEqual([j["filename"] for j in sent], ["f0.pdf"])
        self.assertEqual(self.reg.list_jobs(states=[]), [])
        self.assertEqual(len(self.reg.list_jobs(since="2026-01-04T00:00:00Z")), 3)

    def test_threads(self):
        def work(base):
            for i in range(50):
                self.reg.upsert_job(self._job(base + i))
        ts = [threading.Thread(target=work, args=(b,)) for b in (0, 1000)]
        for t in ts:
            t.start()
        for t in ts:
            t.join()
        self.assertEqual(len(self.reg.list_jobs(limit=1000)), 100)

    def test_one_shared_connection(self):
        def work():
            for _ in range(5):
                self.reg.list_jobs()
        ts = [threading.Thread(target=work) for _ in range(20)]
        for t in ts:
            t.start()
        for t in ts:
            t.join()
        self.assertEqual(self.reg.connection_count(), 1)

    def test_find_active_by_filename(self):
        a = self.reg.upsert_job(self._job(1, created_at="2026-01-01T00:00:00Z"))["id"]
        self.reg.set_state(a, "sent")
        self.assertIsNone(self.reg.find_active_by_filename("f1.pdf"))
        self.assertEqual(self.reg.find_by_filename("f1.pdf")["id"], a)
        b = self.reg.upsert_job(self._job(2, filename="f1.pdf"))["id"]
        self.assertEqual(self.reg.find_active_by_filename("f1.pdf")["id"], b)
        self.assertIsNone(self.reg.find_active_by_filename(""))

    def test_set_state_if(self):
        j = self.reg.upsert_job(self._job(1))["id"]
        self.assertEqual(self.reg.set_state_if(j, ("queued",), "dialing")["state"], "dialing")
        self.reg.set_state(j, "sent")
        self.assertIsNone(self.reg.set_state_if(j, ("dialing", "sending"), "sending", detail="x"))
        self.assertEqual(self.reg.get_job(j)["state"], "sent")
        self.assertEqual([e["state"] for e in self.reg.job_events(j)], ["dialing", "sent"])
        self.assertIsNone(self.reg.set_state_if("missing", ("queued",), "dialing"))

    def test_count_by_state(self):
        for i, st in enumerate(("queued", "queued", "converting", "sending", "sent")):
            self.reg.upsert_job(self._job(i, state=st))
        self.reg.upsert_job(self._job(9, direction="in", state="received"))
        self.assertEqual(self.reg.count_by_state(direction="out"),
                         {"queued": 2, "converting": 1, "sending": 1, "sent": 1})


    # ---- follow-up review items -----------------------------------------
    def test_cancelled_is_terminal(self):
        self.assertIn("cancelled", JOB_STATES)
        self.assertIn("cancelled", TERMINAL_STATES)
        j = self.reg.upsert_job(self._job(1))["id"]
        r = self.reg.set_state(j, "cancelled")
        self.assertEqual(r["state"], "cancelled")
        self.assertIsNotNone(r["finished_at"])
        self.assertIsNone(self.reg.find_active_by_filename("f1.pdf"))

    def test_client_key_unique(self):
        a = self.reg.upsert_job(self._job(1, client_key="k-1"))
        self.assertEqual(a["client_key"], "k-1")
        self.assertEqual(self.reg.find_by_client_key("k-1")["id"], a["id"])
        self.assertIsNone(self.reg.find_by_client_key("nope"))
        self.assertIsNone(self.reg.find_by_client_key(""))
        self.reg.upsert_job(self._job(2))          # NULL keys never collide
        self.reg.upsert_job(self._job(3))
        with self.assertRaises(sqlite3.IntegrityError):
            self.reg.upsert_job(self._job(4, client_key="k-1"))
        self.assertIsNone(self.reg.get_job(self._job(4)["id"]))

    def test_migration_adds_client_key_to_existing_db(self):
        path = os.path.join(self.tmp.name, "old.db")
        db = sqlite3.connect(path)
        db.executescript("""
            CREATE TABLE jobs (id TEXT PRIMARY KEY, direction TEXT NOT NULL DEFAULT 'out',
              state TEXT NOT NULL DEFAULT 'queued', filename TEXT, to_number TEXT,
              from_number TEXT, subject TEXT, origin TEXT, client TEXT, ast_jobid TEXT,
              pages INTEGER, error TEXT, pdf_path TEXT, created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL, finished_at TEXT);
            CREATE TABLE job_events (id INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL,
              ts TEXT NOT NULL, state TEXT, detail TEXT);
            INSERT INTO jobs (id, created_at, updated_at, subject) VALUES ('shold', 'x', 'x', 'kept');
        """)
        db.close()
        reg = Registry(path)
        try:
            self.assertEqual(reg.get_job("shold")["subject"], "kept")
            self.assertIsNone(reg.get_job("shold")["client_key"])
            reg.upsert_job(self._job(1, client_key="dup"))
            with self.assertRaises(sqlite3.IntegrityError):
                reg.upsert_job(self._job(2, client_key="dup"))
        finally:
            reg.close()
        Registry(path).close()   # re-opening a migrated db is a no-op

    def test_list_jobs_updated_since(self):
        self.reg.upsert_job(self._job(1))
        self.reg.upsert_job(self._job(2))
        with self.reg._lock:
            self.reg._conn().execute("UPDATE jobs SET updated_at='2026-01-01T00:00:00Z' WHERE id=?",
                                     (self._job(1)["id"],))
        got = self.reg.list_jobs(updated_since="2026-06-01T00:00:00Z")
        self.assertEqual([j["id"] for j in got], [self._job(2)["id"]])
        self.assertEqual(len(self.reg.list_jobs(updated_since="2025-01-01T00:00:00Z")), 2)

    def test_event_ids_and_replay(self):
        a = self.reg.upsert_job(self._job(1))["id"]
        b = self.reg.upsert_job(self._job(2))["id"]
        r1 = self.reg.set_state(a, "queued")
        r2 = self.reg.set_state(b, "queued")
        r3 = self.reg.set_state(a, "dialing")
        self.assertLess(r1["event_id"], r2["event_id"])
        self.assertLess(r2["event_id"], r3["event_id"])
        self.assertNotIn("event_id", self.reg.get_job(a))   # only on the row a transition returns
        self.assertEqual(self.reg.last_event_id(a), r3["event_id"])
        self.assertIsNone(self.reg.last_event_id("nope"))
        since = self.reg.job_events_since(r1["event_id"])
        self.assertEqual([(e["job_id"], e["id"]) for e in since],
                         [(b, r2["event_id"]), (a, r3["event_id"])])
        self.assertEqual(len(self.reg.job_events_since(0, limit=1)), 1)
        self.assertEqual(self.reg.job_events_since(r3["event_id"]), [])


if __name__ == "__main__":
    unittest.main()
