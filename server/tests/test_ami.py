import threading
import time
import unittest

from faxd.ami import AmiClient, AmiNotConnected, AmiProgress, AmiTimeout
from tests.fake_ami import FakeAmiServer


def wait_until(pred, timeout=3.0):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if pred():
            return True
        time.sleep(0.01)
    return pred()


def dbget(p):
    if p.get("Key") == "here":
        return [{"Response": "Success", "Message": "Result will follow"},
                {"Event": "DBGetResponse", "Family": p["Family"], "Key": "here", "Val": "hello"},
                {"Event": "DBGetComplete", "EventList": "Complete"}]
    return [{"Response": "Error", "Message": "Database entry not found"}]


ACTIONS = {
    "Ping": lambda p: [{"Response": "Success", "Ping": "Pong"}],
    "FAXSessions": lambda p: [
        {"Response": "Success", "EventList": "start", "Message": "x"},
        {"Event": "FAXSessionsEntry", "Channel": "PJSIP/a-1", "PagesTransmitted": "2"},
        {"Event": "FAXSessionsEntry", "Channel": "PJSIP/b-2", "PagesTransmitted": "0"},
        {"Event": "FAXSessionsComplete", "EventList": "Complete", "Total": "2"},
    ],
    "DBGet": dbget,
    "DBPut": lambda p: [{"Response": "Success", "Message": "Updated database successfully"}],
    "DBDel": lambda p: [{"Response": "Success", "Message": "Key deleted successfully"}],
    "Getvar": lambda p: [{"Response": "Success", "Variable": p["Variable"], "Value": "42"}],
    "Silent": lambda p: None,
}


class ClientBase(unittest.TestCase):
    secret = "s3cret"

    def setUp(self):
        self.events = []
        self.server = FakeAmiServer(user="faxd", secret="s3cret", actions=ACTIONS)
        self.client = AmiClient("127.0.0.1", self.server.port, "faxd", self.secret,
                                self.events.append, backoff_initial=0.05, backoff_max=0.2)
        self.addCleanup(self.server.close)
        self.addCleanup(self.client.stop)

    def up(self):
        self.client.start()
        self.assertTrue(self.server.wait_logins(1))
        self.assertTrue(wait_until(self.client.connected))


class TestClient(ClientBase):
    def test_login_connects(self):
        self.up()
        self.assertEqual(self.server.received[0]["Action"], "Login")
        self.assertEqual(self.server.received[0]["Events"], "on")

    def test_ping(self):
        self.up()
        r = self.client.action("Ping")
        self.assertEqual(r["Response"], "Success")
        self.assertIn("ActionID", r)

    def test_list_response(self):
        self.up()
        r = self.client.action("FAXSessions")
        self.assertEqual(len(r["list"]), 2)
        self.assertEqual(r["list"][0]["Channel"], "PJSIP/a-1")

    def test_db(self):
        self.up()
        self.assertEqual(self.client.db_get("fax", "here"), "hello")
        self.assertIsNone(self.client.db_get("fax", "gone"))
        self.assertTrue(self.client.db_put("fax", "k", "v"))
        self.assertTrue(self.client.db_del("fax", "k"))
        self.assertEqual(self.client.get_var("PJSIP/a-1", "X"), "42")

    def test_timeout_then_recovers(self):
        self.up()
        with self.assertRaises(AmiTimeout):
            self.client.action("Silent", timeout=0.2)
        self.assertEqual(self.client.action("Ping")["Response"], "Success")

    def test_not_connected_raises_immediately(self):
        t0 = time.monotonic()
        with self.assertRaises(AmiNotConnected):
            self.client.action("Ping")
        self.assertLess(time.monotonic() - t0, 0.5)

    def test_events_in_order_and_callback_errors_survive(self):
        seen = []

        def cb(ev):
            seen.append(ev["N"])
            if ev["N"] == "1":
                raise RuntimeError("boom")
        self.client._on_event = cb
        self.up()
        with self.assertLogs("faxd.ami", level="ERROR"):
            for n in "123":
                self.server.emit({"Event": "Foo", "N": n})
            self.assertTrue(wait_until(lambda: len(seen) == 3))
        self.assertEqual(seen, ["1", "2", "3"])
        self.assertEqual(self.client.action("Ping")["Response"], "Success")

    def test_reconnect(self):
        self.up()
        self.server.drop_client()
        self.assertTrue(self.server.wait_logins(2))
        self.assertTrue(wait_until(self.client.connected))
        self.assertEqual(self.client.action("Ping")["Response"], "Success")

    def test_pending_fails_on_disconnect(self):
        self.up()
        res = []

        def call():
            try:
                self.client.action("Silent", timeout=3)
            except Exception as e:
                res.append(e)
        t = threading.Thread(target=call)
        t.start()
        time.sleep(0.1)
        self.server.drop_client()
        t.join(2)
        self.assertTrue(res and isinstance(res[0], AmiNotConnected))

    def test_stop_prompt(self):
        self.up()
        t0 = time.monotonic()
        self.client.stop()
        self.assertLess(time.monotonic() - t0, 2.0)
        self.assertFalse(self.client.connected())


class TestBadSecret(ClientBase):
    secret = "wrong"

    def test_bad_secret(self):
        with self.assertLogs("faxd.ami", level="ERROR") as cm:
            self.client.start()
            self.assertTrue(self.server.wait_failed_logins(1))
            self.assertTrue(wait_until(lambda: len(cm.output) > 0))
        self.assertFalse(self.client.connected())


