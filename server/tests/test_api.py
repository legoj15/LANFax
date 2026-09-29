import http.client
import json
import os
import tempfile
import threading
import time
import unittest
from types import SimpleNamespace

from faxd.api import Api
from tests.fakes_api import FakeArm, FakeBus, FakeRegistry

TOKEN = "s3cret-token"
PDF = b"%PDF-1.4\n%fake\n%%EOF\n"


def make_api(reg=None, **kw):
    cfg = SimpleNamespace(http_bind="127.0.0.1", http_port=0, token=TOKEN, station_id="TESTSTN")
    reg = reg or FakeRegistry()
    bus, arm = FakeBus(), FakeArm()
    calls = {"enqueue": [], "testpage": []}

    def enqueue(pdf, to, *, subject="", origin="api", client=""):
        if to == "bad":
            raise ValueError("bad number")
        calls["enqueue"].append(dict(pdf=pdf, to=to, subject=subject, origin=origin, client=client))
        return {"id": "lf000000000001", "state": "queued", "to_number": to}

    def testpage(*, to, station, when, note=""):
        calls["testpage"].append(dict(to=to, station=station, note=note, when=when))
        return PDF

    api = Api(cfg, reg, bus, arm, lambda: {"daemon": {"version": "t"}}, enqueue, testpage, **kw)
    t = threading.Thread(target=api.serve_forever, daemon=True)
    t.start()
    api.bus_, api.arm_, api.calls, api.thread = bus, arm, calls, t
    return api


def req(api, method, path, body=None, headers=None, token=True):
    h = dict(headers or {})
    if token:
        h["Authorization"] = "Bearer " + (token if isinstance(token, str) else TOKEN)
    if isinstance(body, (dict, list)):
        body = json.dumps(body).encode()
        h.setdefault("Content-Type", "application/json")
    c = http.client.HTTPConnection("127.0.0.1", api.port, timeout=5)
    try:
        c.request(method, path, body=body, headers=h)
        r = c.getresponse()
        return r.status, r, r.read()
    finally:
        c.close()


def jreq(api, *a, **kw):
    s, r, b = req(api, *a, **kw)
    return s, json.loads(b)


