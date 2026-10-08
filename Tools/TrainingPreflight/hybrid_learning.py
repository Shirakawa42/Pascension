"""Outcome calibration and conservative expert iteration from complete hybrid games.

No PPO: the searched action is not a sample from the network behavior policy.
Test output-head updates or constrained semantic-network updates. Card identity
embeddings stay frozen in semantic mode; the generic effect layout is unchanged.
Optional advantage weighting uses actual terminal returns, never invented PPO ratios.
Validation is split by whole game, and repeated visible positions across the split
are excluded from training. Strength evaluation uses an entirely separate seed range.
"""
import argparse
import hashlib
import json
import os
import shutil
import sys
from pathlib import Path
import time

import numpy as np
import torch
from gpu_search import load_policy
from search_distillation import write_native

WIDTH = 5575


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--data', type=Path, required=True)
    parser.add_argument('--policy', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--epochs', type=int, default=8)
    parser.add_argument('--value-scope', choices=['head','semantic'], default='head')
    parser.add_argument('--kinds', choices=['both','value','actor'], default='both')
    parser.add_argument('--actor-scope', choices=['heads','semantic'], default='heads')
    parser.add_argument('--advantage-temperature', type=float, default=0.)
    parser.add_argument('--advice-weight', type=float, default=.15)
    parser.add_argument('--round-discount', type=float, default=1.,
                        help='Value-only experiment: discount signed terminal outcomes per remaining full round; 1 preserves the original objective')
    args = parser.parse_args()
    if args.advantage_temperature and not .25 <= args.advantage_temperature <= 2: raise ValueError('Advantage temperature must be zero or .25..2')
    if not 0 < args.advice_weight <= 1: raise ValueError('Advice weight must be in (0,1]')
    if not .95 <= args.round_discount <= 1: raise ValueError('Round discount must be .95..1')
    if args.round_discount != 1 and args.kinds != 'value':
        raise ValueError('Discounted targets require a separate value-only experiment')
    if args.kinds=='both' and args.actor_scope=='semantic' and args.value_scope=='semantic':
        raise ValueError('Independently trained shared trunks cannot be combined; use separate value/actor experiments')
    if not 1 <= args.epochs <= 16:
        raise ValueError('Use a bounded 1..16 epoch experiment')
    available=sorted(os.sched_getaffinity(0)); physical={}
    for cpu in available:
        topology=Path(f'/sys/devices/system/cpu/cpu{cpu}/topology')
        key=((topology/'physical_package_id').read_text(), (topology/'core_id').read_text())
        physical.setdefault(key,cpu)
    cpus=list(physical.values())[:8]
    for thread in Path('/proc/self/task').iterdir():
        try: os.sched_setaffinity(int(thread.name),cpus)
        except ProcessLookupError: pass
    torch.set_num_threads(1)
    torch.manual_seed(927903)
    torch.backends.cuda.matmul.allow_tf32 = False
    args.output.mkdir(parents=True, exist_ok=False)
    manifest = json.loads((args.data.parent / 'manifest.json').read_text())
    games = json.loads((args.data.parent / 'games.json').read_text())
    if not manifest.get('completed') or games['games'] != games['total']:
        raise ValueError('Experience must contain complete, finalized games')
    if manifest['policy_sha256'] != hashlib.sha256(args.policy.read_bytes()).hexdigest():
        raise ValueError('Teacher policy does not match experience')
    data = np.memmap(args.data, mode='r', dtype='<f4').reshape(-1, WIDTH)
    if len(data) != games['experienceRows'] or not np.isfinite(data).all():
        raise ValueError('Invalid or incomplete experience')
    if len({r['seed'] for r in games['rows']}) != len(games['rows']):
        raise ValueError('Training games must have independent seeds')
    if not np.isin(data[:, 5574], [-1, 0, 1]).all():
        raise ValueError('Invalid terminal returns')
    ids = data[:, 5569].astype(int)
    if set(ids) != {r['index'] for r in games['rows']}:
        raise ValueError('Missing game experience')
    for r in games['rows']:
        ix = ids == r['index']
        expected = np.where(r['winner'] < 0, 0, np.where(data[ix, 5571] == r['winner'], 1, -1))
        if not np.array_equal(data[ix, 5574], expected):
            raise ValueError('Wrong actor perspective in terminal labels')
    masks = data[:, 5376:5440]
    targets = data[:, 5504:5568]
    if not np.isin(targets, [0, 1]).all() or not targets.any(1).all() or (targets > masks).any():
        raise ValueError('Illegal expert target')
    held = ids % 5 == 0
    # Exact public position identity: no private state, action labels, or outcomes.
    keys = [hashlib.sha256(row[:5440].tobytes()).digest() for row in data]
    held_keys = {key for key, is_held in zip(keys, held) if is_held}
    train = np.asarray([not is_held and key not in held_keys for key, is_held in zip(keys, held)])
    if len(set(ids[train])) < 100 or len(set(ids[held])) < 40:
        raise ValueError('Insufficient independent game coverage')
    tensor = torch.tensor(np.asarray(data), device='cuda')
    obs, candidates, mask = tensor[:, :3328], tensor[:, 3328:5376].reshape(-1, 64, 32), tensor[:, 5376:5440].bool()
    old, good, returns = tensor[:, 5440:5504], tensor[:, 5504:5568].bool(), tensor[:, 5574]
    value_targets = returns
    if args.round_discount != 1:
        final_round = {r['index']: r['rounds'] for r in games['rows']}
        remaining_rounds = np.asarray([final_round[index] for index in ids]) - data[:, 5573]
        if (remaining_rounds < 0).any() or not np.equal(remaining_rounds, np.floor(remaining_rounds)).all():
            raise ValueError('Invalid recorded distance to terminal round')
        # Existing label validation above checks actor perspective and draws.
        # Only the training target uses terminal metadata, never model inputs.
        factors = torch.tensor(np.power(args.round_discount, remaining_rounds), dtype=returns.dtype, device='cuda')
        value_targets = returns * factors
    advantage_weights = ((returns-tensor[:,5568])/args.advantage_temperature).exp().clamp(.1,5.) if args.advantage_temperature else torch.ones_like(returns)
    train_ix = torch.tensor(np.flatnonzero(train), device='cuda')
    held_ix = torch.tensor(np.flatnonzero(held), device='cuda')
    # Each completed game has equal total training weight, regardless of length.
    counts = np.bincount(ids[train], minlength=ids.max()+1)
    weights = torch.tensor(1 / counts[ids[train]], device='cuda')
    reference = load_policy(args.policy)
    source_root=Path(__file__).resolve().parent
    runtime_sources={}
    for module in tuple(sys.modules.values()):
        source=getattr(module,'__file__',None)
        if not source: continue
        source=Path(source).resolve()
        if source.suffix!='.py' or not source.is_relative_to(source_root): continue
        relative=source.relative_to(source_root)
        destination=args.output/'sources'/relative
        destination.parent.mkdir(parents=True,exist_ok=True)
        shutil.copy2(source,destination)
        runtime_sources[str(relative)]=hashlib.sha256(destination.read_bytes()).hexdigest()

    @torch.no_grad()
    def measure(policy, ix):
        sums = dict(value_brier=0., value_nll=0., expert_nll=0., advantage_nll=0., expert_top1=0., policy_kl=0.)
        all_values = []
        for part in ix.split(512):
            logits, value = policy(obs[part].contiguous(), candidates[part].contiguous(), mask[part].contiguous())
            logp = logits.log_softmax(-1)
            prob = ((value+1)/2).clamp(1e-7, 1-1e-7)
            y = (returns[part]+1)/2
            terms = dict(value_brier=(prob-y).square(),
                         value_nll=-(y*prob.log()+(1-y)*torch.log1p(-prob)),
                         expert_nll=-logp.masked_fill(~good[part], -torch.inf).logsumexp(-1),
                         advantage_nll=-logp.masked_fill(~good[part], -torch.inf).logsumexp(-1)*advantage_weights[part],
                         expert_top1=good[part].gather(1, logits.argmax(-1, keepdim=True)).float().flatten(),
                         policy_kl=(old[part]*(old[part].clamp_min(1e-30).log()-logp)).sum(-1))
            for name, value_ in terms.items():
                sums[name] += value_.sum().item()
            all_values.append(value)
        values = torch.cat(all_values)
        probabilities = ((values+1)/2).clamp(1e-7, 1-1e-7)
        outcomes = (returns[ix]+1)/2
        brier = (probabilities-outcomes).square()
        nll = -(outcomes*probabilities.log()+(1-outcomes)*torch.log1p(-probabilities))
        target_mse = (values-value_targets[ix]).square()
        _, game_inverse, game_counts = torch.unique(tensor[ix,5569].long(), return_inverse=True, return_counts=True)
        game_weights = 1/game_counts[game_inverse].float()
        groups = {}
        def group(name, selected):
            count = int(selected.sum().item())
            if count:
                groups[name] = dict(rows=count, value_brier=brier[selected].mean().item(),
                    value_nll=nll[selected].mean().item(), predicted_win=probabilities[selected].mean().item(),
                    observed_win=outcomes[selected].mean().item())
        for hero in range(5): group(f'hero_{hero}', tensor[ix,5572]==hero)
        for seat in range(2): group(f'seat_{seat}', tensor[ix,5571]==seat)
        group('rounds_1_4', tensor[ix,5573]<=4)
        group('rounds_5_8', (tensor[ix,5573]>4)&(tensor[ix,5573]<=8))
        group('rounds_9_plus', tensor[ix,5573]>8)
        group('own_main', (obs[ix,1]==1)&(obs[ix,4]==0))
        group('own_menu', (obs[ix,1]==1)&(obs[ix,4]!=0))
        group('other_turn', obs[ix,1]==0)
        return {key: value/len(ix) for key, value in sums.items()} | {
            'saturated_fraction': (values.abs() > .98).float().mean().item(),
            'value_game_brier': (brier*game_weights).sum().item()/len(game_counts),
            'value_game_nll': (nll*game_weights).sum().item()/len(game_counts),
            'value_target_mse': target_mse.mean().item(),
            'value_target_game_mse': (target_mse*game_weights).sum().item()/len(game_counts),
            'calibration': groups}

    report = dict(schema='hybrid-expert-iteration-v1', source_policy_sha256=manifest['policy_sha256'],
                  data_sha256=hashlib.sha256(args.data.read_bytes()).hexdigest(),
                  runtime_sources_sha256=runtime_sources,
                  training_source_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),torch=torch.__version__,
                  device=torch.cuda.get_device_name(),
                  games=len(set(ids)), train_games=len(set(ids[train])), held_games=len(set(ids[held])),
                  rows=len(data), train_rows=int(train.sum()), held_rows=int(held.sum()),
                  heroes={str(i): int(((data[:, 5572] == i)&train).sum()) for i in range(5)},
                  advantage_temperature=args.advantage_temperature,
                  round_discount=args.round_discount,
                  value_semantics='signed win outcome' if args.round_discount==1 else
                      'signed outcome discounted by remaining full rounds; mapped values are utilities, not calibrated win probabilities',
                  actor_scope=args.actor_scope, value_scope=args.value_scope, kinds=args.kinds, cpu_affinity=cpus, advice_weight=args.advice_weight,
                  objective=('Terminal-outcome value calibration; group-aware search imitation + reference KL; no PPO'
                             if args.round_discount==1 else 'Discounted signed-outcome value fit with policy KL constraint; no PPO'),
                  baseline=measure(reference, held_ix), candidates=[], deployed=False)
    with torch.no_grad():
        logits, value = reference(obs[:64].contiguous(), candidates[:64].contiguous(), mask[:64].contiguous())
        error = max((logits.softmax(-1)-old[:64]).abs().max().item(), (value-tensor[:64, 5568]).abs().max().item())
        if error > 1e-4:
            raise ValueError(f'Collection / training model mismatch: {error}')
        report['parity_error'] = error
    def save_report():
        (args.output/'report.json').write_text(json.dumps(report, indent=2)+'\n')
    save_report()
    saved = {}
    for kind in (('value', 'actor') if args.kinds=='both' else (args.kinds,)):
        policy = load_policy(args.policy).train()
        prefixes = ('core.value.',) if kind == 'value' else ('core.query.', 'core.candidate_bias.', 'core.volos_head.', 'core.decision_head.')
        for name, parameter in policy.named_parameters():
            if kind=='value' and args.value_scope=='semantic':
                parameter.requires_grad_(name.startswith(('core.value.', 'core.trunk.', 'core.information.', 'core.menu_information.', 'core.effect_state.', 'core.effect_encoder.')))
                continue
            parameter.requires_grad_((name.startswith('core.') and not name.startswith(('core.value.','core.card_embedding.'))) if kind=='actor' and args.actor_scope=='semantic' else name.startswith(prefixes))
        train_names = {name for name,p in policy.named_parameters() if p.requires_grad}
        parameters = [p for p in policy.parameters() if p.requires_grad]
        optimizer = torch.optim.AdamW(parameters, lr=3e-4 if kind == 'value' and args.value_scope=='head' else 1e-5, weight_decay=0.)
        raw_value = []
        handle = policy.core.value.register_forward_hook(lambda module, inputs, output: raw_value.append(output))
        best = float('inf')
        start = time.monotonic()
        for epoch in range(1, args.epochs+1):
            # Weighted draws are independent of rollout action random streams.
            choices = train_ix[torch.multinomial(weights, len(train_ix), replacement=True)]
            for part in choices.split(512):
                optimizer.zero_grad(set_to_none=True)
                raw_value.clear()
                logits, predicted_value = policy(obs[part].contiguous(), candidates[part].contiguous(), mask[part].contiguous())
                if kind == 'value':
                    # BCE on unsquashed value avoids vanishing tanh gradients at
                    # wrong near-certain predictions. sigmoid(2*z)=(tanh(z)+1)/2.
                    loss = torch.nn.functional.binary_cross_entropy_with_logits(2*raw_value[-1].flatten(), (value_targets[part]+1)/2)
                    if args.value_scope=='semantic':
                        logp=logits.log_softmax(-1)
                        loss=loss+(old[part]*(old[part].clamp_min(1e-30).log()-logp)).sum(-1).mean()
                else:
                    logp = logits.log_softmax(-1)
                    advice = (-logp.masked_fill(~good[part], -torch.inf).logsumexp(-1)*advantage_weights[part]).mean()
                    kl = (old[part]*(old[part].clamp_min(1e-30).log()-logp)).sum(-1).mean()
                    loss = args.advice_weight*advice + kl + .5*(predicted_value-tensor[part,5568]).square().mean()
                if not torch.isfinite(loss):
                    raise RuntimeError('Nonfinite hybrid learning loss')
                loss.backward()
                norm = torch.nn.utils.clip_grad_norm_(parameters, 1.)
                if not torch.isfinite(norm):
                    raise RuntimeError('Nonfinite hybrid gradient')
                optimizer.step()
            raw_value.clear()
            metrics = measure(policy, held_ix)
            raw_value.clear()
            key = metrics['value_brier'] if kind == 'value' else metrics['advantage_nll'] if args.advantage_temperature else metrics['expert_nll']
            if kind == 'value' and args.round_discount != 1:
                key = metrics['value_target_game_mse']
            checkpoint = dict(kind=kind, epoch=epoch, seconds=time.monotonic()-start, metrics=metrics)
            if key < best and metrics['policy_kl'] <= .04:
                best = key
                path = args.output/f'{kind}-epoch-{epoch:02d}.bytes'
                write_native(policy, path)
                (args.output/f'{kind}-best.bytes').write_bytes(path.read_bytes())
                checkpoint['sha256'] = hashlib.sha256(path.read_bytes()).hexdigest()
                saved[kind] = {k: v.detach().clone() for k, v in policy.state_dict().items() if k in train_names}
                checkpoint['selected'] = True
                checkpoint['policy'] = str(path)
            report['candidates'].append(checkpoint)
            save_report()
            print(json.dumps(checkpoint), flush=True)
        handle.remove()
    combined = load_policy(args.policy)
    state = combined.state_dict()
    for changes in saved.values():
        state.update(changes)
    combined.load_state_dict(state)
    write_native(combined, args.output/'combined.bytes')
    report['combined'] = measure(combined, held_ix)
    save_report()


if __name__ == '__main__':
    main()
