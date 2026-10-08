import copy
import json
from pathlib import Path
import tempfile
import unittest

from selfplay_statistics import validate_games, read_resume, save
from league import heroes_for_seed


class CompletedGamesTests(unittest.TestCase):
    def setUp(self):
        self.seed = 8002896537837432820
        self.rows = [dict(seed=self.seed+i, heroes=heroes_for_seed(self.seed+i),
            completed=True, winner=i%2) for i in range(20)]

    def test_all_twenty_matchups_and_both_seats_are_balanced(self):
        validate_games(self.rows, self.seed, 20)

    def test_incomplete_duplicate_foreign_or_wrong_hero_results_rejected(self):
        for change in (lambda rows: rows.pop(),
                       lambda rows: rows.__setitem__(1, rows[0]),
                       lambda rows: rows[0].update(completed=False),
                       lambda rows: rows[0].update(seed=self.seed+100),
                       lambda rows: rows[0].update(heroes=('rez', 'rez')),
                       lambda rows: rows[0].update(winner=2)):
            rows = copy.deepcopy(self.rows)
            change(rows)
            with self.assertRaises(ValueError):
                validate_games(rows, self.seed, 20)


class ResumeTests(CompletedGamesTests):
    def fixture(self, directory):
        rawdir = directory/'raw/forced-random'
        rawdir.mkdir(parents=True)
        manifest = dict(seed=self.seed, games=40, batch=20, policy_sha256='policy', host_sha256='host',
            settings_source_manifest_sha256='settings', both_seats_same_new_ai=True,
            scry_temperature=64, depth=24, width=4, worlds=2, rollout_styles=4, fixed_inference_batch=512)
        save(directory/'manifest.json', manifest)
        save(directory/'games.json', dict(rows=self.rows))
        save(rawdir/'session-test.json', dict(host_binary_sha256='host', purpose='final_evaluation',
            totals=dict(completed_games=20, censored_games=0, unfinished_discarded_games=0,
                        seat0_wins=10, seat1_wins=10)))
        (rawdir/'strategy-games.jsonl').write_text('\ufeff'+'\n'.join(json.dumps(dict(seed=f'{r["seed"]:x}',
            winner=r['winner'], players=[dict(hero=h) for h in r['heroes']])) for r in self.rows)+'\n')
        return manifest

    def test_complete_cohorts_preserve_all_records_and_normalize_trace_bom(self):
        with tempfile.TemporaryDirectory() as path:
            directory=Path(path); manifest=self.fixture(directory)
            _, rows, raw, trace=read_resume(directory, manifest)
            self.assertEqual(rows, json.loads(json.dumps(self.rows)))
            self.assertEqual(raw['totals']['completed_games'], 20)
            self.assertFalse(trace.startswith(b'\xef\xbb\xbf'))
            self.assertEqual(len(trace.splitlines()), 20)

    def test_unreported_completed_games_and_changed_policy_cannot_be_lost_or_mixed(self):
        with tempfile.TemporaryDirectory() as path:
            directory=Path(path); manifest=self.fixture(directory)
            with self.assertRaisesRegex(ValueError, 'policy_sha256'):
                read_resume(directory, {**manifest, 'policy_sha256':'other'})
            rawpath=directory/'raw/forced-random/session-test.json'
            raw=json.loads(rawpath.read_text());raw['totals']['completed_games']=21;save(rawpath, raw)
            with self.assertRaisesRegex(ValueError, 'refusing to lose'):
                read_resume(directory, manifest)

    def test_duplicate_trace_seed_is_rejected(self):
        with tempfile.TemporaryDirectory() as path:
            directory=Path(path); manifest=self.fixture(directory)
            trace=directory/'raw/forced-random/strategy-games.jsonl'
            lines=trace.read_text(encoding='utf-8-sig').splitlines();lines[1]=lines[0]
            trace.write_text('\n'.join(lines)+'\n')
            with self.assertRaisesRegex(ValueError, 'duplicate seeds'):
                read_resume(directory, manifest)


if __name__ == '__main__':
    unittest.main()
