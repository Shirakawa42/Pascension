import copy
import unittest
from audit_goal_evaluation import audit,search_profile,heroes_for_seed


class GoalEvaluationTests(unittest.TestCase):
    def fixture(self,wins=400,draws=0):
        seed=0xAA00000000000000//20*20;n=600
        plan=dict(games=n,seed=seed,batch=20,candidate=dict(policy_file_sha256='policy'),
                  binary_sha256='runtime',incumbent_manifest_sha256='incumbent',hybrid=True,
                  smoke=False,learning_updates=False,incumbent_inference_backend='native')
        protocol=dict(games=n,seed=seed,batch=20,confidence=.95,
            requirements=dict(minimum_actual_wins=360,minimum_paired_confidence_lower_bound=.5),
            candidate=dict(policy_sha256='policy',binary_sha256='runtime',incumbent_manifest_sha256='incumbent',
                           search_profile=search_profile(plan)))
        games=[dict(seed=seed+i//2,learner_seat=i%2,heroes=list(heroes_for_seed(seed+i//2)),completed=True,
                    winner=i%2 if i<wins else -1 if i<wins+draws else 1-i%2) for i in range(n)]
        result=dict(state='complete',completed=n,planned=n,completed_games=games)
        return protocol,plan,result

    def test_complete_balanced_frozen_winning_candidate_passes(self):
        report=audit(*self.fixture())
        self.assertTrue(report['target_achieved']);self.assertEqual(report['by_hero']['rez']['games'],120)

    def test_optional_market_plans_cannot_change_frozen_profile(self):
        protocol,plan,result=self.fixture()
        plan['market_plans']=False
        self.assertTrue(audit(protocol,plan,result)['target_achieved'])
        plan['market_plans']=True
        with self.assertRaises(ValueError):audit(protocol,plan,result)

    def test_nondefault_shared_capacity_is_part_of_frozen_profile(self):
        protocol,plan,result=self.fixture()
        plan['shared_capacity']=2048
        self.assertTrue(audit(protocol,plan,result)['target_achieved'])
        plan['shared_capacity']=8192
        with self.assertRaises(ValueError):audit(protocol,plan,result)

    def test_draw_score_cannot_replace_actual_wins(self):
        report=audit(*self.fixture(330,60))
        self.assertAlmostEqual(report['summary']['resolved_game_score'],.6)
        self.assertFalse(report['target_achieved'])

    def test_censored_missing_foreign_duplicate_and_wrong_hero_fail(self):
        for name in ('censored','missing','foreign','duplicate','hero','winner'):
            protocol,plan,result=self.fixture();games=result['completed_games']
            if name=='censored':games[0]['completed']=False
            if name=='missing':games.pop()
            if name=='foreign':games[0]['seed']+=10000
            if name=='duplicate':games[0]=copy.deepcopy(games[1])
            if name=='hero':games[0]['heroes']=['decima','decima']
            if name=='winner':games[0]['winner']=7
            with self.subTest(name=name),self.assertRaises(ValueError):audit(protocol,plan,result)

    def test_runtime_policy_settings_and_backend_cannot_change(self):
        for name in ('runtime','policy','calibration','backend'):
            protocol,plan,result=self.fixture()
            if name=='runtime':plan['binary_sha256']='different'
            if name=='policy':plan['candidate']['policy_file_sha256']='different'
            if name=='calibration':plan['scry_finish_bias']=3.
            if name=='backend':plan['incumbent_inference_backend']='gpu'
            with self.subTest(name=name),self.assertRaises(ValueError):audit(protocol,plan,result)


if __name__=='__main__':unittest.main()
