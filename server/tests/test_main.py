"""End-to-end: the whole daemon in-process against temp dirs and a fake AMI."""
import http.client
import json
import os
import shutil
import tempfile
import threading
import time
import unittest

from faxd import main as faxmain
from faxd.config import Config
from tests.fake_ami import FakeAmiServer

TOKEN = "e2e-token"
PDF = b"%PDF-1.4\n1 0 obj\n<< >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n"


def registrations(_pkt):
    return [
        {"Response": "Success", "EventList": "start", "Message": "listing"},
        {"Event": "OutboundRegistrationDetail", "ObjectName": "trunk-a", "Status": "Registered"},
        {"Event": "OutboundRegistrationDetail", "ObjectName": "trunk-b", "Status": "Registered"},
        {"Event": "OutboundRegistrationDetailComplete", "EventList": "Complete", "ListItems": "2"},
    ]


def sessions(_pkt):
    return [
        {"Response": "Success", "EventList": "start", "Message": "listing"},
        {"Event": "FAXSessionsComplete", "EventList": "Complete", "Total": "0"},
    ]


class SseClient(threading.Thread):
    def __init__(self, port):
        super().__init__(daemon=True)
        self.types = []
        self.lock = threading.Lock()
        self.conn = http.client.HTTPConnection("127.0.0.1", port, timeout=20)
        self.ready = threading.Event()

    def run(self):
        try:
            self.conn.request("GET", "/events?token=" + TOKEN)
            resp = self.conn.getresponse()
            self.ready.set()
            while True:
                line = resp.fp.readline()
                if not line:
                    return
                line = line.decode().strip()
                if line.startswith("event:"):
                    with self.lock:
                        self.types.append(line.split(":", 1)[1].strip())
        except Exception:  # noqa: BLE001 - closed at teardown
            self.ready.set()

    def seen(self):
        with self.lock:
            return list(self.types)

    def close(self):
        try:
            self.conn.close()
        except Exception:  # noqa: BLE001
            pass


