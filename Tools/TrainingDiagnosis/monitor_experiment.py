"""Read-only local view of the matched experiment; no GPU or checkpoint reads."""
import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import math
from pathlib import Path
import sys
import threading
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'ZeroDepthTraining'))
from monitor import JsonlTail, observed_rate, _raw_now, _boot_id, finite


def read(path, default=None):
    try:
        return json.loads(path.read_text())
    except (OSError, ValueError):
        return default


def process_alive(owner, proc=Path('/proc')):
    """Match the saved process identity, not merely a potentially reused PID."""
    owner = owner.get('owner', owner)
    try:
        pid = int(owner['pid'])
        if owner.get('boot_id') and owner['boot_id'] != (proc/'sys/kernel/random/boot_id').read_text().strip():
            return False
        fields = (proc/str(pid)/'stat').read_text().rsplit(')', 1)[1].split()
        if fields[0] == 'Z' or int(fields[19]) != int(owner.get('startticks', owner.get('start_ticks'))):
            return False
        command = (proc/str(pid)/'cmdline').read_bytes().rstrip(b'\0').split(b'\0')
        return command == [part.encode() for part in owner['command']]
    except (OSError, ValueError, KeyError, TypeError, IndexError):
        return False


def job_timing(directory, progress, plan, alive, now=None):
    """Use elapsed wall time for deadline-facing rates, not a slewed timer.

    Older frozen experiments did not publish a wall start. Their immutable
    plan's creation time provides a conservative start near host launch.
    A stopped process must not keep accumulating elapsed time in the view.
    """
    def number(value):
        return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)
    start = plan.get('started_wall')
    if not number(start):
        try: start = (directory/'plan.json').stat().st_mtime
        except OSError: return dict(games_per_second=0., elapsed_seconds=None, basis='unavailable')
    if alive:
        end = time.time() if now is None else now
    else:
        saved = progress.get('elapsed_wall_seconds')
        if number(saved) and saved >= 0:
            end = start + saved
        else:
            try: end = (directory/'progress.json').stat().st_mtime
            except OSError: return dict(games_per_second=0., elapsed_seconds=None, basis='unavailable')
    elapsed = end-start
    if elapsed <= 0:
        return dict(games_per_second=0., elapsed_seconds=None, basis='unavailable')
    return dict(games_per_second=(progress.get('completed') or 0)/elapsed,
        elapsed_seconds=elapsed, basis='whole-run wall-clock average')


def job_snapshot(directory):
    progress = read(directory/'progress.json', {})
    plan = read(directory/'plan.json', {})
    owner = read(directory/'owner.json')
    alive = process_alive(owner) if owner else None
    saved_state = progress.get('state', 'preparing')
    # Completed evidence stays valid after its producer exits normally.
    if saved_state in ('running', 'training', 'validating') and alive is False:
        progress = dict(progress, saved_state=saved_state, state='Process stopped; last saved results shown')
    stage = ('training outcomes, not a strength test' if progress.get('training_only') else
             'final benchmark' if plan.get('final_protocol_sha256') else 'development test')
    snapshot = dict(name=directory.name, progress=progress, process_alive=alive, stage=stage,
        timing=job_timing(directory,progress,plan,alive))
    if snapshot['timing']['elapsed_seconds'] is not None:
        # Existing open pages already use this field for rates. Correct the
        # read-only view too, without rewriting frozen experiment evidence.
        snapshot['progress'] = dict(progress, reported_elapsed_seconds=progress.get('elapsed_seconds'),
            elapsed_seconds=snapshot['timing']['elapsed_seconds'],
            elapsed_clock_basis=snapshot['timing']['basis'])
    if plan.get('hero_routed') and plan.get('protocol'):
        protocol = read(Path(plan['protocol']), {})
        parts = [job_snapshot(Path(component['output']))
                 for component in protocol.get('candidate', {}).get('components', {}).values()]
        snapshot['components'] = parts
        snapshot['progress'] = dict(snapshot['progress'], root_choices_in_current_cohort=sum(
            part['progress'].get('root_choices_in_current_cohort') or 0 for part in parts))
    return snapshot


