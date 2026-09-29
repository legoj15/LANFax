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

    def enqueue(pdf, to, *, subject="", origin="api", client="", client_key=None):
        if to == "bad":
            raise ValueError("bad number")
        calls["enqueue"].append(dict(pdf=pdf, to=to, subject=subject, origin=origin, client=client,
                                     client_key=client_key))
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


# ---- follow-up review items -------------------------------------------------
def read_event_full(resp):
    """Next real event as (event, data, id); comments are skipped."""
    ev = data = eid = None
    while True:
        line = resp.fp.readline().decode().rstrip("\r\n")
        if line.startswith(":"):
            continue
        if line.startswith("event:"):
            ev = line[6:].strip()
        elif line.startswith("id:"):
            eid = line[3:].strip()
        elif line.startswith("data:"):
            data = json.loads(line[5:].strip())
        elif line == "" and ev:
            return ev, data, eid


class RealRegCase(unittest.TestCase):
    """Api against a real Registry, EventBus and enqueue_pdf."""

    def setUp(self):
        from faxd.db import Registry
        from faxd.enqueue import enqueue_pdf
        from faxd.events import EventBus
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        r = self.tmp.name
        self.cfg = SimpleNamespace(http_bind="127.0.0.1", http_port=0, token=TOKEN, station_id="T",
                                   outbox=os.path.join(r, "outbox"),
                                   queue_meta=os.path.join(r, "queue-meta"),
                                   allowed_numbers="nanp,000,ext")
        self.reg = Registry(os.path.join(r, "f.db"))
        self.addCleanup(self.reg.close)
        self.bus = EventBus()

        def enqueue(pdf, to, **kw):
            return enqueue_pdf(self.cfg, self.reg, self.bus, pdf, to, **kw)

        self.api = Api(self.cfg, self.reg, self.bus, FakeArm(), lambda: {"daemon": {}}, enqueue,
                       lambda **kw: PDF, sse_keepalive=0.2)
        threading.Thread(target=self.api.serve_forever, daemon=True).start()
        self.addCleanup(self.api.shutdown)

    def post(self, key, to="000"):
        h = {"Content-Type": "application/pdf", "X-Fax-To": to}
        if key is not None:
            h["Idempotency-Key"] = key
        return jreq(self.api, "POST", "/jobs", PDF, h)

    def test_idempotent_post(self):
        s1, j1 = self.post("tray-1:abc")
        s2, j2 = self.post("tray-1:abc")
        self.assertEqual((s1, s2), (201, 200))
        self.assertEqual(j1["id"], j2["id"])
        self.assertEqual(j1["client_key"], "tray-1:abc")
        self.assertEqual(os.listdir(self.cfg.outbox), [j1["filename"]])
        self.assertEqual(len(self.reg.list_jobs()), 1)
        s3, j3 = self.post("tray-1:other")
        self.assertEqual(s3, 201)
        self.assertNotEqual(j3["id"], j1["id"])

    def test_idempotent_test_send(self):
        h = {"Idempotency-Key": "t.1"}
        s1, j1 = jreq(self.api, "POST", "/test/send", {"to": "000"}, h)
        s2, j2 = jreq(self.api, "POST", "/test/send", {"to": "000"}, h)
        self.assertEqual((s1, s2), (201, 200))
        self.assertEqual(j1["id"], j2["id"])
        self.assertEqual(len(os.listdir(self.cfg.outbox)), 1)

    def test_bad_idempotency_key(self):
        for bad in ("has space", "x" * 129, "semi;colon"):
            self.assertEqual(self.post(bad)[0], 400, bad)
            self.assertEqual(jreq(self.api, "POST", "/test/send", {"to": "000"},
                                  {"Idempotency-Key": bad})[0], 400, bad)
        self.assertFalse(os.path.exists(self.cfg.outbox) and os.listdir(self.cfg.outbox))

    def _history(self):
        a = self.reg.upsert_job({"id": "lfaaaaaaaaaaaa", "direction": "out", "state": "queued"})["id"]
        b = self.reg.upsert_job({"id": "lfbbbbbbbbbbbb", "direction": "out", "state": "queued"})["id"]
        e1 = self.reg.set_state(a, "queued")["event_id"]
        e2 = self.reg.set_state(a, "dialing")["event_id"]
        e3 = self.reg.set_state(b, "queued")["event_id"]
        return a, b, e1, e2, e3

    def _replay(self, path, headers=None):
        c = http.client.HTTPConnection("127.0.0.1", self.api.port, timeout=5)
        self.addCleanup(c.close)
        c.request("GET", path, headers=headers or {})
        r = c.getresponse()
        self.assertEqual(r.status, 200)
        self.assertEqual(read_event_full(r)[0], "status")
        self.assertEqual(read_event_full(r)[0], "arm")
        return r

    def test_sse_replays_missed_transitions_from_last_event_id(self):
        a, b, e1, e2, e3 = self._history()
        r = self._replay("/events?token=" + TOKEN, {"Last-Event-ID": str(e1)})
        ev, data, eid = read_event_full(r)
        self.assertEqual((ev, data["id"], data["state"], eid), ("job", a, "dialing", str(e2)))
        ev, data, eid = read_event_full(r)
        self.assertEqual((ev, data["id"], eid), ("job", b, str(e3)))
        e4 = self.reg.set_state(b, "dialing")
        self.bus.publish("job", **e4)                     # live event after the replay
        ev, data, eid = read_event_full(r)
        self.assertEqual((ev, data["state"], eid), ("job", "dialing", str(e4["event_id"])))

    def test_sse_replay_via_query_and_live_ids(self):
        a, b, e1, e2, e3 = self._history()
        r = self._replay("/events?token=%s&last_event_id=%d" % (TOKEN, e2))
        ev, data, eid = read_event_full(r)
        self.assertEqual((data["id"], eid), (b, str(e3)))
        row = self.reg.upsert_job({"id": a, "pages": 3})    # no transition: id is the job's last event
        self.bus.publish("received", **row)
        ev, data, eid = read_event_full(r)
        self.assertEqual((ev, eid), ("received", str(e2)))

    def test_sse_without_last_event_id_does_not_replay(self):
        self._history()
        r = self._replay("/events?token=" + TOKEN)
        self.bus.publish("arm", armed=False)
        self.assertEqual(read_event_full(r)[0], "arm")

    def test_jobs_updated_since(self):
        self._history()
        s, d = jreq(self.api, "GET", "/jobs?updated_since=2000-01-01T00:00:00Z")
        self.assertEqual((s, len(d["jobs"])), (200, 2))
        s, d = jreq(self.api, "GET", "/jobs?updated_since=2999-01-01T00:00:00%2B00:00")
        self.assertEqual((s, d["jobs"]), (200, []))
        self.assertEqual(jreq(self.api, "GET", "/jobs?updated_since=yesterday")[0], 400)


