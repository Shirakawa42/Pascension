"""Publish verified, dated progress snapshots without restarting the trainer."""
from datetime import datetime, timezone
import html
import json
import math
from pathlib import Path
from zoneinfo import ZoneInfo

from fixed_opponent_comparison import compare_fixed_opponent
from paired_followup import merge_blocks


START = '<!-- audited-progress:start -->'
END = '<!-- audited-progress:end -->'


def fixed_snapshot(directory):
    directory = Path(directory)
    plan = json.loads((directory / 'plan.json').read_text())
    arms = []
    for arm in ('older', 'newer'):
        blocks = [json.loads((directory / f'{arm}-block-{index + 1}.json').read_text())
                  for index in range(plan['games_per_version'] // 4000)]
        result = merge_blocks(blocks)
        published = json.loads((directory / f'{arm}.json').read_text())
        for key in ('candidate', 'baseline', 'results', 'summary', 'mirror_initial_publications'):
            if result[key] != published[key]:
                raise ValueError('Published fixed-opponent arena differs from raw blocks')
        if (result['planned_games'] != plan['games_per_version']
                or result['seed_base'] != plan['seed_base']
                or result['policy_seed'] != plan['policy_seed']):
            raise ValueError('Fixed-opponent arena differs from its plan')
        arms.append(result)
    checked = compare_fixed_opponent(*arms)
    published = json.loads((directory / 'comparison.json').read_text())
    if any(published.get(key) != value for key, value in checked.items()):
        raise ValueError('Fixed-opponent comparison differs from raw outcomes')
    return checked


def recent_snapshot(directory):
    directory = Path(directory)
    published = json.loads((directory / 'comparison.json').read_text())
    count = published['planned_games']
    blocks = [json.loads((directory / f'comparison-block-{index + 1}.json').read_text())
              for index in range(count // 4000)]
    checked = merge_blocks(blocks)
    for key in ('candidate', 'baseline', 'results', 'summary', 'hero_comparisons'):
        if published[key] != checked[key]:
            raise ValueError('Recent-reference comparison differs from raw blocks')
    plan = json.loads((directory / 'plan.json').read_text())
    if (count != plan['games'] or checked['seed_base'] != plan['seed_base']
            or checked['policy_seed'] != plan['policy_seed']):
        raise ValueError('Recent-reference comparison differs from its plan')
    return checked


def render(campaign, *, fixed=None, recent=None, published_at=None, deadline_wall=None):
    if fixed is None and recent is None:
        raise ValueError('At least one completed comparison is required')
    stamp = published_at or datetime.now(timezone.utc).isoformat(timespec='seconds')
    escape = lambda value: html.escape(str(value), quote=True)
    pct = lambda value: f'{100 * value:.2f}%'
    delta = lambda value: f'{100 * value:+.2f} pp'
    interval = lambda values: '[' + ', '.join(delta(value) for value in values) + ']'
    parts = [START, '<section class="panel"><h2>Audited progress checks</h2>',
             '<p class="foot">Completed snapshots for ' + escape(campaign) + ' · published ' + escape(stamp)
             + '. These checkpoint comparisons are separate from the live counters and the final current-AI evaluation.</p>']
    if deadline_wall is not None:
        if type(deadline_wall) not in (int, float) or not math.isfinite(deadline_wall) or deadline_wall <= 0:
            raise ValueError('Invalid original training deadline')
        deadline = datetime.fromtimestamp(deadline_wall, ZoneInfo('Europe/Paris')).strftime('%Y-%m-%d %H:%M:%S')
        parts.append('<p><b>Original training deadline: ' + deadline + ' Paris.</b> '
                     'The watchdog enforces this cap even if charged-time allocation remains. '
                     'The final current-AI evaluation is a separate phase.</p>')
    if recent is not None:
        summary = recent['summary']
        low, high = summary['paired_hoeffding_score_interval']
        verdict = ('Progress demonstrated on this reference' if low > .5 else
                   'Regression detected on this reference' if high < .5 else
                   'Further progress not established by this check')
        parts.append('<p><b>' + verdict + '</b> · checkpoint '
                     + f"{recent['candidate']['training']['games']:,}" + ' vs '
                     + f"{recent['baseline']['training']['games']:,}" + ' training games · '
                     + f"{summary['recorded_games']:,}" + ' paired benchmark games · score '
                     + pct(summary['resolved_game_score']) + ' · 95% interval ['
                     + pct(low) + ', ' + pct(high) + '].</p>')
    if fixed is not None:
        older = fixed['older_policy']['training']['games']
        newer = fixed['newer_policy']['training']['games']
        opponent = fixed['fixed_opponent']['training']['games']
        parts.append(f'<p><b>Hero progress against the same opponent</b> · {older:,} → {newer:,} training games. '
                     f'Both versions faced the same frozen {opponent:,}-game opponent, '
                     f"with {fixed['games_per_version']:,} games per version.</p>")
        low, high = fixed['overall_difference_interval']
        verdict = ('Overall gain demonstrated against this opponent' if low > 0 else
                   'Overall regression detected against this opponent' if high < 0 else
                   'Overall improvement not established by this check')
        parts.append('<p><b>' + verdict + '</b> · score change '
                     + delta(fixed['overall_paired_difference']) + ' · 95% interval '
                     + interval([low, high]) + '.</p>')
        parts.append('<div class="tablewrap"><table><thead><tr><th>Hero</th><th>Older score</th>'
                     '<th>Newer score</th><th>Change</th><th>Joint 95% interval</th></tr></thead><tbody>')
        for hero, row in fixed['heroes'].items():
            values = (hero.title(), pct(row['older_score']), pct(row['newer_score']),
                      delta(row['paired_difference']), interval(row['simultaneous_difference_interval']))
            parts.append('<tr>' + ''.join('<td>' + escape(value) + '</td>' for value in values) + '</tr>')
        parts.append('</tbody></table></div>')
        rez = fixed['rez_primary']
        low, high = rez['primary_difference_interval']
        verdict = ('Rez improved against this opponent' if low > 0 else
                   'Rez regressed against this opponent' if high < 0 else
                   'Rez improvement is not established by this check')
        parts.append('<p><b>' + verdict + '</b> · predeclared primary 95% interval '
                     + interval([low, high]) + '.</p>')
        parts.append('<p class="foot">Matching starting conditions and seats; no search. '
                     'The table adjusts for checking all five heroes. An interval crossing zero is inconclusive. '
                     'These are exploratory comparisons, not automatic champion promotions or proof about every opponent.</p>')
    parts.extend(['</section>', END])
    return '\n'.join(parts)


def publish(dashboard, panel):
    dashboard = Path(dashboard)
    document = dashboard.read_text()
    if START in document or END in document:
        if document.count(START) != 1 or document.count(END) != 1:
            raise ValueError('Ambiguous dashboard progress markers')
        first, last = document.index(START), document.index(END)
        if last < first:
            raise ValueError('Reversed dashboard progress markers')
        document = document[:first] + panel + document[last + len(END):]
    else:
        anchor = '<section class="panel"><h2>Retained champion</h2>'
        if document.count(anchor) != 1:
            raise ValueError('Dashboard insertion point is absent or ambiguous')
        document = document.replace(anchor, panel + '\n' + anchor, 1)
    temporary = dashboard.with_name(dashboard.name + '.progress.tmp')
    with temporary.open('x') as stream:
        stream.write(document)
    temporary.chmod(dashboard.stat().st_mode & 0o777)
    temporary.replace(dashboard)


def main():
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--campaign', required=True)
    parser.add_argument('--fixed', type=Path)
    parser.add_argument('--recent', type=Path)
    parser.add_argument('--deadline-wall', type=float, help='Original watchdog cap, Unix seconds')
    parser.add_argument('--dashboard', type=Path, required=True)
    args = parser.parse_args()
    if args.fixed is None and args.recent is None:
        parser.error('Provide at least one completed comparison directory')
    fixed = fixed_snapshot(args.fixed) if args.fixed else None
    recent = recent_snapshot(args.recent) if args.recent else None
    publish(args.dashboard, render(args.campaign, fixed=fixed, recent=recent, deadline_wall=args.deadline_wall))
    print(json.dumps({'dashboard': str(args.dashboard), 'completed_fixed_check': fixed is not None,
                      'completed_recent_check': recent is not None, 'trainer_restarted': False}))


if __name__ == '__main__':
    main()
