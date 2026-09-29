import os
import re
import shutil
import subprocess
import tempfile
import unittest
from datetime import datetime

from faxd.testpage import make_test_pdf

SCRATCH = os.environ.get("FAXD_SCRATCH", tempfile.gettempdir())


class TestPageTests(unittest.TestCase):
    def setUp(self):
        self.pdf = make_test_pdf(to="+1 (954) 321-6309 (x\\y)", station="LANFax",
                                 when=datetime(2026, 9, 29, 12, 0), note="hello")

    def test_shape(self):
        p = self.pdf
        self.assertTrue(p.startswith(b"%PDF-1.4"))
        self.assertTrue(p.endswith(b"%%EOF\n"))
        self.assertIn(b"/Type /Page", p)
        self.assertIn(b"954", p)
        self.assertIn(b"2026-09-29 12:00", p)
        self.assertIn(b"\\(x\\\\y\\)", p)

    def test_xref(self):
        p = self.pdf
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

    def test_write_sample_and_gs(self):
        path = os.path.join(SCRATCH, "faxd-testpage.pdf")
        with open(path, "wb") as f:
            f.write(self.pdf)
        gs = shutil.which("gs")
        if not gs:
            self.skipTest("gs not installed")
        r = subprocess.run([gs, "-q", "-dNOPAUSE", "-dBATCH", "-sDEVICE=nullpage", path], capture_output=True)
        self.assertEqual(r.returncode, 0, r.stderr)


if __name__ == "__main__":
    unittest.main()
