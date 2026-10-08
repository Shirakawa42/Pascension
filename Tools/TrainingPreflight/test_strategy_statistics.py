import json
from pathlib import Path
import tempfile
import unittest
from strategy_statistics import build, patterns

class StrategyStatisticsTests(unittest.TestCase):
    def setUp(self):
        self.metadata={'cards':[{'id':'card','name':'Card','type':'Ally','faction':'Order'},
                                {'id':'starter','name':'Starter','type':'Starter','faction':'None'}],
                       'heroes':[{'id':'tetra','name':'Tetra'},{'id':'rez','name':'Rez'}]}
        self.player=dict(hero='tetra',seat=0,relic=None,destiny=None,mastery=30,health=20,starter_banishes=5,
                         fastplays=5,champions=4,champion_activations=8,monsters=3,rerolls=5,healed=20,
                         peak_power=30,mastery10_round=4,mastery20_round=8,mastery30_round=10,
                         extra_destinies=[],collection={'card':5,'starter':1})
        self.game=dict(seed='0001',winner=0,round=12,players=[self.player,{**self.player,'hero':'rez','seat':1}])
        self.snapshot={'totals':dict(resolved_games=1,seat0_wins=1,seat1_wins=0,draws=0,mean_rounds=12)}
    def run_build(self,games,snapshot=None):
        with tempfile.TemporaryDirectory() as directory:
            path=Path(directory)/'games.jsonl';path.write_text(''.join(json.dumps(g)+'\n' for g in games))
            return build(path,snapshot or self.snapshot,self.metadata)
    def test_both_seats_contribute_outcomes_but_one_win_per_pattern(self):
        r=self.run_build([self.game]);row=next(r for r in r['rows'] if r['id']=='pattern:champion_engine')
        self.assertEqual((row['games'],row['wins'],row['losses'],row['score'],row['win_share']),(2,1,1,.5,1))
        self.assertEqual((row['seat0_wins'],row['seat1_wins']),(1,0))
        setups=[r for r in r['rows'] if r['category']=='builds']
        self.assertEqual(sum(r['games'] for r in setups),2)
        self.assertEqual(sum(r['wins'] for r in setups),1)
        self.assertEqual(row['supporting_cards'][0]['winning_presence'],1)
        self.assertNotIn('starter',[r['id'] for r in row['supporting_cards']])
    def test_draws_are_not_wins(self):
        game={**self.game,'winner':-1};snapshot={'totals':{**self.snapshot['totals'],'seat0_wins':0,'draws':1}}
        rows=self.run_build([game],snapshot)['rows']
        self.assertTrue(all(r['wins']==0 and r['score']==.5 and r['mean_winning_rounds'] is None for r in rows))
    def test_duplicate_or_wrong_evaluation_rejected(self):
        with self.assertRaisesRegex(ValueError,'Duplicate'):self.run_build([self.game,self.game])
        with self.assertRaisesRegex(ValueError,'do not match'):self.run_build([{**self.game,'winner':1}])
    def test_thresholds_not_invented_for_missing_progress(self):
        p={**self.player,'mastery30_round':0,'mastery20_round':9,'champion_activations':7,'starter_banishes':4,'collection':{'card':4}}
        keys={x[0] for x in patterns(p,{x['id']:x for x in self.metadata['cards']})}
        self.assertFalse(keys & {'mastery30','mastery_ramp','champion_engine','starter_thinning','faction_Order'})
    def test_winning_cards_are_not_counted_from_losers(self):
        game={**self.game,'players':[{**self.player,'collection':{}},self.game['players'][1]]}
        row=next(r for r in self.run_build([game])['rows'] if r['id']=='pattern:champion_engine')
        self.assertEqual(row['supporting_cards'][0]['wins'],0)

if __name__=='__main__':unittest.main()
