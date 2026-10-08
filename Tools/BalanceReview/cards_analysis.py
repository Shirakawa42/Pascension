"""Observational card analysis of completed, paired-player strategy traces.

Does not run games. Adjustments are descriptive, never randomized causal estimates.
"""
from __future__ import annotations
import argparse
import collections
import itertools
import json
import math
from pathlib import Path
import numpy as np


def wilson(wins, n):
    if not n:
        return [None, None]
    p = wins / n
    d = 1 + 3.8416 / n
    mid = (p + 1.9208 / n) / d
    delta = 1.96 * math.sqrt(p * (1 - p) / n + .9604 / n**2) / d
    return [mid - delta, mid + delta]


def bh(rows):
    ordered = sorted((r['adjusted']['p'], i) for i, r in enumerate(rows)
                     if r.get('adjusted') and r['adjusted']['p'] is not None)
    q = 1.
    for rank in range(len(ordered), 0, -1):
        p, i = ordered[rank - 1]
        q = min(q, p * len(ordered) / rank)
        rows[i]['adjusted']['q_within_family'] = q


class Analysis:
    def __init__(self, games, catalog):
        self.games, self.catalog = games, catalog
        self.rows = [(i, g, p) for i, g in enumerate(games) for p in g['players']]
        self.y = np.array([float(g['winner'] == p['seat']) for _, g, p in self.rows])
        self.clusters = np.array([i for i, _, _ in self.rows])
        cells = [(p['hero'], g['players'][1 - p['seat']]['hero'], p['seat']) for _, g, p in self.rows]
        levels = sorted(set(cells))
        self.base = np.array([[float(c == level) for level in levels] for c in cells])

    def fit(self, values, subset=None, extra=None):
        mask = np.ones(len(self.rows), dtype=bool) if subset is None else np.array(subset, dtype=bool)
        v = np.array(values, dtype=float)[mask]
        if v.ndim == 1:
            v = v[:, None]
        if not len(v) or np.std(v[:, -1]) == 0:
            return None
        base = self.base[mask]
        base = base[:, np.any(base != 0, axis=0)]
        if extra is not None:
            base = np.column_stack([base, np.array(extra)[mask]])
        X = np.column_stack([base, v])
        y = self.y[mask]
        inv = np.linalg.pinv(X.T @ X)
        beta = inv @ X.T @ y
        residual = y - X @ beta
        ids = self.clusters[mask]
        unique, positions = np.unique(ids, return_inverse=True)
        cluster_scores = np.zeros((len(unique), X.shape[1]))
        np.add.at(cluster_scores, positions, X * residual[:, None])
        covariance = inv @ (cluster_scores.T @ cluster_scores) @ inv
        n, k, m = len(y), np.linalg.matrix_rank(X), len(unique)
        covariance *= (m / (m - 1)) * ((n - 1) / (n - k))
        se = math.sqrt(max(0, covariance[-1, -1]))
        effect = float(beta[-1])
        p = math.erfc(abs(effect / se) / math.sqrt(2)) if se else None
        return dict(delta_pp=effect * 100, ci95_pp=[(effect - 1.96 * se) * 100,
                    (effect + 1.96 * se) * 100], p=p, n=n, games=m)

    def describe(self, mask):
        ids = np.flatnonzero(mask)
        wins = sum(self.y[ids])
        return dict(n=len(ids), wins=int(wins), winrate=float(wins / len(ids)) if len(ids) else None,
                    ci95=wilson(wins, len(ids)))

    def card_family(self, kind, max_round=None):
        output = []
        for card in self.catalog.values():
            if card['type'] in ['Starter', 'Relic', 'Destiny', 'Ingeminex']:
                continue
            cid = card['id']
            if kind == 'fast_only_first_round':
                mask = [p['first_acquired_round'].get(cid) == 1 and
                        p['fast_acquired'].get(cid, 0) == p['acquired'].get(cid, -1)
                        for _, _, p in self.rows]
            elif kind == 'final_collection':
                mask = [p['collection'].get(cid, 0) > 0 for _, _, p in self.rows]
            else:
                mask = [0 < p['first_acquired_round'].get(cid, 0) <= (max_round or 400)
                        for _, _, p in self.rows]
            if not any(mask):
                continue
            row = dict(id=cid, name=card['name'], cost=card['cost'], type=card['type'],
                       faction=card['faction'], **self.describe(mask))
            row['adjusted'] = self.fit(mask) if row['n'] >= 30 else None
            if kind == 'fast_only_first_round':
                possible = [p['first_acquired_round'].get(cid) == 1 and p['fast_acquired'].get(cid, 0) > 0
                            for _, _, p in self.rows]
                row['possible_first_round_fast_count'] = sum(possible)
            output.append(row)
        bh(output)
        return sorted(output, key=lambda r: -(r['adjusted'] or {}).get('delta_pp', -999))

    def destinies(self):
        eligible = [p.get('destiny') is not None for _, _, p in self.rows]
        # Destiny selection round is a pre-selection timing marker, not a measure of exposure.
        extra = [[float(min(p['destiny_round'], 10) == r) for r in range(2, 11)] for _, _, p in self.rows]
        output = []
        for card in self.catalog.values():
            if card['type'] != 'Destiny':
                continue
            cid = card['id']
            mask = [p.get('destiny') == cid for _, _, p in self.rows]
            if not any(mask):
                continue
            selected = [p for _, _, p in self.rows if p.get('destiny') == cid]
            row = dict(id=cid, name=card['name'], **self.describe(mask),
                       mean_selected_round=float(np.mean([p['destiny_round'] for p in selected])),
                       mean_activations=float(np.mean([p['activated'].get(cid, 0) for p in selected])),
                       adjusted=self.fit(mask, eligible, extra) if sum(mask) >= 30 else None)
            row['heroes'] = {hero: self.describe([m and p['hero'] == hero for m, (_, _, p) in zip(mask, self.rows)])
                             for hero in sorted({p['hero'] for _, _, p in self.rows})}
            output.append(row)
        bh(output)
        return sorted(output, key=lambda r: -(r['adjusted'] or {}).get('delta_pp', -999))

    def card_hero(self, cards):
        output = []
        for cid in cards:
            main = [0 < p['first_acquired_round'].get(cid, 0) <= 2 for _, _, p in self.rows]
            for hero in sorted({p['hero'] for _, _, p in self.rows}):
                interaction = [v and p['hero'] == hero for v, (_, _, p) in zip(main, self.rows)]
                if sum(interaction) < 30:
                    continue
                output.append(dict(id=cid, hero=hero, **self.describe(interaction),
                                   adjusted=self.fit(np.column_stack([main, interaction]))))
        bh(output)
        return sorted(output, key=lambda r: r['adjusted']['p'])

    def pairs(self, early_cards):
        values = {c['id']: np.array([0 < p['first_acquired_round'].get(c['id'], 0) <= 2
                                    for _, _, p in self.rows]) for c in early_cards if c['n'] >= 150}
        output = []
        for a, b in itertools.combinations(values, 2):
            together = values[a] & values[b]
            if sum(together) < 30:
                continue
            output.append(dict(a=a, b=b, **self.describe(together),
                               adjusted=self.fit(np.column_stack([values[a], values[b], together]))))
        bh(output)
        return sorted(output, key=lambda r: r['adjusted']['p'])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--trace', type=Path, required=True)
    parser.add_argument('--catalog', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    games = [json.loads(line) for line in args.trace.read_text(encoding='utf-8-sig').splitlines()]
    assert len(set(g['seed'] for g in games)) == len(games)
    assert all(g['winner'] in [0, 1] for g in games)
    catalog = {c['id']: c for c in json.loads(args.catalog.read_text())['cards']}
    analysis = Analysis(games, catalog)
    result = dict(games=len(games), players=2*len(games),
                  method='Linear probability model adjusted for fully interacted own hero, opponent hero and seat; sandwich standard errors clustered by game. Destiny model also adjusts selection round buckets. Exploratory, not causal. BH correction separately within each family.',
                  first_round=analysis.card_family('acquired', 1),
                  by_second_round=analysis.card_family('acquired', 2),
                  ever_acquired=analysis.card_family('acquired'),
                  final_collection=analysis.card_family('final_collection'),
                  first_round_fast_guaranteed=analysis.card_family('fast_only_first_round'),
                  destinies=analysis.destinies())
    result['early_card_hero_interactions'] = analysis.card_hero([c['id'] for c in result['by_second_round'] if c['n'] >= 150])
    result['early_card_pairs'] = analysis.pairs(result['by_second_round'])
    result['catalog'] = catalog
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    print(f'{len(games)} games analyzed; saved {args.output}')


if __name__ == '__main__':
    main()
