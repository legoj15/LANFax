"""Tests for faxd.spool: drive scan_once() over a fake share tree."""
import os
import queue
import tempfile
import unittest
from datetime import datetime, timedelta, timezone

from faxd.config import Config
from faxd.db import Registry
from faxd.events import EventBus
from faxd.enqueue import normalize_number
from faxd.spool import SpoolObserver, parse_env_file


def _write(path: str, text: str = "x") -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", newline="\n") as f:
        f.write(text)


class ParseHelpersTests(unittest.TestCase):
    def test_env_quoting_variants(self):
        with tempfile.TemporaryDirectory() as d:
            cases = {
                "SRCFILE=/srv/fax/sending/9543216309.pdf": "/srv/fax/sending/9543216309.pdf",
                r"SRCFILE=/srv/fax/sending/my\ file\ \(1\).pdf": "/srv/fax/sending/my file (1).pdf",
                "SRCFILE='/srv/fax/sending/x y.pdf'": "/srv/fax/sending/x y.pdf",
                'SRCFILE="/srv/fax/sending/q z.pdf"': "/srv/fax/sending/q z.pdf",
            }
            for line, want in cases.items():
                p = os.path.join(d, "a.env")
                _write(p, "# c\n\nJOBID=j1\n" + line + "\nnoequals\n")
                env = parse_env_file(p)
                self.assertEqual(env["SRCFILE"], want, line)
                self.assertEqual(env["JOBID"], "j1")

    def test_normalize(self):
        self.assertEqual(normalize_number("9543216309"), "19543216309")
        self.assertEqual(normalize_number("19543216309"), "19543216309")
        self.assertEqual(normalize_number("1234567890"), "1234567890")  # not NANP
        self.assertEqual(normalize_number("000"), "000")


class SpoolTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        r = self.tmp.name
        self.cfg = Config(
            share_root=os.path.join(r, "fax"),
            outqueue=os.path.join(r, "outqueue"),
            db_path=os.path.join(r, "db", "f.db"),
        )
        for d in (self.cfg.outbox, self.cfg.sending, self.cfg.sent, self.cfg.failed,
                  self.cfg.inbox, self.cfg.queue_meta, self.cfg.outqueue):
            os.makedirs(d)
        self.reg = Registry(self.cfg.db_path)
        self.bus = EventBus()
        self.q = self.bus.subscribe()
        self.obs = SpoolObserver(self.cfg, self.reg, self.bus)

    def tearDown(self):
        self.reg.close()
        self.tmp.cleanup()

    def events(self):
        out = []
        while True:
            try:
                out.append(self.q.get_nowait())
            except queue.Empty:
                return out

    def job_events(self):
        return [(e["type"], e.get("state")) for e in self.events() if e["type"] != "arm"]

    def only(self, **kw):
        rows = self.reg.list_jobs(**kw)
        self.assertEqual(len(rows), 1, rows)
        return rows[0]

    # ---- outbound lifecycle -------------------------------------------
    def _queue(self, name="9543216309_hello.pdf"):
        _write(os.path.join(self.cfg.outbox, name), "%PDF")
        self.obs.scan_once()
        return name

    def test_1_outbox_queued(self):
        name = self._queue()
        row = self.only()
        self.assertEqual(row["direction"], "out")
        self.assertEqual(row["state"], "queued")
        self.assertEqual(row["origin"], "share")
        self.assertTrue(row["id"].startswith("sh"))
        self.assertEqual(row["to_number"], "19543216309")
        self.assertEqual(row["filename"], name)
        self.assertEqual(row["pdf_path"], os.path.join(self.cfg.outbox, name))
        self.assertEqual(self.job_events(), [("job", "queued")])

    def test_1b_already_1_prefixed_and_tif(self):
        _write(os.path.join(self.cfg.outbox, "19543216309.TIF"))
        self.obs.scan_once()
        self.assertEqual(self.only()["to_number"], "19543216309")

    def test_2_env_and_sending(self):
        name = self._queue()
        self.events()
        _write(os.path.join(self.cfg.outqueue, "j20260929-113512-1.env"),
               "JOBID=j20260929-113512-1\nTO=19543216309\n"
               f"SRCFILE={self.cfg.sending}/{name}\n")
        os.replace(os.path.join(self.cfg.outbox, name), os.path.join(self.cfg.sending, name))
        self.obs.scan_once()
        row = self.only()
        self.assertEqual(row["state"], "dialing")
        self.assertEqual(row["ast_jobid"], "j20260929-113512-1")
        self.assertEqual(row["pdf_path"], os.path.join(self.cfg.sending, name))
        self.assertEqual(self.job_events(), [("job", "dialing")])
        self.assertEqual([e["state"] for e in self.reg.job_events(row["id"])], ["queued", "dialing"])

    def test_2b_env_with_result_does_not_dial(self):
        name = self._queue()
        _write(os.path.join(self.cfg.outqueue, "j1.env"),
               f"JOBID=j1\nTO=19543216309\nSRCFILE=/srv/fax/sending/{name}\n")
        _write(os.path.join(self.cfg.outqueue, "j1.result"))
        self.obs.scan_once()
        row = self.only()
        self.assertEqual(row["state"], "queued")
        self.assertEqual(row["ast_jobid"], "j1")

    def test_2c_env_without_row_and_selftest(self):
        _write(os.path.join(self.cfg.outqueue, "SELFTEST.env"),
               "JOBID=jself\nTO=whatever\nSRCFILE=/srv/fax/sending/selftest.pdf\n")
        self.obs.scan_once()
        row = self.only()
        self.assertEqual(row["to_number"], "000")
        self.assertEqual(row["filename"], "selftest.pdf")
        self.assertEqual(row["state"], "dialing")

    def test_2d_sending_only_moves_queued_to_dialing(self):
        name = self._queue()
        os.replace(os.path.join(self.cfg.outbox, name), os.path.join(self.cfg.sending, name))
        self.obs.scan_once()
        self.assertEqual(self.only()["state"], "dialing")

    def _to_dialing(self, name="9543216309_hello.pdf", jobid="j20260929-113512-1"):
        self._queue(name)
        _write(os.path.join(self.cfg.outqueue, jobid + ".env"),
               f"JOBID={jobid}\nTO=19543216309\nSRCFILE={self.cfg.sending}/{name}\n")
        os.replace(os.path.join(self.cfg.outbox, name), os.path.join(self.cfg.sending, name))
        self.obs.scan_once()
        self.events()
        return name, jobid

    def test_3_sent_receipt(self):
        name, jobid = self._to_dialing()
        os.remove(os.path.join(self.cfg.sending, name))
        _write(os.path.join(self.cfg.sent, name), "%PDF")
        _write(os.path.join(self.cfg.sent, jobid + ".txt"),
               "Status: SUCCESS\nTo: 19543216309\nPages: 1\nDate: Tue Sep 29 11:36:00 EDT 2026\n")
        self.obs.scan_once()
        row = self.only()
        self.assertEqual(row["state"], "sent")
        self.assertEqual(row["pages"], 1)
        self.assertIsNotNone(row["finished_at"])
        self.assertEqual(row["pdf_path"], os.path.join(self.cfg.sent, name))
        self.assertEqual(self.job_events(), [("job", "sent")])

    def test_3b_receipt_matched_by_filename_when_no_env_seen(self):
        name = self._queue()
        os.remove(os.path.join(self.cfg.outbox, name))
        _write(os.path.join(self.cfg.sent, name), "%PDF")
        _write(os.path.join(self.cfg.sent, "jzzz.txt"), "Status: SUCCESS\nTo: 19543216309\nPages: 3\n")
        self.obs.scan_once()
        row = self.only()
        self.assertEqual(row["state"], "sent")
        self.assertEqual(row["pages"], 3)
        self.assertEqual(row["ast_jobid"], "jzzz")

    def test_4_failed_receipt(self):
        name, jobid = self._to_dialing()
        os.remove(os.path.join(self.cfg.sending, name))
        _write(os.path.join(self.cfg.failed, name), "%PDF")
        _write(os.path.join(self.cfg.failed, jobid + ".txt"),
               "Status: FAILED\nTo: 19543216309\nPages: 0\nError: no answer\n")
        self.obs.scan_once()
        row = self.only()
        self.assertEqual(row["state"], "failed")
        self.assertEqual(row["error"], "no answer")
        self.assertEqual(row["pdf_path"], os.path.join(self.cfg.failed, name))
        self.assertEqual(self.job_events(), [("job", "failed")])

    def test_5_invalid_number_and_convert_failed(self):
        name = self._queue("12_bad.pdf")
        self.events()
        os.replace(os.path.join(self.cfg.outbox, name),
                   os.path.join(self.cfg.failed, "invalid-number-" + name))
        self.obs.scan_once()
        row = self.only()
        self.assertEqual(row["state"], "failed")
        self.assertEqual(row["error"], "invalid number")
        self.assertEqual(row["pdf_path"], os.path.join(self.cfg.failed, "invalid-number-" + name))
        self.assertEqual(self.job_events(), [("job", "failed")])
        _write(os.path.join(self.cfg.failed, "convert-failed-9543216309_x.pdf"))
        self.obs.scan_once()
        rows = {r["filename"]: r for r in self.reg.list_jobs()}
        self.assertEqual(rows["9543216309_x.pdf"]["error"], "conversion failed")
        self.assertEqual(rows["9543216309_x.pdf"]["state"], "failed")

    # ---- inbound ------------------------------------------------------
    def test_6_inbox(self):
        fn = "fax_20260929-113512_from_5551234567.pdf"
        _write(os.path.join(self.cfg.inbox, fn), "%PDF")
        self.obs.scan_once()
        row = self.only()
        self.assertEqual(row["direction"], "in")
        self.assertEqual(row["state"], "received")
        self.assertEqual(row["origin"], "inbound")
        self.assertTrue(row["id"].startswith("in"))
        self.assertEqual(row["from_number"], "5551234567")
        self.assertEqual(row["pdf_path"], os.path.join(self.cfg.inbox, fn))
        want = (datetime(2026, 9, 29, 11, 35, 12).astimezone(timezone.utc)
                .isoformat(timespec="seconds").replace("+00:00", "Z"))
        self.assertEqual(row["created_at"], want)
        self.assertIsNotNone(row["finished_at"])
        self.assertEqual(self.job_events(), [("job", "received"), ("received", "received")])

    def test_7_failed_recv(self):
        _write(os.path.join(self.cfg.failed, "recv_20260929-120000_from_unknown.txt"),
               "Status: FAILED\nError: negotiation failed\n")
        self.obs.scan_once()
        row = self.only()
        self.assertEqual(row["direction"], "in")
        self.assertEqual(row["state"], "failed")
        self.assertEqual(row["error"], "negotiation failed")
        self.assertEqual(row["from_number"], "unknown")
        self.assertEqual(self.job_events(), [("job", "failed")])

    # ---- arm ----------------------------------------------------------
    def test_8_arm_status(self):
        self.assertEqual(self.obs.arm_state()["armed"], False)  # missing file
        end = datetime.now() + timedelta(minutes=60)
        _write(self.cfg.arm_status_file,
               "Fax receiver: ARMED - receiving faxes on ext 300\n"
               f"Window ends: {end.strftime('%Y-%m-%d %H:%M')} EDT (~1h 00m remaining)\n"
               "Last change: Tue Sep 29 11:35:12 EDT 2026\n")
        self.obs.scan_once()
        evs = [e for e in self.events() if e["type"] == "arm"]
        self.assertEqual(len(evs), 1)
        self.assertTrue(evs[0]["armed"])
        st = self.obs.arm_state()
        self.assertTrue(st["armed"])
        self.assertEqual(st["ext"], "300")
        self.assertTrue(55 <= st["remaining_min"] <= 60, st)
        self.assertIsNotNone(st["until"])
        self.assertTrue(st["until"].endswith("Z"))
        self.assertEqual(st["changed"][:4], "2026")
        self.assertIn("ARMED", st["raw"])
        # unchanged mtime -> no repeat event
        self.obs.scan_once()
        self.assertEqual([e for e in self.events() if e["type"] == "arm"], [])
        # change to DISARMED (bump mtime explicitly)
        _write(self.cfg.arm_status_file,
               "Fax receiver: DISARMED - send-only; inbound calls ring the phones "
               "and go to the SIP provider voicemail\nLast change: Tue Sep 29 12:00:00 EDT 2026\n")
        st_mtime = os.stat(self.cfg.arm_status_file).st_mtime
        os.utime(self.cfg.arm_status_file, (st_mtime + 5, st_mtime + 5))
        self.obs.scan_once()
        evs = [e for e in self.events() if e["type"] == "arm"]
        self.assertEqual(len(evs), 1)
        self.assertFalse(evs[0]["armed"])
        st = self.obs.arm_state()
        self.assertEqual((st["armed"], st["until"], st["remaining_min"]), (False, None, 0))

    # ---- offline self-test (number 000) -------------------------------
    def _selftest_setup(self):
        name = self._queue("000_testpage-lf1.pdf")
        src = f"{self.cfg.sending}/{name}"
        _write(os.path.join(self.cfg.outqueue, "j20260929-181336-000testpagelf1.env"),
               f"JOBID=j20260929-181336-000testpagelf1\nTO=000\nSRCFILE={src}\n")
        _write(os.path.join(self.cfg.outqueue, "SELFTEST.env"),
               f"JOBID=SELFTEST\nTO=SELFTEST\nSRCFILE={src}\n")
        return name

    def test_11_selftest_two_envs_are_stable(self):
        self._selftest_setup()
        self.obs.scan_once()
        self.events()
        self.obs.scan_once()
        self.obs.scan_once()
        self.assertEqual(self.events(), [])  # no flip-flop between the two env files
        row = self.only()
        self.assertEqual(row["ast_jobid"], "j20260929-181336-000testpagelf1")
        self.assertEqual(row["state"], "dialing")

    def test_12_selftest_receipt_completes_the_job(self):
        name = self._selftest_setup()
        self.obs.scan_once()
        os.replace(os.path.join(self.cfg.outbox, name), os.path.join(self.cfg.sent, name))
        _write(os.path.join(self.cfg.sent, "SELFTEST.txt"),
               "Status: DELIVERED\nTo: SELFTEST\nPages: 1\nDate: Tue Sep 29 18:13:58 EDT 2026\n")
        future = os.stat(os.path.join(self.cfg.outqueue, "SELFTEST.env")).st_mtime + 5
        os.utime(os.path.join(self.cfg.sent, "SELFTEST.txt"), (future, future))
        self.obs.scan_once()
        row = self.only()
        self.assertEqual((row["state"], row["pages"]), ("sent", 1))
        self.assertEqual(row["pdf_path"], os.path.join(self.cfg.sent, name))

    def test_13_stale_selftest_receipt_ignored(self):
        self._selftest_setup()
        _write(os.path.join(self.cfg.sent, "SELFTEST.txt"), "Status: DELIVERED\nPages: 1\n")
        old = os.stat(os.path.join(self.cfg.outqueue, "SELFTEST.env")).st_mtime - 60
        os.utime(os.path.join(self.cfg.sent, "SELFTEST.txt"), (old, old))
        self.obs.scan_once()
        self.assertEqual(self.only()["state"], "dialing")

    # ---- historical adoption --------------------------------------------
    def test_orphan_receipts_adopted_with_history_dates(self):
        _write(os.path.join(self.cfg.failed, "0008_test1.pdf"))
        _write(os.path.join(self.cfg.failed, "j20260910-221123-0008test1.txt"),
               "Status: FAILED\nTo: 0008\nError: FAILED (Timed out)\nDate: Thu Sep 10 22:12:58 EDT 2026\n")
        _write(os.path.join(self.cfg.sent, "j20260911-192520-300test1.txt"),
               "Status: DELIVERED\nTo: 300\nPages: 1\nDate: Fri Sep 11 19:36:00 EDT 2026\n")
        self.obs.scan_once()
        rows = {r["ast_jobid"]: r for r in self.reg.list_jobs(direction="out")}
        f = rows["j20260910-221123-0008test1"]
        self.assertEqual((f["state"], f["to_number"], f["origin"]), ("failed", "0008", "share"))
        self.assertEqual(f["filename"], "0008_test1.pdf")
        self.assertIn("Timed out", f["error"])
        self.assertTrue(f["created_at"].startswith("2026-09-1"), f)
        self.assertTrue(f["finished_at"].startswith("2026-09-1"), f)
        s = rows["j20260911-192520-300test1"]
        self.assertEqual((s["state"], s["pages"], s["to_number"]), ("sent", 1, "300"))
        self.assertIsNone(s["filename"])
        self.obs.scan_once()
        self.assertEqual(len(self.reg.list_jobs(direction="out")), 2)

    def test_orphan_not_adopted_while_active_row_beside_it(self):
        # two live rows with files beside the receipt: ambiguous, so do not invent a third
        for n in ("15551230001_a-lf1.pdf", "15551230002_b-lf2.pdf"):
            _write(os.path.join(self.cfg.outbox, n))
        self.obs.scan_once()
        for n in ("15551230001_a-lf1.pdf", "15551230002_b-lf2.pdf"):
            os.replace(os.path.join(self.cfg.outbox, n), os.path.join(self.cfg.sent, n))
        _write(os.path.join(self.cfg.sent, "j20260929-100000-1555123000.txt"), "Status: DELIVERED\nPages: 1\n")
        self.obs.scan_once()
        self.assertEqual(len(self.reg.list_jobs(direction="out")), 2)

    # ---- filename reuse -------------------------------------------------
    def test_14_same_name_redropped_after_sent_gets_new_row(self):
        name, ja = self._to_dialing()
        os.replace(os.path.join(self.cfg.sending, name), os.path.join(self.cfg.sent, name))
        _write(os.path.join(self.cfg.sent, ja + ".txt"), "Status: SUCCESS\r\nTo: 19543216309\r\nPages: 1\r\n")
        self.obs.scan_once()
        old = self.only()
        self.assertEqual(old["state"], "sent")
        # same file name dropped again; the stale env of the first job stays in outqueue
        _write(os.path.join(self.cfg.outbox, name), "%PDF")
        self.obs.scan_once()
        jb = "j20260929-120000-2"
        _write(os.path.join(self.cfg.outqueue, jb + ".env"),
               f"JOBID={jb}\r\nTO=19543216309\r\nSRCFILE={self.cfg.sending}/{name}\r\n")
        os.replace(os.path.join(self.cfg.outbox, name), os.path.join(self.cfg.sending, name))
        self.obs.scan_once()
        os.replace(os.path.join(self.cfg.sending, name), os.path.join(self.cfg.failed, name))
        _write(os.path.join(self.cfg.failed, jb + ".txt"), "Status: FAILED\r\nTo: 19543216309\r\nError: busy\r\n")
        self.obs.scan_once()
        self.obs.scan_once()
        rows = {r["id"]: r for r in self.reg.list_jobs(direction="out")}
        self.assertEqual(len(rows), 2, rows)
        a = rows[old["id"]]
        self.assertEqual((a["state"], a["ast_jobid"], a["pdf_path"]),
                         ("sent", ja, os.path.join(self.cfg.sent, name)))
        (b,) = [r for r in rows.values() if r["id"] != old["id"]]
        self.assertEqual((b["state"], b["ast_jobid"], b["error"]), ("failed", jb, "busy"))

    def test_15_invalid_marker_redropped_is_not_duplicated(self):
        name = self._queue("12_bad.pdf")
        marker = os.path.join(self.cfg.failed, "invalid-number-" + name)
        os.replace(os.path.join(self.cfg.outbox, name), marker)
        self.obs.scan_once()
        self._queue(name)
        os.replace(os.path.join(self.cfg.outbox, name), marker)
        st = os.stat(marker).st_mtime + 5
        os.utime(marker, (st, st))
        self.obs.scan_once()
        self.obs.scan_once()
        rows = self.reg.list_jobs()
        self.assertEqual(len(rows), 2, rows)
        self.assertEqual({r["state"] for r in rows}, {"failed"})

    # ---- scan cost ------------------------------------------------------
    def test_16_unchanged_receipts_not_reread(self):
        from unittest import mock
        import faxd.spool as sp
        name, jobid = self._to_dialing()
        os.replace(os.path.join(self.cfg.sending, name), os.path.join(self.cfg.sent, name))
        _write(os.path.join(self.cfg.sent, jobid + ".txt"), "Status: SUCCESS\r\nPages: 1\r\n")
        _write(os.path.join(self.cfg.failed, "j20260910-221123-0008test1.txt"), "Status: FAILED\r\nTo: 0008\r\n")
        self.obs.scan_once()
        with mock.patch.object(sp, "parse_receipt", wraps=sp.parse_receipt) as pr:
            self.obs.scan_once()
            self.assertEqual(pr.call_count, 0)
            # a changed receipt is read again
            p = os.path.join(self.cfg.sent, jobid + ".txt")
            st = os.stat(p).st_mtime + 5
            os.utime(p, (st, st))
            self.obs.scan_once()
            self.assertEqual(pr.call_count, 1)

    def test_17_folders_listed_once_per_scan(self):
        from unittest import mock
        import faxd.spool as sp
        with mock.patch.object(sp, "_listdir", wraps=sp._listdir) as ld:
            self.obs.scan_once()
        dirs = [c.args[0] for c in ld.call_args_list]
        for d in (self.cfg.sent, self.cfg.failed, self.cfg.inbox):
            self.assertEqual(dirs.count(d), 1, (d, dirs))

    def test_18_ambiguous_receipt_warned_once(self):
        for n in ("15551230001_a-lf1.pdf", "15551230002_b-lf2.pdf"):
            _write(os.path.join(self.cfg.outbox, n))
        self.obs.scan_once()
        for n in ("15551230001_a-lf1.pdf", "15551230002_b-lf2.pdf"):
            os.replace(os.path.join(self.cfg.outbox, n), os.path.join(self.cfg.sent, n))
        _write(os.path.join(self.cfg.sent, "jx.txt"), "Status: DELIVERED\r\nPages: 1\r\n")
        with self.assertLogs("faxd.spool", "WARNING") as cm:
            self.obs.scan_once()
            self.obs.scan_once()
            self.obs.scan_once()
        self.assertEqual(len([m for m in cm.output if "ambiguous" in m]), 1)

    # ---- robustness ---------------------------------------------------
    def test_9_idempotent(self):
        name, jobid = self._to_dialing()
        os.remove(os.path.join(self.cfg.sending, name))
        _write(os.path.join(self.cfg.sent, name))
        _write(os.path.join(self.cfg.sent, jobid + ".txt"), "Status: SUCCESS\nPages: 2\n")
        _write(os.path.join(self.cfg.inbox, "fax_20260929-113512_from_5551234567.pdf"))
        _write(os.path.join(self.cfg.failed, "recv_20260929-120000_from_unknown.txt"), "Error: x\n")
        _write(os.path.join(self.cfg.failed, "invalid-number-1_a.pdf"))
        self.obs.scan_once()
        n_rows = len(self.reg.list_jobs())
        self.events()
        self.obs.scan_once()
        self.obs.scan_once()
        self.assertEqual(len(self.reg.list_jobs()), n_rows)
        self.assertEqual(self.events(), [])

    def test_10_ignored_names(self):
        for n in ("ARM-60.txt", "ARM-1H.pdf", "DISARM.pdf", ".hidden.pdf",
                  "9543216309_a.pdf.part", "notes.txt", "arm-now.pdf", "disarm-now.tif"):
            _write(os.path.join(self.cfg.outbox, n))
        self.obs.scan_once()
        self.assertEqual(self.reg.list_jobs(), [])
        self.assertEqual(self.job_events(), [])

    def test_missing_dirs_and_malformed_files_tolerated(self):
        import shutil
        shutil.rmtree(self.cfg.inbox)
        shutil.rmtree(self.cfg.outqueue)
        _write(os.path.join(self.cfg.outbox, "nonum.pdf"))
        os.makedirs(self.cfg.outqueue)
        with open(os.path.join(self.cfg.outqueue, "bad.env"), "wb") as f:
            f.write(b"\xff\xfe\x00garbage\n")
        _write(os.path.join(self.cfg.sent, "jbad.txt"), "\x00\x01 not a receipt")
        self.obs.scan_once()  # must not raise

    def test_run_stops(self):
        import threading
        stop = threading.Event()
        self.cfg.poll_interval = 0.01
        t = threading.Thread(target=self.obs.run, args=(stop,))
        t.start()
        _write(os.path.join(self.cfg.outbox, "9543216309_a.pdf"))
        for _ in range(200):
            if self.reg.list_jobs():
                break
            stop.wait(0.02)
        stop.set()
        t.join(timeout=5)
        self.assertFalse(t.is_alive())
        self.assertEqual(len(self.reg.list_jobs()), 1)


if __name__ == "__main__":
    unittest.main()
