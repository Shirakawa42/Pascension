import argparse
import json
import os
from pathlib import Path
import sys
import rez_runtime as runtime

if __name__=='__main__':
    mode=sys.argv.pop(1)
    if mode=='prepare':
        p=argparse.ArgumentParser();p.add_argument('--source',type=Path,default=runtime.SOURCE);p.add_argument('--block',type=int,required=True)
        a=p.parse_args();print(json.dumps(dict(directory=str(runtime.prepare(a.source,a.block)))))
    elif mode=='train':
        p=argparse.ArgumentParser();p.add_argument('--run-dir',type=Path,required=True);a,_=p.parse_known_args()
        os.environ['SHARDS_REZ_TRAINING']='1'
        runtime.install(statistics_directory=a.run_dir/'training-statistics')
        runtime.train_campaign.main()
    elif mode=='supervise':
        p=argparse.ArgumentParser();p.add_argument('--block',type=int,required=True);a=p.parse_args()
        from supervise_training import supervise
        folder=runtime.CAMPAIGN/f'block-{a.block:03d}'
        command=[sys.executable,str(Path(__file__).resolve()),'train','--run-dir',str(folder/'train'),
            '--ledger',str(folder/'budget.json'),'--seconds','600','--config',str(folder/'config.json'),
            '--resume',str(folder/'initial/latest.soicp'),'--label',f'Rez repair block {a.block}: 10 minutes']
        raise SystemExit(supervise(command,folder/'train',folder/'budget.json'))
    else: raise SystemExit('Expected prepare/train/supervise')
