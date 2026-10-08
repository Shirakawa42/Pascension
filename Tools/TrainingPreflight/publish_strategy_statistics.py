"""Publish complete strategy evidence; never combine distinct evaluation cohorts."""
import argparse
import copy
import hashlib
import json
from pathlib import Path
import time
from strategy_statistics import build
from balance_analysis import build as build_balance
from bench_common import save_json

def publish(campaign, evaluation, new_cohort=False):
    manifest=json.loads((evaluation/'manifest.json').read_text())
    original=None if new_cohort else json.loads((campaign/'final-50000/statistics.json').read_text())
    replay=json.loads((evaluation/'statistics.json').read_text())
    if manifest['state']!='completed' or manifest['completed_games']!=50000 or not manifest['frozen_weights_unchanged']:
        raise ValueError('A complete frozen 50,000-game evaluation is required')
    if original is not None and manifest['policy_sha256']!=original['scope']['policy_sha256']:
        raise ValueError('Strategy capture used a different policy')
    if replay['scope']['policy_sha256']!=manifest['policy_sha256']:raise ValueError('Snapshot policy does not match manifest')
    metadata=replay.get('card_catalog') or json.loads((Path(__file__).parent/'results/balance-card-catalog-v10.json').read_text())
    strategies=build(evaluation/'raw/forced-random/strategy-games.jsonl',replay,metadata)
    # If a replay differs, use that entire new evaluation; never splice its strategy
    # counts into a different cohort's card/hero totals.
    keys=['totals','rankings','hero_seats','hero_matchups','hero_choice_rows','final_round_histogram','final_state_sums']
    changed=[key for key in keys if original[key]!=replay[key]] if original is not None else []
    replay_exact=not changed if original is not None else None
    snapshot=copy.deepcopy(replay if original is None or changed else original)
    snapshot['strategies']=strategies
    snapshot['balance_analysis']=build_balance(evaluation/'raw/forced-random/strategy-games.jsonl',replay)
    snapshot['strategy_provenance']=dict(evaluation_directory=str(evaluation),
        original_aggregate_replay_exact=replay_exact,capture_kind="new_cohort" if new_cohort else "replay",aggregate_differences=changed,
        policy_sha256=manifest['policy_sha256'],host_sha256=manifest['host_sha256'],
        games=50000,optimizer_updates=0)
    snapshot['snapshot_id']=hashlib.sha256((snapshot['snapshot_id']+strategies['trace_sha256']+
        json.dumps(snapshot['balance_analysis'],sort_keys=True,separators=(',',':'))).encode()).hexdigest()[:16]
    snapshot['updated_wall']=time.time()
    snapshot['scope']['notes'].append('Joint strategy records captured from complete games; normal picks are separated from reward destinies.')
    save_json(evaluation/'statistics-enriched.json',snapshot)
    save_json(campaign/'balance-statistics-final.json',snapshot)
    save_json(campaign/'final-statistics-mode.json',dict(enabled=True,evaluation_directory=str(evaluation),target_games=50000))
    report=dict(games=50000,original_aggregate_replay_exact=replay_exact,capture_kind="new_cohort" if new_cohort else "replay",aggregate_differences=changed,
                strategy_rows=len(strategies['rows']),victory=snapshot['balance_analysis']['victory'],trace_sha256=strategies['trace_sha256'],snapshot_id=snapshot['snapshot_id'])
    save_json(evaluation/'strategy-publication.json',report)
    return report

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--campaign',type=Path,required=True);p.add_argument('--evaluation',type=Path,required=True);p.add_argument('--new-cohort',action='store_true')
    a=p.parse_args();print(json.dumps(publish(a.campaign,a.evaluation,a.new_cohort)))
