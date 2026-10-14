"""New send features over HTTP: multipart multi-document POST /jobs, POST /analyze
(the blank-page pre-check), and POST /jobs/{id}/cancel."""
import os
import tempfile
import threading
import unittest
from types import SimpleNamespace
from unittest.mock import patch

from faxd.api import Api
from tests.fakes_api import FakeArm
from tests.test_api import PDF, TOKEN, jreq, make_api, req


def multipart_body(boundary, *parts):
    """A multipart/form-data body: parts are (filename, bytes)."""
    out = b""
    for name, data in parts:
        out += (b"--" + boundary + b"\r\n"
                b'Content-Disposition: form-data; name="file"; filename="' + name + b'"\r\n'
                b"Content-Type: application/pdf\r\n\r\n" + data + b"\r\n")
    return out + b"--" + boundary + b"--\r\n"


class MultipartCase(unittest.TestCase):
    """POST /jobs with several PDF parts, and the /analyze pre-send check."""

    BOUNDARY = b"lanfax-test-boundary-7381"

    def setUp(self):
        self.api = make_api()
        self.addCleanup(self.api.shutdown)

    def post_multipart(self, *parts, path="/jobs", extra=None):
        h = {"Content-Type": "multipart/form-data; boundary=" + self.BOUNDARY.decode(),
             "X-Fax-To": "5551234567"}
        h.update(extra or {})
        return jreq(self.api, "POST", path, multipart_body(self.BOUNDARY, *parts), h)

    def test_two_documents_arrive_as_parts(self):
        s, d = self.post_multipart((b"cover.pdf", PDF), (b"scan nr2.pdf", b"%PDF-second"))
        self.assertEqual(s, 201)
        parts = self.api.calls["enqueue"][0]["pdf"]
        self.assertEqual([(n, bytes(b)) for n, b in parts],
                         [("cover.pdf", PDF), ("scan nr2.pdf", b"%PDF-second")])

    def test_single_multipart_part_still_works(self):
        s, _ = self.post_multipart((b"one.pdf", PDF))
        self.assertEqual(s, 201)
        self.assertEqual(bytes(self.api.calls["enqueue"][0]["pdf"][0][1]), PDF)

    def test_unquoted_filename_is_parsed(self):
        # .NET's MultipartFormDataContent leaves simple tokens unquoted
        body = (b"--" + self.BOUNDARY + b"\r\n"
                b"Content-Disposition: form-data; name=file; filename=a.pdf\r\n\r\n" + PDF + b"\r\n"
                b"--" + self.BOUNDARY + b"--\r\n")
        h = {"Content-Type": "multipart/form-data; boundary=" + self.BOUNDARY.decode(),
             "X-Fax-To": "5551234567"}
        s, _ = jreq(self.api, "POST", "/jobs", body, h)
        self.assertEqual(s, 201)
        parts = self.api.calls["enqueue"][0]["pdf"]
        self.assertEqual([(n, bytes(b)) for n, b in parts], [("a.pdf", PDF)])

    def test_non_pdf_part_is_400(self):
        self.assertEqual(self.post_multipart((b"a.pdf", PDF), (b"b.txt", b"hello"))[0], 400)
        self.assertEqual(self.api.calls["enqueue"], [])

    def test_bad_content_type_is_400(self):
        h = {"Content-Type": "multipart/form-data", "X-Fax-To": "555"}
        self.assertEqual(jreq(self.api, "POST", "/jobs", b"--x--\r\n", h)[0], 400)

    def test_empty_multipart_is_400(self):
        h = {"Content-Type": "multipart/form-data; boundary=" + self.BOUNDARY.decode(),
             "X-Fax-To": "555"}
        self.assertEqual(jreq(self.api, "POST", "/jobs",
                              b"--" + self.BOUNDARY + b"--\r\n", h)[0], 400)

    def test_analyze_reports_blank_pages(self):
        with patch("faxd.gs.compose", return_value=b"%PDF merged") as compose, \
                patch("faxd.gs.blank_pages", return_value=[1]) as blanks, \
                patch("faxd.gs.page_count", return_value=3) as pages:
            s, d = self.post_multipart((b"a.pdf", PDF), (b"b.pdf", PDF), path="/analyze")
        self.assertEqual(s, 200)
        self.assertEqual(d, {"pages": 3, "blank_pages": [1]})
        self.assertEqual(len(compose.call_args[0][1]), 2)   # both parts, unstamped
        self.assertEqual(blanks.call_args[0][1], b"%PDF merged")
        self.assertEqual(pages.call_args[0][1], b"%PDF merged")
        self.assertEqual(self.api.calls["enqueue"], [])      # nothing was queued

    def test_analyze_raw_body(self):
        with patch("faxd.gs.compose", return_value=b"%PDF") as compose, \
                patch("faxd.gs.blank_pages", return_value=[]), \
                patch("faxd.gs.page_count", return_value=1):
            s, d = jreq(self.api, "POST", "/analyze", PDF,
                        {"Content-Type": "application/pdf"})
        self.assertEqual((s, d), (200, {"pages": 1, "blank_pages": []}))
        self.assertEqual([(n, bytes(b)) for n, b in compose.call_args[0][1]],
                         [("fax.pdf", PDF)])

    def test_analyze_merge_failure_is_400(self):
        from faxd import gs as gs_mod
        with patch.object(gs_mod, "compose", side_effect=gs_mod.GsError("broken")):
            s, d = self.post_multipart((b"a.pdf", PDF), (b"b.pdf", PDF), path="/analyze")
        self.assertEqual(s, 400)
        self.assertIn("could not be combined", d["error"])

    def test_analyze_requires_the_token(self):
        s, _, _ = req(self.api, "POST", "/analyze", PDF,
                      {"Content-Type": "application/pdf"}, token=False)
        self.assertEqual(s, 401)


