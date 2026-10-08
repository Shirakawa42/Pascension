"""Controlled frozen final learner versus the retained random initial policy."""
import sys,json
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import cpu_shadow_eval as shadow
import variant_v12_runtime as runtime
import learning_eval,learning_model
from bench_common import save_json
shadow.configure_cpu();runtime.install();shadow._HOST=learning_eval.LearningHost
shadow.checkpoints.identity=runtime.variant_identity;shadow.checkpoints.LearningPolicy=learning_model.LearningPolicy
checkpoint=Path('/home/lva/.local/share/shards-training/2026-09-26/overnight-v12/retained/segment-0029/latest.soicp')
a,b=shadow.frozen_selections(checkpoint,'learner',checkpoint,'initial')
policies=[shadow.checkpoints.materialize_policy(s,'cpu') for s in (a,b)]
r=shadow.run_cpu_match(*policies,games=256,batch=32,seed=0xC300000000000000,sampling_seed=270928,telemetry=False,max_seconds=150)
r.update(schema='shards-archive-hero-confounding-v12',policy_a=a.metadata,policy_b=b.metadata,scope='Diagnostic final learner vs retained initial policy; natural drafts, not equal-strength hero balance')
save_json('Tools/TrainingPreflight/results/v12-archive-hero-confounding-2026-09-27.json',r)
print(json.dumps({k:r[k] for k in ('complete','games','score_a','censored_games','seconds')}));print(json.dumps(r['shadow_diagnostics']['hero_seat_summary']))
