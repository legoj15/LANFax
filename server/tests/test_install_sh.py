import os
import shutil
import subprocess
import unittest

SERVER = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SH = os.path.join(SERVER, "install.sh")


def read(p):
    with open(p, encoding="utf-8", newline="") as f:
        return f.read()


class InstallShTests(unittest.TestCase):
    def test_static(self):
        s = read(SH)
        self.assertTrue(s.startswith("#!/bin/bash"))
        self.assertNotIn("\r", s)
        for t in ("set -euo pipefail", "useradd", "manager.d/faxd.conf", "FAXD_TOKEN", "FAXD_AMI_SECRET",
                  "visudo -c", "swapon", "systemctl daemon-reload", "healthz", "manager reload"):
            self.assertIn(t, s)

    def test_arm_script_checked_before_sudoers(self):
        s = read(SH)
        i = s.index("stat -c")
        self.assertLess(i, s.index("/etc/sudoers.d/faxd"))
        self.assertIn("-perm /022", s)

    def test_health_port_from_settings(self):
        s = read(SH)
        self.assertIn("get_env FAXD_HTTP_PORT", s)
        self.assertNotIn("127.0.0.1:8080/healthz", s)

    def test_unit(self):
        u = read(os.path.join(SERVER, "faxd.service"))
        for t in ("User=faxd", "SupplementaryGroups=asterisk fax", "MemoryMax=96M"):
            self.assertIn(t, u)

    def test_syntax(self):
        b = shutil.which("bash")
        if not b:
            self.skipTest("no bash")
        r = subprocess.run([b, "-n", SH.replace("\\", "/")], capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)


    def test_rollback_and_smoke_import(self):
        s = read(SH)
        self.assertIn("cp -a /opt/faxd/faxd /opt/faxd/faxd.prev", s)
        i_prev = s.index("faxd.prev")
        i_sync = s.index("rsync -a --delete")
        i_smoke = s.index('import faxd.main')
        i_restart = s.index("systemctl restart faxd")
        self.assertLess(i_prev, i_sync)
        self.assertLess(i_sync, i_smoke)
        self.assertLess(i_smoke, i_restart)
        self.assertIn("PYTHONPATH=/opt/faxd", s[i_smoke - 200:i_smoke])
        tail = s[i_restart:]
        self.assertIn("rollback", tail)
        self.assertIn("faxd.prev", tail)
        self.assertIn("exit 1", tail)

    def test_unit_start_limits(self):
        u = read(os.path.join(SERVER, "faxd.service"))
        unit = u.split("[Service]")[0]
        self.assertIn("StartLimitIntervalSec=120", unit)
        self.assertIn("StartLimitBurst=5", unit)

    def test_ami_conf_never_world_readable(self):
        s = read(SH)
        sec = s[s.index("# 4. AMI user"):s.index("# 5.")]
        self.assertIn("mktemp", sec)
        self.assertIn('install -m 0640 -o root -g asterisk', sec)
        self.assertNotIn('cat > "$AMI_CONF"', sec)   # a plain redirect is created world-readable


if __name__ == "__main__":
    unittest.main()
