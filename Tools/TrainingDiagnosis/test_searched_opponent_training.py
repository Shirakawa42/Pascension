import copy
import unittest
import numpy as np
from train_against_search import owned_rollout, ROW, ACTIONS


class OnPolicyCreditTests(unittest.TestCase):
    def sample(self):
        rows=np.zeros((5,ROW),np.float32);rows[:,-ACTIONS:-ACTIONS+3]=1
        return dict(rows=rows,lanes=np.array([0,1,0,1,0]),actors=np.array([0,1,0,1,0]),
            targets=np.array([0,1,2,0,2]),priors=np.tile(np.r_[np.log([.2,.3,.5]),np.full(61,-1e9)],(5,1)).astype(np.float32),
            values=np.array([.9,-.3,-.2,.8,.1],np.float32),improved=np.zeros(5,np.int32),
            report=dict(games=[dict(lane=0,completed=True,winner=1),dict(lane=1,completed=True,winner=1)]))

    def test_terminal_credit_survives_every_intermediate_value(self):
        data=self.sample();store,indices,returns,advantages=owned_rollout(data)
        np.testing.assert_array_equal(indices,np.arange(5))
        np.testing.assert_array_equal(returns,[-1,1,-1,1,-1])
        np.testing.assert_allclose(store.packet[:,1],np.log([.2,.3,.5,.2,.5]),atol=2e-7)
        self.assertTrue(np.shares_memory(store.obs,data['rows']))
        self.assertAlmostEqual(float(advantages.mean()),0.,places=6)

    def test_censor_search_and_opponent_rows_are_rejected(self):
        for change in ('censor','search','opponent','illegal'):
            data=self.sample()
            if change=='censor':data['report']['games'][0]['completed']=False
            if change=='search':data['improved'][1]=1
            if change=='opponent':data['actors'][1]=0
            if change=='illegal':data['targets'][1]=5
            with self.subTest(change=change),self.assertRaises(ValueError):owned_rollout(data)

    def test_all_legal_actions_keep_original_probability(self):
        data=self.sample();data['targets'][:]=2
        store,_,_,_=owned_rollout(data)
        np.testing.assert_allclose(store.packet[:,1],np.log(.5),atol=1e-7)


if __name__=='__main__':unittest.main()
