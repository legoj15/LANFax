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


class MultipartHardeningCase(unittest.TestCase):
    """The parser's abuse answers: tiny boundaries, too many parts, bodies that lie."""

    BOUNDARY = b"lanfax-hardening-bnd"

    def setUp(self):
        self.api = make_api()
        self.addCleanup(self.api.shutdown)

    def post(self, body, boundary=BOUNDARY, ctype_extra=""):
        h = {"Content-Type": f"multipart/form-data; boundary={boundary.decode()}{ctype_extra}",
             "X-Fax-To": "5551234567"}
        return jreq(self.api, "POST", "/jobs", body, h)

    def test_short_boundary_is_400(self):
        self.assertEqual(self.post(multipart_body(b"x12", (b"a.pdf", PDF)), boundary=b"x12")[0], 400)

    def test_boundary_with_surrounding_space_is_used_trimmed(self):
        body = multipart_body(self.BOUNDARY, (b"a.pdf", PDF))
        s, _ = self.post(body, ctype_extra=" ")   # "boundary=<b> " -- trailing space in the header value
        self.assertEqual(s, 201)

    def test_too_many_parts_is_400(self):
        parts = [(b"a.pdf", PDF)] * 51
        self.assertEqual(self.post(multipart_body(self.BOUNDARY, *parts))[0], 400)
        self.assertEqual(self.api.calls["enqueue"], [])

    def test_missing_closing_boundary_is_400(self):
        body = multipart_body(self.BOUNDARY, (b"a.pdf", PDF))[:-len(b"--" + self.BOUNDARY + b"--\r\n")]
        self.assertEqual(self.post(body)[0], 400)

    def test_part_without_headers_is_400(self):
        body = (b"--" + self.BOUNDARY + b"\r\njust data no header break\r\n"
                b"--" + self.BOUNDARY + b"--\r\n")
        self.assertEqual(self.post(body)[0], 400)

    def test_a_bare_delimiter_inside_data_does_not_split_the_part(self):
        # no CRLF before the "--boundary" occurrence inside the document bytes: RFC 2046
        payload = b"%PDF-with--" + self.BOUNDARY + b"-inside"
        s, _ = self.post(multipart_body(self.BOUNDARY, (b"a.pdf", payload)))
        self.assertEqual(s, 201)
        parts = self.api.calls["enqueue"][0]["pdf"]
        self.assertEqual([(n, bytes(b)) for n, b in parts], [("a.pdf", payload)])


class CancelShareFileCase(unittest.TestCase):
    """cancel_job's file handling: faxd's staging copies go, the user's share file survives."""

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
                                   failed=os.path.join(r, "failed"),
                                   queue_meta=os.path.join(r, "queue-meta"),
                                   allowed_numbers="nanp,000,ext")
        os.makedirs(self.cfg.outqueue)
        os.makedirs(self.cfg.failed)
        os.makedirs(self.cfg.outbox)
        os.makedirs(self.cfg.sending)
        self.reg = Registry(os.path.join(r, "f.db"))
        self.addCleanup(self.reg.close)
        self.bus = EventBus()
        self.progress = SimpleNamespace(channel_for=lambda job_id: None)
        self.ami = SimpleNamespace(action=lambda name, **f: {"Response": "Success"})
        self.cancel = lambda job_id: cancel_job(self.cfg, self.reg, self.bus, self.progress, self.ami, job_id)
        _ = (enqueue_pdf,)  # imported for the share-row helper below

    def share_row(self, name="5551234567_cover.pdf"):
        """A queued row for a file a user dropped on the share (origin share)."""
        path = os.path.join(self.cfg.outbox, name)
        with open(path, "wb") as f:
            f.write(PDF)
        row = self.reg.upsert_job({"id": "shshare000001", "direction": "out", "state": "queued",
                                   "filename": name, "to_number": "5551234567", "origin": "share",
                                   "pdf_path": path})
        return self.reg.set_state(row["id"], "queued") or row

    def test_share_file_is_moved_to_failed_not_deleted(self):
        row = self.share_row()
        out = self.cancel(row["id"])
        self.assertEqual(out["state"], "cancelled")
        self.assertFalse(os.path.exists(os.path.join(self.cfg.outbox, row["filename"])))
        self.assertTrue(os.path.exists(os.path.join(self.cfg.failed, row["filename"])))
        self.assertEqual(os.listdir(self.cfg.outbox), [])

    def test_api_staging_copy_is_deleted(self):
        row = self.share_row("5551234567_x-lf000000000001.pdf")
        self.reg.upsert_job({"id": row["id"], "origin": "api"})
        out = self.cancel(row["id"])
        self.assertEqual(out["state"], "cancelled")
        self.assertFalse(os.path.exists(os.path.join(self.cfg.outbox, row["filename"])))
        self.assertEqual(os.listdir(self.cfg.failed), [])

    def test_a_file_that_cannot_be_removed_reverts_the_row_and_refuses(self):
        from unittest.mock import patch
        import faxd.main as main_mod
        from faxd.api import CancelRefused
        row = self.share_row()
        name = row["filename"]
        # neither a move nor an unlink can take the file (a watcher holds it, say): the "file"
        # survives the cancel, so the row must go back to queued and the cancel must refuse
        real = main_mod._unlink
        stubborn = lambda path: False if os.path.basename(path) == name else real(path)
        with patch.object(main_mod, "_unlink", stubborn), patch.object(main_mod, "_move", lambda s, d: False):
            with self.assertRaises(CancelRefused):
                self.cancel(row["id"])
        self.assertEqual(self.reg.get_job(row["id"])["state"], "queued")   # honest again

    def test_pending_part_file_and_unobserved_env_are_removed(self):
        from faxd.enqueue import enqueue_pdf
        row = self.share_row("5551234567_env-lf000000000002.pdf")
        self.reg.upsert_job({"id": row["id"], "origin": "api"})
        name = row["filename"]
        # the enqueue's rename is still pending, and the pipeline wrote the env after the observer's scan
        os.unlink(os.path.join(self.cfg.outbox, name))
        with open(os.path.join(self.cfg.outbox, "." + name + ".part"), "wb") as f:
            f.write(PDF)
        env = os.path.join(self.cfg.outqueue, "j20261014-160000-x.env")
        with open(env, "w", encoding="utf-8") as f:
            f.write(f"TO='5551234567'\nSRCFILE='/srv/fax/outbox/{name}'\n")
        other = os.path.join(self.cfg.outqueue, "j20261014-160001-y.env")
        with open(other, "w", encoding="utf-8") as f:
            f.write("TO='5550000000'\nSRCFILE='/srv/fax/outbox/other.pdf'\n")
        out = self.cancel(row["id"])
        self.assertEqual(out["state"], "cancelled")
        self.assertFalse(os.path.exists(os.path.join(self.cfg.outbox, "." + name + ".part")))
        self.assertFalse(os.path.exists(env))
        self.assertTrue(os.path.exists(other))
