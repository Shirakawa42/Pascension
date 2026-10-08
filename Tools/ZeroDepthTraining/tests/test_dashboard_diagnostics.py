"""Optional CPU-only browser rendering of the exact dashboard helper."""
import importlib.util
import json
from pathlib import Path
import shutil
import unittest


def browser():
    installed = shutil.which('chromium') or shutil.which('google-chrome')
    if installed:return installed
    return next((str(path) for path in sorted(Path.home().glob('.cache/ms-playwright/chromium-*/chrome-linux*/chrome'),reverse=True)
                 if path.is_file()), None)


@unittest.skipUnless(browser() and importlib.util.find_spec('playwright'),
                     'Optional Playwright and local Chromium required for dashboard rendering')
class Tests(unittest.TestCase):
    def render(self, payload):
        source = (Path(__file__).resolve().parents[1]/'dashboard.html').read_text()
        script = source.split('<script>',1)[1].split('</script>',1)[0]
        helpers = '\n'.join(line for line in script.splitlines()
                            if line.startswith('const $=') or line.startswith('function renderLearningDetails(')
                            or line.startswith('function renderChampion(') or line.startswith('function renderFixedBenchmark('))
        html = '<!doctype html><div id="lossLabels"></div><div id="learningRate"></div><div id="learningDetails"></div><div id="archivePool"></div><div id="fixedBenchmark"></div><div id="championBenchmark"></div>'
        html += '<script>'+helpers+'\nrenderLearningDetails('+json.dumps(payload)+');renderFixedBenchmark('+json.dumps(payload)+');</script>'
        from playwright.sync_api import sync_playwright
        errors=[]
        with sync_playwright() as driver:
            instance=driver.chromium.launch(executable_path=browser(),headless=True,timeout=10000,
                args=['--no-sandbox','--disable-gpu','--disable-software-rasterizer'])
            try:
                page=instance.new_page()
                page.on('pageerror',lambda error:errors.append(str(error)))
                page.set_content(html,timeout=5000)
                rendered=page.locator('body').inner_text()
            finally:
                instance.close()
        self.assertEqual(errors,[])
        return rendered

    def test_accepted_scope_rejected_kl_and_archive_history_are_rendered(self):
        rendered=self.render(dict(losses=dict(diagnostic_scope='row_weighted_accepted_minibatches_before_optimizer_step',
            loss=.1,entropy=.8,approx_kl=.009,gradient_norm=2.,normalized_entropy=.5,clip_fraction=.2,value_mse=.7,
            value_explained_variance=.3,effective_epochs=2.1,update_coverage=.7,rejected_kl=.05,diagnostic_rows=50000),
            archive_pool=dict(strategy='historical',counts_by_pool=dict(anchor=1,recent=3,historical=8)),
            selected_opponent=dict(pool='historical',age_generations=100)))
        for text in ('Accepted row means','normalized entropy 50.0%','clipped ratios 20.0%',
                     '70.0% of planned passes','rejected KL 0.05','rejected minibatches are excluded',
                     'historical 8','selected historical checkpoint'):
            self.assertIn(text,rendered)

    def test_legacy_metrics_are_not_falsely_called_aggregates(self):
        rendered=self.render(dict(losses=dict(loss=.1,entropy=.8,approx_kl=.009,gradient_norm=2.)))
        self.assertIn('Last minibatch: loss',rendered)
        self.assertIn('Aggregate diagnostics will appear',rendered)
        self.assertIn('Awaiting archive pool metadata',rendered)

    def test_active_learning_rate_and_real_change_marker_are_rendered(self):
        rendered=self.render(dict(model=dict(learning_rate=.0001,learning_rate_source='identity'),
            learning_rate_improvement=dict(before_learning_rate=.0003,after_learning_rate=.0001,at_games=1700000)))
        self.assertIn('Adam learning rate 0.0001 (pinned configuration)',rendered)
        self.assertIn('changed from 0.0003 to 0.0001 at',rendered)
        self.assertIn('training games',rendered)

    def test_marker_cannot_override_active_rate_or_claim_uncommitted_change(self):
        rendered=self.render(dict(model=dict(learning_rate=.0003,learning_rate_source='identity'),
            learning_rate_improvement=dict(before_learning_rate=.0003,after_learning_rate=.0001,at_games=1700000)))
        self.assertIn('Adam learning rate 0.0003 (pinned configuration)',rendered)
        self.assertNotIn('changed from',rendered)

    def test_fixed_reference_score_uses_its_own_games_interval_and_hero_coverage(self):
        payload=dict(league=dict(candidate=dict(training=dict(games=1600000)),baseline=dict(training=dict(games=1500000)),
            planned_games=400,elapsed_seconds=18.5,rounds=dict(mean_natural_rounds=12.1),
            summary=dict(recorded_games=400,wins=220,draws=0,losses=180,resolved_game_score=.55,
                         confidence=.95,paired_hoeffding_score_interval=[.45,.65],evaluation_finished=True),
            hero_coverage={str(i):dict(candidate_seat0=10,candidate_seat1=10) for i in range(20)}))
        rendered=self.render(payload)
        for text in ('400 / 400 benchmark games','220 / 0 / 180','score 55.0%',
                     '95.0% interval [45.0%, 65.0%]','20 / 20 matchups in both seats','mean rounds 12.1','18.5s'):
            self.assertIn(text,rendered)

    def test_retained_champion_and_new_challenge_are_visible(self):
        rendered=self.render(dict(champion=dict(state=dict(training=dict(games=2800000),promotions=1),
            latest=dict(candidate_training=dict(games=2800000),opponent_training=dict(games=2700000),promote=True,
                summary=dict(resolved_game_score=.7,wins=280,losses=120,draws=0,paired_hoeffding_score_interval=[.59,.81])))))
        for value in ('Retained checkpoint: 2,800,000','1 promotions','score 70.0%','280 / 0 / 120','new champion promoted'):
            self.assertIn(value,rendered)

    def test_whole_page_shows_rounds_separately_by_opponent_mode(self):
        from playwright.sync_api import sync_playwright
        source=(Path(__file__).resolve().parents[1]/'dashboard.html').read_text()
        payload=dict(campaign='isolated-browser-fixture',wall=1700000000,phase='training',
            rolling_game_stats=dict(window=100000,games_in_window=128,total_natural_games=128,
                totals={},mean_rounds=12.4,mean_rounds_by_mode=dict(selfplay=12.7,archive=11.5),
                mean_mastery_by_seat=[20.,21.],mean_collection_size_by_seat=[22.,23.]))
        errors=[]
        with sync_playwright() as driver:
            instance=driver.chromium.launch(executable_path=browser(),headless=True,timeout=10000,
                args=['--no-sandbox','--disable-gpu','--disable-software-rasterizer'])
            try:
                page=instance.new_page()
                page.on('pageerror',lambda error:errors.append(str(error)))
                def route(request):
                    if request.request.url.endswith('/api/state'):
                        request.fulfill(content_type='application/json',body=json.dumps(payload))
                    else:request.fulfill(content_type='text/html',body=source)
                page.route('http://dashboard.test/**',route)
                page.goto('http://dashboard.test/',timeout=5000)
                page.wait_for_function("document.getElementById('deckSummary').textContent.includes('archive rounds 11.5')",timeout=5000)
                summary=page.locator('#deckSummary').inner_text()
                self.assertIn('self-play rounds 12.7',summary)
                self.assertIn('archive rounds 11.5',summary)
            finally:instance.close()
        self.assertEqual(errors,[])


if __name__=='__main__':unittest.main()
