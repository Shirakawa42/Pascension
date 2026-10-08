"""Run the explicitly authorized twenty-minute balance-patch continuation."""
import argparse
import json
from pathlib import Path
import sys
import balance_patch_runtime as runtime

if __name__=='__main__':
    mode=sys.argv.pop(1)
    if mode=='migrate':print(json.dumps(runtime.migrate()))
    elif mode=='train':
        parser=argparse.ArgumentParser();parser.add_argument('--run-dir',type=Path,required=True)
        args,_=parser.parse_known_args()
        runtime.install(statistics_directory=args.run_dir/'training-statistics')
        runtime.train_campaign.main()
    elif mode=='supervise':
        from supervise_training import supervise
        run=runtime.PATCH/'train'
        command=[sys.executable,str(Path(__file__).resolve()),'train','--run-dir',str(run),
                 '--ledger',str(runtime.PATCH/'budget.json'),'--seconds','1200',
                 '--config',str(runtime.PATCH/'config.json'),'--resume',str(runtime.PATCH/'initial/latest.soicp'),
                 '--label','September-27 balance adaptation · 20 minutes']
        raise SystemExit(supervise(command,run,runtime.PATCH/'budget.json'))
    elif mode=='evaluate':
        runtime.install()
        import evaluate_checkpoints as e,learning_model
        e.identity=runtime.variant_identity;e.LearningPolicy=learning_model.LearningPolicy
        raise SystemExit(e.main())
    else:raise SystemExit('Expected migrate/train/supervise/evaluate')
