import argparse
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import MagicMock, patch

import run_campaign_with_audit as campaign


class CampaignAuditTests(unittest.TestCase):
    def exercise(self, codes, stop_after_child=None, variant=False):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            args = argparse.Namespace(run_dir=root / "main", ledger=root / "budget.json",
                seconds=43200, config=root / "config.json", resume=root / "pilot.soicp",
                label="main", audit_games=4096)
            if variant:
                args.variant_entry = root/"variant_entry.py"
            children = []
            handlers = {}
            for index, code in enumerate(codes):
                child = MagicMock(pid=999999)
                child.wait.return_value = code
                child.poll.return_value = code
                if index == stop_after_child:
                    def stop_at_boundary(*args, code=code, **kwargs):
                        handlers[campaign.signal.SIGTERM](campaign.signal.SIGTERM, None)
                        return code
                    child.wait.side_effect = stop_at_boundary
                children.append(child)
            with patch.object(campaign.subprocess, "Popen", side_effect=children) as popen, \
                 patch.object(campaign.signal, "signal", side_effect=lambda number, handler: handlers.update({number:handler})), \
                 patch.object(campaign.os, "killpg"):
                result = campaign.run(args)
            status = json.loads((args.run_dir / "post-evaluation.json").read_text())
            return result, status, [call.args[0] for call in popen.call_args_list]

    def test_failed_training_never_evaluates_or_retries(self):
        code, status, commands = self.exercise([2])
        self.assertEqual(code, 2)
        self.assertEqual(status["state"], "skipped_training_failed")
        self.assertEqual(len(commands), 1)

    def test_success_runs_frozen_audits_without_budget_argument(self):
        code, status, commands = self.exercise([0, 0, 0])
        self.assertEqual(code, 0)
        self.assertEqual(status["state"], "complete")
        self.assertEqual(len(status["completed_reports"]), 2)
        for command in commands[1:]:
            self.assertNotIn("--ledger", command)
            self.assertIn("--telemetry", command)
            self.assertGreaterEqual(int(command[command.index("--seed")+1]), 0x4000000000000000)

    def test_evaluation_failure_retains_completed_report(self):
        code, status, commands = self.exercise([0, 0, 7])
        self.assertEqual(code, 7)
        self.assertEqual(status["state"], "failed")
        self.assertEqual(len(status["completed_reports"]), 1)
        self.assertFalse(status["automatic_retry"])

    def test_stop_between_audits_prevents_next_gpu_job(self):
        code, status, commands = self.exercise([0, 0], stop_after_child=1)
        self.assertEqual(code, 0)
        self.assertEqual(status['state'], 'stopped')
        self.assertEqual(len(commands), 2)
        self.assertEqual(len(status['completed_reports']), 1)

    def test_variant_routes_training_and_both_audits_to_same_entry(self):
        code, status, commands = self.exercise([0, 0, 0], variant=True)
        self.assertEqual(code, 0)
        self.assertEqual(commands[0][2], "supervise")
        self.assertIn("--ledger", commands[0])
        for command in commands[1:]:
            self.assertEqual(command[1], commands[0][1])
            self.assertEqual(command[2], "evaluate")
            self.assertNotIn("--ledger", command)


if __name__ == "__main__":
    unittest.main()
