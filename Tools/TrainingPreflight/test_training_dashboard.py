"""One CPU-only browser regression for balance views, filtering and unknown data.

Uses an installed Chromium (CHROMIUM_BINARY can override its location), a local
temporary fixture and no network/API server. No training modules are imported.
"""
from pathlib import Path
import os
import subprocess
import tempfile
import unittest


HERE = Path(__file__).resolve().parent
CHROME = Path(os.environ.get("CHROMIUM_BINARY", str(Path.home() /
    ".cache/ms-playwright/chromium_headless_shell-1243/chrome-headless-shell-linux64/chrome-headless-shell")))

HARNESS = r"""
<script>
try {
 let checks=0;
 const check=(condition,message)=>{if(!condition)throw Error(message);checks++};
 const fixture={schema:'test-observations-v1',state:'completed',snapshot_id:'test-42',updated_wall:1780000000,
  scope:{label:'Frozen seat-swapped matches',policy_label:'Learner H128',policy_versions:[2500],opponent_label:'Frozen champion',opponent_versions:[2400],inference:'CPU FP32',sample_unit:'completed learner-perspective games',confidence_method:'Conservative paired-seed bounds'},
  refresh:{every_games:100000,games_since_snapshot:25000,total_games:325000,evaluation_sample_count:512},
  totals:{attempted_games:512,resolved_games:511,censored_games:1,seat0_wins:270,draws:2,mean_rounds:24.5,mean_wrapper_decisions:340},
  rankings:{heroes:[
   {id:'decima',name:'Decima',games:100,wins:61,draws:0,losses:39,score:.61,score_bound_95:[.4,.82]},
   {id:'tetra',name:'Tetra',games:60,wins:31,draws:0,losses:29,score:31/60,score_bound_95:[.27,.77]},
   {id:'unseen',name:'Unobserved hero',games:0,wins:0,draws:0,losses:0,score:null,score_bound_95:null}],
   relics:[],destinies:[],cards:[
   {id:'comet',name:'Comet',set:'duel',choice_kind:'buy',games:44,wins:0,draws:0,losses:44,score:0,score_bound_95:[0,.3],pick_count:44,opportunities:100,pick_rate:.44,mean_acquisition_round:11},
   {id:'mercenary',name:'Mercenary',choice_kind:'buy',games:30,wins:20,draws:0,losses:10,score:2/3,score_bound_95:[.2,1],pick_count:30,opportunities:80,pick_rate:.375,mean_acquisition_round:8},
   {id:'mercenary',name:'Mercenary',choice_kind:'fastplay',games:25,wins:18,draws:0,losses:7,score:.72,score_bound_95:[.2,1],pick_count:33,opportunities:90,pick_rate:33/90,mean_acquisition_round:6},
   {id:'capped',name:'Capped item',games:25,wins:10,draws:0,losses:15,censored_games:1,score:null,score_bound_95:[0,1],score_identification_interval:[10/26,11/26],pick_count:26,opportunities:null,pick_rate:null},
   {id:'rare',name:'Rare card',games:3,wins:3,draws:0,losses:0,score:1,score_bound_95:[0,1]},
   {id:'unseen',name:'<img src=x onerror=alert(1)>',games:0,wins:0,draws:0,losses:0,score:null,score_bound_95:null,pick_count:0,opportunities:0,pick_rate:null}]},
  hero_seats:[{hero_id:'decima',seat:0,games:40,wins:24,draws:0,losses:16,score:.6,score_bound_95:[.2,1]}],
  hero_matchups:[{hero_a:'decima',hero_b:'tetra',games:40,wins:24,draws:0,losses:16,score:.6,score_bound_95:[.2,1]}]};
 const data={wall:1780000010,runs:[],budget:{exists:false},resource:{},resources:[],balance_statistics:fixture,balance_statistics_error:'Rejected incompatible statistics fixture'};
 render(data);
 const text=()=>$('balanceTable').textContent;
 const rows=()=>$('balanceTable').querySelectorAll('tbody tr');
 const tab=name=>$('balanceTabs').querySelector('[data-balance-view="'+name+'"]').click();
 const change=(id,value)=>{$(id).value=value;$(id).dispatchEvent(new Event('change'))};
 check(rows().length===2,'default sample filter must omit zero coverage');
 check($('alerts').textContent.includes('Independent balance statistics: Rejected incompatible statistics fixture'),'statistics failures visible independently of training health');
 check($('balanceScope').textContent.includes('Learner H128')&&$('balanceScope').textContent.includes('2400'),'policy and opponent version scope');
 check($('balanceSummary').textContent.includes('Conservative paired-seed bounds'),'display confidence method verbatim');
 check($('balanceCadence').textContent.includes('100,000 training games')&&$('balanceCadence').textContent.includes('512 frozen matches'),'cadence units distinguish training and evaluation');
 check($('balanceCadenceBar').style.width==='25%','cadence progress');check($('balanceCards').textContent.includes('Latest pre-action observation')&&$('balanceInterpretation').textContent.includes('not the exact terminal round'),'frozen rounds not asserted as terminal rounds');
 tab('cards');check(rows().length===4,'card view excludes rare and unobserved by default');
 check(rows()[0].textContent.includes('Fast-play entry'),'observed score sort and fastplay label');
 check(text().includes('Normal purchase'),'buy and fastplay entry remain separate');
 check(rows()[rows().length-1].textContent.includes('Capped item'),'unknown score sorts after numerical scores, including zero');
 check(text().includes('Unknown outcome')&&text().includes('unresolved; not draws'),'censored outcomes are not zero scores or draws');
 check(text().includes('Unknown-outcome range:'),'identification interval supplied by backend');
 check(text().includes('26 selections / — opportunity menus'),'unknown exposure must remain unknown');
 check([...rows()].find(r=>r.textContent.includes('Comet')).querySelector('.score-range strong').textContent==='0%','zero is a valid observed score');
 check([...rows()].find(r=>r.textContent.includes('Comet')).textContent.includes('Duel of Doom'),'expansion labels distinguish same-name replacement definitions');
 check([...rows()].find(r=>r.textContent.includes('Comet')).querySelector('td').title==='Definition: comet','definition ID remains inspectable');
 check($('balanceTable').querySelectorAll('.score-track').length===4,'render supplied uncertainty bands');
 change('balanceSort','games');check(rows()[0].textContent.includes('Comet'),'sort by sample count');
 $('balanceUnobserved').checked=true;$('balanceUnobserved').dispatchEvent(new Event('change'));
 check(rows().length===5&&text().includes('No exposure recorded'),'zero-sample toggle preserves catalog entries');
 check(!$('balanceTable').querySelector('img'),'catalog names rendered safely as text');
 check(text().includes('<img src=x onerror=alert(1)>'),'catalog labels are not silently dropped');
 change('balanceMinimum','0');check(rows().length===6&&text().includes('Limited sample'),'all-sample setting includes sparse data');
 $('balanceSearch').value='Mercenary';$('balanceSearch').dispatchEvent(new Event('input'));check(rows().length===2,'search retains separate action modes');
 $('balanceSearch').value='';$('balanceSearch').dispatchEvent(new Event('input'));
 tab('hero_seats');check(text().includes('Decima · Seat 0'),'hero seat0 preserved and catalog names resolved');
 tab('hero_matchups');check(text().includes('Learner ↓ / opponent →')&&text().includes('60%'),'matchup roles explicit');
 check($('balanceTable').querySelector('tbody th').textContent==='Decima'&&$('balanceTable').querySelectorAll('thead th')[1].textContent==='Tetra','hero matchup direction independent of physical seat');
 $('balanceTabs').querySelector('[aria-selected=true]').dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowRight',bubbles:true}));
 check(balanceView==='heroes','keyboard tabs wrap and select');
 fixture.scope.label='Training pool aggregates';delete fixture.refresh.evaluation_sample_count;renderBalance(fixture);
 check($('balanceScope').textContent.includes('Training pool aggregates')&&!$('balanceCadence').textContent.includes('frozen matches'),'pool scope not mislabeled frozen evaluation');
 renderBalance(null);check($('balanceTable').textContent.includes('Awaiting balance data'),'empty data supported without false results');
 data.balance_statistics_sources={training_pool:null,frozen:fixture};renderBalanceFromData(data);
 check($('balanceSource').value==='frozen','default uses frozen data while pool not available');
 const pool={...fixture,state:'ready',scope:{...fixture.scope,label:'Training pool',policy_label:'Pooled changing-policy training games',sample_unit:'completed pooled player-games'},refresh:{every_games:10000,games_since_snapshot:1500},hero_choice_rows:[
  {id:'comet:buy:decima',name:'Comet',type:'Ally',hero_id:'decima',choice_kind:'buy',games:22,wins:2,draws:0,losses:20,score:2/22,score_bound_95:[0,1],pick_count:22,opportunities:null},
  {id:'mercenary:fastplay:tetra',name:'Mercenary',type:'Mercenary',hero_id:'tetra',choice_kind:'fastplay',games:21,wins:2,draws:0,losses:19,score:2/21,score_bound_95:[0,1],pick_count:24,opportunities:null}]};
 data.balance_statistics_sources.training_pool=pool;renderBalanceFromData(data);
 check($('balanceSource').value==='training_pool'&&$('balanceScope').textContent.includes('Pooled changing-policy'),'default follows ready pool');
 tab('cards');check(!$('balanceHero').disabled,'hero selector enabled only for supplied exact acquisition rows');
 change('balanceHero','decima');check(rows().length===6&&text().includes('Comet')&&text().includes('Mercenary'),'exact hero filter retains unknown choices');const missing=balanceVisibleRows(pool).find(r=>r.id==='mercenary:buy:decima');check(missing.games===0&&missing.wins===0&&missing.pick_count===0&&missing.score===null&&missing.mean_acquisition_round===null,'zero hero row does not inherit global counts');
 check(rows()[0].querySelector('.score-range strong').textContent==='9.1%','hero-specific score never copied from global card outcome');
 tab('relics');check(rows().length===0&&$('balanceHeroNote').textContent.includes('without pooled totals'),'absent hero/category does not fall back to pooled counts');
 pool.rankings.relics.push({id:'decima_relic:relic',name:'Decima relic',type:'Relic',character:'decima',choice_kind:'relic',games:50,wins:40,draws:0,losses:10,score:.8,pick_count:60,mean_acquisition_round:9},{id:'tetra_relic:relic',name:'Tetra relic',type:'Relic',character:'tetra',choice_kind:'relic',games:60,wins:50,draws:0,losses:10,score:5/6,pick_count:70});renderBalanceFromData(data);check(rows().length===1&&text().includes('Decima relic')&&!text().includes('Tetra relic'),'eligible zero relic without foreign hero relic');check(balanceVisibleRows(pool)[0].games===0&&balanceVisibleRows(pool)[0].pick_count===0,'zero relic no copied global outcomes');
 tab('hero_matchups');check(text().includes('Tracked player ↓ / opponent →'),'pool matchup does not falsely claim learner-only scope');
 change('balanceSource','frozen');renderBalanceFromData(data);check($('balanceSource').value==='frozen'&&balanceCurrentSource==='frozen','explicit source choice retained on refresh');
 check($('balanceScope').textContent.includes('Learner H128'),'frozen data restored after pool switch');
 tab('cards');check($('balanceHero').disabled&&$('balanceHero').value==='__all__','unsupported source cannot appear filtered by hero');
 data.balance_statistics_sources.training_random={...pool,scope:{...pool.scope,label:'Forced random hero starts'}};
 data.balance_statistics_sources.training_natural={...pool,scope:{...pool.scope,label:'Ordinary policy-selected drafts'}};
 renderBalanceFromData(data);check($('balanceSource').value==='frozen','new cohort publication does not replace an explicit user source choice');
 balanceSourcePinned=false;renderBalanceFromData(data);check($('balanceSource').value==='training_random','default prioritizes ready randomized-hero cohort');
 change('balanceHero','decima');check(rows().length===6&&text().includes('22 events'),'random cohort retains exact per-hero acquisition and pool event semantics');
 tab('hero_matchups');check(text().includes('Tracked player ↓ / opponent →'),'random cohort is not mislabeled controlled learner-only evaluation');
 data.balance_statistics_sources.training_random=null;renderBalanceFromData(data);check($('balanceSource').value==='training_pool','fallback prefers earlier ready pool over natural cohort as configured');
 data.balance_statistics_sources.training_pool=null;renderBalanceFromData(data);check($('balanceSource').value==='training_natural','natural cohort fallback remains separate from frozen evaluation');
 render(data);check($('downloads').textContent.includes('balance-statistics-natural-draft.json'),'allowlisted natural cohort raw statistics linked');
 change('balanceSource','frozen');tab('cards');
 fixture.scope.label='Frozen seat-swapped matches';fixture.refresh.evaluation_sample_count=512;
 latest=data;balanceView='cards';$('balanceMinimum').value='20';$('balanceUnobserved').checked=false;$('balanceSort').value='score';renderBalance(fixture);
 // Keep the screenshot focused on the component, after testing its real
 // integration and handlers above. This also avoids headless scroll painting.
 document.querySelector('main').replaceChildren($('balancePanel'));window.scrollTo(0,0);
 check(document.documentElement.scrollWidth<=window.innerWidth,'table scrolling is contained within viewport');
 const result=document.createElement('div');result.id='browser-test-result';result.textContent='PASS '+checks+' checks';document.body.append(result);
} catch(error) {const result=document.createElement('pre');result.id='browser-test-result';result.textContent='FAIL '+error.stack;document.body.append(result)}
</script>
"""


