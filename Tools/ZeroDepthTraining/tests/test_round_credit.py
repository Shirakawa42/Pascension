import sys,unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import numpy as np
from train import lambda_returns,Rollout,TrainConfig
from test_learning import fake_host,small_catalog

class Tests(unittest.TestCase):
    def test_intermediate_choices_do_not_shorten_round_credit(self):
        rewards=np.array([[1,-1]],np.float32)
        short=lambda_returns(np.zeros(3,int),np.zeros(3,int),np.zeros(3,np.float32),rewards,.95,
                             rounds=np.array([1,2,3]))
        long=lambda_returns(np.zeros(102,int),np.zeros(102,int),np.zeros(102,np.float32),rewards,.95,
                            rounds=np.r_[np.ones(100,int),2,3])
        np.testing.assert_allclose(short,long[[0,100,101]],atol=1e-6)
        self.assertAlmostEqual(float(long[0]),.95**2,places=6)

    def test_same_round_bootstrap_is_skipped_and_seats_remain_separate(self):
        actual=lambda_returns(np.array([0,0,0,0]),np.array([0,1,0,1]),
                              np.array([.2,-.3,.7,-.8],np.float32),np.array([[1,-1]],np.float32),.95,
                              rounds=np.array([1,1,1,2]))
        np.testing.assert_allclose(actual,[1,-.99,1,-1],atol=1e-6)

    def test_rounds_must_be_monotonic_per_trajectory(self):
        with self.assertRaises(ValueError):
            lambda_returns(np.array([0,0]),np.array([0,0]),np.zeros(2,np.float32),
                           np.array([[1,-1]],np.float32),.95,rounds=np.array([2,1]))

    def test_rollout_keeps_real_rounds_and_excludes_censors(self):
        host=fake_host();store=Rollout(32,small_catalog());packet=np.zeros((3,3),np.float32)
        host.obs[:,2]=.01;store.append(host,packet,[0,1,2])
        host.obs[:,2]=.02;store.append(host,packet,[0,1,2])
        rows,targets,_,excluded=store.seal([1,2,1],[[1,-1],[0,0],[-1,1]],gae_lambda=.95,trace_mode='round')
        np.testing.assert_allclose(targets,[.95,-.95,1,-1],atol=1e-6)
        self.assertEqual(excluded,2)

    def test_legacy_default_is_unchanged_and_invalid_modes_rejected(self):
        self.assertEqual(TrainConfig().trace_mode,'decision')
        TrainConfig(trace_mode='round').validate()
        with self.assertRaises(ValueError):TrainConfig(trace_mode='nonsense').validate()

if __name__=='__main__':unittest.main()