class FollowUpFakeCase(unittest.TestCase):
    def test_updated_since_normalized_to_utc_z(self):
        reg = FakeRegistry()
        api = make_api(reg)
        try:
            s, _ = jreq(api, "GET", "/jobs?updated_since=2026-09-29T08:00:00-04:00&since=abc")
            self.assertEqual(s, 200)
            self.assertEqual(reg.list_calls[-1]["updated_since"], "2026-09-29T12:00:00Z")
            self.assertEqual(reg.list_calls[-1]["since"], "abc")
        finally:
            api.shutdown()

    def test_events_setup_failure_is_one_clean_500(self):
        api = make_api()
        try:
            api.status_fn = lambda: 1 / 0
            with self.assertLogs("faxd.api", "ERROR"):
                out = raw_request(api, ("GET /events?token=%s HTTP/1.1\r\nHost: x\r\n"
                                        "Connection: close\r\n\r\n" % TOKEN).encode())
            self.assertIn(b" 500 ", out.split(b"\r\n")[0])
            self.assertEqual(out.count(b"HTTP/1."), 1, out)
            self.assertEqual(api.bus_.count(), 0)
        finally:
            api.shutdown()

    def test_upload_guard_503_without_reading_body(self):
        import socket
        api = make_api(max_uploads=1)
        try:
            auth = ("Authorization: Bearer %s\r\n" % TOKEN).encode()
            head = (b"POST /jobs HTTP/1.1\r\nHost: x\r\n" + auth +
                    b"Content-Type: application/pdf\r\nX-Fax-To: 000\r\nConnection: close\r\n"
                    b"Content-Length: %d\r\n\r\n" % len(PDF))
            a = socket.create_connection(("127.0.0.1", api.port), timeout=5)
            a.sendall(head + PDF[:4])            # holds the only upload slot mid-body
            deadline = time.time() + 5
            out = b""
            while time.time() < deadline:
                b = socket.create_connection(("127.0.0.1", api.port), timeout=5)
                b.sendall(head)                  # headers only: the server must not wait for a body
                out = b""
                while True:
                    chunk = b.recv(65536)
                    if not chunk:
                        break
                    out += chunk
                b.close()
                if b" 503 " in out.split(b"\r\n")[0]:
                    break
                time.sleep(0.05)
            self.assertIn(b" 503 ", out.split(b"\r\n")[0], out[:200])
            self.assertIn(b"Retry-After: 2", out)
            a.sendall(PDF[4:])
            resp = b""
            while True:
                chunk = a.recv(65536)
                if not chunk:
                    break
                resp += chunk
            a.close()
            self.assertIn(b" 201 ", resp.split(b"\r\n")[0])
            self.assertEqual(jreq(api, "POST", "/jobs", PDF,
                                  {"Content-Type": "application/pdf", "X-Fax-To": "000"})[0], 201)
        finally:
            api.shutdown()

    def test_web_page_handles_cancelled_and_reconnect(self):
        from faxd import api as api_mod
        with open(api_mod._WEB, encoding="utf-8") as f:
            html = f.read()
        self.assertIn('cancelled: "gray"', html)
        self.assertIn("reloadOnOpen", html)