class View:
    def __init__(self, directory):
        self.directory = directory
        self.plan = read(directory / 'plan.json')
        self.parent_games = read(directory / 'validation.json')['parent_games']
        self.tails = {name: JsonlTail(directory / name / 'metrics.jsonl', max_rows=1000)
                      for name in ('control', 'candidate')}
        self.lock = threading.Lock()

    def snapshot(self):
        with self.lock:
            state = read(self.directory / 'run-state.json', {'state': 'preparing'})
            # Check ownership as well as the last published status after a crash.
            try:
                pid = int((self.directory / 'RUN-CLAIM').read_text())
                command = (Path('/proc') / str(pid) / 'cmdline').read_bytes().split(b'\0')
                alive = (any(x.endswith(b'/TrainingDiagnosis/next_training.py') for x in command)
                         and b'_run' in command and str(self.directory).encode() in command)
            except (OSError, ValueError):
                alive = False
            arms = {}
            for name, tail in self.tails.items():
                tail.refresh()
                rows = list(tail.rows)
                latest = next((r for r in reversed(rows) if r.get('event') == 'generation'), {})
                active = alive and state.get('state') == 'training' and state.get('active_arm') == name
                rate = observed_rate(rows, time.time(), running=active,
                                     raw_now=_raw_now(), boot_id=_boot_id())
                arms[name] = dict(games_added=max(0, latest.get('games', self.parent_games) - self.parent_games),
                    active=active, rate=rate, last_update=latest.get('wall'),
                    rolling=tail.latest_rolling, optimizer_steps=latest.get('total_optimizer_steps'))
            evaluation = None
            if state.get('review') and state.get('comparison') and state.get('state') == 'evaluating':
                evaluation = read(self.directory / f'review-{int(state["review"]):03}' /
                                  f'against-{state["comparison"]}.json')
                if evaluation:
                    evaluation = evaluation.get('summary')
            goal = None
            link = read(self.directory / 'active-goal.json')
            if link:
                folder = Path(link['directory'])
                goal = read(folder / 'goal-state.json', {})
                goal['plan'] = read(folder / 'goal-plan.json', {})
                if goal.get('active_job'):
                    goal['progress'] = job_snapshot(Path(goal['active_job']))['progress']
                goal['experiments'] = [job_snapshot(Path(path)) for path in goal.get('tracked_jobs', [])]
            return finite(dict(controller_alive=alive, state=state, arms=arms, evaluation=evaluation,
                parent_games=self.parent_games, games_per_block=self.plan['games_per_arm_per_review'],
                updated_wall=time.time(), goal=goal))


