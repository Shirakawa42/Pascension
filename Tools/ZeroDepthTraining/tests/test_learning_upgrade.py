"""Learning curriculum migration preserves weights, RNG and a recovered cap."""
import copy
import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
from test_performance_upgrade import UpgradeTests, persistence, upgrade


class LearningUpgradeTests(UpgradeTests):
    def historical_identity(self):
        self.new = copy.deepcopy(self.new)
        self.new['configuration']['archive_strategy'] = 'historical'
        return {'archive_strategy': {'before': None, 'after': 'historical'}}

    def test_historical_curriculum_requires_exact_approval_and_no_other_tuning(self):
        approved = self.historical_identity()
        with self.assertRaises(upgrade.UpgradeError):
            upgrade.validate_transition(self.old, self.new, self.before, self.after, self.approved)
        self.assertEqual(upgrade.validate_transition(self.old, self.new, self.before,
            self.after, self.approved, approved), self.approved)
        for key, value in (('learning_rate', .0001), ('entropy', .003), ('epochs', 2),
                           ('width', 256), ('archive_fraction', .5), ('hero_mode', 'policy')):
            changed = copy.deepcopy(self.new)
            changed['configuration'][key] = value
            delta = {field: {'before': self.old['configuration'].get(field), 'after': target}
                     for field, target in changed['configuration'].items()
                     if self.old['configuration'].get(field) != target}
            with self.subTest(field=key), self.assertRaises(upgrade.UpgradeError):
                upgrade.validate_transition(self.old, changed, self.before, self.after,
                                            self.approved, delta)
        for before, after in (('historical', 'recent'), ('unknown', 'historical'),
                              ('recent', 'unknown'), ('recent', None)):
            with self.subTest(before=before, after=after), self.assertRaises(upgrade.UpgradeError):
                upgrade.validate_configuration({'archive_strategy': before},
                    {'archive_strategy': after}, {'archive_strategy': {'before': before, 'after': after}})

    def test_recovered_39012_second_allocation_keeps_exact_state_and_original_deadline(self):
        with persistence.CampaignBudget(self.campaign / 'budget.json', 39012, clock=self.clock) as budget:
            with budget.start_session('zero-depth', 39012, 20) as session:
                self.anchor = {'campaign_id': budget.campaign_id, 'limit_seconds': 39012,
                    'boot_id': self.clock.boot, 'hard_deadline_monotonic': session.hard_deadline_monotonic,
                    'hard_deadline_wall': session.hard_deadline_wall}
                self.clock.advance(100)
                persistence.save_checkpoint_atomic(self.campaign / 'latest.soicp', self.state,
                    identity=self.old, budget=budget, include_cuda_rng=False)
            original = persistence.load_checkpoint(self.campaign / 'latest.soicp', expected_identity=self.old, budget=budget)
            approved = self.historical_identity()
            self.clock.advance(50)
            with budget.start_session('learning-upgrade-pause', 50, 0):
                self.clock.advance(50)
            lineage = self.commit(budget, approved_configuration_changes=approved)
            migrated = persistence.load_checkpoint(self.campaign / 'latest.soicp', expected_identity=self.new, budget=budget)
            self.assertTrue(upgrade._same_state(original['rng'], migrated['rng']))
            for key in self.state:
                if key != 'configuration':
                    self.assertTrue(upgrade._same_state(original['state'][key], migrated['state'][key]), key)
            self.assertEqual(lineage['configuration_changes'], approved)
            grant, deadline = upgrade.session_seconds(self.campaign, budget, 39012, with_deadline=True)
            self.assertEqual(deadline, self.anchor['hard_deadline_monotonic'])
            self.assertEqual(grant, 39012 - 200)
            self.assertEqual(budget.limit_seconds, 39012)
            for seconds in (43200, 39013, 39011):
                with self.subTest(seconds=seconds), self.assertRaises(upgrade.UpgradeError):
                    upgrade.session_seconds(self.campaign, budget, seconds)
            changed = dict(self.anchor, limit_seconds=43200)
            with self.assertRaises(upgrade.UpgradeError):
                upgrade.validate_anchor(changed, json.loads(budget.path.read_text()))


if __name__ == '__main__':
    unittest.main()

