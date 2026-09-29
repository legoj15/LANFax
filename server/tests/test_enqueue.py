import json
import os
import tempfile
import unittest
from types import SimpleNamespace

from faxd.enqueue import enqueue_pdf, normalize_number, safe_slug


class Reg:
    def __init__(self):
        self.rows = []

    def upsert_job(self, job):
        self.rows.append(dict(job))
        return dict(job, marker=1)


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

    def test_empty_subject_slug(self):
        enqueue_pdf(self.cfg, self.reg, self.bus, b"%PDF-", "000")
        self.assertIn("000_fax-", self.reg.rows[0]["filename"])

    def test_bad(self):
        with self.assertRaises(ValueError):
            enqueue_pdf(self.cfg, self.reg, self.bus, b"nope", "9543216309")
        self.assertFalse(os.path.exists(self.cfg.outbox) and os.listdir(self.cfg.outbox))
        self.assertEqual(self.reg.rows, [])


if __name__ == "__main__":
    unittest.main()
