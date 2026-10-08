"""Publish only one frozen, both-seat lookahead cohort to the existing card statistics UI."""
import hashlib
import json
from pathlib import Path
from balance_host_statistics import combine, delta
from bench_common import save_json


def publish(directory, campaign=None, final=False):
    directory=Path(directory)
    manifest=json.loads((directory/'manifest.json').read_text())
    args=manifest['args'];target=args['games'];capped=manifest.get('user_cap')
    if args['mode']!='statistics' or manifest['training_updates']!=0:
        raise ValueError('Only isolated frozen both-seat statistics may be published')
    files=list((directory/'raw/forced-random').glob('session-*.json'))
    if any(p.name.endswith('.error.json') for p in files):raise RuntimeError('Statistics collector failed')
    if not files:return
    if len(files)!=1:raise ValueError('Mixed statistics sessions')
    raw=json.loads(files[0].read_text())
    if raw['purpose']!='final_evaluation' or raw['host_binary_sha256']!=manifest['host_sha256']:
        raise ValueError('Statistics provenance mismatch')
    if raw['totals']['censored_games'] or raw['totals']['unfinished_discarded_games']:
        raise ValueError('Incomplete games invalidate statistics')
    count=raw['totals']['completed_games']
    previous=directory/'statistics.json'
    if not final and previous.exists() and json.loads(previous.read_text())['totals']['resolved_games']==count:return
    if count==0:return
    metadata=json.loads((directory/'card-catalog.json').read_text())
    run_id=directory.name
    window=delta(raw);window['run_id']=run_id
    snapshot=combine([window],metadata,run_id=run_id,target_games=target)
    snapshot['card_catalog']=metadata
    snapshot['scope'].update(label=f'Frozen lookahead evaluation · {target:,} new games',
        policy_label=(args.get('policy_label') or 'Frozen policy '+manifest['policy_sha256'][:12])+' · '+('hybrid turn planning' if args.get('hybrid') else 'all-turn lookahead'),policy_versions=[],
        opponent_label='Identical frozen AI and search on both seats',
        inference=f"GPU policy/value · {args['candidates']} candidates · depth {args['depth']}{' (up to 64 to finish a turn)' if args.get('hybrid_finish_turn') else ''} · {args['worlds']} worlds · {args['terminal_nodes']} finishing nodes",
        final_evaluation=True,policy_sha256=manifest['policy_sha256'],
        confidence_method='Descriptive 95% game-cluster bounds; acquisitions are associations, not causal card strength',
        notes=['Only this new evaluation is included; no training, benchmarks or previous cohorts.',
               'Unique engine seeds; randomly ordered, exactly balanced distinct-hero pairs at completion.',
               'Both players use lookahead from the opening turn. No Unity or artificial delays.',
               'Hero/card rankings describe this bounded-search AI; they do not establish optimal balance.'])
    if capped:
        snapshot['scope']['notes'][1]='Random heroes from the original shuffled schedule; early stopping leaves unequal observed hero-pair counts.'
        original_target = capped.get('original_target_games') if isinstance(capped, dict) else None
        original_note = f' Finalized before the original {original_target:,}-game target.' if original_target else ''
        snapshot['scope']['notes'].append('User-capped cohort: first '+str(target)+' completed games. Extra completed and in-flight games are excluded.'+original_note)
    snapshot['refresh'].update(evaluation_sample_count=count,snapshot_training_games=0,total_games=count)
    snapshot['evaluation_progress']=dict(completed=count,target=target,state='completed' if final else 'running')
    if final:
        if (not raw['final'] and not capped) or count!=target:raise ValueError('Final cohort is incomplete')
        if not capped and (len(raw['matchup_rows'])!=20 or any(r['games']!=target//20 for r in raw['matchup_rows'])):raise ValueError('Hero pairs not balanced')
        if not capped and (len(raw['hero_seat_rows'])!=10 or any(r['games']!=target//5 for r in raw['hero_seat_rows'])):raise ValueError('Hero seats not balanced')
        games=json.loads((directory/'games.json').read_text())
        rows=games['rows'];seeds={r['seed'] for r in rows}
        if len(rows)!=target or len(seeds)!=target:raise ValueError('Duplicate or missing evaluation game')
        if len({r['index'] for r in rows})!=target or (not capped and {r['index'] for r in rows}!=set(range(target))):raise ValueError('Missing game index')
        for seat in range(2):
            if sum(r['winner']==seat for r in rows)!=snapshot['totals'][f'seat{seat}_wins']:raise ValueError('Outcome telemetry mismatch')
        from strategy_statistics import build as strategies
        from balance_analysis import build as balance
        trace=directory/'raw/forced-random/strategy-games.jsonl'
        snapshot['strategies']=strategies(trace,snapshot,metadata)
        snapshot['balance_analysis']=balance(trace,snapshot)
        snapshot['strategy_provenance']=dict(evaluation_directory=str(directory),capture_kind='new_cohort',
            policy_sha256=manifest['policy_sha256'],host_sha256=manifest['host_sha256'],games=count,optimizer_updates=0)
        snapshot['snapshot_id']=hashlib.sha256((snapshot['snapshot_id']+snapshot['strategies']['trace_sha256']).encode()).hexdigest()[:16]
    save_json(previous,snapshot)
    if campaign:
        campaign=Path(campaign)
        save_json(campaign/'balance-statistics-final.json',snapshot)
        save_json(campaign/'final-statistics-mode.json',dict(enabled=True,evaluation_directory=str(directory),target_games=target))
    return snapshot