class PrioritizedUpgradeTests(UpgradeTests):
    def prepare_prioritized(self):
        from opponent_archive import OpponentArchive
        self.configuration.update(archive_strategy='historical',archive_limit=6,archive_every=8)
        self.old['configuration']=copy.deepcopy(self.configuration)
        self.new['configuration']={**self.configuration,'archive_strategy':'prioritized'}
        self.write('identity.json',self.old);self.write('config.json',self.configuration)
        pool=OpponentArchive('historical',limit=6,every=8,generation=0,current=self.state['policy'])
        for generation in range(8,49,8):pool.add(generation,self.state['policy'])
        self.state.update(generations=48,archive=pool.weights,opponent_archive=pool.state_dict(48))
        return {'archive_strategy':{'before':'historical','after':'prioritized'}}

    def test_exact_history_weights_optimizer_rng_and_budget_survive_priority_migration(self):
        approved=self.prepare_prioritized()
        with self.budget() as budget:
            self.original_checkpoint(budget)
            original=persistence.load_checkpoint(self.campaign/'latest.soicp',expected_identity=self.old,budget=budget)
            self.commit(budget,approved_configuration_changes=approved)
            result=persistence.load_checkpoint(self.campaign/'latest.soicp',expected_identity=self.new,budget=budget)
            self.assertTrue(upgrade._same_state(original['rng'],result['rng']))
            for key,value in original['state'].items():
                if key not in ('configuration','opponent_archive'):
                    self.assertTrue(upgrade._same_state(value,result['state'][key]),key)
            self.assertEqual(result['state']['opponent_archive'],
                {**original['state']['opponent_archive'],'strategy':'prioritized','payoffs':{}})
            self.assertEqual(upgrade.session_seconds(self.campaign,budget,43200,with_deadline=True)[1],self.anchor['hard_deadline_monotonic'])

    def test_priority_requires_valid_historical_archive_and_exact_single_change(self):
        approved=self.prepare_prioritized()
        with self.assertRaises(upgrade.UpgradeError):
            upgrade.validate_configuration(self.old['configuration'],{**self.new['configuration'],'entropy':.001},approved)
        self.state['opponent_archive']['history_seen']+=1
        with self.budget() as budget:
            self.original_checkpoint(budget)
            with self.assertRaises(ValueError):self.commit(budget,approved_configuration_changes=approved)
            self.assertFalse((self.campaign/'performance-upgrade.json').exists())

class TemporalCreditTests(unittest.TestCase):
    def test_td_lambda_targets_follow_same_player_across_interleaved_lanes(self):
        import numpy as np
        from train import lambda_returns
        lanes=np.array([0,1,0,0,1,0]); seats=np.array([0,1,1,0,1,0])
        values=np.array([.1,-.2,.4,.2,-.4,.3],np.float32)
        rewards=np.array([[1,-1],[1,-1]],np.float32)
        actual=lambda_returns(lanes,seats,values,rewards,.5)
        np.testing.assert_allclose(actual,[.425,-.7,-1,.65,-1,1],atol=1e-7)

    def test_lambda_one_is_terminal_credit_and_zero_is_next_value(self):
        import numpy as np
        from train import lambda_returns
        lanes=np.array([0,0,0]);seats=np.array([1,1,1]);values=np.array([.1,.2,.3],np.float32)
        rewards=np.array([[-1,1]],np.float32)
        np.testing.assert_allclose(lambda_returns(lanes,seats,values,rewards,1),[1,1,1])
        np.testing.assert_allclose(lambda_returns(lanes,seats,values,rewards,0),[.2,.3,1])
        with self.assertRaises(ValueError):lambda_returns(lanes,seats,values,rewards,1.1)

    def test_censored_rows_never_bootstrap_natural_games(self):
        import numpy as np
        from test_learning import fake_host,small_catalog
        from train import Rollout
        host=fake_host();store=Rollout(32,small_catalog());packet=np.zeros((3,3),np.float32)
        packet[:,2]=[.2,9999,-.3]
        store.append(host,packet,[0,1,2]);packet[:,2]=[.4,-9999,-.1];store.append(host,packet,[0,1,2])
        indices,targets,advantages,excluded=store.seal([1,2,1],[[1,-1],[0,0],[-1,1]],gae_lambda=.5)
        np.testing.assert_array_equal(indices,[0,2,3,5]);self.assertEqual(excluded,2)
        np.testing.assert_allclose(targets,[.7,-.55,1,-1],atol=1e-7)
        self.assertTrue(np.isfinite(advantages).all())

    def test_randomized_matches_scalar_per_player_recurrence(self):
        import numpy as np
        from train import lambda_returns
        rng=np.random.default_rng(814)
        for _ in range(20):
            lanes=rng.integers(0,7,200);seats=rng.integers(0,2,200);values=rng.uniform(-1,1,200).astype(np.float32)
            rewards=np.column_stack((np.ones(7),-np.ones(7))).astype(np.float32)
            expected=np.empty(200,np.float32)
            for lane in range(7):
                for seat in range(2):
                    rows=np.flatnonzero((lanes==lane)&(seats==seat));future=rewards[lane,seat];next_value=future
                    for index in rows[::-1]:
                        future=.05*next_value+.95*future;expected[index]=future;next_value=values[index]
            np.testing.assert_allclose(lambda_returns(lanes,seats,values,rewards,.95),expected,atol=1e-6)
