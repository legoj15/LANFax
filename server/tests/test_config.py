"""Tests for faxd.config."""
import os
import tempfile
import unittest

from faxd import config


class ConfigTests(unittest.TestCase):
    def _write(self, d: str, text: str) -> str:
        p = os.path.join(d, "settings.env")
        with open(p, "w", newline="\n") as f:
            f.write(text)
        return p

    def test_missing_file_gives_defaults(self):
        with tempfile.TemporaryDirectory() as d:
            cfg = config.load(os.path.join(d, "nope.env"), environ={})
        self.assertEqual(cfg.share_root, "/srv/fax")
        self.assertEqual(cfg.http_port, 8080)
        self.assertEqual(cfg.poll_interval, 2.0)
        self.assertEqual(cfg.token, "")
        self.assertEqual(cfg.faxin_ext, "300")

    def test_file_values_quotes_comments(self):
        text = (
            "# comment\n\n"
            "FAXD_SHARE_ROOT=/tmp/share\n"
            'FAXD_TOKEN="abc def"\n'
            "FAXD_STATION_ID='My Fax'\n"
            "export FAXD_AMI_USER=bob\n"
            "  FAXD_HTTP_PORT = 9090  \n"
            "FAXD_POLL_INTERVAL=0.5\n"
            "garbage line\n"
            "FAXIN_EXT=301\n"
        )
        with tempfile.TemporaryDirectory() as d:
            cfg = config.load(self._write(d, text), environ={})
        self.assertEqual(cfg.share_root, "/tmp/share")
        self.assertEqual(cfg.token, "abc def")
        self.assertEqual(cfg.station_id, "My Fax")
        self.assertEqual(cfg.ami_user, "bob")
        self.assertEqual(cfg.http_port, 9090)
        self.assertIsInstance(cfg.http_port, int)
        self.assertEqual(cfg.poll_interval, 0.5)
        self.assertIsInstance(cfg.poll_interval, float)
        self.assertEqual(cfg.faxin_ext, "301")

    def test_environ_overlay_wins(self):
        with tempfile.TemporaryDirectory() as d:
            p = self._write(d, "FAXD_TOKEN=fromfile\nFAXD_HTTP_PORT=1111\n")
            cfg = config.load(p, environ={"FAXD_TOKEN": "fromenv", "FAXD_HTTP_PORT": "2222"})
        self.assertEqual(cfg.token, "fromenv")
        self.assertEqual(cfg.http_port, 2222)

    def test_bad_number_falls_back_to_default(self):
        with tempfile.TemporaryDirectory() as d:
            cfg = config.load(self._write(d, "FAXD_HTTP_PORT=abc\n"), environ={})
        self.assertEqual(cfg.http_port, 8080)

    def test_path_properties(self):
        cfg = config.Config(share_root=os.path.join("x", "fax"))
        j = os.path.join
        self.assertEqual(cfg.outbox, j("x", "fax", "outbox"))
        self.assertEqual(cfg.sending, j("x", "fax", "sending"))
        self.assertEqual(cfg.sent, j("x", "fax", "sent"))
        self.assertEqual(cfg.failed, j("x", "fax", "failed"))
        self.assertEqual(cfg.inbox, j("x", "fax", "inbox"))
        self.assertEqual(cfg.queue_meta, j("x", "fax", "queue-meta"))
        self.assertEqual(cfg.arm_status_file, j("x", "fax", "ARM-STATUS.txt"))


if __name__ == "__main__":
    unittest.main()