class ThirdPartyReviewCase(unittest.TestCase):
    """Bounded replay, one-query job lookup, caller-scoped Idempotency-Key."""

    # RealRegCase's harness, without re-running its tests
    setUp, post, _history, _replay = (RealRegCase.setUp, RealRegCase.post, RealRegCase._history,
                                      RealRegCase._replay)

    def _many(self, n):
        a = self.reg.upsert_job({"id": "lfaaaaaaaaaaaa", "direction": "out", "state": "queued"})["id"]
        first = self.reg.set_state(a, "queued")
        for _ in range(n - 1):
            last = self.reg.set_state(a, "queued")
        return a, first, last

    def test_replay_over_200_events_sends_one_resync(self):
        a, first, last = self._many(201)
        r = self._replay("/events?token=" + TOKEN, {"Last-Event-ID": "0"})
        ev, data, eid = read_event_full(r)
        self.assertEqual(ev, "resync")
        self.assertEqual(data["reason"], "too_many_events")
        self.assertEqual(data["since"], self.reg.job_events(a)[0]["ts"])
        self.assertEqual(eid, str(last["event_id"]))       # a later reconnect does not resync again
        self.bus.publish("arm", armed=False)                # then the live stream, no job replay
        self.assertEqual(read_event_full(r)[0], "arm")

    def test_replay_of_200_events_is_normal(self):
        a, first, last = self._many(200)
        r = self._replay("/events?token=" + TOKEN, {"Last-Event-ID": "0"})
        ev, data, eid = read_event_full(r)
        self.assertEqual((ev, data["id"], eid), ("job", a, str(last["event_id"])))

    def test_replay_looks_jobs_up_in_one_query(self):
        a, b, e1, e2, e3 = self._history()

        def boom(job_id):
            raise AssertionError("per-job get_job in the replay")
        self.reg.get_job = boom
        r = self._replay("/events?token=" + TOKEN, {"Last-Event-ID": str(e1)})
        self.assertEqual(read_event_full(r)[1]["id"], a)
        self.assertEqual(read_event_full(r)[1]["id"], b)

    def test_idempotency_key_scoped_to_caller(self):
        other = self.reg.upsert_job({"id": "lfcccccccccccc", "direction": "out", "state": "queued",
                                     "client": "10.9.9.9", "client_key": "shared:1"})
        s1, j1 = self.post("shared:1")
        self.assertEqual(s1, 201)                           # another caller's key is not ours
        self.assertNotEqual(j1["id"], other["id"])
        s2, j2 = self.post("shared:1")
        self.assertEqual((s2, j2["id"]), (200, j1["id"]))

    def test_web_page_resync_and_receiving_expiry(self):
        from faxd import api as api_mod
        with open(api_mod._WEB, encoding="utf-8") as f:
            html = f.read()
        self.assertIn('on("resync"', html)
        self.assertIn("RECEIVING_TTL_MS = 120000", html)


if __name__ == "__main__":
    unittest.main()