HTML = '''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Shards · Matched training</title>
<style>:root{color-scheme:dark;font-family:system-ui,sans-serif;background:#0d1522;color:#edf3ff}body{max-width:1150px;margin:auto;padding:26px;line-height:1.5}h1{margin-bottom:8px}p,small{color:#b1c1d8}a{color:#8dd9ff}.cards{display:grid;grid-template-columns:1fr 1fr;gap:18px}.card,section{background:#162337;border:1px solid #334963;border-radius:12px;padding:20px;margin:15px 0}.number{font-size:32px;font-weight:700}.badge{background:#254465;border-radius:14px;padding:5px 12px}table{width:100%;border-collapse:collapse}td,th{padding:10px;text-align:left;border-bottom:1px solid #34485e}progress{width:100%;accent-color:#77cbef}#error{color:#ffb7a8;white-space:pre-wrap}.scroll{overflow:auto}@media(max-width:700px){body{padding:14px}.cards{grid-template-columns:1fr}}</style>
<h1 id="title">Shards · Matched training experiment</h1><p id="specs">512 width · 13.75M parameters · batch 64 · 6 workers · balanced heroes · no search</p>
<span class="badge" id="state">Connecting…</span><p id="clock"></p><p id="activity"></p>
<section id="goal" hidden><h2>Current development goal</h2><p id="goal-target"></p><p id="goal-phase"></p><p id="goal-progress"></p><div id="experiments"></div><p id="goal-findings"></p><small>Development tests are preliminary. A strength claim requires the complete frozen benchmark. No automatic deployment.</small></section>
<details><summary>Earlier matched-training experiment</summary>
<div class="cards" id="arms"></div><section><h2>Strength checks</h2><p>Candidate versus the fixed parent, matched control and best accepted experimental checkpoint. Three reviews without demonstrated progress stop the experiment; clear regression or an incomplete test stops it immediately.</p><p id="evaluation"></p><div class="scroll"><table><thead><tr><th>Review</th><th>New games / arm</th><th>vs parent</th><th>vs control</th><th>vs best</th><th>Decision</th></tr></thead><tbody id="reviews"></tbody></table></div><p id="empty">No review completed yet.</p></section>
<section><h2>Rolling game statistics</h2><p>Last 100,000 completed games per arm. Initially includes games inherited from the parent checkpoint. Round length and self-play scores are descriptive; the strength checks decide progress.</p><div id="stats"></div></section></details><p id="error"></p><p id="fresh"></p><p><a href="http://localhost:8767/diagnosis">Diagnosis and experiment rationale</a> · <a href="/status.json">Live data</a></p>
<script>
const $=x=>document.getElementById(x), fmt=x=>Number(x||0).toLocaleString(), pct=x=>Number.isFinite(x)?(100*x).toFixed(2)+'%':'—';
const when=x=>new Date(x*1000).toLocaleString('en-GB',{timeZone:'Europe/Paris'});
function render(d){const s=d.state;const active=['training','evaluating','validating'].includes(s.state);$('state').textContent=!d.controller_alive&&active?'Controller stopped unexpectedly':s.state.replaceAll('_',' ');$('clock').textContent='Started '+when(s.started_wall)+' · latest stop '+when(s.deadline_wall)+' Paris · may stop earlier';$('activity').textContent=s.state==='training'?'Review '+s.review+' · training '+s.active_arm+' arm':s.state==='evaluating'?'Review '+s.review+' · evaluating against '+s.comparison:'Automatic controller: '+s.state;
$('arms').innerHTML=Object.entries(d.arms).map(([name,a])=>{const target=(s.review||1)*d.games_per_block,base=target-d.games_per_block,progress=Math.max(0,Math.min(d.games_per_block,a.games_added-base));return `<div class="card"><h2>${name==='control'?'Control · decision credit':'Candidate · round credit'} ${a.active?'●':''}</h2><div class="number">${fmt(a.games_added)} new games</div><p>${a.rate.games_per_second.toFixed(1)} games/s · ${fmt(Math.round(a.rate.estimated_games_per_hour))} estimated games/hour</p><progress max="${d.games_per_block}" value="${progress}"></progress><small>${fmt(progress)} / ${fmt(d.games_per_block)} games in this block · ${a.active?'active':'idle'}</small></div>`}).join('');
$('reviews').innerHTML=(s.reviews||[]).map(r=>`<tr><td>${r.review}</td><td>${fmt(r.games_added_per_arm)}</td><td>${pct(r.scores.parent)}</td><td>${pct(r.scores.control)}</td><td>${pct(r.scores.champion)}</td><td>${r.decision.replaceAll('_',' ')}</td></tr>`).join('');$('empty').hidden=!!s.reviews?.length;
$('evaluation').textContent=d.evaluation?`${fmt(d.evaluation.recorded_games)} evaluation games completed · candidate score ${pct(d.evaluation.resolved_game_score)} (preliminary)`:'Each review uses three 2,000-game paired tests. No automatic deployment.';
$('stats').innerHTML=Object.entries(d.arms).map(([name,a])=>`<p><strong>${name}</strong>: ${fmt(a.rolling?.games_in_window)} games in window · mean ${Number.isFinite(a.rolling?.mean_rounds)?a.rolling.mean_rounds.toFixed(2):'—'} rounds</p>`).join('');$('error').textContent=s.error||'';$('fresh').textContent='Updated '+when(d.updated_wall)+' Paris';
if(d.goal){const g=d.goal,p=g.progress||{};$('goal').hidden=false;$('title').textContent='Shards · AI development';$('specs').textContent=p.hero_routed?'Two frozen 512-width policies · 13.75M parameters each · 6 CPU cores total · public-hero routing':'512 width · 13.75M parameters · 6 CPU cores · balanced hero matchups · search experiments';$('state').textContent=g.status||'Goal active';$('clock').textContent='Goal deadline: '+when(g.plan.deadline_wall)+' Paris';$('activity').textContent='Live experiments against the frozen in-game AI. Results update after complete paired cohorts.';$('goal-target').textContent='Target: at least 60% wins against the deployed AI, all five heroes including Rez.';$('goal-phase').textContent=g.phase||'';$('goal-progress').textContent=p.planned?`${fmt(p.completed)} / ${fmt(p.planned)} games · ${p.state||'running'} · ${p.training_only?'training score':'score'} ${pct(p.summary?.resolved_game_score)} · ${p.training_only?fmt(p.decisions)+' completed training decisions':fmt(p.root_choices_in_current_cohort)+' decisions in current cohort'}`:'Preparing the next bounded experiment';$('goal-findings').textContent=(g.findings||[]).slice(-4).join(' ');$('error').textContent=g.error||'';
$('experiments').replaceChildren(...(g.experiments||[]).map(job=>{const p=job.progress||{},s=p.summary||{},box=document.createElement('div'),title=document.createElement('strong'),line=document.createElement('p'),heroes=document.createElement('p');box.className='card';title.textContent=job.name+' · '+job.stage;const rate=job.timing?.games_per_second||0;line.textContent=`${p.state||'preparing'} · ${fmt(p.completed)} / ${fmt(p.planned)} games · ${fmt(s.wins)} wins, ${fmt(s.losses)} losses, ${fmt(s.draws)} draws · ${p.training_only?'training score':'score'} ${pct(s.resolved_game_score)} · ${rate.toFixed(3)} games/s · ${fmt(Math.round(rate*3600))} estimated games/hour (${job.timing?.basis||'timing unavailable'})`;heroes.textContent=Object.entries(p.wins_by_hero||{}).map(([name,h])=>`${name}: ${h.wins}/${h.games} wins`).join(' · ');box.append(title,line,heroes);if(job.components){const activity=document.createElement('p');activity.textContent=job.components.map(c=>`${c.name}: ${c.process_alive?'process alive':c.progress.state||'idle'} · ${fmt(c.progress.root_choices_in_current_cohort)} decisions in current cohort · ${fmt(c.progress.completed)}/${fmt(c.progress.planned)} completed games`).join(' · ');box.append(activity);}if(p.victory_causes&&Object.values(p.victory_causes).some(c=>Object.values(c).some(n=>n>0))){const causes=document.createElement('p');causes.textContent=Object.entries(p.victory_causes).map(([role,c])=>`${role}: ${fmt(c.mastery)} Infinity Shard, ${fmt(c.normal_damage)} normal damage, ${fmt(c.comet)} Comet, ${fmt((c.other_health_loss||0)+(c.concession||0)+(c.unknown||0))} other wins`).join(' · ');box.append(causes);}return box;}));}}
async function poll(){try{const r=await fetch('/status.json',{cache:'no-store'});if(!r.ok)throw Error('HTTP '+r.status);render(await r.json())}catch(e){$('error').textContent='View disconnected: '+e.message}}poll();setInterval(poll,3000);
</script></html>'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', type=Path, required=True)
    parser.add_argument('--port', type=int, default=8768)
    args = parser.parse_args()
    view = View(args.directory.resolve())
    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            route = self.path.split('?', 1)[0]
            if route == '/':
                data, mime = HTML.encode(), 'text/html; charset=utf-8'
            elif route == '/status.json':
                data, mime = json.dumps(view.snapshot(), allow_nan=False).encode(), 'application/json'
            else:
                self.send_error(404); return
            self.send_response(200)
            self.send_header('Content-Type', mime)
            self.send_header('Cache-Control', 'no-store')
            self.send_header('Content-Length', str(len(data)))
            self.end_headers(); self.wfile.write(data)
        def log_message(self, *_):
            pass
    ThreadingHTTPServer(('127.0.0.1', args.port), Handler).serve_forever()


if __name__ == '__main__':
    main()
