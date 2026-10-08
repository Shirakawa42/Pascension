"""Read two completed isolated cohorts and write a descriptive comparison."""
import argparse
import json
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--before', type=Path, required=True)
    parser.add_argument('--after', type=Path, required=True)
    parser.add_argument('--strength', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    old, new = [json.loads(p.read_text()) for p in (args.before, args.after)]
    strength = json.loads(args.strength.read_text())
    for snapshot in (old, new):
        if snapshot['evaluation_progress']['state'] != 'completed':
            raise ValueError('Comparison requires completed cohorts')
        if snapshot['totals']['censored_games']:
            raise ValueError('Comparison requires uncensored cohorts')
    def pct(n):
        return f'{100*n:.1f}%'
    lo, hi = strength['paired_bootstrap_95']
    lines = ['# Validated AI and fresh statistics, 28 September', '',
             f"The independent strength test finished {strength['wins']}–{strength['losses']} over {strength['games']} games: **{pct(strength['score'])}**, paired 95% bootstrap interval **{pct(lo)}–{pct(hi)}**.", '',
             'The candidate and original model/search each occupied both seats equally. Both used the same corrected engine. This measures the complete model-and-search change, not the weights alone.', '',
             f"The new statistics contain **{new['totals']['resolved_games']:,} completed games** with the identical frozen candidate on both seats. They describe its play and the resulting balance; the separate head-to-head test provides the strength evidence.", '',
             '| Hero | Previous cohort | New cohort | Change |',
             '|---|---:|---:|---:|']
    before = {r['id']: r for r in old['rankings']['heroes']}
    for row in new['rankings']['heroes']:
        prior = before[row['id']]
        lines.append(f"| {row['name']} | {pct(prior['score'])} | {pct(row['score'])} | {100*(row['score']-prior['score']):+.1f} pp |")
    lines += ['', '| Measure | Previous cohort | New cohort |', '|---|---:|---:|']
    lines += [f"| Seat 0 wins | {pct(old['totals']['seat0_wins']/old['totals']['resolved_games'])} | {pct(new['totals']['seat0_wins']/new['totals']['resolved_games'])} |",
              f"| Mean final round | {old['totals']['mean_rounds']:.2f} | {new['totals']['mean_rounds']:.2f} |"]
    old_victory = {r['id']: r for r in old['balance_analysis']['victory']}
    for row in new['balance_analysis']['victory']:
        prior = old_victory.get(row['id'])
        if prior is not None:
            lines.append(f"| {row['name']} wins | {prior['count']} ({pct(prior['share'])}) | {row['count']} ({pct(row['share'])}) |")
    lines += ['', 'Hero results are relative to the other heroes under the same AI. A lower hero win rate or longer game is not, by itself, evidence that the AI weakened. Card/relic/destiny acquisition results are observational associations and require consideration of when and why they were selected.', '',
              f"Previous model SHA-256: `{old['scope']['policy_sha256']}`.",
              f"New model SHA-256: `{new['scope']['policy_sha256']}`.",
              f"New statistics snapshot: `{new['snapshot_id']}`.", '',
              'The UI contains only the new cohort. This report compares separately preserved snapshots; it does not pool their games.', '']
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text('\n'.join(lines))


if __name__ == '__main__':
    main()
