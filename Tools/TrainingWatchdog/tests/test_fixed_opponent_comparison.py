import copy
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from fixed_opponent_comparison import compare_fixed_opponent, compatible_training_changes
from paired_followup import merge_blocks
from test_paired_followup import block


def arena(winner):
    blocks = [block(index, batch=100) for index in range(2)]
    from league import ArenaGame, summarize_arena
    for value in blocks:
        for row in value['results']:
            row['winner'] = winner(row)
        value['summary'], value['rounds'], value['hero_coverage'] = summarize_arena(
            [ArenaGame(**row) for row in value['results']], planned_pairs=2000)
    return merge_blocks(blocks)


class FixedOpponentTests(unittest.TestCase):
    def test_older_candidate_execution_only_allows_training_source_changes(self):
        model = 'Tools/ZeroDepthTraining/model.py'
        older = {'files': {model: {'sha256': 'unchanged'}}}
        for path in ('Tools/ZeroDepthTraining/train.py', 'Tools/ZeroDepthTraining/performance_upgrade.py',
                     'Tools/ZeroDepthTraining/opponent_archive.py', 'Tools/ZeroDepthTraining/tests/test_new.py'):
            newer = copy.deepcopy(older)
            newer['files'][path] = {'sha256': 'new'}
            self.assertEqual(compatible_training_changes(older, newer), [path])
        for path in (model, 'Tools/ZeroDepthTraining/adaptive.py', 'Tools/ZeroDepthTraining/league.py',
                     'Assets/Scripts/Shards/Engine/Rules.cs'):
            newer = copy.deepcopy(older)
            newer['files'][path] = {'sha256': 'new'}
            with self.subTest(path=path), self.assertRaises(ValueError):
                compatible_training_changes(older, newer)

    def test_same_hero_results_show_no_improvement(self):
        older = arena(lambda row: 0)
        newer = copy.deepcopy(older)
        newer['candidate'] = {'sha': 'newer-policy'}
        result = compare_fixed_opponent(older, newer)
        self.assertEqual(result['overall_paired_difference'], 0)
        for hero in result['heroes'].values():
            self.assertEqual(hero['paired_difference'], 0)
            self.assertEqual(hero['games_per_version'], 1600)
        self.assertFalse(result['promotion_allowed'])

    def test_win_loss_direction_and_primary_confidence(self):
        older = arena(lambda row: 1-row['learner_seat'])
        newer = arena(lambda row: row['learner_seat'])
        result = compare_fixed_opponent(older, newer)
        self.assertEqual(result['overall_paired_difference'], 1)
        rez = result['rez_primary']
        self.assertEqual((rez['older_score'], rez['newer_score']), (0, 1))
        self.assertGreater(rez['primary_difference_interval'][0], rez['simultaneous_difference_interval'][0])
        reverse = compare_fixed_opponent(newer, older)
        self.assertEqual(reverse['rez_primary']['paired_difference'], -1)

    def test_changing_the_opponent_is_rejected_even_with_matching_heroes(self):
        older = arena(lambda row: 0)
        newer = copy.deepcopy(older)
        newer['baseline'] = {'sha': 'different-opponent'}
        with self.assertRaisesRegex(ValueError, 'Fixed opponent'):
            compare_fixed_opponent(older, newer)

    def test_changed_sampling_initial_states_or_invalid_results_are_rejected(self):
        older = arena(lambda row: 0)
        mutations = [lambda x: x.update(policy_seed=2),
                     lambda x: x['mirror_initial_publications'][0].update(initial_publication_sha256='b'*64),
                     lambda x: x.update(integrity_verified=False),
                     lambda x: x['summary'].update(wins=0),
                     lambda x: x['results'].pop(),
                     lambda x: x['results'][0].update(censored=True),
                     lambda x: x['results'].__setitem__(0, copy.deepcopy(x['results'][1]))]
        for mutate in mutations:
            newer = copy.deepcopy(older)
            mutate(newer)
            with self.subTest(mutation=mutate), self.assertRaises(ValueError):
                compare_fixed_opponent(older, newer)


if __name__ == '__main__':
    unittest.main()
