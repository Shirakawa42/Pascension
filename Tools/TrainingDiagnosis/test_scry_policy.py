import unittest
import torch
from scry_policy import calibrate,require_calibration


class ScryPolicyTests(unittest.TestCase):
    def inputs(self):
        obs=torch.zeros(3,160);obs[:,144]=1;obs[:,157]=torch.tensor([2/26,3/26,2/26]);obs[2,144]=0
        z=torch.tensor([[-40.,0.,-1.,-1e9]]*3,requires_grad=True)
        c=torch.zeros(3,4,48);c[:,0,13]=1;c[:,1:3,12]=1
        mask=torch.tensor([[1.,1.,1.,0.]]*3)
        return z,obs,c,mask

    def test_disabled_path_is_identical_object(self):
        z,obs,c,mask=self.inputs()
        self.assertIs(calibrate(z,obs,c,mask,context_count=26,scry_context=2),z)

    def test_other_contexts_and_illegal_logits_unchanged(self):
        z,obs,c,mask=self.inputs();copy=z.detach().clone()
        changed=calibrate(z,obs,c,mask,context_count=26,scry_context=2,temperature=64)
        self.assertTrue(torch.equal(changed[1:],z[1:]));self.assertTrue(torch.equal(changed[:,3],z[:,3]))
        self.assertTrue(torch.equal(copy,z));self.assertGreater(float(changed.softmax(-1)[0,0].detach()),.1)

    def test_rare_selected_action_retains_useful_gradient(self):
        z,obs,c,mask=self.inputs()
        changed=calibrate(z,obs,c,mask,context_count=26,scry_context=2,temperature=64)
        logp=changed.log_softmax(-1)[0,0];gradient=torch.autograd.grad(logp,z)[0][0,0]
        self.assertGreater(float(gradient),.01)

    def test_required_distribution_cannot_be_silently_removed_or_changed(self):
        manifest=dict(required_inference_calibration=dict(scry_temperature=64.,scry_finish_bias=0.))
        require_calibration(manifest,64,0)
        with self.assertRaises(ValueError):require_calibration(manifest)
        with self.assertRaises(ValueError):require_calibration(manifest,32,0)
        with self.assertRaises(ValueError):require_calibration(manifest,64,2)


if __name__=='__main__':unittest.main()
