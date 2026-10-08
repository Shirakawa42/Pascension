"""Sensitivity analyses; observational and clustered by original game."""
import argparse
import json
import math
from pathlib import Path
import numpy as np
from cards_analysis import Analysis, bh


def multiple(a, columns, names):
    X = np.column_stack([a.base, columns])
    inverse = np.linalg.pinv(X.T @ X)
    beta = inverse @ X.T @ a.y
    residuals = a.y - X @ beta
    scores = np.zeros((len(a.games), X.shape[1]))
    np.add.at(scores, a.clusters, X * residuals[:, None])
    n, m, k = len(a.y), len(a.games), np.linalg.matrix_rank(X)
    cov = inverse @ (scores.T @ scores) @ inverse * m / (m - 1) * (n - 1) / (n - k)
    out = []
    start = a.base.shape[1]
    for index, name in enumerate(names):
        i = start + index
        effect, se = beta[i], math.sqrt(max(0, cov[i, i]))
        out.append(dict(id=name, adjusted=dict(delta_pp=effect * 100,
                    ci95_pp=[(effect - 1.96*se)*100, (effect + 1.96*se)*100],
                    p=math.erfc(abs(effect/se)/math.sqrt(2)))))
    bh(out)
    return sorted(out, key=lambda r: r['adjusted']['p'])


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--trace', type=Path, required=True)
    p.add_argument('--analysis', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    args = p.parse_args()
    games = [json.loads(l) for l in args.trace.read_text(encoding='utf-8-sig').splitlines()]
    previous = json.loads(args.analysis.read_text())
    a = Analysis(games, previous['catalog'])
    result = {}
    for window, family in [(1, 'first_round'), (2, 'by_second_round')]:
        names = [c['id'] for c in previous[family] if c['n'] >= 75]
        columns = np.array([[0 < row['first_acquired_round'].get(cid, 0) <= window for cid in names]
                            for _, _, row in a.rows])
        result['joint_'+family] = multiple(a, columns, names)
        if window == 1:
            early_columns = columns
    eligible = [row.get('destiny') is not None for _, _, row in a.rows]
    timing = [[float(min(row['destiny_round'], 10) == r) for r in range(2, 11)] for _, _, row in a.rows]
    extra = np.column_stack([timing, early_columns])
    result['destiny_with_round1_card_profile'] = []
    for destiny in previous['destinies']:
        cid = destiny['id']
        if destiny['n'] < 30:
            continue
        mask = [row.get('destiny') == cid for _, _, row in a.rows]
        result['destiny_with_round1_card_profile'].append(dict(id=cid, adjusted=a.fit(mask, eligible, extra)))
    bh(result['destiny_with_round1_card_profile'])
    result['card_mechanisms'] = []
    for cid in ['giga_source_adept', 'order_initiate_duel', 'shard_seer', 'shard_abstractor',
                'duplication_fabricator_duel', 'fao_cutul', 'furrowing_elemental_duel', 'the_dispossessed']:
        out = dict(id=cid, groups={})
        for present in [False, True]:
            rows = [(g, row) for _, g, row in a.rows
                    if (0 < row['first_acquired_round'].get(cid, 0) <= 2) == present]
            wins = [(g, row) for g, row in rows if g['winner'] == row['seat']]
            reached = [row['mastery10_round'] for _, row in rows if row['mastery10_round']]
            out['groups'][str(present)] = dict(n=len(rows), winrate=len(wins)/len(rows),
                mastery_victories=sum(g['victory']=='mastery' for g, _ in wins),
                normal_damage_victories=sum(g['victory']=='normal_damage' for g, _ in wins),
                reached_m10=len(reached), mean_m10_round_if_reached=float(np.mean(reached)),
                acquired_by_hero={h: dict(n=sum(row['hero']==h for _, row in rows),
                    wins=sum(row['hero']==h for _, row in wins))
                    for h in ['decima','kosynwu','rez','tetra','volos']})
        result['card_mechanisms'].append(out)
    args.output.write_text(json.dumps(result, indent=2)+'\n')
    print(args.output)


if __name__ == '__main__':
    main()
