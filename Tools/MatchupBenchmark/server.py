from http.server import SimpleHTTPRequestHandler,ThreadingHTTPServer
from pathlib import Path
import argparse,json,time
from throughput import recent_throughput
p=argparse.ArgumentParser();p.add_argument('--work',type=Path,required=True);p.add_argument('--port',type=int,default=8767);a=p.parse_args()
class Handler(SimpleHTTPRequestHandler):
 def do_GET(self):
  route=self.path.split('?',1)[0]
  files={'/':Path(__file__).with_name('dashboard.html'),'/status.json':a.work/'status.json','/plan.json':a.work/'plan.json','/results.json':a.work/'results.json','/lifecycle.json':a.work/'lifecycle.json','/performance.json':a.work/'performance.json',
   '/diagnosis':a.work.parent/'full-diagnosis/report.html',
   '/diagnosis.json':a.work.parent/'full-diagnosis/diagnosis.json',
   '/next-training.json':a.work.parent/'next-training-diagnosed/plan.json'}
  target=files.get(route)
  if target is None or not target.is_file():self.send_error(404);return
  data=target.read_bytes()
  if route in ('/status.json','/results.json'):
   value=json.loads(data);performance=a.work/'performance.json'
   proof=json.loads(performance.read_text()) if performance.exists() else None
   if proof:value['performance_upgrade']=proof
   journal=a.work/'games.jsonl'
   if route=='/status.json' and journal.exists():
    records=[]
    for line in journal.read_text().splitlines():
     try:records.append(json.loads(line))
     except json.JSONDecodeError:pass # Last line may still be being written.
    value.update(recent_throughput(value,records,time.time(),proof))
   data=json.dumps(value).encode()
  self.send_response(200);self.send_header('Content-Type','text/html; charset=utf-8' if target.suffix=='.html' else 'application/json');self.send_header('Cache-Control','no-store');self.send_header('Content-Length',str(len(data)));self.end_headers();self.wfile.write(data)
 def log_message(self,*args):pass
ThreadingHTTPServer(('127.0.0.1',a.port),Handler).serve_forever()
