"""Bounded diagnostic, independent of stopped campaigns: same opponent without search."""
import argparse, json, os, sys, time
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT/'Tools/ZeroDepthTraining'), str(ROOT/'Tools/MatchupBenchmark')]
import numpy as np
import torch
from host import Host
from league import load_frozen_policy, heroes_for_seed
from model import Actor
from catalog_check import compatible_catalog
from cpu_affinity import select_cpus
from run import SEED, matchup, HEROES

def run(work, source, games=160, balanced=False):
    work.mkdir(parents=True,exist_ok=True)
    os.sched_setaffinity(0, select_cpus(os.sched_getaffinity(0), 6))
    os.environ['SHARDS_DIAG_NO_SEARCH'] = '1'
    os.environ['SHARDS_DIAG_BALANCED_EVAL'] = '1' if balanced else '0'
    torch.set_num_threads(1)
    torch.backends.fp32_precision = 'ieee'
    torch.backends.cuda.matmul.fp32_precision = 'ieee'
    assert games > 0 and games % 32 == 0
    policy, meta = load_frozen_policy(source/'learner', device='cuda')
    actor = Actor(policy, 32, graph=True, packed=True)
    torch.manual_seed(6102026)
    binary = ROOT/'Tools/TrainingDiagnosis/Host/bin/Release/net8.0/TrainingDiagnosisHost.dll'
    start = time.monotonic()
    outcomes, traces, retained = [], [], []
    reservoir = np.random.default_rng(113)
    observed = 0
    def pairing(seed,seat):
        if not balanced:return matchup(seed)
        heroes=heroes_for_seed(seed)
        return heroes[seat],heroes[1-seat]
    for first in range(0, games//2, 16):
        with Host(binary=binary, batch=32, workers=6, seed=SEED+first,
                  opponent_bundle=source/'incumbent', paired=True, timeout=90) as host:
            compatible_catalog(policy.catalog, host.catalog)
            seen = np.zeros(32, bool)
            local = []
            while not seen.all():
                if time.monotonic()-start > 300:
                    raise TimeoutError('Bounded diagnostic exceeded five minutes')
                for lane in np.flatnonzero((host.done != 0) & ~seen):
                    seat = int(lane)%2
                    result = dict(seed=SEED+first+int(lane)//2, learner_seat=seat,
                                  reward=float(host.rewards[lane,seat]), censored=bool(host.done[lane] == 2),
                                  learner_hero=pairing(SEED+first+int(lane)//2,seat)[0])
                    outcomes.append(result)
                    seen[lane] = True
                if seen.all(): break
                live = np.flatnonzero(~seen)
                assert np.all(host.actors[live] == live%2)
                for lane in live:
                    actual = (HEROES[int(round(float(host.obs[lane,22])*5))-1],
                              HEROES[int(round(float(host.obs[lane,86])*5))-1])
                    assert actual == pairing(SEED+first+int(lane)//2,int(lane)%2)
                actions, packet = actor.act(host)
                for lane in live:
                    row = dict(seed=SEED+first+int(lane)//2, seat=int(lane)%2,
                               value=float(packet[lane,2]), logp=float(packet[lane,1]),
                               context=int(round(float(host.obs[lane,157])*26)) if host.obs[lane,144] else 0,
                               kind=int(host.candidates[lane,actions[lane],:16].argmax()),
                               hero=int(round(float(host.obs[lane,22])*5))-1,
                               round=int(round(float(host.obs[lane,2])*100)),
                               legal=int(host.mask[lane].sum()))
                    local.append(row)
                    observed += 1
                    slot = observed-1 if observed <= 2048 else int(reservoir.integers(observed))
                    if slot < 2048:
                        sample = (host.obs[lane].copy(), host.candidates[lane].copy(), host.mask[lane].copy())
                        if slot == len(retained): retained.append(sample)
                        else: retained[slot] = sample
                actions[seen] = -1
                host.advance(actions)
            utility = {int(i): float(host.rewards[i,int(i)%2]) for i in range(32)}
            for row in local:
                lane = (row['seed']-SEED-first)*2+row['seat']
                row['utility'] = utility[lane]
            traces.extend(local)
        (work/'search-ablation-progress.json').write_text(json.dumps(dict(completed=len(outcomes), planned=games)))
    assert not any(r['censored'] for r in outcomes)
    old = [json.loads(x) for x in (source/'games.jsonl').read_text().splitlines()
           if json.loads(x)['seed'] < SEED+games//2]
    old_score = None if balanced else sum((r['winner']==r['learner_seat']) + .5*(r['winner']==-1) for r in old)/len(old)
    report = dict(schema='shards-search-ablation-v1', planned_games=games, completed=len(outcomes),
                  learner_checkpoint_games=meta['training']['games'], learner_sha256=meta['policy_file_sha256'],
                  opponent_policy_unchanged=True, opponent_search=False, learner_search=False,
                  score=sum((r['reward']+1)/2 for r in outcomes)/len(outcomes),
                  same_seed_reference_search_score=old_score, reference_games=0 if balanced else len(old),
                  balanced_all_heroes=balanced,
                  heroes={hero:dict(games=sum(r['learner_hero']==hero for r in outcomes),
                      wins=sum(r['learner_hero']==hero and r['reward']==1 for r in outcomes)) for hero in HEROES},
                  elapsed_seconds=time.monotonic()-start, outcomes=outcomes,
                  note='Exploratory search ablation, not a promotion test; paths and random-number consumption differ.')
    (work/'search-ablation.json').write_text(json.dumps(report, indent=2))
    (work/'decision-traces.jsonl').write_text(''.join(json.dumps(r)+'\n' for r in traces))
    np.savez_compressed(work/'unbiased-public-corpus.npz', **{k:np.stack([r[i] for r in retained])
                         for i,k in enumerate(('obs','candidates','mask'))})
    print(json.dumps({k:v for k,v in report.items() if k!='outcomes'}), flush=True)

if __name__ == '__main__':
    p=argparse.ArgumentParser();p.add_argument('--work',type=Path,required=True)
    p.add_argument('--source',type=Path,required=True);p.add_argument('--games',type=int,default=160)
    p.add_argument('--balanced',action='store_true')
    a=p.parse_args();run(a.work,a.source,a.games,a.balanced)
