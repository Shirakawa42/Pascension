import copy
import json
from pathlib import Path
import tempfile
import unittest
from balance_analysis import build, METRICS, MILESTONES


def player(seat):
    return dict(**{k:0 for k in METRICS}, **{k:0 for k in MILESTONES}, seat=seat,
                hero=['tetra','rez'][seat], health=30, mastery=10, destiny=None,
                acquired={},played={},activated={},deployed={},fast_acquired={},banished={},rerolled={},
                first_acquired_round={},first_played_round={},modes={},extra_destinies=[])

class BalanceAnalysisTests(unittest.TestCase):
    def game(self, seed='a', winner=0, cause='normal_damage'):
        return dict(schema=2, seed=seed, winner=winner, victory=cause, round=12,
                    comet_market_seen=False, players=[player(0),player(1)])
    def run_build(self,games,totals=None):
        totals=totals or dict(resolved_games=len(games), seat0_wins=sum(g['winner']==0 for g in games),
             seat1_wins=sum(g['winner']==1 for g in games), draws=sum(g['winner']==-1 for g in games),
             mean_rounds=sum(g['round'] for g in games)/len(games))
        with tempfile.TemporaryDirectory() as d:
            path=Path(d)/'games.jsonl';path.write_text(''.join(json.dumps(g)+'\n' for g in games))
            return build(path,dict(totals=totals))
    def test_finishes_partition_games_and_seats_do_not_double_count_wins(self):
        games=[self.game('a',0,'mastery'),self.game('b',1,'comet'),self.game('c',-1,'draw')]
        r=self.run_build(games)
        self.assertEqual(sum(x['count'] for x in r['victory']),3)
        self.assertEqual((r['groups']['all:all']['games'],r['groups']['all:all']['wins'],r['groups']['all:all']['draws']),(6,2,2))
        self.assertEqual(r['groups']['tetra:0']['victory'],{'mastery':1})
        self.assertEqual(r['groups']['rez:1']['victory'],{'comet':1})
        self.assertEqual(r['rounds']['count'],3)
    def test_use_rate_includes_fastplays_direct_deployment_and_activation(self):
        g=self.game();p=g['players'][0]
        p.update(acquired={'ally':2,'merc':1,'champ':1,'relic':1,'late':1},
                 first_acquired_round=dict(ally=2,merc=3,champ=4,relic=5,late=12),
                 played={'ally':3},fast_acquired={'merc':1},deployed={'champ':1},activated={'relic':2})
        cards={r['id']:r for r in self.run_build([g])['groups']['all:all']['cards']}
        self.assertEqual(cards['ally']['acquired_games'],1)
        self.assertEqual(cards['ally']['acquired_count'],2)
        for id_ in ('ally','merc','champ','relic'):self.assertEqual(cards[id_]['use_rate'],1)
        self.assertEqual(cards['late']['use_rate'],0)
    def test_milestone_denominators_include_players_who_never_reached(self):
        g=self.game();g['players'][0]['mastery30_round']=10
        r=next(x for x in self.run_build([g])['groups']['all:all']['milestones'] if x['id']=='mastery30_round')
        self.assertEqual((r['games'],r['reach_rate'],r['mean_round'],r['score']),(1,.5,10,1))
    def test_normal_and_bonus_destinies_have_separate_conditional_scores(self):
        g=self.game();g['players'][0].update(destiny='d',destiny_round=4,first_acquired_round={'d':4})
        g['players'][1].update(extra_destinies=['d'],first_acquired_round={'d':10})
        r=self.run_build([g])['groups']['all:all']['destiny_sources']
        self.assertEqual([(x['source'],x['score'],x['mean_round']) for x in r],[('normal',1,4),('reward',0,10)])
    def test_comet_ownership_is_not_a_comet_finish(self):
        g=self.game();g['comet_market_seen']=True
        g['players'][0].update(acquired={'comet':1},first_acquired_round={'comet':11})
        c=self.run_build([g])['comet'];self.assertEqual(c['acquired_unplayed_players'],1);self.assertEqual(c['finishing_wins'],0)
    def test_corrupt_or_wrong_cohort_is_rejected(self):
        g=self.game()
        for changed in [dict(victory='unknown'),dict(victory='draw'),dict(schema=1)]:
            with self.subTest(changed=changed),self.assertRaises(ValueError):self.run_build([{**g,**changed}])
        with self.assertRaisesRegex(ValueError,'Duplicate'):self.run_build([g,g])
        with self.assertRaisesRegex(ValueError,'outcomes'):self.run_build([g],dict(resolved_games=2))
    def test_modes_count_actions_but_score_each_player_once(self):
        g=self.game();g['players'][0]['modes']={'volos|heal':8};g['players'][1]['modes']={'volos|heal':1}
        r=self.run_build([g])['groups']['all:all']['modes'][0]
        self.assertEqual((r['count'],r['games'],r['wins'],r['score']),(9,2,1,.5))

if __name__=='__main__':unittest.main()
