"""Authorized extension invariants on disposable copies; never writes live ledger."""
import copy,json,sys,tempfile,unittest
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import extended_budget_v10 as ext
import campaign_state as persistence

class Clock:
    def __init__(self,now):self.now=now
    def time(self):return self.now
    def monotonic(self):return 1000.
    def boot_id(self):return 'extension-test-boot'

class Extension(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name);self.ledger=self.root/'budget.json'
        self.doc=json.loads((ext.ROOT/'budget.json').read_text())
        self.assertIsNone(self.doc['active'])
        self.receipt=ext.authorization()
        self.ledger.write_text(json.dumps(self.doc));(self.root/'receipt.json').write_text(json.dumps(self.receipt))
        for target,value in [('ROOT',self.root),('RECEIPT',self.root/'receipt.json')]:
            p=patch.object(ext,target,value);p.start();self.addCleanup(p.stop)
        p=patch.object(persistence,'MAX_CAMPAIGN_SECONDS',self.receipt['new_limit_seconds']);p.start();self.addCleanup(p.stop)
    def test_open_preserves_every_historical_byte(self):
        before=self.ledger.read_bytes()
        with ext.ExtendedBudget(self.ledger):pass
        self.assertEqual(before,self.ledger.read_bytes())
    def test_session_capped_at_absolute_deadline(self):
        clock=Clock(self.receipt['deadline_wall']-123)
        with ext.ExtendedBudget(self.ledger,clock=clock) as budget:
            budget.start_session('deadline-cap',9999)
            self.assertEqual(budget._data['active']['grant_seconds'],123)
            self.assertEqual(budget._data['active']['hard_deadline_wall'],self.receipt['deadline_wall'])
        after=json.loads(self.ledger.read_text())
        self.assertEqual(after['charged_seconds'],self.doc['charged_seconds'])
        self.assertEqual(after['sessions'][:-1],self.doc['sessions'])
    def test_expired_deadline_blocks_session(self):
        with ext.ExtendedBudget(self.ledger,clock=Clock(self.receipt['deadline_wall'])) as budget:
            with self.assertRaises(persistence.BudgetExceeded):budget.start_session('too-late',120)
    def test_other_campaign_rejected(self):
        self.doc['campaign_id']='different';self.ledger.write_text(json.dumps(self.doc))
        with self.assertRaisesRegex(RuntimeError,'Different campaign'):
            with ext.ExtendedBudget(self.ledger):pass
    def test_other_ledger_rejected(self):
        with self.assertRaisesRegex(RuntimeError,'authorized ledger'):ext.ExtendedBudget(self.root/'other.json')
    def test_ceiling_change_rejected(self):
        with self.assertRaisesRegex(RuntimeError,'Allocation mismatch'):ext.ExtendedBudget(self.ledger,86400)

if __name__=='__main__':unittest.main()
