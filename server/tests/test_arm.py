import os
import tempfile
import unittest
from datetime import datetime, timedelta
from types import SimpleNamespace

from faxd.arm import parse_status, sanitize_origin, ArmController, ArmError


class Bus:
    def __init__(self):
        self.events = []

    def publish(self, type, **data):
        self.events.append((type, data))


class Runner:
    def __init__(self, rc=0, out="", err=""):
        self.calls = []
        self.rc, self.out, self.err = rc, out, err

    def __call__(self, argv, **kw):
        self.calls.append((argv, kw))
        return SimpleNamespace(returncode=self.rc, stdout=self.out, stderr=self.err)


class ArmTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.path = os.path.join(self.tmp.name, "ARM-STATUS.txt")
        self.cfg = SimpleNamespace(arm_status_file=self.path, arm_script="/usr/local/bin/fax-arm.sh", faxin_ext="300")
        self.bus = Bus()

    def write(self, text):
        with open(self.path, "w", encoding="utf-8") as f:
            f.write(text)

    def armed_text(self, delta):
        end = datetime.now() + delta
        return ("Fax receiver: ARMED - receiving faxes on ext 300\n"
                f"Window ends: {end:%Y-%m-%d %H:%M} EDT (~1h 00m remaining)\n"
                "Last change: Tue Sep 29 11:35:12 EDT 2026\n")

    def test_armed(self):
        self.write(self.armed_text(timedelta(minutes=60)))
        s = ArmController(self.cfg, self.bus, Runner()).state()
        self.assertTrue(s["armed"])
        self.assertEqual(s["ext"], "300")
        self.assertTrue(58 <= s["remaining_min"] <= 61, s)
        self.assertTrue(s["until"])
        self.assertIn("ARMED", s["raw"])

    def test_past_window(self):
        self.write(self.armed_text(timedelta(minutes=-30)))
        s = ArmController(self.cfg, self.bus, Runner()).state()
        self.assertFalse(s["armed"])
        self.assertEqual(s["remaining_min"], 0)

    def test_disarmed(self):
        self.write("Fax receiver: DISARMED - send-only; x\nLast change: Tue Sep 29 11:35:12 EDT 2026\n")
        s = ArmController(self.cfg, self.bus, Runner()).state()
        self.assertFalse(s["armed"])
        self.assertIsNone(s["until"])
        self.assertEqual(s["ext"], "300")

    def test_parse_status_now_param(self):
        text = ("Fax receiver: ARMED - receiving faxes on ext 301\n"
                "Window ends: 2026-09-29 12:35 EDT (~1h 00m remaining)\n"
                "Last change: Tue Sep 29 11:35:12 EDT 2026\n")
        s = parse_status(text, now=datetime(2026, 9, 29, 11, 35, 0))
        self.assertEqual((s["armed"], s["remaining_min"], s["ext"]), (True, 60, "301"))
        self.assertTrue(s["until"].endswith("Z") and s["changed"].endswith("Z"))
        s = parse_status(text, now=datetime(2026, 9, 29, 13, 0, 0))
        self.assertEqual((s["armed"], s["until"], s["remaining_min"]), (False, None, 0))

    def test_missing(self):
        s = ArmController(self.cfg, self.bus, Runner()).state()
        self.assertFalse(s["armed"])
        self.assertEqual(s["raw"], "")

    def test_arm_and_disarm(self):
        self.write(self.armed_text(timedelta(minutes=60)))
        r = Runner()
        c = ArmController(self.cfg, self.bus, r)
        st = c.arm(60, "faxd api from 1.2.3.4")
        argv, kw = r.calls[0]
        self.assertEqual(argv, ["sudo", "-n", "/usr/local/bin/fax-arm.sh", "60M", "faxd api from 1.2.3.4"])
        self.assertEqual((kw["capture_output"], kw["text"], kw["timeout"]), (True, True, 20))
        self.assertTrue(st["armed"])
        self.assertEqual(self.bus.events[-1][0], "arm")
        c.disarm("me")
        self.assertEqual(r.calls[1][0], ["sudo", "-n", "/usr/local/bin/fax-arm.sh", "off", "me"])
        self.assertEqual(len(self.bus.events), 2)

    def test_sanitize_origin(self):
        self.assertEqual(sanitize_origin("faxd api from 192.168.1.5"), "faxd api from 192.168.1.5")
        self.assertEqual(sanitize_origin("faxd api from ::1"), "faxd api from ::1")
        self.assertEqual(sanitize_origin('a"; rm -rf / $(x) `y` \n|&<>'), "a rm -rf  x y ")
        self.assertEqual(sanitize_origin("user@host_name-1.x"), "user@host_name-1.x")
        self.assertEqual(len(sanitize_origin("x" * 200)), 80)
        self.assertEqual(sanitize_origin(None), "")

    def test_origin_sanitized_on_command_line(self):
        self.write(self.armed_text(timedelta(minutes=60)))
        r = Runner()
        ArmController(self.cfg, self.bus, r).arm(5, "evil`id`;x")
        self.assertEqual(r.calls[0][0][-1], "evilidx")

    def test_error(self):
        c = ArmController(self.cfg, self.bus, Runner(rc=1, err="boom"))
        with self.assertRaises(ArmError) as cm:
            c.arm(5, "x")
        self.assertIn("boom", str(cm.exception))
        self.assertEqual(self.bus.events, [])


    def test_runner_timeout_and_spawn_errors_are_arm_errors(self):
        import subprocess

        def timeout(argv, **kw):
            raise subprocess.TimeoutExpired(argv, 20)

        def missing(argv, **kw):
            raise FileNotFoundError("sudo")
        for runner in (timeout, missing):
            with self.assertRaises(ArmError):
                ArmController(self.cfg, self.bus, runner).arm(5, "x")
        self.assertEqual(self.bus.events, [])


if __name__ == "__main__":
    unittest.main()
