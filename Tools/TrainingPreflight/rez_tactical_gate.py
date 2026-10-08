"""Fixed held-out tactical acceptance criteria; no training or action overrides."""
import argparse
import json
from collections import defaultdict
from pathlib import Path

def assess(source):
    data=json.loads(Path(source).read_text());groups=defaultdict(list)
    for row in data['samples']:groups[row['group']].append(row)
    results={}
    for name,rows in groups.items():
        # Scry is a sequence of picks. Selecting a different card first is not
        # a failure if the monster is still buried before the decision commits.
        mean=sum(r.get('completion_probability') if r.get('completion_probability') is not None else r['probability'] for r in rows)/len(rows)
        correct=sum(r.get('greedy_completion_correct') if r.get('greedy_completion_correct') is not None else r['argmax_correct'] for r in rows)/len(rows)
        results[name]=dict(samples=len(rows),mean_probability=mean,argmax_accuracy=correct,
                          passed=mean>=.9 and correct>=.95)
    if data['verified_winning_lines']!=192 or len(groups)!=10:raise ValueError('Incomplete tactical battery')
    rollouts=defaultdict(list)
    for row in data.get('policy_rollouts',[]):rollouts[row['family']+('_sampled' if row['sampled'] else '_greedy')].append(row['won'])
    finishes={k:dict(wins=sum(v),games=len(v),passed=sum(v)/len(v)>=.95) for k,v in rollouts.items()}
    return dict(passed=all(r['passed'] for r in results.values()) and all(r['passed'] for r in finishes.values()),groups=results,whole_turns=finishes,
                criterion='Each group: >=90% mean target probability and >=95% top-choice accuracy',
                scope='Constructed held-out positions; preferences are not universal dominance rules')

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('source',type=Path);p.add_argument('--output',type=Path);a=p.parse_args()
    result=assess(a.source);body=json.dumps(result,indent=2)
    if a.output:a.output.write_text(body+'\n')
    print(body)
    raise SystemExit(0 if result['passed'] else 2)
