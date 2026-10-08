import unittest
import numpy as np
from split_interval_guard import blocked_intervals, blocked_batch, SPLIT


class SplitGuardTests(unittest.TestCase):
    def fixture(self, power=5, defense=4):
        obs = np.zeros(2048, np.float32)
        obs[112:116] = SPLIT
        obs[6] = obs[7] = obs[13] = power/1000
        obs[93] = 1/20
        obs[1856:1862] = [1/192,1,0,0,defense/50,0]
        c = np.zeros((64,32),np.float32)
        legal = np.arange(64) <= power
        for i in range(power+1):
            c[i,15] = c[i,28] = 1
            c[i,29] = c[i,30] = i/1000
        return obs,c,legal

    def test_retains_face_and_all_potentially_lethal_amounts(self):
        for power in range(1,20):
            for defense in range(1,20):
                o,c,m = self.fixture(power,defense)
                b=blocked_intervals(o,c,m)
                self.assertEqual(np.flatnonzero(m & ~b).tolist(),[n for n in range(power+1) if power-n==0 or power-n>=defense])

    def test_retains_coarse_intervals_containing_any_valid_amount(self):
        o,c,m=self.fixture(10,4)
        m[:]=False;m[:3]=True
        c[:3,29]=np.array([0,7,9])/1000;c[:3,30]=np.array([6,8,10])/1000
        self.assertEqual(blocked_intervals(o,c,m)[:3].tolist(),[False,True,False])

    def test_excludes_taunt_multiple_champions_and_other_contexts(self):
        for slot,value in [(6,0),(93,2/20),(112,0)]:
            o,c,m=self.fixture();o[slot]=value
            self.assertFalse(blocked_intervals(o,c,m).any())

    def test_unsampled_opponent_lane_may_already_have_incompatible_interval(self):
        a,ac,am=self.fixture(10,4)
        b,bc,bm=self.fixture(10,4)
        bm[:]=False;bm[7:9]=True  # Opponent previously chose this interval.
        result=blocked_batch(np.stack([a,b]),np.stack([ac,bc]),np.stack([am,bm]),np.array([True,False]))
        self.assertTrue(result[0].any())
        self.assertFalse(result[1].any())


if __name__=='__main__':unittest.main()
