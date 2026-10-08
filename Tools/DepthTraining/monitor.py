"""Small read-only local dashboard; no CUDA or model inference."""
from http.server import ThreadingHTTPServer,BaseHTTPRequestHandler
from pathlib import Path
import argparse,json,time
PAGE='''<!doctype html><meta charset="utf-8"><title>Shards · Depth training</title>
<style>body{background:#101723;color:#e4eaf2;font:17px system-ui;max-width:1100px;margin:40px auto;padding:20px}h1{font-size:30px}.grid{display:grid;grid-template-columns:repeat(3,1fr);gap:14px}.card{background:#1b283a;padding:22px;border-radius:12px}.n{font-size:30px;color:#72d6bf}pre{white-space:pre-wrap;background:#1b283a;padding:20px;border-radius:12px}small{color:#a4b4c9}</style>
<h1>Shards of Infinity · Depth training</h1><p id="phase">Loading…</p><div class="grid" id="cards"></div><p id="search"></p><h2>Strength against the retained champion</h2><p id="strength">The first paired strength check runs after one hour.</p><small>Game length describes play; wins against opponents measure strength. Search training uses real completed-game outcomes.</small><h2>Learning and hero coverage</h2><pre id="details"></pre><h2>Hourly reviews</h2><pre id="reviews"></pre>
<script>const fmt=n=>n==null?'—':Number(n).toLocaleString(undefined,{maximumFractionDigits:2});async function refresh(){try{const s=await(await fetch('/api/status',{cache:'no-store'})).json();document.getElementById('phase').textContent=s.state+' · '+fmt(s.remaining_seconds/3600)+' hours remaining · updated '+fmt(Date.now()/1000-s.updated_wall)+' seconds ago';const cards=[['Depth games completed',s.games],['Games / second',s.games_per_second],['Estimated games / hour',s.estimated_games_per_hour],['Mean rounds',s.mean_rounds],['Rolling game window',s.rolling_games],['Parameters',s.parameters]];document.getElementById('cards').innerHTML=cards.map(([k,v])=>'<div class="card"><small>'+k+'</small><div class="n">'+fmt(v)+'</div></div>').join('');document.getElementById('search').textContent='Up to '+s.depth+' decisions ahead · '+s.candidates+' leading moves + a sampled alternative · '+s.worlds+' sampled hidden worlds · 8 CPU workers · '+fmt(s.search_nodes)+' simulated steps · '+fmt(100*s.search_overrides/Math.max(1,s.search_decisions))+'% search overrides';if(s.strength)document.getElementById('strength').textContent=JSON.stringify(s.strength.summary,null,2);document.getElementById('details').textContent=JSON.stringify({learning:s.learning,hero_games:s.hero_games,champion:s.champion},null,2);document.getElementById('reviews').textContent=JSON.stringify(s.reviews||{status:'Waiting for scheduled review'},null,2)}catch(e){document.getElementById('phase').textContent='Waiting for trainer status: '+e} }refresh();setInterval(refresh,5000)</script>'''
def main():
    p=argparse.ArgumentParser();p.add_argument('--run',type=Path,required=True);p.add_argument('--port',type=int,default=8766);a=p.parse_args()
    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            if self.path=='/api/status':
                try:
                    s=json.loads((a.run/'status.json').read_text());r=a.run.parent/'reviews/active.json'
                    if r.exists():s['reviews']=json.loads(r.read_text())
                    data=json.dumps(s).encode();content='application/json'
                except FileNotFoundError:self.send_error(503);return
            elif self.path=='/':data=PAGE.encode();content='text/html; charset=utf-8'
            else:self.send_error(404);return
            self.send_response(200);self.send_header('Content-Type',content);self.send_header('Cache-Control','no-store');self.send_header('Content-Length',str(len(data)));self.end_headers();self.wfile.write(data)
        def log_message(self,*args):pass
    ThreadingHTTPServer(('127.0.0.1',a.port),Handler).serve_forever()
if __name__=='__main__':main()