class CancelCase(unittest.TestCase):
    """POST /jobs/{id}/cancel over HTTP against the real cancel_job logic."""

    def setUp(self):
        from faxd.db import Registry
        from faxd.enqueue import enqueue_pdf
        from faxd.events import EventBus
        from faxd.main import cancel_job
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        r = self.tmp.name
        self.cfg = SimpleNamespace(http_bind="127.0.0.1", http_port=0, token=TOKEN, station_id="T",
                                   outbox=os.path.join(r, "outbox"),
                                   sending=os.path.join(r, "sending"),
                                   outqueue=os.path.join(r, "outqueue"),
                                   queue_meta=os.path.join(r, "queue-meta"),
                                   allowed_numbers="nanp,000,ext")
        self.reg = Registry(os.path.join(r, "f.db"))
        self.addCleanup(self.reg.close)
        self.bus = EventBus()
        self.progress = SimpleNamespace(channel_for=lambda job_id: None)
        self.ami = SimpleNamespace(action=self.ami_action)
        self.ami_calls = []

        def cancel(job_id):
            return cancel_job(self.cfg, self.reg, self.bus, self.progress, self.ami, job_id)

        def enqueue(pdf, to, **kw):
            return enqueue_pdf(self.cfg, self.reg, self.bus, pdf, to, **kw)

        self.api = Api(self.cfg, self.reg, self.bus, FakeArm(), lambda: {"daemon": {}}, enqueue,
                       lambda **kw: PDF, cancel_fn=cancel, sse_keepalive=0.2)
        threading.Thread(target=self.api.serve_forever, daemon=True).start()
        self.addCleanup(self.api.shutdown)

    def ami_action(self, name, **fields):
        self.ami_calls.append((name, fields))
        return {"Response": "Success"}

    def enqueue_job(self, to="000"):
        from faxd.enqueue import enqueue_pdf
        return enqueue_pdf(self.cfg, self.reg, self.bus, PDF, to)

    def test_cancel_queued_removes_files_and_row(self):
        job = self.enqueue_job()
        self.assertTrue(os.path.exists(job["pdf_path"]))
        s, d = jreq(self.api, "POST", "/jobs/%s/cancel" % job["id"])
        self.assertEqual(s, 200)
        self.assertEqual(d["state"], "cancelled")
        self.assertFalse(os.path.exists(job["pdf_path"]))
        self.assertEqual(os.listdir(self.cfg.queue_meta), [])
        row = self.reg.get_job(job["id"])
        self.assertEqual(row["state"], "cancelled")
        self.assertIsNotNone(row["finished_at"])

    def test_cancel_unknown_is_404(self):
        self.assertEqual(jreq(self.api, "POST", "/jobs/lfnope/cancel")[0], 404)

    def test_cancel_terminal_is_409(self):
        job = self.enqueue_job()
        self.reg.set_state(job["id"], "sent", pages=1)
        s, d = jreq(self.api, "POST", "/jobs/%s/cancel" % job["id"])
        self.assertEqual(s, 409)
        self.assertIn("already finished", d["error"])

    def test_cancel_dialing_hangs_up_the_channel(self):
        job = self.enqueue_job()
        self.reg.set_state(job["id"], "dialing")
        self.progress.channel_for = lambda jid: "PJSIP/trunk-1"
        s, d = jreq(self.api, "POST", "/jobs/%s/cancel" % job["id"])
        self.assertEqual(s, 200)
        self.assertEqual(d["state"], "cancelled")
        self.assertEqual(self.ami_calls, [("Hangup", {"Channel": "PJSIP/trunk-1"})])
        self.assertTrue(os.path.exists(job["pdf_path"]))   # the file is the pipeline's now

    def test_cancel_dialing_without_a_channel_is_409(self):
        job = self.enqueue_job()
        self.reg.set_state(job["id"], "dialing")
        self.assertEqual(jreq(self.api, "POST", "/jobs/%s/cancel" % job["id"])[0], 409)

    def test_cancel_race_lost_to_the_pipeline(self):
        job = self.enqueue_job()
        self.reg.set_state(job["id"], "sending")
        # the row moves on between cancel_job's read and its guarded write
        real_set = self.reg.set_state_if

        def racing(job_id, expected, state, **kw):
            self.reg.set_state_if = real_set
            return real_set(job_id, None, "sent", pages=1)   # pipeline wins first

        self.reg.set_state_if = racing
        self.progress.channel_for = lambda jid: "PJSIP/x"
        s, d = jreq(self.api, "POST", "/jobs/%s/cancel" % job["id"])
        self.assertEqual(s, 409)
        self.assertEqual(self.reg.get_job(job["id"])["state"], "sent")

    def test_cancel_not_wired_is_501(self):
        api = make_api()
        self.addCleanup(api.shutdown)
        self.assertEqual(jreq(api, "POST", "/jobs/lfwhatever1/cancel")[0], 501)


if __name__ == "__main__":
    unittest.main()
