"""The confirmation sheet (GET /jobs/<id>/confirmation.pdf, built by faxd.confirmation)
and the compose preview (POST /preview)."""
import base64
import os
import re
import shutil
import subprocess
import tempfile
import threading
import unittest
from datetime import datetime, timezone
from types import SimpleNamespace
from unittest.mock import patch

from faxd.api import Api
from tests.fakes_api import FakeArm, FakeBus, FakeRegistry
from tests.test_api import PDF, TOKEN, req, jreq


def tiny_jpeg(w=2, h=3):
    """A synthetic JPEG whose SOF0 says (w, h): all the sheet's parser needs."""
    return (b"\xff\xd8\xff\xc0\x00\x0b\x08" + h.to_bytes(2, "big") + w.to_bytes(2, "big")
            + b"\x01\x01\x01" + b"\xff\xd9")


def make_api(reg=None, confirmations_dir=None, fax_header=False, **kw):
    """make_api with the fields the confirmation/preview routes read from cfg."""
    cfg = SimpleNamespace(http_bind="127.0.0.1", http_port=0, token=TOKEN, station_id="TESTSTN",
                          gs_bin="gs", confirmations_dir=confirmations_dir, fax_header=fax_header)
    reg = reg or FakeRegistry()
    bus, arm = FakeBus(), FakeArm()
    api = Api(cfg, reg, bus, arm, lambda: {"daemon": {"version": "t"}},
              enqueue_fn=lambda *a, **k: (_ for _ in ()).throw(AssertionError("not expected here")),
              testpage_fn=lambda **k: PDF, **kw)
    t = threading.Thread(target=api.serve_forever, daemon=True)
    t.start()
    api.reg = reg
    return api


JOBS = {
    "lf1": {"id": "lf1", "direction": "out", "state": "sent", "to_number": "15551234567",
            "subject": "march invoice", "filename": "15551234567_m-lf1.pdf", "pages": 3,
            "created_at": "2026-10-14T16:44:00+00:00", "finished_at": "2026-10-14T16:45:25+00:00",
            "detail": "sent 3 page(s) at 14400", "error": None, "pdf_path": None},
    "lf2": {"id": "lf2", "direction": "out", "state": "failed", "to_number": "15551234567",
            "error": "no answer", "created_at": "2026-10-14T16:44:00+00:00",
            "finished_at": "2026-10-14T16:45:25+00:00", "pdf_path": None},
    "lf3": {"id": "lf3", "direction": "out", "state": "cancelled", "to_number": "15551234567",
            "error": "cancelled before sending", "created_at": "2026-10-14T16:44:00+00:00",
            "finished_at": "2026-10-14T16:44:10+00:00", "pdf_path": None},
    "lfq": {"id": "lfq", "direction": "out", "state": "queued", "to_number": "15551234567",
            "created_at": "2026-10-14T16:44:00+00:00", "finished_at": None, "pdf_path": None},
    "in1": {"id": "in1", "direction": "in", "state": "received", "from_number": "15550001111",
            "created_at": "2026-10-14T16:44:00+00:00", "finished_at": "2026-10-14T16:45:00+00:00",
            "pdf_path": None},
}
EVENTS = {"lf1": [
    {"id": 1, "job_id": "lf1", "ts": "2026-10-14T16:44:00+00:00", "state": "queued", "detail": "enqueued via api"},
    {"id": 2, "job_id": "lf1", "ts": "2026-10-14T16:45:25+00:00", "state": "sent", "detail": None},
]}


