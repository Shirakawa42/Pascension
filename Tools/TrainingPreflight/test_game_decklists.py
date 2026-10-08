import json
from pathlib import Path
import tempfile
import unittest

from game_decklists import GameDecklists


class DecklistTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.path=Path(self.temp.name)/'games.json'
        self.games=[dict(seed=f'{i:016x}',number=i+1,round=5+i//2,winner=i%2,victory='normal_damage',
            players=[dict(hero=h,health=20,mastery=6,collection={'crystal':7, **({'lucky':1} if i==0 else {})},
                fast_acquired={'temporary':1}) for h in ('decima','kosynwu')]) for i in range(4)]
        self.path.write_text(json.dumps(dict(schema='shards-completed-game-decklists-v1',count=4,
            state='stopped',catalog={},games=self.games)))
        self.browser=GameDecklists(self.path)

    def test_pages_are_stable_and_cover_every_game_once(self):
        first=self.browser.query({'limit':['2']});second=self.browser.query({'limit':['2'],'offset':['2']})
        self.assertEqual([g['number'] for g in first['games']+second['games']],[1,2,3,4])
        self.assertEqual(first['total'],4)

    def test_filters_use_final_collection_not_fast_play_acquisitions(self):
        self.assertEqual(self.browser.query({'card':['temporary']})['matched'],0)
        self.assertEqual(self.browser.query({'card':['lucky'],'hero':['decima'],'max_round':['5']})['matched'],1)
        self.assertEqual(self.browser.query({'min_round':['5'],'max_round':['5']})['matched'],2)
        self.assertEqual(self.browser.query({'hero':['rez']})['matched'],0)

    def test_exact_detail_preserves_multiplicity_and_fastplays_separately(self):
        detail=self.browser.query({'seed':['0000000000000000']})['game']
        self.assertEqual(detail,self.games[0])
        self.assertEqual(detail['players'][0]['collection']['crystal'],7)

    def test_invalid_requests_are_rejected(self):
        for query in ({'seed':['../secrets']},{'seed':['000000000000ffff']},{'offset':['-1']},
                      {'limit':['1000']},{'min_round':['10'],'max_round':['5']},{'sort':['invalid']}):
            with self.subTest(query=query),self.assertRaises(ValueError):self.browser.query(query)


if __name__=='__main__':unittest.main()