class FakeReg:
    def __init__(self):
        self.jobs = {}
        self.calls = []

    def add(self, job_id, ast_jobid, state="queued"):
        self.jobs[job_id] = dict(id=job_id, ast_jobid=ast_jobid, state=state, pages=None,
                                 detail=None)

    def find_by_ast_jobid(self, a):
        return next((dict(j) for j in self.jobs.values() if j["ast_jobid"] == a), None)

    def get_job(self, i):
        j = self.jobs.get(i)
        return dict(j) if j else None

    def upsert_job(self, job):
        self.jobs.setdefault(job["id"], {}).update(job)
        return dict(self.jobs[job["id"]])

    def set_state(self, job_id, state, *, pages=None, error=None, detail=None):
        j = self.jobs.get(job_id)
        if not j:
            return None
        j["state"] = state
        if pages is not None:
            j["pages"] = pages
        if error is not None:
            j["error"] = error
        if detail is not None:
            j["detail"] = detail
        self.calls.append((job_id, state, pages, detail))
        return dict(j)


class FakeBus:
    def __init__(self):
        self.published = []

    def publish(self, type, **data):
        self.published.append((type, data))


class FakeAmi:
    def __init__(self):
        self.sessions = []

    def action(self, name, **kw):
        assert name == "FAXSessions"
        return {"Response": "Success", "list": self.sessions}


def varset(uid, jobid, chan="PJSIP/x-1"):
    return {"Event": "VarSet", "Channel": chan, "Uniqueid": uid,
            "Variable": "JOBID", "Value": jobid}


class TestProgress(unittest.TestCase):
    def setUp(self):
        self.reg = FakeReg()
        self.bus = FakeBus()
        self.ami = FakeAmi()
        self.p = AmiProgress(self.reg, self.bus, self.ami)

    def test_full_sequence(self):
        self.reg.add("lf1", "j1")
        self.p.handle(varset("u1", "j1"))
        self.assertEqual(self.reg.jobs["lf1"]["state"], "dialing")
        self.p.handle({"Event": "FAXStatus", "Uniqueid": "u1", "Operation": "send",
                       "Status": "Negotiating"})
        self.assertEqual(self.reg.jobs["lf1"]["state"], "sending")
        self.assertEqual(self.reg.jobs["lf1"]["detail"], "Negotiating")
        self.p.handle({"Event": "SendFAX", "Uniqueid": "u1", "PagesTransferred": "1",
                       "TransferRate": "14400"})
        self.assertEqual(self.reg.jobs["lf1"]["state"], "sending")
        self.assertEqual(self.reg.jobs["lf1"]["pages"], 1)
        self.p.handle({"Event": "Hangup", "Uniqueid": "u1"})
        n = len(self.reg.calls)
        self.p.handle({"Event": "FAXStatus", "Uniqueid": "u1", "Operation": "send", "Status": "x"})
        self.assertEqual(len(self.reg.calls), n)

    def test_row_appears_later(self):
        self.p.handle(varset("u1", "j9"))
        self.assertEqual(self.reg.calls, [])
        self.reg.add("lf9", "j9")
        self.p.handle({"Event": "Newexten", "Uniqueid": "u1"})
        self.assertEqual(self.reg.jobs["lf9"]["state"], "dialing")

    def test_receive_publishes_status(self):
        self.p.handle({"Event": "ReceiveFAX", "Uniqueid": "r1", "CallerIDNum": "5551234"})
        self.p.handle({"Event": "FAXStatus", "Uniqueid": "r1", "Operation": "receive",
                       "CallerIDNum": "5551234", "Status": "Receiving"})
        self.assertEqual(len(self.bus.published), 2)
        t, d = self.bus.published[0]
        self.assertEqual(t, "status")
        self.assertTrue(d["receiving"])
        self.assertEqual(d["from"], "5551234")

    def test_originate_failure(self):
        self.reg.add("lf1", "j1")
        self.p.handle(varset("u1", "j1"))
        self.p.handle({"Event": "OriginateResponse", "Response": "Failure",
                       "Channel": "PJSIP/x-1", "Uniqueid": "u1"})
        self.assertEqual(self.reg.jobs["lf1"]["detail"], "dial attempt failed")
        self.assertEqual(self.reg.jobs["lf1"]["state"], "dialing")

    def test_poll_sessions(self):
        self.reg.add("lf1", "j1")
        self.reg.add("lf2", "j2")
        self.p.handle(varset("u1", "j1", "PJSIP/a-1"))
        self.p.handle(varset("u2", "j2", "PJSIP/b-2"))
        self.p.handle({"Event": "FAXStatus", "Uniqueid": "u1", "Operation": "send", "Status": "s"})
        self.ami.sessions = [
            {"Event": "FAXSessionsEntry", "Channel": "PJSIP/a-1", "PagesTransmitted": "2",
             "PageNumber": "3"},
            {"Event": "FAXSessionsEntry", "Channel": "PJSIP/zzz-9", "PagesTransmitted": "7"},
        ]
        self.p.poll_sessions()
        self.assertEqual(self.reg.jobs["lf1"]["pages"], 2)
        self.assertEqual(self.reg.jobs["lf1"]["state"], "sending")
        self.assertIsNone(self.reg.jobs["lf2"]["pages"])

    def test_poll_swallows_ami_errors(self):
        class Bad:
            def action(self, *a, **k):
                raise AmiNotConnected()
        AmiProgress(self.reg, self.bus, Bad()).poll_sessions()


if __name__ == "__main__":
    unittest.main()
