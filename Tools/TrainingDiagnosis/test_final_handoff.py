import json
from pathlib import Path
import tempfile
import unittest

from final_handoff import heroes_for_seed, select_candidate, sha256_file


class FinalHandoffTests(unittest.TestCase):
    def fixture(self, root, scores=(44, 45)):
        seed = 0xA700000000000000//20*20
        config = dict(development_games=80, development_seed=seed, candidates=[])
        for i, wins in enumerate(scores):
            folder = root/str(i);(folder/'runtime').mkdir(parents=True)
            (folder/'runtime/DepthHost.dll').write_bytes(b'identical frozen runtime')
            (folder/'policy.pt').write_bytes(str(i).encode())
            plan = dict(games=80, seed=seed, binary_sha256=sha256_file(folder/'runtime/DepthHost.dll'),
                        incumbent_manifest_sha256='incumbent', candidate=dict(policy_file_sha256=sha256_file(folder/'policy.pt')))
            rows = [dict(seed=seed+j//2, learner_seat=j%2, heroes=list(heroes_for_seed(seed+j//2)),
                         completed=True, winner=j%2 if j<wins else 1-j%2) for j in range(80)]
            result = dict(state='complete', completed=80, planned=80, completed_games=rows,
                          summary=dict(wins=80))  # Deliberately wrong: selection must recount actual games.
            (folder/'plan.json').write_text(json.dumps(plan));(folder/'result.json').write_text(json.dumps(result))
            config['candidates'].append(dict(experiment=str(folder), policy=str(folder)))
        return config

    def test_actual_results_determine_selection_and_first_candidate_wins_ties(self):
        for scores, expected in [((44, 45), '1'), ((45, 44), '0'), ((45, 45), '0')]:
            with tempfile.TemporaryDirectory() as temporary:
                selected, _ = select_candidate(self.fixture(Path(temporary), scores))
                self.assertEqual(Path(selected[0]['experiment']).name, expected)

    def test_incomplete_censored_duplicate_invalid_and_changed_weights_are_rejected(self):
        for mutation in ('incomplete', 'censored', 'duplicate', 'invalid', 'weights', 'runtime', 'incumbent'):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temporary:
                root=Path(temporary);config=self.fixture(root);folder=root/'1'
                path=folder/'result.json';result=json.loads(path.read_text())
                if mutation=='incomplete':result['state']='running'
                if mutation=='censored':result['completed_games'][0]['completed']=False
                if mutation=='duplicate':result['completed_games'][0]=result['completed_games'][1]
                if mutation=='invalid':result['completed_games'][0]['winner']=3
                if mutation=='weights':(folder/'policy.pt').write_bytes(b'changed')
                if mutation=='runtime':(folder/'runtime/DepthHost.dll').write_bytes(b'changed')
                if mutation=='incumbent':
                    plan=json.loads((folder/'plan.json').read_text());plan['incumbent_manifest_sha256']='other'
                    (folder/'plan.json').write_text(json.dumps(plan))
                path.write_text(json.dumps(result))
                with self.assertRaises(ValueError):select_candidate(config)


if __name__ == '__main__':
    unittest.main()