class E2ETests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp, True)
        t = self.tmp
        for d in ("outbox", "sending", "sent", "failed", "inbox", "queue-meta", "outqueue", "done"):
            os.makedirs(os.path.join(t, d))
        self.ami = FakeAmiServer(user="faxd", secret="amisecret", actions={
            "PJSIPShowRegistrationsOutbound": registrations, "FAXSessions": sessions})
        self.addCleanup(self.ami.close)
        self.cfg = Config(
            share_root=t, outqueue=os.path.join(t, "outqueue"),
            db_path=os.path.join(t, "faxd.db"), http_bind="127.0.0.1", http_port=0, token=TOKEN,
            ami_host="127.0.0.1", ami_port=self.ami.port, ami_user="faxd", ami_secret="amisecret",
            arm_script="/nonexistent/fax-arm.sh", poll_interval=0.2, station_id="E2E")
        self.d = faxmain.Daemon(self.cfg)
        self.d.start()
        self.stopped = False
        self.addCleanup(lambda: None if self.stopped else self.d.stop())

    def call(self, method, path, body=None, headers=None):
        h = {"Authorization": "Bearer " + TOKEN}
        h.update(headers or {})
        c = http.client.HTTPConnection("127.0.0.1", self.d.port, timeout=5)
        try:
            c.request(method, path, body=body, headers=h)
            r = c.getresponse()
            return r.status, r.read()
        finally:
            c.close()

    def jcall(self, method, path, obj=None, headers=None):
        body = json.dumps(obj).encode() if obj is not None else None
        s, b = self.call(method, path, body, headers)
        return s, json.loads(b)

    def wait(self, fn, timeout=6.0):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            v = fn()
            if v:
                return v
            time.sleep(0.05)
        self.fail("timed out waiting for condition")

    def test_full_lifecycle(self):
        t = self.tmp
        self.assertTrue(self.ami.wait_logins(1, 5))
        sse = SseClient(self.d.port)
        sse.start()
        self.assertTrue(sse.ready.wait(5))
        self.addCleanup(sse.close)

        # (a) status
        st = self.wait(lambda: (lambda s: s if s["daemon"]["ami_connected"] else None)(
            self.jcall("GET", "/status")[1]))
        self.assertEqual(st["registrations"], [{"name": "trunk-a", "status": "Registered"},
                                               {"name": "trunk-b", "status": "Registered"}])
        self.assertIn("arm", st)

        # (b) enqueue
        s, b = self.call("POST", "/jobs", PDF, {"Content-Type": "application/pdf",
                                                "X-Fax-To": "9543216309"})
        self.assertEqual(s, 201)
        job = json.loads(b)
        jid, name = job["id"], job["filename"]
        self.assertTrue(jid.startswith("lf"))
        self.assertTrue(name.startswith("19543216309_") and name.endswith("-%s.pdf" % jid), name)
        self.assertTrue(os.path.isfile(os.path.join(t, "outbox", name)))
        self.assertTrue(os.path.isfile(os.path.join(t, "queue-meta", name + ".json")))
        self.assertEqual(self.jcall("GET", "/jobs/" + jid)[1]["state"], "queued")

        # (c) bash pipeline moves it to sending, writes the env, Asterisk emits events
        ast = "j20260929-120000-1"
        os.replace(os.path.join(t, "outbox", name), os.path.join(t, "sending", name))
        with open(os.path.join(t, "outqueue", ast + ".env"), "w") as f:
            f.write("JOBID=%s\nTO=19543216309\nSRCFILE=%s\n" % (ast, os.path.join(t, "sending", name).replace(os.sep, "/")))
        self.wait(lambda: self.jcall("GET", "/jobs/" + jid)[1]["ast_jobid"] == ast)
        self.ami.emit({"Event": "VarSet", "Channel": "Local/1@x-1", "Uniqueid": "u1",
                       "Variable": "JOBID", "Value": ast})
        self.ami.emit({"Event": "FAXStatus", "Channel": "Local/1@x-1", "Uniqueid": "u1",
                       "Operation": "send", "Status": "Transmitting"})
        self.wait(lambda: self.jcall("GET", "/jobs/" + jid)[1]["state"] == "sending")

        # (d) receipt and file move
        os.replace(os.path.join(t, "sending", name), os.path.join(t, "sent", name))
        with open(os.path.join(t, "sent", ast + ".txt"), "w") as f:
            f.write("Status: SUCCESS\nTo: 19543216309\nPages: 1\nDate: now\n")
        self.wait(lambda: self.jcall("GET", "/jobs/" + jid)[1]["state"] == "sent")
        j = self.jcall("GET", "/jobs/" + jid)[1]
        self.assertEqual(j["pages"], 1)
        s, body = self.call("GET", "/jobs/%s/pdf" % jid)
        self.assertEqual((s, body), (200, PDF))

        # (e) inbox
        with open(os.path.join(t, "inbox", "fax_20260929-120000_from_15551234567.pdf"), "wb") as f:
            f.write(PDF)
        inbox = self.wait(lambda: self.jcall("GET", "/inbox")[1]["jobs"])
        self.assertEqual(inbox[0]["from_number"], "15551234567")

        # (f) SSE saw events
        self.wait(lambda: "received" in sse.seen())
        self.assertIn("job", sse.seen())

        # (g) test page
        s, tj = self.jcall("POST", "/test/send", {"to": "000"})
        self.assertIn(s, (200, 201))
        self.assertEqual(tj["state"], "queued")
        with open(os.path.join(t, "outbox", tj["filename"]), "rb") as f:
            self.assertTrue(f.read().startswith(b"%PDF"))

        # (h) clean stop
        t0 = time.monotonic()
        self.stopped = True
        self.d.stop()
        self.assertLess(time.monotonic() - t0, 5)

    def test_dead_thread_exits_3(self):
        import threading as th
        self.assertIsNone(faxmain.check_once(self.d))
        dead = th.Thread(target=lambda: None, name="spool")
        dead.start()
        dead.join()
        self.d._threads[0] = dead
        with self.assertLogs("faxd.main", "CRITICAL"):
            self.assertEqual(faxmain.check_once(self.d), 3)
        stop = th.Event()
        with self.assertLogs("faxd.main", "CRITICAL"):
            self.assertEqual(faxmain.run_loop(self.d, stop, interval=0.01), 3)
        stop.set()
        self.assertEqual(faxmain.run_loop(self.d, stop, interval=0.01), 0)

    def test_status_queue_counts(self):
        # phase 1 cannot observe `converting` (the bash pipeline converts), so it is not a state
        self.d.reg.upsert_job({"id": "sh000000000001", "direction": "out", "state": "dialing"})
        q = self.d.status_fn()["queue"]
        self.assertEqual(q, {"queued": 0, "dialing": 1, "sending": 0})

    def test_empty_token_refused(self):
        rc = faxmain.main(["--env-file", os.path.join(self.tmp, "none.env")], environ={})
        self.assertEqual(rc, 2)


    def test_stop_leaves_registry_usable_for_straggling_requests(self):
        self.stopped = True
        self.d.stop()
        self.addCleanup(self.d.reg.close)
        self.assertEqual(self.d.reg.list_jobs(), [])   # a late request thread must not hit a closed db

