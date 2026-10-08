import copy
import unittest
from audit_routed_evaluation import audit_routed,expected_keys,HEROES
from audit_goal_evaluation import heroes_for_seed,search_profile


class RoutedEvaluationTests(unittest.TestCase):
    def fixture(self):
        protocol=dict(games=600,batch=60,seed=0xAA00000000000000//20*20,confidence=.95,
            requirements=dict(minimum_actual_wins=360,minimum_paired_confidence_lower_bound=.5),candidate=dict(components={}))
        parts={}
        for name,heroes in [('scry',['decima','rez']),('average',['kosynwu','tetra','volos'])]:
            plan=dict(games=600,batch=60,seed=protocol['seed'],hero_filter=heroes,hybrid=True,
                      candidate=dict(policy_file_sha256=name),binary_sha256='runtime',incumbent_manifest_sha256='old')
            if name=='scry':plan.update(scry_temperature=64.,rez_coverage=True,future_scry=True)
            protocol['candidate']['components'][name]=dict(heroes=heroes,policy_sha256=name,binary_sha256='runtime',
                incumbent_manifest_sha256='old',search_profile=search_profile(plan))
            keys=sorted(expected_keys(protocol,heroes))
            games=[dict(seed=seed,learner_seat=seat,heroes=list(heroes_for_seed(seed)),completed=True,
                        winner=seat if i%5<3 else 1-seat) for i,(seed,seat) in enumerate(keys)]
            parts[name]=(plan,dict(state='complete',completed=len(keys),planned=len(keys),completed_games=games))
        return protocol,parts

    def test_disjoint_complete_hero_routes_pass_same600_game_target(self):
        report=audit_routed(*self.fixture())
        self.assertTrue(report['target_achieved']);self.assertEqual(report['actual_games'],600)
        self.assertEqual(report['by_hero']['rez']['games'],120);self.assertEqual(report['summary']['wins'],360)

    def test_single_policy_parallel_requires_identical_controller_in_every_partition(self):
        protocol,parts=self.fixture();protocol['single_policy_parallel']=True
        for name,(plan,_) in parts.items():
            plan['candidate']['policy_file_sha256']='same'
            plan.update(scry_temperature=64.,rez_coverage=True,future_scry=True)
            protocol['candidate']['components'][name].update(policy_sha256='same',search_profile=search_profile(plan))
        self.assertTrue(audit_routed(protocol,parts)['target_achieved'])
        # Each component remains internally consistent, but they may not be
        # presented as one model when their frozen identities differ.
        parts['average'][0]['candidate']['policy_file_sha256']='different'
        protocol['candidate']['components']['average']['policy_sha256']='different'
        with self.assertRaises(ValueError):audit_routed(protocol,parts)

    def test_missing_duplicate_wrong_route_identity_calibration_and_censors_fail(self):
        for mutation in ('missing','duplicate','wrong_route','identity','calibration','censor','overlap','missing_hero'):
            protocol,parts=self.fixture();plan,result=parts['scry']
            if mutation=='missing':result['completed_games'].pop()
            if mutation=='duplicate':result['completed_games'][0]=copy.deepcopy(result['completed_games'][1])
            if mutation=='wrong_route':result['completed_games'][0]=copy.deepcopy(parts['average'][1]['completed_games'][0])
            if mutation=='identity':plan['candidate']['policy_file_sha256']='changed'
            if mutation=='calibration':plan['scry_temperature']=1.
            if mutation=='censor':result['completed_games'][0]['completed']=False
            if mutation=='overlap':protocol['candidate']['components']['average']['heroes'].append('rez')
            if mutation=='missing_hero':protocol['candidate']['components']['average']['heroes'].remove('volos')
            with self.subTest(mutation=mutation),self.assertRaises(ValueError):audit_routed(protocol,parts)


if __name__=='__main__':unittest.main()
