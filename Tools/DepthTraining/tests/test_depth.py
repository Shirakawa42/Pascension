import hashlib,json,subprocess,sys,tempfile,unittest
from pathlib import Path
import numpy as np
HERE=Path(__file__).resolve().parents[1];sys.path.insert(0,str(HERE))
from learning import labels
from supervisor import check,lock_file

class DepthContracts(unittest.TestCase):
    def test_terminal_labels_use_deciding_seat_and_drop_censors(self):
        keep,value=labels({'lanes':np.array([0,0,1,1,2]),'actors':np.array([0,1,0,1,1]),
            'report':{'games':[{'lane':0,'completed':True,'winner':1},{'lane':1,'completed':False,'winner':-1},{'lane':2,'completed':True,'winner':-1}]}})
        np.testing.assert_array_equal(keep,[True,True,False,False,True]);np.testing.assert_array_equal(value,[-1,1,0,0,0])
    def test_distillation_keeps_unsurpassed_policy_distribution(self):
        import torch
        from learning import update
        from client import ROW,ACTIONS
        class Toy(torch.nn.Module):
            def __init__(self):
                super().__init__();self.logits=torch.nn.Parameter(torch.zeros(ACTIONS));self.v=torch.nn.Parameter(torch.zeros(()))
            def forward(self,obs,candidates,mask):
                return self.logits.expand(len(obs),-1).masked_fill(~mask.bool(),-1e9),self.v.expand(len(obs))
        model=Toy();optimizer=torch.optim.SGD(model.parameters(),lr=.1)
        rows=np.zeros((1,ROW),np.float32);rows[0,-ACTIONS:]=1
        collection={'rows':rows,'lanes':np.array([0]),'actors':np.array([0]),'targets':np.array([1]),'priors':np.zeros((1,ACTIONS),np.float32),'improved':np.array([0]),'report':{'games':[{'lane':0,'completed':True,'winner':-1}]}}
        update(model,optimizer,collection,epochs=1)
        self.assertTrue(torch.allclose(model.logits,torch.zeros(ACTIONS),atol=1e-8,rtol=0))
        collection['improved'][0]=1
        update(model,optimizer,collection,epochs=1)
        self.assertGreater(float(model.logits[1]),float(model.logits[0]))
        before=model.logits.detach().clone();collection['report']['games'][0]['completed']=False
        result=update(model,optimizer,collection,epochs=1)
        self.assertEqual(result['rows'],0);self.assertTrue(torch.equal(before,model.logits))

    def test_budget_and_source_pins_fail_closed(self):
        with tempfile.TemporaryDirectory() as td:
            file=Path(td)/'source';file.write_text('original')
            config={'start_wall':1000,'hard_deadline_wall':1000+28800,'seconds':28800,'pins':{str(file):hashlib.sha256(file.read_bytes()).hexdigest()}}
            check(config);config['seconds']=28801
            with self.assertRaises(ValueError):check(config)
            config['seconds']=28800;file.write_text('changed')
            with self.assertRaises(RuntimeError):check(config)
    def test_supervisor_lock_is_exclusive(self):
        with tempfile.TemporaryDirectory() as td:
            path=Path(td)/'owner.lock';first=lock_file(path);self.assertIsNotNone(first)
            self.assertIsNone(lock_file(path));first.close();second=lock_file(path);self.assertIsNotNone(second);second.close()
    def test_stopped_native_sentinel_never_restarts(self):
        with tempfile.TemporaryDirectory() as td:
            root=Path(td);config=root/'config.json';supervisor=HERE/'supervisor.py'
            config.write_text(json.dumps({'start_wall':1000,'hard_deadline_wall':29800,'seconds':28800,'pins':{}}));(root/'STOP').touch()
            run=subprocess.run([sys.executable,str(supervisor),'--config',str(config),'--expected-sha',hashlib.sha256(config.read_bytes()).hexdigest(),
                '--hard-deadline-wall','29800','--expected-runner-sha',hashlib.sha256(supervisor.read_bytes()).hexdigest()],capture_output=True,text=True,timeout=10)
            self.assertEqual(run.returncode,0,run.stderr);self.assertEqual(json.loads(run.stdout)['disposition'],'terminal')
if __name__=='__main__':unittest.main()
