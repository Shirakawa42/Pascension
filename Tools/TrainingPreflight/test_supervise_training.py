"""Actual child-process deadline/failure checks; no CUDA or learning."""
import json
from pathlib import Path
import sys
import tempfile
import time
import unittest

from supervise_training import supervise


class SupervisorTests(unittest.TestCase):
    def test_failed_child_is_not_retried(self):
        with tempfile.TemporaryDirectory() as folder:
            code = supervise([sys.executable, "-c", "raise SystemExit(7)"], folder, Path(folder)/"budget.json")
            self.assertEqual(code, 7)
            saved = json.loads((Path(folder)/"supervisor.json").read_text())
            self.assertFalse(saved["automatic_retry"])
            self.assertEqual(saved["state"], "failed")

    def test_deadline_kills_unresponsive_owned_child(self):
        with tempfile.TemporaryDirectory() as folder:
            ledger = Path(folder)/"budget.json"
            script = """import json,os,signal,sys,time
signal.signal(signal.SIGTERM,signal.SIG_IGN)
now=time.monotonic()
open(sys.argv[1],'w').write(json.dumps({'active':{'pid':os.getpid(),'hard_deadline_monotonic':now+1.5,'last_heartbeat_monotonic':now}}))
time.sleep(30)
"""
            started = time.monotonic()
            code = supervise([sys.executable, "-c", script, str(ledger)], folder, ledger)
            self.assertEqual(code, -9)
            self.assertLess(time.monotonic()-started, 4)
            saved = json.loads((Path(folder)/"supervisor.json").read_text())
            self.assertEqual(saved["stop_reason"], "hard_deadline")


if __name__ == "__main__":
    unittest.main()