class DashboardBrowserTests(unittest.TestCase):
    @unittest.skipUnless(CHROME.is_file(), "Chromium is not installed")
    def test_balance_views_and_unknown_outcomes_in_browser(self):
        html = (HERE / "training_dashboard.html").read_text()
        # Leave the real render/event handlers intact, but the initial fetch
        # never resolves, so the fixture cannot contact the live monitor.
        html = html.replace("<script>", "<script>window.fetch=()=>new Promise(()=>{});</script><script>", 1)
        html = html.replace("</body>", HARNESS + "</body>")
        with tempfile.TemporaryDirectory(prefix="shards-dashboard-test-") as tmp:
            root = Path(tmp)
            fixture = root / "fixture.html"
            fixture.write_text(html)
            for width in (1440, 430):
                with self.subTest(viewport_width=width):
                    command = [str(CHROME), "--no-sandbox", "--disable-gpu",
                        "--disable-background-networking", "--disable-dev-shm-usage",
                        "--run-all-compositor-stages-before-draw", "--virtual-time-budget=250",
                        "--hide-scrollbars", f"--window-size={width},1150",
                        "--user-data-dir=" + str(root / f"profile-{width}"), "--dump-dom"]
                    screenshot = os.environ.get("SHARDS_DASHBOARD_TEST_SCREENSHOT")
                    if screenshot and width == 1440:
                        command.append("--screenshot=" + screenshot)
                    result = subprocess.run(command + [fixture.as_uri()], capture_output=True,
                        text=True, timeout=30, check=False)
                    self.assertEqual(result.returncode, 0, result.stderr[-3000:])
                    marker = '<div id="browser-test-result">PASS '
                    self.assertIn(marker, result.stdout, result.stdout[-6000:] + result.stderr[-1000:])


if __name__ == "__main__":
    unittest.main()