class ApiCase(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.pdf_path = os.path.join(cls.tmp.name, "out.pdf")
        with open(cls.pdf_path, "wb") as f:
            f.write(PDF)
        jobs = {
            "in1": {"id": "in1", "direction": "in", "state": "received", "filename": "fax_a.pdf", "pdf_path": None},
            "lf1": {"id": "lf1", "direction": "out", "state": "sent", "filename": "1555_x-lf1.pdf",
                    "pdf_path": cls.pdf_path},
            "lf2": {"id": "lf2", "direction": "out", "state": "failed", "filename": "b.pdf",
                    "pdf_path": os.path.join(cls.tmp.name, "missing.pdf")},
            "lf3": {"id": "lf3", "direction": "out", "state": "queued", "filename": "c.pdf",
                    "pdf_path": None},
        }
        events = {"lf1": [{"id": 1, "job_id": "lf1", "ts": "t", "state": "sent", "detail": None}]}
        cls.reg = FakeRegistry(jobs, events)
        cls.api = make_api(cls.reg)

    @classmethod
    def tearDownClass(cls):
        cls.api.shutdown()
        cls.tmp.cleanup()

    def setUp(self):
        self.api.calls["enqueue"].clear()
        self.api.calls["testpage"].clear()
        self.api.arm_.calls.clear()
        self.reg.list_calls.clear()

    # --- public / auth
    def test_index_no_auth(self):
        s, r, b = req(self.api, "GET", "/", token=False)
        self.assertEqual(s, 200)
        self.assertEqual(r.getheader("Content-Type"), "text/html; charset=utf-8")
        self.assertEqual(r.getheader("Cache-Control"), "no-store")
        self.assertIn(b"<html", b.lower())

    def test_healthz_no_auth(self):
        s, d = jreq(self.api, "GET", "/healthz", token=False)
        self.assertEqual((s, d), (200, {"ok": True}))

    def test_auth_required(self):
        for path in ("/status", "/jobs", "/jobs/lf1", "/inbox", "/nope"):
            self.assertEqual(jreq(self.api, "GET", path, token=False)[0], 401, path)
            self.assertEqual(jreq(self.api, "GET", path, token="wrong")[0], 401, path)
        s, d = jreq(self.api, "GET", "/status", token=False)
        self.assertEqual(d, {"error": "unauthorized"})
        self.assertEqual(jreq(self.api, "POST", "/arm", {"minutes": 5}, token=False)[0], 401)

    def test_query_token_only_for_events(self):
        s, _ = jreq(self.api, "GET", "/status?token=" + TOKEN, token=False)
        self.assertEqual(s, 401)

    def test_status_merges_arm(self):
        s, d = jreq(self.api, "GET", "/status")
        self.assertEqual(s, 200)
        self.assertEqual(d["daemon"], {"version": "t"})
        self.assertIs(d["arm"]["armed"], False)

    # --- jobs
    def test_jobs_filters(self):
        s, d = jreq(self.api, "GET", "/jobs?direction=out&state=sent,failed&limit=5")
        self.assertEqual(s, 200)
        self.assertEqual([j["id"] for j in d["jobs"]], ["lf1", "lf2"])
        self.assertEqual(self.reg.list_calls[-1]["direction"], "out")
        self.assertEqual(self.reg.list_calls[-1]["states"], ["sent", "failed"])
        self.assertEqual(self.reg.list_calls[-1]["limit"], 5)

    def test_jobs_bad_params(self):
        self.assertEqual(jreq(self.api, "GET", "/jobs?limit=abc")[0], 400)
        self.assertEqual(jreq(self.api, "GET", "/jobs?direction=sideways")[0], 400)

    def test_job_detail_and_404(self):
        s, d = jreq(self.api, "GET", "/jobs/lf1")
        self.assertEqual(s, 200)
        self.assertEqual(d["id"], "lf1")
        self.assertEqual(len(d["events"]), 1)
        self.assertEqual(jreq(self.api, "GET", "/jobs/zzz")[0], 404)
        self.assertEqual(jreq(self.api, "GET", "/jobs/a.b")[0], 404)
        self.assertEqual(jreq(self.api, "GET", "/jobs/a%2Fb")[0], 404)

    def test_pdf(self):
        s, r, b = req(self.api, "GET", "/jobs/lf1/pdf")
        self.assertEqual(s, 200)
        self.assertEqual(b, PDF)
        self.assertEqual(r.getheader("Content-Type"), "application/pdf")
        self.assertEqual(r.getheader("Content-Disposition"), 'inline; filename="1555_x-lf1.pdf"')
        self.assertEqual(r.getheader("Content-Length"), str(len(PDF)))

    def test_pdf_missing(self):
        self.assertEqual(jreq(self.api, "GET", "/jobs/lf2/pdf")[0], 404)
        self.assertEqual(jreq(self.api, "GET", "/jobs/lf3/pdf")[0], 404)
        self.assertEqual(jreq(self.api, "GET", "/jobs/nope/pdf")[0], 404)

    def test_inbox(self):
        s, d = jreq(self.api, "GET", "/inbox?limit=10")
        self.assertEqual([j["id"] for j in d["jobs"]], ["in1"])
        self.assertEqual(self.reg.list_calls[-1]["direction"], "in")

    # --- post job
    def test_post_job(self):
        s, d = jreq(self.api, "POST", "/jobs", PDF,
                    {"Content-Type": "application/pdf", "X-Fax-To": "5551234567", "X-Fax-Subject": "hello"})
        self.assertEqual(s, 201)
        self.assertEqual(d["id"], "lf000000000001")
        c = self.api.calls["enqueue"][0]
        self.assertEqual((c["pdf"], c["to"], c["subject"], c["origin"], c["client"]),
                         (PDF, "5551234567", "hello", "api", "127.0.0.1"))

    def test_post_job_errors(self):
        h = {"Content-Type": "application/pdf"}
        self.assertEqual(jreq(self.api, "POST", "/jobs", PDF, h)[0], 400)
        self.assertEqual(jreq(self.api, "POST", "/jobs", b"hello", dict(h, **{"X-Fax-To": "555"}))[0], 400)
        self.assertEqual(jreq(self.api, "POST", "/jobs", PDF, dict(h, **{"X-Fax-To": "bad"}))[0], 400)
        self.assertEqual(self.api.calls["enqueue"], [])

    # --- arm
    def test_arm(self):
        s, d = jreq(self.api, "POST", "/arm", {"minutes": 60})
        self.assertEqual(s, 200)
        self.assertTrue(d["armed"])
        self.assertEqual(self.api.arm_.calls, [("arm", 60, "faxd api from 127.0.0.1")])

    def test_disarm(self):
        s, d = jreq(self.api, "POST", "/disarm")
        self.assertEqual(s, 200)
        self.assertIs(d["armed"], False)
        self.assertEqual(self.api.arm_.calls, [("disarm", "faxd api from 127.0.0.1")])

    def test_test_receive_default(self):
        s, d = jreq(self.api, "POST", "/test/receive")
        self.assertEqual(s, 200)
        self.assertEqual(self.api.arm_.calls, [("arm", 20, "faxd api from 127.0.0.1")])
        jreq(self.api, "POST", "/test/receive", {"minutes": 30})
        self.assertEqual(self.api.arm_.calls[-1][1], 30)

    def test_invalid_minutes(self):
        for bad in (0, -5, 1441, "60", 1.5, None, True):
            self.assertEqual(jreq(self.api, "POST", "/arm", {"minutes": bad})[0], 400, bad)
            self.assertEqual(jreq(self.api, "POST", "/test/receive", {"minutes": bad})[0], 400, bad)
        self.assertEqual(jreq(self.api, "POST", "/arm", {})[0], 400)
        self.assertEqual(jreq(self.api, "POST", "/arm", b"not json")[0], 400)
        self.assertEqual(self.api.arm_.calls, [])

    def test_test_send(self):
        s, d = jreq(self.api, "POST", "/test/send", {"to": "5551234567", "note": "hi"})
        self.assertEqual(s, 201)
        tp = self.api.calls["testpage"][0]
        self.assertEqual((tp["to"], tp["station"], tp["note"]), ("5551234567", "TESTSTN", "hi"))
        e = self.api.calls["enqueue"][0]
        self.assertEqual((e["pdf"], e["to"], e["subject"], e["origin"]), (PDF, "5551234567", "test page", "test"))
        self.assertEqual(jreq(self.api, "POST", "/test/send", {"note": "x"})[0], 400)
        self.assertEqual(jreq(self.api, "POST", "/test/send", {"to": "bad"})[0], 400)

    # --- routing
    def test_405_and_404(self):
        self.assertEqual(jreq(self.api, "POST", "/", b"x", token=False)[0], 405)
        self.assertEqual(jreq(self.api, "GET", "/arm")[0], 405)
        self.assertEqual(jreq(self.api, "DELETE", "/jobs/lf1")[0], 405)
        s, d = jreq(self.api, "GET", "/nope")
        self.assertEqual((s, d), (404, {"error": "not found"}))

    def test_500_generic(self):
        api = make_api()
        try:
            api.status_fn = lambda: 1 / 0
            with self.assertLogs("faxd.api", "ERROR"):
                s, d = jreq(api, "GET", "/status")
            self.assertEqual((s, d), (500, {"error": "internal error"}))
        finally:
            api.shutdown()


def raw_request(api, data):
    import socket
    s = socket.create_connection(("127.0.0.1", api.port), timeout=5)
    try:
        s.sendall(data)
        out = b""
        while True:
            b = s.recv(65536)
            if not b:
                break
            out += b
        return out
    finally:
        s.close()


class ReviewFixCase(unittest.TestCase):
    def test_no_content_length_is_empty_body(self):
        api = make_api()
        try:
            auth = ("Authorization: Bearer %s\r\n" % TOKEN).encode()
            out = raw_request(api, b"POST /disarm HTTP/1.1\r\nHost: x\r\n" + auth + b"Connection: close\r\n\r\n")
            self.assertTrue(out.startswith(b"HTTP/1.0 200") or out.startswith(b"HTTP/1.1 200"), out[:80])
            out = raw_request(api, b"POST /test/receive HTTP/1.1\r\nHost: x\r\n" + auth + b"Connection: close\r\n\r\n")
            self.assertIn(b" 200 ", out.split(b"\r\n")[0])
            self.assertEqual(api.arm_.calls[-1][1], 20)
            out = raw_request(api, b"POST /arm HTTP/1.1\r\nHost: x\r\n" + auth + b"Connection: close\r\n\r\n")
            self.assertIn(b" 400 ", out.split(b"\r\n")[0])
        finally:
            api.shutdown()

    def test_arm_error_is_502(self):
        from faxd.arm import ArmError
        api = make_api()
        try:
            def boom(*a):
                raise ArmError("sudo: a password is required")
            api.arm.arm = boom
            s, d = jreq(api, "POST", "/arm", {"minutes": 5})
            self.assertEqual((s, d), (502, {"error": "sudo: a password is required"}))
        finally:
            api.shutdown()

    def test_events_token_never_logged(self):
        api = make_api(sse_keepalive=0.1)
        try:
            with self.assertLogs("faxd.api", "DEBUG") as cm:
                c, r = open_sse(api)
                read_event(r)
                c.close()
                jreq(api, "GET", "/jobs?token=" + TOKEN)
            self.assertTrue(cm.output)
            self.assertFalse([m for m in cm.output if TOKEN in m], cm.output)
        finally:
            api.shutdown()

    def test_upload_body_not_copied(self):
        api = make_api()
        try:
            s, _ = jreq(api, "POST", "/jobs", PDF, {"Content-Type": "application/pdf", "X-Fax-To": "555"})
            self.assertEqual(s, 201)
            pdf = api.calls["enqueue"][-1]["pdf"]
            self.assertIsInstance(pdf, bytearray)   # the receive buffer itself, no bytes() copy
            self.assertEqual(pdf, PDF)
        finally:
            api.shutdown()

    def test_limits(self):
        from faxd.api import MAX_BODY, _Handler
        self.assertEqual(MAX_BODY, 25 * 1024 * 1024)
        self.assertEqual(_Handler.timeout, 30)

    def test_connection_cap(self):
        import socket
        api = make_api(max_conns=1)
        try:
            c, r = open_sse(api)          # holds the only slot
            read_event(r)
            s = socket.create_connection(("127.0.0.1", api.port), timeout=5)
            s.sendall(b"GET /healthz HTTP/1.0\r\n\r\n")
            self.assertEqual(s.recv(100), b"")   # closed without an answer
            s.close()
            r.close()
            c.close()
            deadline = time.time() + 5
            while time.time() < deadline:
                api.bus_.publish("job", id="x")
                try:
                    if jreq(api, "GET", "/healthz", token=False)[0] == 200:
                        break
                except OSError:
                    pass
                time.sleep(0.1)
            self.assertEqual(jreq(api, "GET", "/healthz", token=False)[0], 200)
        finally:
            api.shutdown()

    def test_registry_single_connection(self):
        from faxd.db import Registry
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        reg = Registry(os.path.join(tmp.name, "r.db"))
        self.addCleanup(reg.close)
        api = make_api(reg)
        try:
            for _ in range(200):
                self.assertEqual(jreq(api, "GET", "/jobs")[0], 200)
            self.assertEqual(reg.connection_count(), 1)
        finally:
            api.shutdown()


class BodyLimitCase(unittest.TestCase):
    def test_413(self):
        api = make_api(max_body=100)
        try:
            h = {"Content-Type": "application/pdf", "X-Fax-To": "555"}
            self.assertEqual(jreq(api, "POST", "/jobs", PDF + b"x" * 200, h)[0], 413)
            self.assertEqual(jreq(api, "POST", "/jobs", PDF, h)[0], 201)
        finally:
            api.shutdown()


def open_sse(api, path="/events?token=" + TOKEN):
    c = http.client.HTTPConnection("127.0.0.1", api.port, timeout=5)
    c.request("GET", path)
    return c, c.getresponse()


def read_event(resp):
    """Return (event, data) or (':comment', text)."""
    ev, data = None, None
    while True:
        line = resp.fp.readline().decode().rstrip("\r\n")
        if line.startswith(":"):
            return (":comment", line)
        if line.startswith("event:"):
            ev = line[6:].strip()
        elif line.startswith("data:"):
            data = json.loads(line[5:].strip())
        elif line == "" and ev:
            return ev, data
        elif line == "" and data is None and ev is None:
            continue


class SseCase(unittest.TestCase):
    def test_bad_token_401(self):
        api = make_api()
        try:
            c, r = open_sse(api, "/events?token=nope")
            self.assertEqual(r.status, 401)
            c.close()
            c, r = open_sse(api, "/events")
            self.assertEqual(r.status, 401)
            c.close()
        finally:
            api.shutdown()

    def test_stream_events_and_cleanup(self):
        api = make_api()
        try:
            c, r = open_sse(api)
            self.assertEqual(r.status, 200)
            self.assertIn("text/event-stream", r.getheader("Content-Type"))
            self.assertEqual(read_event(r)[0], "status")
            self.assertEqual(read_event(r)[0], "arm")
            api.bus_.publish("job", id="lf9", state="sent")
            ev, data = read_event(r)
            self.assertEqual((ev, data["id"], data["type"]), ("job", "lf9", "job"))
            self.assertEqual(api.bus_.count(), 1)
            r.close()
            c.close()
            deadline = time.time() + 5
            while api.bus_.count() and time.time() < deadline:
                api.bus_.publish("job", id="x")   # forces a write so the dead socket is detected
                time.sleep(0.1)
            self.assertEqual(api.bus_.count(), 0)
        finally:
            api.shutdown()

    def test_keepalive(self):
        api = make_api(sse_keepalive=0.2)
        try:
            c, r = open_sse(api)
            read_event(r)
            read_event(r)
            self.assertEqual(read_event(r), (":comment", ": keepalive"))
            c.close()
        finally:
            api.shutdown()

    def test_shutdown_with_open_stream(self):
        api = make_api()
        c, r = open_sse(api)
        read_event(r)
        t0 = time.time()
        api.shutdown()
        api.thread.join(3)
        self.assertLess(time.time() - t0, 3)
        self.assertFalse(api.thread.is_alive())
        c.close()


if __name__ == "__main__":
    unittest.main()
