import json
import os
import tempfile
import unittest
from types import SimpleNamespace

from faxd.enqueue import ExistingJob, check_number, enqueue_pdf, normalize_number, safe_slug


class Reg:
    def __init__(self):
        self.rows = []
        self.states = []

    def upsert_job(self, job):
        self.rows.append(dict(job))
        return dict(job, marker=1)

    def set_state(self, job_id, state, **kw):
        self.states.append((job_id, state))
        return dict(self.rows[-1], marker=1, state=state)

    def find_by_client_key(self, key):
        for r in self.rows:
            if key and r.get("client_key") == key:
                return dict(r)
        return None


class Bus:
    def __init__(self):
        self.events = []

    def publish(self, type, **data):
        self.events.append((type, data))


class NumberTests(unittest.TestCase):
    def test_cases(self):
        self.assertEqual(normalize_number("+1 (954) 321-6309"), "19543216309")
        self.assertEqual(normalize_number("9543216309"), "19543216309")
        self.assertEqual(normalize_number("000"), "000")
        for bad in ("1", "abc", ""):
            with self.assertRaises(ValueError):
                normalize_number(bad)

    def test_slug(self):
        self.assertRegex(safe_slug("a b/c\\d..e"), r"^[A-Za-z0-9-]*$")
        self.assertEqual(safe_slug("Hello World"), "HelloWorld")
        self.assertEqual(len(safe_slug("x" * 100)), 24)
        self.assertEqual(safe_slug("!!!"), "")


class AllowlistTests(unittest.TestCase):
    RULES = "nanp,000,ext"

    def ok(self, n, rules=RULES):
        self.assertEqual(check_number(n, rules), n)

    def bad(self, n, rules=RULES, word=None):
        with self.assertRaises(ValueError) as cm:
            check_number(n, rules)
        if word:
            self.assertIn(word, str(cm.exception))

    def test_selftest_always(self):
        self.ok("000")
        self.ok("000", rules="")

    def test_ext(self):
        for n in ("300", "12", "0008", "4321"):
            self.ok(n)
        for n in ("911", "9111", "9110", "933", "988"):
            self.bad(n, word="emergency")
        self.ok("9330")  # 933/988 only blocked as 3-digit codes
        self.bad("300", rules="nanp", word="ext")

    def test_nanp(self):
        self.ok("19543216309")
        for n in ("19003216309", "19763216309"):
            self.bad(n, word="premium")
        for n in ("11543216309", "19541216309", "29543216309", "1954321630"):
            self.bad(n)
        self.bad("19543216309", rules="ext", word="nanp")

    def test_other_rejected(self):
        for n in ("01144201234567", "12345", "5551234"):
            self.bad(n, word="not allowed")


class EnqueueTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        r = self.tmp.name
        self.cfg = SimpleNamespace(outbox=os.path.join(r, "outbox"), queue_meta=os.path.join(r, "queue-meta"))
        self.reg, self.bus = Reg(), Bus()

    def test_ok(self):
        data = b"%PDF-1.4\nhello"
        row = enqueue_pdf(self.cfg, self.reg, self.bus, data, "(954) 321-6309", subject="Hello World", client="1.2.3.4")
        self.assertEqual(row["marker"], 1)
        r = self.reg.rows[0]
        self.assertTrue(r["id"].startswith("lf") and len(r["id"]) == 14)
        self.assertEqual(r["filename"], f"19543216309_HelloWorld-{r['id']}.pdf")
        self.assertEqual((r["direction"], r["state"], r["origin"], r["client"]), ("out", "queued", "api", "1.2.3.4"))
        self.assertEqual(r["to_number"], "19543216309")
        self.assertEqual(r["pdf_path"], os.path.join(self.cfg.outbox, r["filename"]))
        self.assertEqual(os.listdir(self.cfg.outbox), [r["filename"]])
        with open(r["pdf_path"], "rb") as f:
            self.assertEqual(f.read(), data)
        with open(os.path.join(self.cfg.queue_meta, r["filename"] + ".json")) as f:
            m = json.load(f)
        self.assertEqual(m["id"], r["id"])
        for k in ("to", "subject", "origin", "client", "created_at"):
            self.assertIn(k, m)
        self.assertEqual(self.bus.events[0][0], "job")
        self.assertEqual(self.bus.events[0][1]["marker"], 1)
        self.assertEqual(self.reg.states, [(r["id"], "queued")])  # initial job_events row

    def test_row_and_meta_exist_before_final_file(self):
        seen = []
        orig = self.reg.upsert_job

        def spy(job):
            seen.append(sorted(os.listdir(self.cfg.outbox)))
            self.assertTrue(os.path.exists(os.path.join(self.cfg.queue_meta, job["filename"] + ".json")))
            return orig(job)
        self.reg.upsert_job = spy
        enqueue_pdf(self.cfg, self.reg, self.bus, b"%PDF-1", "000")
        self.assertTrue(seen and all(n.startswith(".") for n in seen[0]), seen)

    def test_row_failure_leaves_no_file(self):
        def boom(job):
            raise RuntimeError("db down")
        self.reg.upsert_job = boom
        with self.assertRaises(RuntimeError):
            enqueue_pdf(self.cfg, self.reg, self.bus, b"%PDF-1", "000")
        self.assertEqual(os.listdir(self.cfg.outbox), [])
        self.assertEqual(os.listdir(self.cfg.queue_meta), [])
        self.assertEqual(self.bus.events, [])

    def test_failure_after_replace_still_returns_job(self):
        def boom(*a, **k):
            raise RuntimeError("late")
        self.reg.set_state = boom
        with self.assertLogs("faxd.enqueue", "ERROR"):
            row = enqueue_pdf(self.cfg, self.reg, self.bus, b"%PDF-1", "000")
        self.assertEqual(os.listdir(self.cfg.outbox), [row["filename"]])

    def test_disallowed_number_rejected_before_writing(self):
        self.cfg.allowed_numbers = "nanp,000,ext"
        with self.assertRaises(ValueError):
            enqueue_pdf(self.cfg, self.reg, self.bus, b"%PDF-1", "1-900-321-6309")
        with self.assertRaises(ValueError):
            enqueue_pdf(self.cfg, self.reg, self.bus, b"%PDF-1", "911")
        self.assertFalse(os.path.exists(self.cfg.outbox) and os.listdir(self.cfg.outbox))

    def test_empty_subject_slug(self):
        enqueue_pdf(self.cfg, self.reg, self.bus, b"%PDF-", "000")
        self.assertIn("000_fax-", self.reg.rows[0]["filename"])

    def test_bad(self):
        with self.assertRaises(ValueError):
            enqueue_pdf(self.cfg, self.reg, self.bus, b"nope", "9543216309")
        self.assertFalse(os.path.exists(self.cfg.outbox) and os.listdir(self.cfg.outbox))
        self.assertEqual(self.reg.rows, [])


    # ---- idempotency ----------------------------------------------------
    def test_client_key_stored_in_row_and_meta(self):
        row = enqueue_pdf(self.cfg, self.reg, self.bus, b"%PDF-1", "000", client_key="abc:1")
        self.assertEqual(self.reg.rows[0]["client_key"], "abc:1")
        with open(os.path.join(self.cfg.queue_meta, row["filename"] + ".json")) as f:
            self.assertEqual(json.load(f)["client_key"], "abc:1")

    def test_repeat_client_key_writes_nothing(self):
        first = enqueue_pdf(self.cfg, self.reg, self.bus, b"%PDF-1", "000", client_key="k")
        n_rows, n_events = len(self.reg.rows), len(self.bus.events)
        with self.assertRaises(ExistingJob) as cm:
            enqueue_pdf(self.cfg, self.reg, self.bus, b"%PDF-1", "000", client_key="k")
        self.assertEqual(cm.exception.job["id"], first["id"])
        self.assertEqual(os.listdir(self.cfg.outbox), [first["filename"]])
        self.assertEqual(len(os.listdir(self.cfg.queue_meta)), 1)
        self.assertEqual((len(self.reg.rows), len(self.bus.events)), (n_rows, n_events))

    def test_client_key_race_at_insert_returns_existing(self):
        winner = {"id": "lfwinner00001", "client_key": "k", "state": "queued"}
        calls = []

        def find(key):      # empty on the pre-check, the winner after the collision
            calls.append(key)
            return None if len(calls) == 1 else dict(winner)

        def collide(job):
            raise RuntimeError("UNIQUE constraint failed: jobs.client_key")
        self.reg.find_by_client_key = find
        self.reg.upsert_job = collide
        with self.assertRaises(ExistingJob) as cm:
            enqueue_pdf(self.cfg, self.reg, self.bus, b"%PDF-1", "000", client_key="k")
        self.assertEqual(cm.exception.job["id"], "lfwinner00001")
        self.assertEqual(os.listdir(self.cfg.outbox), [])
        self.assertEqual(os.listdir(self.cfg.queue_meta), [])

    def test_client_key_is_global(self):
        a = enqueue_pdf(self.cfg, self.reg, self.bus, b"%PDF-1", "000", client="10.0.0.1",
                        client_key="k")
        with self.assertRaises(ExistingJob) as cm:      # same key from another address
            enqueue_pdf(self.cfg, self.reg, self.bus, b"%PDF-1", "000", client="10.0.0.2",
                        client_key="k")
        self.assertEqual(cm.exception.job["id"], a["id"])


if __name__ == "__main__":
    unittest.main()
