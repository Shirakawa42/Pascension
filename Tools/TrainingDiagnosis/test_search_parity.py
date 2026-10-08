import unittest
import numpy as np
from search_parity import metrics,acceptable


class SearchParityTests(unittest.TestCase):
    def setUp(self):
        self.expected=np.array([[0.,-.2,-1e9,.4],[8.,0.,-1e9,-.8]],dtype=np.float64)
        self.mask=np.array([[1,1,0],[1,1,0]])

    def test_common_logit_offset_is_irrelevant(self):
        actual=self.expected.copy();actual[:,:-1]+=20
        self.assertTrue(acceptable(metrics(self.expected,actual,self.mask)))

    def test_small_measured_roundoff_is_accepted(self):
        actual=self.expected.copy();actual[0,0]+=.00001
        self.assertTrue(acceptable(metrics(self.expected,actual,self.mask)))

    def test_policy_drift_is_rejected(self):
        actual=self.expected.copy();actual[0,0]+=.01
        self.assertFalse(acceptable(metrics(self.expected,actual,self.mask)))

    def test_changed_action_is_rejected(self):
        actual=self.expected.copy();actual[0,1]=.1
        self.assertFalse(acceptable(metrics(self.expected,actual,self.mask)))

    def test_rare_action_search_prior_drift_is_rejected(self):
        actual=self.expected.copy();actual[1,1]+=.1
        report=metrics(self.expected,actual,self.mask)
        self.assertLess(report['total_variation'],.0001)
        self.assertFalse(acceptable(report))

    def test_value_drift_is_rejected(self):
        actual=self.expected.copy();actual[0,-1]+=.001
        self.assertFalse(acceptable(metrics(self.expected,actual,self.mask)))

    def test_nonfinite_output_is_rejected(self):
        actual=self.expected.copy();actual[0,0]=np.nan
        with self.assertRaises(ValueError):metrics(self.expected,actual,self.mask)


if __name__=='__main__':unittest.main()
