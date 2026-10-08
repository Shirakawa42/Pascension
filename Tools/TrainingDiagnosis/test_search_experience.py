import unittest
import numpy as np
from search_experience import SearchExperience,dense_rows


class SearchExperienceTests(unittest.TestCase):
    def test_round_trip_and_seat_specific_outcomes(self):
        rng=np.random.default_rng(735191);packet=rng.standard_normal((7,27712),dtype=np.float32)
        packet[rng.random(packet.shape)<.97]=0;packet[3]=0
        meta=np.array([[i%2,i%2] for i in range(7)]);store=SearchExperience(27712)
        for start,end in ((0,3),(3,7)):store.append(packet[start:end],meta[start:end],np.arange(start,end),np.zeros(end-start))
        data=store.arrays([dict(lane=i,winner=0,completed=True,seed=2**63+i) for i in range(2)])
        np.testing.assert_array_equal(dense_rows(data,np.arange(7)),packet)
        np.testing.assert_array_equal(dense_rows(data,[6,3,0]),packet[[6,3,0]])
        np.testing.assert_array_equal(data['outcomes'],[1,-1,1,-1,1,-1,1])
        self.assertEqual(data['seeds'][1],2**63+1)

    def test_partial_games_are_not_labelled_as_draws(self):
        store=SearchExperience(3);store.append(np.ones((1,3)),np.array([[0,0]]),[0],[0])
        with self.assertRaises(ValueError):store.arrays([dict(lane=0,winner=-1,completed=False,seed=4)])


if __name__=='__main__':unittest.main()
