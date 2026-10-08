"""CPU-only controlled representation diagnostics; no campaign update/checkpoint write.

CE labels are artificial and repeated: this measures optimizability, never game
strength. Read-only actual gameplay supplies separate calibration measurements.
"""
import argparse
import copy
import json
from pathlib import Path
from unittest.mock import patch
from collections import defaultdict
import sys
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import cpu_shadow_eval as shadow
import torch
import numpy as np
from bench_common import save_json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--checkpoint', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--games', type=int, default=64)
    args = parser.parse_args()
    if args.output.exists():
        raise ValueError('Output exists')
    shadow.configure_cpu()
    shadow.configure_variant('v7')
    selection, _ = shadow.frozen_selections(args.checkpoint, 'learner', args.checkpoint, 'learner')
    original = shadow.checkpoints.materialize_policy(selection, 'cpu')
    original_hash = shadow.checkpoints.policy_hash(original)
    result = {'schema': 'shards-learning-signal-diagnosis-v1', 'policy': selection.metadata,
        'scope': 'CPU supervised repeated-fixture trainability diagnostic plus separate frozen gameplay calibration. Artificial CE targets are not game outcome labels and no resulting weights are saved or deployed.',
        'campaign_optimizer_updates': 0, 'cuda_initialized': False}
    fixture = Path(__file__).resolve().parents[1]/'results/strategy-reactor-source-inputs.json'
    cases = json.loads(fixture.read_text())['cases']
    flat = torch.tensor([case['flat'] for case in cases], dtype=torch.float32)
    obs, candidates, mask = flat[:, :2048], flat[:, 2048:4096].reshape(-1, 64, 32), flat[:, 4096:].bool()
    assert torch.equal(flat[0], flat[1]), 'The red alias fixture must remain identical'
    result['identical_information_alias'] = {'input_max_abs_difference': float((flat[0]-flat[1]).abs().max()),
        'balanced_contradictory_target_CE_lower_bound': float(np.log(2)),
        'balanced_contradictory_target_accuracy_upper_bound': .5,
        'note': 'Different desired labels are a structural capacity test, not a claim about optimal physical Reactor behavior in this constructed position.'}
    base = copy.deepcopy(original).requires_grad_(True)
    transformed_obs, transformed_candidates = base.transform_inputs(obs[:1], candidates[:1])
    logits, values = base(obs[:1], candidates[:1], mask[:1])
    contrast = logits[0, 1]-logits[0, 0]
    contrast.backward()
    result['mode_contrast'] = {'candidate_different_columns': torch.where(candidates[0, 0] != candidates[0, 1])[0].tolist(),
        'raw_candidate_delta': (candidates[0, 1]-candidates[0, 0]).tolist(),
        'transformed_candidate_delta_norm': float(torch.linalg.vector_norm(transformed_candidates[0, 1]-transformed_candidates[0, 0])),
        'initial_logit_margin_mode1_minus_mode0': float(contrast.detach()),
        'per_parameter_contrast_gradient_norm': {name: None if param.grad is None else float(param.grad.norm()) for name, param in base.named_parameters()}}
    fits = []
    for variant in ('existing_full_policy', 'zero_residual_categorical_head_frozen_trunk'):
        policy = copy.deepcopy(original).requires_grad_(variant == 'existing_full_policy')
        with torch.no_grad():
            state = policy.core.trunk(policy.transform_inputs(obs[:1], candidates[:1])[0]).detach()
            frozen_logits = policy(obs[:1], candidates[:1], mask[:1])[0][:, :2].detach()
        head = torch.nn.Linear(state.shape[1], 2)
        torch.nn.init.zeros_(head.weight); torch.nn.init.zeros_(head.bias)
        optimizer = torch.optim.AdamW(policy.parameters() if variant == 'existing_full_policy' else head.parameters(), lr=3e-4, weight_decay=0)
        records = []
        first95 = None
        for step in range(501):
            output = policy(obs[:1], candidates[:1], mask[:1])[0][:, :2] if variant == 'existing_full_policy' else frozen_logits + head(state)
            loss = torch.nn.functional.cross_entropy(output, torch.ones(1, dtype=torch.long))
            probability = float(output.softmax(-1)[0, 1].detach())
            if first95 is None and probability >= .95:
                first95 = step
            if step in (0, 1, 2, 5, 10, 20, 50, 100, 200, 500):
                records.append({'step': step, 'desired_probability': probability, 'CE_loss': float(loss.detach())})
            if step < 500:
                optimizer.zero_grad()
                loss.backward()
                norm = torch.nn.utils.clip_grad_norm_(policy.parameters() if variant == 'existing_full_policy' else head.parameters(), .5)
                optimizer.step()
        fits.append({'variant': variant, 'steps_to_95_percent': first95, 'records': records})
    result['best_case_repeated_fixture_trainability'] = fits
    save_json(args.output, result)

    pending = defaultdict(list)
    resolved = []
    captured = []
    base_advance = shadow.ObservedHost.advance_active
    def observe(host, actions, active):
        for lane in np.flatnonzero(active):
            seat = int(host.seats[lane]); role = int(seat != host.seat_a)
            packet, _ = host.packets[role]
            value = float(packet[lane, 2]); ob = host.obs[lane]
            legal = np.flatnonzero(host.mask[lane] == 1)
            kind = int(host.candidates[lane, actions[lane], :16].argmax())
            key = (host.seed, host.seat_a, int(lane))
            pending[key].append((seat, value, int(round(float(ob[2])*100)), kind, len(legal)))
            if len(captured) < 256 and len(legal) > 1 and len(pending[key]) % 19 == 0:
                captured.append((ob.copy(), host.candidates[lane].copy(), host.mask[lane].copy()))
        result_value = base_advance(host, actions, active)
        for lane in np.flatnonzero(active & (host.done != 0)):
            key = (host.seed, host.seat_a, int(lane))
            for seat, value, turn, kind, choices in pending.pop(key):
                if host.done[lane] == 1:
                    resolved.append((value, float(host.rewards[lane, seat]), turn, kind, choices))
        return result_value
    with patch.object(shadow.ObservedHost, 'advance_active', observe):
        result['frozen_match'] = shadow.run_cpu_match(original, original, games=args.games,
            seed=0x7C00000000000000, batch=min(32, args.games//2), sampling_seed=320928,
            max_seconds=240, step_delay_ms=3, telemetry=False)
    rows = np.asarray(resolved)
    def calibration(data):
        if not len(data): return {'count': 0}
        v, y = data[:, 0], data[:, 1]
        return {'count': len(data), 'mean_value': float(v.mean()), 'mean_outcome': float(y.mean()),
            'mse': float(((v-y)**2).mean()), 'constant_zero_mse': float((y*y).mean()),
            'abs_value_over_95_fraction': float((abs(v) > .95).mean()),
            'abs_value_over_99_fraction': float((abs(v) > .99).mean()),
            'mean_tanh_derivative': float((1-v*v).mean()),
            'wrong_sign_saturated_95_fraction': float(((abs(v) > .95) & (v*y < 0)).mean())}
    result['value_calibration'] = {'all_decisions': calibration(rows),
        'round_1_to_5': calibration(rows[rows[:, 2] <= 5]),
        'round_6_to_10': calibration(rows[(rows[:, 2] > 5) & (rows[:, 2] <= 10)]),
        'round_11_plus': calibration(rows[rows[:, 2] > 10]),
        'bins': [dict(calibration(rows[(rows[:, 0] >= lo) & (rows[:, 0] < hi)]), low=lo, high=hi)
                 for lo, hi in zip((-1.01, -.8, -.4, 0, .4, .8), (-.8, -.4, 0, .4, .8, 1.01))],
        'scope': 'Acting decisions only; outcome resolved by the acting seat. Same frozen policy both sides, natural hero choices, decision-weighted correlated rows. Not independent confidence samples.'}
    # Pooled card candidates absent from current value path: this is a deterministic differential proof.
    if captured:
        co = torch.tensor(np.stack([x[0] for x in captured])); cc = torch.tensor(np.stack([x[1] for x in captured])); cm = torch.tensor(np.stack([x[2] for x in captured])).bool()
        with torch.no_grad():
            a = original(co, cc, cm)[1]
            altered = cc.clone(); altered[..., 16] = 0
            b = original(co, altered, cm)[1]
        result['value_ignores_candidate_card_identity'] = {'sampled_public_rows': len(captured), 'max_abs_value_delta_after_zeroing_candidate_card_codes': float((a-b).abs().max()), 'scope': 'Artificial differential of value dependency only; altered candidate records need not describe legal game positions.'}
    assert not torch.cuda.is_initialized()
    assert shadow.checkpoints.policy_hash(original) == original_hash
    result['complete'] = result['frozen_match']['complete']
    save_json(args.output, result)
    print(json.dumps({'complete': result['complete'], 'trainability': fits, 'value_calibration': result['value_calibration']}, indent=2))

if __name__ == '__main__': main()