class ConfirmationRouteCase(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.conf_dir = os.path.join(self.tmp.name, "confirmations")
        self.reg = FakeRegistry(dict(JOBS), dict(EVENTS))
        self.built = []

        def fake_thumbnail(gs_bin, pdf, **kw):
            self.built.append(pdf)
            return tiny_jpeg()

        self._thumb_patch = patch("faxd.gs.page_jpeg", side_effect=fake_thumbnail)
        self._thumb_patch.start()
        self.addCleanup(self._thumb_patch.stop)
        self.api = make_api(self.reg, confirmations_dir=self.conf_dir)
        self.addCleanup(self.api.shutdown)

    def get(self, job_id):
        return req(self.api, "GET", "/jobs/%s/confirmation.pdf" % job_id)

    def test_sent_job_gets_a_sheet(self):
        s, r, b = self.get("lf1")
        self.assertEqual(s, 200)
        self.assertEqual(r.getheader("Content-Type"), "application/pdf")
        self.assertTrue(b.startswith(b"%PDF"))
        self.assertIn(b"FAX CONFIRMATION", b)
        self.assertIn(b"OK", b)
        self.assertIn(b"15551234567", b)
        self.assertIn(b"TESTSTN", b)
        self.assertIn(b"march invoice", b)

    def test_sheet_is_cached_on_disk(self):
        self.get("lf1")
        cached = os.path.join(self.conf_dir, "lf1.pdf")
        self.assertTrue(os.path.exists(cached))
        with open(cached, "rb") as f:
            first = f.read()
        self.get("lf1")
        with open(cached, "rb") as f:
            self.assertEqual(f.read(), first)

    def test_thumbnail_comes_from_the_sent_pdf(self):
        self.reg.jobs["lf1"]["pdf_path"] = os.path.join(self.tmp.name, "sent.pdf")
        with open(self.reg.jobs["lf1"]["pdf_path"], "wb") as f:
            f.write(PDF)
        s, _, b = self.get("lf1")
        self.assertEqual(s, 200)
        self.assertEqual(self.built, [PDF])
        self.assertIn(b"/DCTDecode", b)

    def test_no_pdf_no_thumbnail_still_a_sheet(self):
        s, _, b = self.get("lf1")
        self.assertEqual(s, 200)
        self.assertEqual(self.built, [])
        self.assertNotIn(b"/DCTDecode", b)

    def test_failed_and_cancelled_get_sheets(self):
        for job_id, word in (("lf2", b"FAILED"), ("lf3", b"CANCELLED")):
            s, _, b = self.get(job_id)
            self.assertEqual(s, 200, job_id)
            self.assertIn(word, b)

    def test_queued_inbound_and_unknown_are_404(self):
        for job_id in ("lfq", "in1", "nope"):
            s, _, _ = self.get(job_id)
            self.assertEqual(s, 404, job_id)

    def test_auth_required(self):
        s, _, _ = req(self.api, "GET", "/jobs/lf1/confirmation.pdf", token=False)
        self.assertEqual(s, 401)

    def test_broken_generation_is_500(self):
        with patch("faxd.confirmation.build_confirmation", side_effect=RuntimeError("boom")):
            s, _, _ = self.get("lf1")
        self.assertEqual(s, 500)

    def test_without_a_cache_dir_it_still_serves(self):
        api = make_api(self.reg, confirmations_dir=None)
        self.addCleanup(api.shutdown)
        s, _, b = req(api, "GET", "/jobs/lf1/confirmation.pdf")
        self.assertEqual(s, 200)
        self.assertTrue(b.startswith(b"%PDF"))

    def test_an_unwritable_cache_dir_degrades_to_per_request_sheets(self):
        blocked = os.path.join(self.tmp.name, "blocked")
        with open(blocked, "w") as f:
            f.write("a file where the cache dir should be")
        api = make_api(self.reg, confirmations_dir=blocked)
        self.addCleanup(api.shutdown)
        s, _, b = req(api, "GET", "/jobs/lf1/confirmation.pdf")
        self.assertEqual(s, 200)
        self.assertTrue(b.startswith(b"%PDF"))

    def test_a_row_that_moved_after_the_sheet_rebuilds_it(self):
        self.get("lf1")
        cached = os.path.join(self.conf_dir, "lf1.pdf")
        with open(cached, "rb") as f:
            first = f.read()
        # the cancel race: the row moves on after its sheet was written, so the sheet is stale
        import time as _time
        from datetime import datetime, timezone as _tz
        old = _time.time() - 60
        os.utime(cached, (old, old))
        self.reg.jobs["lf1"]["updated_at"] = datetime.now(_tz.utc).isoformat()
        self.reg.jobs["lf1"]["pages"] = 9
        s, _, b = self.get("lf1")
        self.assertEqual(s, 200)
        with open(cached, "rb") as f:
            self.assertNotEqual(f.read(), first)
        self.assertIn(b"(9)", b)


class PreviewCase(unittest.TestCase):
    BOUNDARY = b"preview-boundary-0001"

    def setUp(self):
        self.api = make_api()
        self.addCleanup(self.api.shutdown)

    def post(self, *parts, to=None, token=True):
        body = b""
        for name, data in parts:
            body += (b"--" + self.BOUNDARY + b"\r\n"
                     b'Content-Disposition: form-data; name="file"; filename="' + name + b'"\r\n'
                     b"Content-Type: application/pdf\r\n\r\n" + data + b"\r\n")
        body += b"--" + self.BOUNDARY + b"--\r\n"
        h = {"Content-Type": "multipart/form-data; boundary=" + self.BOUNDARY.decode()}
        if to is not None:
            h["X-Fax-To"] = to
        return jreq(self.api, "POST", "/preview", body, h, token=token)

    def test_renders_the_merged_documents(self):
        with patch("faxd.gs.compose", return_value=b"%PDF merged") as compose, \
                patch("faxd.gs.page_count", return_value=5), \
                patch("faxd.gs.preview_pages", return_value=[b"png1", b"png2"]) as pp:
            s, d = self.post((b"a.pdf", PDF), (b"b.pdf", PDF), to="5551234567")
        self.assertEqual(s, 200)
        self.assertEqual(d["pages"], 5)
        self.assertEqual(d["shown"], 2)
        self.assertEqual([base64.b64decode(x) for x in d["rendered"]], [b"png1", b"png2"])
        self.assertFalse(d["stamped"])
        self.assertEqual(len(compose.call_args[0][1]), 2)
        self.assertIsNone(compose.call_args[1]["header"])
        self.assertEqual(pp.call_args[0][1], b"%PDF merged")

    def test_stamped_when_the_server_stamps(self):
        api = make_api(fax_header=True)
        self.addCleanup(api.shutdown)
        with patch("faxd.gs.compose", return_value=b"%PDF x") as compose, \
                patch("faxd.gs.page_count", return_value=1), \
                patch("faxd.gs.preview_pages", return_value=[b"png"]):
            s, d = jreq(api, "POST", "/preview", PDF,
                        {"Content-Type": "application/pdf", "X-Fax-To": "(555) 867-5309"})
        self.assertEqual(s, 200)
        self.assertTrue(d["stamped"])
        header = compose.call_args[1]["header"]
        self.assertIn("TO 15558675309", header)
        self.assertIn("FROM TESTSTN", header)
        self.assertTrue(compose.call_args[1]["footer"])

    def test_stamping_needs_the_number(self):
        api = make_api(fax_header=True)
        self.addCleanup(api.shutdown)
        s, d = jreq(api, "POST", "/preview", PDF, {"Content-Type": "application/pdf"})
        self.assertEqual(s, 400)

    def test_bad_number_is_400_when_stamping(self):
        api = make_api(fax_header=True)
        self.addCleanup(api.shutdown)
        s, d = jreq(api, "POST", "/preview", PDF,
                    {"Content-Type": "application/pdf", "X-Fax-To": "911"})
        self.assertEqual(s, 400)

    def test_merge_failure_is_400(self):
        from faxd import gs as gs_mod
        with patch.object(gs_mod, "compose", side_effect=gs_mod.GsError("broken")):
            s, d = self.post((b"a.pdf", PDF), to="5551234567")
        self.assertEqual(s, 400)
        self.assertIn("could not be combined", d["error"])

    def test_render_failure_is_an_empty_preview(self):
        from faxd import gs as gs_mod
        with patch("faxd.gs.compose", return_value=b"%PDF m"), \
                patch("faxd.gs.page_count", return_value=2), \
                patch.object(gs_mod, "preview_pages", side_effect=gs_mod.GsError("no render")):
            s, d = self.post((b"a.pdf", PDF), to="5551234567")
        self.assertEqual(s, 200)
        self.assertEqual((d["shown"], d["rendered"]), (0, []))
        self.assertEqual(d["pages"], 2)

    def test_a_failed_page_count_still_answers(self):
        from faxd import gs as gs_mod
        with patch("faxd.gs.compose", return_value=b"%PDF m"), \
                patch.object(gs_mod, "page_count", side_effect=gs_mod.GsError("no count")), \
                patch("faxd.gs.preview_pages", return_value=[b"png"]):
            s, d = self.post((b"a.pdf", PDF), to="5551234567")
        self.assertEqual(s, 200)
        self.assertIsNone(d["pages"])
        self.assertEqual(d["shown"], 1)

    def test_non_pdf_part_is_400(self):
        s, _ = self.post((b"a.pdf", PDF), (b"b.txt", b"hi"), to="5551234567")
        self.assertEqual(s, 400)

    def test_one_preview_at_a_time_and_the_fax_slots_stay_free(self):
        # a preview in flight: a second is 503, and POST /analyze keeps its own slots
        self.api.previews.acquire()
        try:
            s, d = self.post((b"a.pdf", PDF), to="5551234567")
            self.assertEqual(s, 503)
            self.assertIn("previews", d["error"])
            # /analyze uses the uploads semaphore: the request goes through (gs is faked
            # here) instead of being refused for a busy preview
            with patch("faxd.gs.compose", return_value=b"%PDF x"), \
                    patch("faxd.gs.blank_pages", return_value=[]), \
                    patch("faxd.gs.page_count", return_value=1):
                s, d = jreq(self.api, "POST", "/analyze", PDF, {"Content-Type": "application/pdf"})
            self.assertEqual(s, 200)
        finally:
            self.api.previews.release()
        with patch("faxd.gs.compose", return_value=b"%PDF x"), \
                patch("faxd.gs.page_count", return_value=1), \
                patch("faxd.gs.preview_pages", return_value=[b"png"]):
            s, d = self.post((b"a.pdf", PDF), to="5551234567")
        self.assertEqual(s, 200)
        self.assertEqual(d["shown"], 1)

    def test_auth_required(self):
        s, _, _ = req(self.api, "POST", "/preview", PDF,
                      {"Content-Type": "application/pdf"}, token=False)
        self.assertEqual(s, 401)


class BuildConfirmationCase(unittest.TestCase):
    """faxd.confirmation.build_confirmation: the sheet's content, by unit."""

    SENT = dict(JOBS["lf1"])
    NOW = datetime(2026, 10, 14, 16, 50, tzinfo=timezone.utc)

    def build(self, job=None, events=None, thumbnail=None):
        from faxd.confirmation import build_confirmation
        return build_confirmation(job or dict(self.SENT), events if events is not None else list(EVENTS["lf1"]),
                                  station_id="OFFICE", version="0.4.0", now=self.NOW, thumbnail=thumbnail)

    def test_shape_and_fields(self):
        p = self.build()
        self.assertTrue(p.startswith(b"%PDF-1.4"))
        self.assertTrue(p.endswith(b"%%EOF\n"))
        for needle in (b"FAX CONFIRMATION", b"OK", b"15551234567", b"OFFICE",
                       b"march invoice", b"1 min 25 s", b"lf1",
                       b"sent 3 page\\(s\\) at 14400", b"Transmission log"):
            self.assertIn(needle, p, needle)

    def test_result_words(self):
        self.assertIn(b"FAILED", self.build(dict(JOBS["lf2"])))
        self.assertIn(b"CANCELLED", self.build(dict(JOBS["lf3"])))

    def test_error_line_and_escaping(self):
        p = self.build(dict(JOBS["lf2"], error="busy (line 1) \\ 2"))
        self.assertIn(b"busy \\(line 1\\) \\\\ 2", p)

    def test_without_events_no_log_section(self):
        self.assertNotIn(b"Transmission log", self.build(events=[]))

    def test_without_thumbnail_no_image(self):
        self.assertNotIn(b"/DCTDecode", self.build())
        self.assertIn(b"/DCTDecode", self.build(thumbnail=tiny_jpeg()))

    def test_the_image_is_in_the_page_resources(self):
        # a renderer skips /Do for a name outside /Resources: the sheet draws, the thumbnail does not
        self.assertIn(b"/Resources << /Font << /F1 5 0 R /F2 6 0 R >> /XObject << /Im7 7 0 R >> >>",
                      self.build(thumbnail=tiny_jpeg()))

    def test_non_ascii_becomes_question_marks(self):
        # é is latin-1 and survives; a character outside latin-1 becomes ?
        self.assertIn(b"caf\xe9 ? A4", self.build(dict(self.SENT, subject="café ✶ A4")))

    def test_jpeg_size_reads_the_sof_marker(self):
        from faxd.confirmation import _jpeg_size
        # SOF0 after a full APP0 segment: precision 8, height 2200, width 1700 (a ~50 dpi Letter page)
        jpeg = b"\xff\xd8\xff\xe0\x00\x10JFIF\x00" + b"\x00" * 9 + \
            b"\xff\xc0\x00\x11\x08" + (2200).to_bytes(2, "big") + (1700).to_bytes(2, "big") + b"\x01" + b"\xff\xd9"
        self.assertEqual(_jpeg_size(jpeg), (1700, 2200))
        self.assertIsNone(_jpeg_size(b"\xff\xd8\xff\xd9"))

    def test_xref_offsets(self):
        p = self.build()
        m = re.search(rb"startxref\n(\d+)\n%%EOF\n$", p)
        off = int(m.group(1))
        self.assertTrue(p[off:].startswith(b"xref\n"))
        lines = p[off:].split(b"\n")
        first, count = map(int, lines[1].split())
        self.assertEqual(first, 0)
        entries = lines[2:2 + count]
        self.assertTrue(entries[0].startswith(b"0000000000 65535 f"))
        for n, e in enumerate(entries[1:], start=1):
            o = int(e[:10])
            self.assertTrue(p[o:].startswith(b"%d 0 obj" % n), (n, o))

    @unittest.skipUnless(shutil.which("gs"), "gs not on PATH")
    def test_gs_accepts_the_sheet(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "c.pdf")
            with open(path, "wb") as f:
                f.write(self.build())
            p = subprocess.run(["gs", "-dNOPAUSE", "-dBATCH", "-dSAFER", "-sDEVICE=nullpage", path],
                               capture_output=True, timeout=60)
        self.assertEqual(p.returncode, 0, p.stderr.decode("utf-8", "replace")[-400:])


class FormatDurationCase(unittest.TestCase):
    def test_cases(self):
        from faxd.confirmation import format_duration
        self.assertEqual(format_duration(0), "0 s")
        self.assertEqual(format_duration(45), "45 s")
        self.assertEqual(format_duration(205), "3 min 25 s")
        self.assertEqual(format_duration(4980), "1 h 23 min")


if __name__ == "__main__":
    unittest.main()