class StatusFnTests(unittest.TestCase):
    def setUp(self):
        from faxd.db import Registry
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.reg = Registry(os.path.join(self.tmp, "f.db"))
        self.addCleanup(self.reg.close)
        self.cfg = Config(db_path=os.path.join(self.tmp, "f.db"))

    def test_full_status_carries_receiving_false(self):
        class Down:
            def connected(self):
                return False
        st = faxmain.build_status_fn(self.cfg, self.reg, Down())()
        self.assertIs(st["receiving"], False)
        self.assertEqual(set(st["queue"]), {"queued", "dialing", "sending"})

    def test_registration_refresh_does_not_block_other_callers(self):
        gate, entered = threading.Event(), threading.Event()

        class SlowAmi:
            calls = 0

            def connected(self):
                return True

            def action(self, name, **kw):
                SlowAmi.calls += 1
                entered.set()
                gate.wait(5)
                return {"Response": "Success", "list": [
                    {"Event": "OutboundRegistrationDetail", "ObjectName": "reg-out",
                     "Status": "Registered"}]}
        status = faxmain.build_status_fn(self.cfg, self.reg, SlowAmi())
        first = threading.Thread(target=status, daemon=True)
        first.start()
        self.assertTrue(entered.wait(2))
        t0 = time.monotonic()
        st = status()                                   # refresh in flight: stale value, no wait
        self.assertLess(time.monotonic() - t0, 1.0)
        self.assertEqual(st["registrations"], [])
        gate.set()
        first.join(5)
        self.assertEqual(status()["registrations"], [{"name": "reg-out", "status": "Registered"}])
        self.assertEqual(SlowAmi.calls, 1)

    def test_ami_outage_reported(self):
        from faxd.ami import AmiClient
        server = FakeAmiServer(user="faxd", secret="s", actions={
            "PJSIPShowRegistrationsOutbound": registrations,
            "Ping": lambda p: [{"Response": "Success", "Ping": "Pong"}]})
        self.addCleanup(server.close)
        ami = AmiClient("127.0.0.1", server.port, "faxd", "s", lambda ev: None,
                        backoff_initial=5.0, backoff_max=5.0, idle_ping_after=0.2, dead_after=0.6)
        self.addCleanup(ami.stop)
        ami.start()
        self.assertTrue(server.wait_logins(1))
        status = faxmain.build_status_fn(self.cfg, self.reg, ami)
        end = time.monotonic() + 3
        while not ami.connected() and time.monotonic() < end:
            time.sleep(0.01)
        self.assertTrue(status()["daemon"]["ami_connected"])
        server.mute()
        end = time.monotonic() + 3
        while status()["daemon"]["ami_connected"] and time.monotonic() < end:
            time.sleep(0.05)
        self.assertFalse(status()["daemon"]["ami_connected"])


if __name__ == "__main__":
    unittest.main()
