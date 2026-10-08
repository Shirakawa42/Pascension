import unittest
from collections import Counter
from run import SEED,MATCHUPS,matchup,summary
class Tests(unittest.TestCase):
 def test_recent_rate_excludes_old_execution_and_future_journal_records(self):
  from throughput import recent_throughput
  s=dict(started_wall=0,updated_wall=600,planned_games=4800,completed_games=100)
  rows=[dict(finished_wall=10)]*50+[dict(finished_wall=550)]*40+[dict(finished_wall=601)]*10
  result=recent_throughput(s,rows,600,dict(activated_wall=400))
  self.assertEqual(result['recent_completed_games'],40)
  self.assertEqual(result['recent_games_per_second'],.2)
  self.assertEqual(result['recent_remaining_seconds'],23500)
  self.assertIsNone(recent_throughput(s,rows,600,dict(activated_wall=500))['recent_games_per_second'])
 def test_affinity_uses_distinct_physical_cores_before_siblings(self):
  import tempfile
  from pathlib import Path
  from cpu_affinity import select_cpus
  with tempfile.TemporaryDirectory() as folder:
   for cpu in range(16):
    p=Path(folder)/f'cpu{cpu}'/'topology';p.mkdir(parents=True)
    (p/'core_id').write_text(str(cpu//2));(p/'physical_package_id').write_text('0')
   self.assertEqual(select_cpus(set(range(16)),6,Path(folder)),[0,2,4,6,8,10])
   self.assertEqual(select_cpus({10,11,12,13,14,15},6,Path(folder)),[10,12,14,11,13,15])

 def test_only_build_local_il_hashes_can_differ(self):
  import copy
  from catalog_check import compatible_catalog
  a={'card_feature_descriptor':{'coverage':{'methodChecksums':{'method':'before'}}},'card_features':[[1,2]],'tables':{'known':3}}
  b=copy.deepcopy(a);b['card_feature_descriptor']['coverage']['methodChecksums']['method']='after'
  self.assertTrue(compatible_catalog(a,b)['semantic_catalog_identical'])
  for key,value in [('card_features',[[2,2]]),('tables',{'known':4})]:
   bad=copy.deepcopy(b);bad[key]=value
   with self.assertRaises(ValueError):compatible_catalog(a,bad)
  b['card_feature_descriptor']['coverage']['methodChecksums']['new']='new'
  with self.assertRaises(ValueError):compatible_catalog(a,b)

 def test_complete_plan_excludes_only_learner_rez_balances_every_seat(self):
  counts=Counter((matchup(SEED+i),seat) for i in range(2400) for seat in (0,1))
  self.assertEqual(len(counts),32);self.assertEqual(set(counts.values()),{150})
  self.assertEqual(len(MATCHUPS),16);self.assertTrue(all(a!='rez' and a!=b for a,b in MATCHUPS))
  self.assertEqual(sum(b=='rez' for a,b in MATCHUPS),4)
 def test_seat_relative_outcomes_and_censors_are_not_inverted(self):
  data=[dict(seed=SEED,learner_seat=0,winner=0,censored=False),dict(seed=SEED,learner_seat=1,winner=0,censored=False),dict(seed=SEED+16,learner_seat=0,winner=-1,censored=False),dict(seed=SEED+16,learner_seat=1,winner=None,censored=True)]
  total,rows=summary(data);row=rows[0]
  self.assertEqual((row['wins'],row['losses'],row['draws'],row['censored']),(1,1,1,1))
  self.assertEqual(row['resolved_game_score'],.5);self.assertFalse(total['evaluation_finished'])
  self.assertFalse(total['stronger_than_current']);self.assertEqual(row['by_learner_seat'][1]['losses'],1)
 def test_same_seed_seat_cannot_count_twice(self):
  r=dict(seed=SEED,learner_seat=0,winner=0,censored=False)
  with self.assertRaises(ValueError):summary([r,r])
 def test_completed_plan_requires_all_matchups_and_seats(self):
  data=[dict(seed=SEED+i,learner_seat=s,winner=s,censored=False) for i in range(2400) for s in (0,1)]
  total,rows=summary(data);self.assertTrue(total['evaluation_finished']);self.assertTrue(all(r['recorded_games']==300 and r['evaluation_finished'] for r in rows))
  self.assertEqual(total['wins'],4800)
if __name__=='__main__':unittest.main()
