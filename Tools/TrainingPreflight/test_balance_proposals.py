"""Disposable-browser regression checks for the balance workshop.

Run with a Python environment containing Playwright, e.g.:
  SHARDS_CHROMIUM=/path/to/chrome-headless-shell python test_balance_proposals.py
The production campaign and its feedback are never modified by this harness.
"""
from __future__ import annotations

import asyncio
import copy
import json
import os
from pathlib import Path
import shutil
import tempfile
import threading
import unittest

try:
    from playwright.async_api import async_playwright
except ImportError:
    async_playwright = None

import monitor_training as monitor

HERE = Path(__file__).resolve().parent
CATALOG = Path('/home/lva/.local/share/shards-zero-depth/2026-10-08/new-ai-search-statistics-10000/card-catalog.json')


class BalanceWorkshopBrowserHarness:
    manifest_fixture = 'proposal_versions/2026-10-08-v2.json'
    preview_fixture = 'proposal_versions/2026-10-08-v2-card-previews.json'

    async def asyncSetUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='shards-balance-browser-')
        self.root = Path(self.temp.name)
        self.assets = self.root / 'Tools/TrainingPreflight'
        self.assets.mkdir(parents=True)
        for name in ('balance_proposals.html', 'balance_proposals.css', 'balance_proposals.js'):
            shutil.copyfile(HERE / name, self.assets / name)
        self.manifest_path = self.assets.parent / 'BalanceReview/balance_proposals.json'
        self.manifest_path.parent.mkdir()
        self.manifest = json.loads((HERE.parent / 'BalanceReview' / self.manifest_fixture).read_text())
        self.manifest_path.write_text(json.dumps(self.manifest))
        preview_path = HERE.parent / 'BalanceReview' / self.preview_fixture
        self.previews = json.loads(preview_path.read_text())
        shutil.copyfile(preview_path, self.manifest_path.parent / 'balance_card_previews.json')
        self.first = self.manifest['proposals'][0]['id']
        self.second = self.manifest['proposals'][1]['id']
        self.campaign = self.root / 'campaign'
        self.campaign.mkdir()
        if CATALOG.exists():
            shutil.copyfile(CATALOG, self.campaign / 'card-catalog.json')
        else:
            cards = [pair['before'] for p in self.previews['proposals'].values()
                     for pair in p['cards'] if pair['before'] and pair['before'].get('id')]
            (self.campaign / 'card-catalog.json').write_text(json.dumps({'cards': cards, 'heroes': []}))
        self.store = self.campaign / 'balance-proposal-reviews.json'
        self.original_here = monitor.HERE
        monitor.HERE = self.assets
        self.server = monitor.make_server(monitor.Monitor(self.campaign), port=0)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.url = 'http://127.0.0.1:' + str(self.server.server_port)
        self.playwright = await async_playwright().start()
        executable = os.environ.get('SHARDS_CHROMIUM')
        if not executable:
            choices = sorted((Path.home() / '.cache/ms-playwright').glob('chromium_headless_shell-*/chrome-headless-shell-linux64/chrome-headless-shell'))
            executable = str(choices[-1]) if choices else None
        self.browser = await self.playwright.chromium.launch(headless=True, **({'executable_path': executable} if executable else {}))
        self.context = await self.browser.new_context()
        self.page = await self.context.new_page()
        self.page.set_default_timeout(8000)
        self.errors = []
        self.page.on('pageerror', lambda error: self.errors.append(str(error)))
        self.page.on('dialog', lambda dialog: dialog.accept())

    async def asyncTearDown(self):
        await self.browser.close()
        await self.playwright.stop()
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(3)
        monitor.HERE = self.original_here
        self.temp.cleanup()
        self.assertEqual(self.errors, [])

    async def load(self, page=None):
        page = page or self.page
        await page.goto(self.url + '/balance-proposals')
        await page.wait_for_selector('.proposal-tile')

    def saved(self):
        return json.loads(self.store.read_text())['versions'][self.manifest['version']]

    async def designs(self, page=None):
        return await (page or self.page).evaluate("() => fetch('/api/card-drafts').then(r=>r.json())")

    async def settled(self, page=None):
        page = page or self.page
        for _ in range(200):
            if await page.evaluate('() => !running && !dirty && !blocked'):
                return
            await asyncio.sleep(.05)
        self.fail('Review did not finish saving: ' + await page.locator('#save-status').inner_text())

    async def open_review(self, proposal=None, page=None):
        page = page or self.page
        await page.locator(f'[data-proposal="{proposal or self.first}"] .card-button').first.click()
        await page.wait_for_selector('#review-dialog[open]')

    async def close_review(self, page=None):
        await (page or self.page).locator('#close-review').click()

    async def open_card_review(self, proposal, card_index, page=None):
        page = page or self.page
        await page.locator(f'[data-proposal="{proposal}"]').nth(card_index).locator('.card-button').first.click()
        await page.wait_for_selector('#review-dialog[open]')

    async def general(self, text):
        await self.page.locator('#package-button').click()
        await self.page.locator('#general-comment').fill(text)
        await self.page.locator('#close-package').click()

    async def new_design(self, name='Test card'):
        await self.page.locator('#create-card').click()
        await self.page.locator('#edit-name').fill(name)
        await self.page.locator('#edit-rules').fill('Gain 2 power.\nM10: draw a card.')

    async def save_design(self, page=None):
        page = page or self.page
        await page.locator('#save-card').click()
        await page.wait_for_selector('#editor-dialog:not([open])', state='attached')


@unittest.skipUnless(async_playwright, 'Playwright is not installed in this Python environment')
class BalanceProposalBrowserTests(BalanceWorkshopBrowserHarness, unittest.IsolatedAsyncioTestCase):
    """Frozen v2 regressions preserve the grouped-card migration coverage."""

    async def test_gallery_all_pairs_and_click_review(self):
        await self.load()
        total = sum(len(v['cards']) for v in self.previews['proposals'].values())
        self.assertEqual(await self.page.locator('.proposal-tile').count(), total)
        self.assertEqual(await self.page.locator('.proposal-tile .pair-slot').count(), total * 2)
        for key, data in self.previews['proposals'].items():
            for index, pair in enumerate(data['cards']):
                tile = self.page.locator(f'[data-proposal="{key}"]').nth(index)
                faces = tile.locator('.pair-slot')
                for side, card in enumerate((pair['before'], pair['after'])):
                    if card:
                        self.assertEqual(await faces.nth(side).locator('.card-name').inner_text(), card['name'])
                        text = await faces.nth(side).locator('.card-rules').inner_text()
                        for line in card['rules_text'].splitlines():
                            self.assertIn(line, text)
                    else:
                        self.assertEqual(await faces.nth(side).locator('.card-empty').count(), 1)
        await self.open_review()
        self.assertIn('Giga', await self.page.locator('#review-title').inner_text())
        self.assertIn('gain 3 mastery', await self.page.locator('#review-cards .pair-slot').nth(0).inner_text())
        self.assertIn('gain 2 mastery', await self.page.locator('#review-cards .pair-slot').nth(1).inner_text())
        await self.close_review()
        await self.page.locator('#search').fill('giga')
        self.assertEqual(await self.page.locator('.proposal-tile').count(), sum(
            'giga' in json.dumps([p['title'], pair]).lower()
            for p in self.manifest['proposals']
            for pair in self.previews['proposals'][p['id']]['cards']))

    async def test_crown_review_does_not_change_entropic_talons(self):
        await self.load()
        await self.open_card_review('crown-draw', 0)
        await self.page.locator('#review-comment').fill('Crown alone should draw one card.')
        await self.page.locator('#review-decision').select_option('change')
        await self.close_review()
        await self.settled()
        await self.open_card_review('crown-draw', 1)
        self.assertEqual(await self.page.locator('#review-comment').input_value(), '')
        self.assertEqual(await self.page.locator('#review-decision').input_value(), 'unreviewed')
        await self.page.locator('#review-comment').fill('Keep Talons exactly as it is.')
        await self.page.locator('#review-decision').select_option('accept')
        await self.close_review()
        await self.settled()
        await self.load()
        await self.open_card_review('crown-draw', 0)
        self.assertEqual(await self.page.locator('#review-comment').input_value(), 'Crown alone should draw one card.')
        self.assertEqual(await self.page.locator('#review-decision').input_value(), 'change')
        await self.close_review()
        await self.open_card_review('crown-draw', 1)
        self.assertEqual(await self.page.locator('#review-comment').input_value(), 'Keep Talons exactly as it is.')
        self.assertEqual(await self.page.locator('#review-decision').input_value(), 'accept')

    async def test_every_gallery_card_keeps_its_own_feedback_after_reload(self):
        await self.load()
        expected = []
        decisions = ('accept', 'change', 'reject')
        for proposal, data in self.previews['proposals'].items():
            for index, pair in enumerate(data['cards']):
                name = (pair['after'] or pair['before'])['name']
                with self.subTest(proposal=proposal, card=name):
                    await self.open_card_review(proposal, index)
                    self.assertEqual(await self.page.locator('#review-comment').input_value(), '')
                    self.assertEqual(await self.page.locator('#review-decision').input_value(), 'unreviewed')
                    comment = f'Independent feedback for {proposal}: {name}'
                    decision = decisions[len(expected) % len(decisions)]
                    await self.page.locator('#review-comment').fill(comment)
                    await self.page.locator('#review-decision').select_option(decision)
                    await self.close_review()
                    expected.append((proposal, index, comment, decision))
        await self.settled()
        await self.load()
        saved = self.saved()['review']
        for proposal, index, comment, decision in expected:
            with self.subTest(proposal=proposal, card_index=index):
                await self.open_card_review(proposal, index)
                self.assertEqual(await self.page.locator('#review-comment').input_value(), comment)
                self.assertEqual(await self.page.locator('#review-decision').input_value(), decision)
                await self.close_review()
                pairs = self.previews['proposals'][proposal]['cards']
                card_id = (pairs[index]['after'] or pairs[index]['before'])['id']
                entry = saved['card_entries'][proposal][card_id] if len(pairs) > 1 else saved['entries'][proposal]
                self.assertEqual(entry, {'decision': decision, 'comment': comment})
        async with self.page.expect_download() as info:
            await self.page.locator('#export').click()
        export = json.loads(Path(await (await info.value).path()).read_text())
        self.assertEqual(export['review']['card_entries'], saved['card_entries'])

    async def test_card_feedback_filters_count_only_the_card_reviewed(self):
        await self.load()
        total = sum(len(v['cards']) for v in self.previews['proposals'].values())
        await self.open_card_review('crown-draw', 0)
        await self.page.locator('#review-comment').fill('Only Crown has been reviewed.')
        await self.close_review()
        await self.settled()
        await self.page.locator('#filter').select_option('reviewed')
        self.assertEqual(await self.page.locator('.proposal-tile').count(), 1)
        self.assertIn('Panconscious Crown', await self.page.locator('.proposal-tile').inner_text())
        await self.page.locator('#filter').select_option('unreviewed')
        self.assertEqual(await self.page.locator('.proposal-tile').count(), total - 1)
        self.assertEqual(await self.page.locator('[data-proposal="crown-draw"]').count(), 1)
        self.assertIn('Entropic Talons', await self.page.locator('[data-proposal="crown-draw"]').inner_text())

    async def test_legacy_shared_reviews_are_preserved_without_attributing_them_to_cards(self):
        entries = {p['id']: {'decision': 'unreviewed', 'comment': ''} for p in self.manifest['proposals']}
        groups = {key: value for key, value in self.previews['proposals'].items() if len(value['cards']) > 1}
        for key in groups:
            entries[key] = {'decision': 'change', 'comment': f'Earlier ambiguous group feedback for {key}'}
        entries[self.first] = {'decision': 'accept', 'comment': 'Existing single-card review'}
        legacy_review = {
            'schema': 'shards-balance-proposal-review-v1', 'proposal_version': self.manifest['version'],
            'revision': 1, 'entries': entries, 'general_comment': 'Keep this overall feedback.',
            'submitted_at': '2026-10-08T11:00:00Z', 'updated_at': '2026-10-08T11:00:00Z',
        }
        document = {'schema': 'shards-balance-proposal-reviews-v1', 'versions': {
            self.manifest['version']: {
                'proposal': self.manifest, 'review': copy.deepcopy(legacy_review),
                'submissions': [copy.deepcopy(legacy_review)],
            }
        }}
        self.store.write_text(json.dumps(document))
        await self.load()
        # A read must not rewrite the user's old store or historical submission.
        self.assertEqual(json.loads(self.store.read_text()), document)
        for key, data in groups.items():
            for index, _ in enumerate(data['cards']):
                with self.subTest(group=key, card_index=index):
                    await self.open_card_review(key, index)
                    self.assertEqual(await self.page.locator('#review-comment').input_value(), '')
                    self.assertEqual(await self.page.locator('#review-decision').input_value(), 'unreviewed')
                    legacy = await self.page.locator('#previous-feedback .legacy-group-feedback').inner_text()
                    self.assertIn('Earlier shared review (preserved)', legacy)
                    self.assertIn(entries[key]['comment'], legacy)
                    await self.close_review()
        await self.open_review()
        self.assertEqual(await self.page.locator('#review-comment').input_value(), 'Existing single-card review')
        self.assertEqual(await self.page.locator('#review-decision').input_value(), 'accept')
        await self.close_review()
        await self.open_card_review('crown-draw', 0)
        await self.page.locator('#review-comment').fill('New unambiguous Crown review')
        await self.close_review()
        await self.settled()
        saved = self.saved()
        self.assertEqual(saved['review']['entries'], entries)
        self.assertEqual(saved['submissions'], [legacy_review])
        self.assertEqual(saved['review']['general_comment'], 'Keep this overall feedback.')
        crown_id = self.previews['proposals']['crown-draw']['cards'][0]['after']['id']
        talons_id = self.previews['proposals']['crown-draw']['cards'][1]['after']['id']
        self.assertEqual(saved['review']['card_entries']['crown-draw'][crown_id]['comment'], 'New unambiguous Crown review')
        self.assertEqual(saved['review']['card_entries']['crown-draw'][talons_id]['comment'], '')

    async def test_legacy_browser_shared_draft_is_preserved_but_not_copied_to_cards(self):
        entries = {p['id']: {'decision': 'unreviewed', 'comment': ''} for p in self.manifest['proposals']}
        entries['crown-draw'] = {'decision': 'reject', 'comment': 'Offline feedback from the old shared form'}
        draft = {'proposal_version': self.manifest['version'], 'base_revision': 0,
                 'entries': entries, 'general_comment': '', 'dirty': True}
        # Seed before the app initializes: injecting into a live page would let
        # its beforeunload handler correctly replace our fixture on navigation.
        await self.page.add_init_script(
            'localStorage.setItem(' + json.dumps('shards-balance-review:' + self.manifest['version']) +
            ', ' + json.dumps(json.dumps(draft)) + ');')
        await self.load()
        await self.settled()
        self.assertEqual(self.saved()['review']['entries']['crown-draw'], entries['crown-draw'])
        for index in (0, 1):
            await self.open_card_review('crown-draw', index)
            self.assertEqual(await self.page.locator('#review-comment').input_value(), '')
            self.assertEqual(await self.page.locator('#review-decision').input_value(), 'unreviewed')
            self.assertIn(entries['crown-draw']['comment'], await self.page.locator('.legacy-group-feedback').inner_text())
            await self.close_review()

    async def test_switching_cards_while_review_is_saving_keeps_both_edits(self):
        await self.load()
        started, release = asyncio.Event(), asyncio.Event()
        calls = 0
        async def slow(route):
            nonlocal calls
            calls += 1
            if calls == 1:
                started.set()
                await release.wait()
            await route.continue_()
        await self.page.route('**/api/balance-reviews', slow)
        await self.open_card_review('crown-draw', 0)
        await self.page.locator('#review-comment').fill('Crown edited first')
        await asyncio.wait_for(started.wait(), 5)
        await self.close_review()
        await self.open_card_review('crown-draw', 1)
        self.assertEqual(await self.page.locator('#review-comment').input_value(), '')
        await self.page.locator('#review-comment').fill('Talons edited while Crown was saving')
        await self.page.locator('#review-decision').select_option('reject')
        await self.close_review()
        release.set()
        await self.settled()
        await self.load()
        await self.open_card_review('crown-draw', 0)
        self.assertEqual(await self.page.locator('#review-comment').input_value(), 'Crown edited first')
        self.assertEqual(await self.page.locator('#review-decision').input_value(), 'unreviewed')
        await self.close_review()
        await self.open_card_review('crown-draw', 1)
        self.assertEqual(await self.page.locator('#review-comment').input_value(), 'Talons edited while Crown was saving')
        self.assertEqual(await self.page.locator('#review-decision').input_value(), 'reject')

    async def test_individual_card_reviews_survive_network_failure_and_browser_recovery(self):
        await self.load()
        async def abort(route):
            await route.abort()
        await self.page.route('**/api/balance-reviews', abort)
        for index, name in enumerate(('Crown', 'Talons')):
            await self.open_card_review('crown-draw', index)
            self.assertEqual(await self.page.locator('#review-comment').input_value(), '')
            await self.page.locator('#review-comment').fill(f'Recover only {name}')
            await self.close_review()
        await self.page.wait_for_selector('#error:not([hidden])')
        self.assertFalse(self.store.exists())
        await self.page.unroute('**/api/balance-reviews', abort)
        await self.load()
        await self.settled()
        for index, name in enumerate(('Crown', 'Talons')):
            await self.open_card_review('crown-draw', index)
            self.assertEqual(await self.page.locator('#review-comment').input_value(), f'Recover only {name}')
            await self.close_review()

    async def test_missing_or_stale_card_previews_never_enable_shared_group_editing(self):
        for mode in ('unavailable', 'stale'):
            with self.subTest(mode=mode):
                async def broken_previews(route):
                    if mode == 'unavailable':
                        await route.abort()
                    else:
                        stale = {**self.previews, 'proposal_version': 'a-different-version'}
                        await route.fulfill(status=200, content_type='application/json', body=json.dumps(stale))
                await self.page.route('**/api/balance-card-previews', broken_previews)
                await self.load()
                for key, data in self.previews['proposals'].items():
                    if len(data['cards']) < 2:
                        continue
                    await self.open_card_review(key, 0)
                    self.assertTrue(await self.page.locator('#review-comment').is_disabled())
                    self.assertTrue(await self.page.locator('#review-decision').is_disabled())
                    await self.close_review()
                await self.open_review()
                self.assertTrue(await self.page.locator('#review-comment').is_enabled())
                await self.close_review()
                self.assertFalse(self.store.exists())
                await self.page.unroute('**/api/balance-card-previews', broken_previews)

    async def test_autosave_reload_submission_edit_export_and_mobile(self):
        await self.load()
        self.assertFalse(self.store.exists())
        await self.open_review()
        await self.page.locator('#review-comment').fill('Keep drawing; mastery 3 → 2. Équilibre <script>safe</script>')
        await self.page.locator('#review-decision').select_option('change')
        await self.close_review()
        await self.general('Evaluate these changes together.')
        await self.settled()
        saved = self.saved()['review']
        self.assertEqual(saved['entries'][self.first]['decision'], 'change')
        self.assertIn('<script>safe</script>', saved['entries'][self.first]['comment'])
        await self.load()
        await self.page.locator('#package-button').click()
        self.assertEqual(await self.page.locator('#general-comment').input_value(), 'Evaluate these changes together.')
        await self.page.locator('#close-package').click()
        await self.page.locator('#finish').click()
        await self.settled()
        self.assertIsNotNone(self.saved()['review']['submitted_at'])
        self.assertEqual(len(self.saved()['submissions']), 1)
        await self.open_review()
        await self.page.locator('#review-comment').fill('A later edit makes this a draft.')
        await self.close_review()
        await self.settled()
        self.assertIsNone(self.saved()['review']['submitted_at'])
        self.assertEqual(len(self.saved()['submissions']), 1)
        self.assertTrue(await self.page.locator('#ready').is_hidden())
        async with self.page.expect_download() as download_info:
            await self.page.locator('#export').click()
        exported = json.loads(Path(await (await download_info.value).path()).read_text())
        self.assertEqual(exported['review']['entries'][self.first]['comment'], 'A later edit makes this a draft.')
        await self.page.set_viewport_size({'width': 390, 'height': 844})
        self.assertTrue(await self.page.evaluate('document.documentElement.scrollWidth <= innerWidth'))
        await self.open_review()
        self.assertTrue(await self.page.evaluate("document.querySelector('#review-dialog').scrollWidth <= document.querySelector('#review-dialog').clientWidth"))
        await self.close_review()
        await self.page.locator('#create-card').click()
        self.assertTrue(await self.page.evaluate("document.querySelector('#editor-dialog').scrollWidth <= document.querySelector('#editor-dialog').clientWidth"))
        self.assertTrue(await self.page.evaluate("[...document.querySelectorAll('textarea')].every(x=>x.labels.length > 0)"))

    async def test_newer_edit_survives_inflight_save_and_ready_request(self):
        await self.load()
        started, release = asyncio.Event(), asyncio.Event()
        calls = 0
        async def slow(route):
            nonlocal calls
            calls += 1
            if calls == 1:
                started.set()
                await release.wait()
            await route.continue_()
        await self.page.route('**/api/balance-reviews', slow)
        await self.open_review()
        await self.page.locator('#review-comment').fill('First snapshot')
        await asyncio.wait_for(started.wait(), 5)
        await self.page.locator('#review-comment').fill('Newer text during request')
        await self.close_review()
        await self.page.locator('#finish').click()
        release.set()
        await self.settled()
        self.assertEqual(self.saved()['review']['entries'][self.first]['comment'], 'Newer text during request')
        self.assertIsNotNone(self.saved()['review']['submitted_at'])
        self.assertEqual(len(self.saved()['submissions']), 1)

    async def test_edit_during_submission_does_not_leave_stale_ready_state(self):
        await self.load()
        started, release = asyncio.Event(), asyncio.Event()
        async def slow_submission(route):
            if route.request.post_data_json.get('submit'):
                started.set()
                await release.wait()
            await route.continue_()
        await self.page.route('**/api/balance-reviews', slow_submission)
        await self.open_review()
        await self.page.locator('#review-comment').fill('Ready text')
        await self.close_review()
        await self.settled()
        await self.page.locator('#finish').click()
        await asyncio.wait_for(started.wait(), 5)
        await self.general('Changed my mind while submitting')
        release.set()
        await self.settled()
        self.assertEqual(self.saved()['review']['general_comment'], 'Changed my mind while submitting')
        self.assertIsNone(self.saved()['review']['submitted_at'])
        self.assertEqual(len(self.saved()['submissions']), 1)
        self.assertTrue(await self.page.locator('#ready').is_hidden())

    async def test_network_failure_recovers_browser_review_on_reload(self):
        await self.load()
        async def abort(route):
            await route.abort()
        await self.page.route('**/api/balance-reviews', abort)
        await self.open_review()
        await self.page.locator('#review-comment').fill('Recover this unsaved draft')
        await self.page.wait_for_selector('#error:not([hidden])')
        self.assertFalse(self.store.exists())
        await self.page.unroute('**/api/balance-reviews', abort)
        await self.load()
        await self.settled()
        self.assertEqual(self.saved()['review']['entries'][self.first]['comment'], 'Recover this unsaved draft')

    async def test_two_tabs_conflict_requires_explicit_resolution(self):
        await self.load()
        second_context = await self.browser.new_context()
        second = await second_context.new_page()
        second.on('dialog', lambda dialog: dialog.accept())
        await self.load(second)
        await self.open_review()
        await self.page.locator('#review-comment').fill('Saved from tab one')
        await self.settled()
        await self.open_review(page=second)
        await second.locator('#review-comment').fill('Draft from tab two')
        await second.wait_for_selector('#use-draft:not([hidden])')
        self.assertEqual(self.saved()['review']['entries'][self.first]['comment'], 'Saved from tab one')
        self.assertEqual(await second.locator('#review-comment').input_value(), 'Draft from tab two')
        await self.close_review(second)
        await second.locator('#use-draft').click()
        await self.settled(second)
        self.assertEqual(self.saved()['review']['entries'][self.first]['comment'], 'Draft from tab two')
        await second_context.close()

    async def test_retry_after_initial_load_failure_saves_without_reloading(self):
        gets = 0
        async def initial_failure(route):
            nonlocal gets
            gets += 1
            if gets == 1:
                await route.fulfill(status=503, content_type='application/json', body=json.dumps({'error': 'temporary test failure'}))
            else:
                await route.continue_()
        await self.page.route('**/api/balance-proposals', initial_failure)
        await self.page.goto(self.url + '/balance-proposals')
        await self.page.wait_for_selector('#error:not([hidden])')
        await self.page.locator('#retry').click()
        await self.page.wait_for_selector('.proposal-tile')
        async def abort(route):
            await route.abort()
        await self.page.route('**/api/balance-reviews', abort)
        await self.open_review()
        await self.page.locator('#review-comment').fill('Retry should save this')
        await self.close_review()
        await self.page.wait_for_function('() => dirty && !running')
        await self.page.unroute('**/api/balance-reviews', abort)
        await self.page.locator('#retry').click()
        await self.settled()
        self.assertEqual(gets, 2)
        self.assertEqual(self.saved()['review']['entries'][self.first]['comment'], 'Retry should save this')

    async def test_new_proposal_version_preserves_old_review_and_blocks_old_tab(self):
        await self.load()
        await self.open_review()
        await self.page.locator('#review-comment').fill('Original version feedback')
        await self.settled()
        newer = copy.deepcopy(self.manifest)
        newer['version'] += '-next'
        self.manifest_path.write_text(json.dumps(newer))
        await self.page.locator('#review-comment').fill('Unsent old-version changes')
        await self.page.wait_for_selector('#error:not([hidden])')
        self.assertTrue(await self.page.locator('#use-draft').is_hidden())
        self.assertEqual(self.saved()['review']['entries'][self.first]['comment'], 'Original version feedback')
        self.assertEqual(await self.page.locator('#review-comment').input_value(), 'Unsent old-version changes')

    async def test_same_version_content_conflict_has_no_dead_override_button(self):
        await self.load()
        await self.open_review()
        await self.page.locator('#review-comment').fill('Original content feedback')
        await self.settled()
        mutated = copy.deepcopy(self.manifest)
        mutated['summary'] += ' Changed without a version bump.'
        self.manifest_path.write_text(json.dumps(mutated))
        await self.page.locator('#review-comment').fill('Preserve this draft')
        await self.page.wait_for_selector('#error:not([hidden])')
        self.assertTrue(await self.page.locator('#use-draft').is_hidden())
        self.assertIn('without a new version', await self.page.locator('#error-text').inner_text())
        self.assertEqual(self.saved()['review']['entries'][self.first]['comment'], 'Original content feedback')

    async def test_editor_create_preview_save_reload_export_delete(self):
        await self.load()
        await self.new_design('Élan <script>safe</script>')
        await self.page.locator('#edit-faction').select_option('Wraethe')
        await self.page.locator('#edit-type').select_option('Champion')
        await self.page.locator('#edit-cost').fill('4')
        await self.page.locator('#edit-defense').fill('6')
        await self.page.locator('#edit-notes').fill('Proposal only, not a game rule.')
        self.assertEqual(await self.page.locator('#live-preview .card-name').inner_text(), 'Élan <script>safe</script>')
        self.assertEqual(await self.page.locator('#live-preview .cost').inner_text(), '4')
        self.assertIn('Defense 6', await self.page.locator('#live-preview .card-stats').inner_text())
        await self.save_design()
        saved = await self.designs()
        self.assertEqual(len(saved['cards']), 1)
        self.assertEqual(saved['cards'][0]['name'], 'Élan <script>safe</script>')
        await self.load()
        await self.page.locator('[data-view="drafts"]').click()
        self.assertEqual(await self.page.locator('.single-tile .card-name').inner_text(), 'Élan <script>safe</script>')
        async with self.page.expect_download() as info:
            await self.page.locator('#export').click()
        export = json.loads(Path(await (await info.value).path()).read_text())
        self.assertEqual(export['card_designs']['cards'], saved['cards'])
        await self.page.locator('.single-tile .card-button').click()
        await self.page.locator('#delete-card').click()
        await self.page.wait_for_selector('#editor-dialog:not([open])', state='attached')
        self.assertEqual((await self.designs())['cards'], [])

    async def test_library_clone_and_customize_proposal(self):
        await self.load()
        await self.page.locator('[data-view="library"]').click()
        await self.page.locator('#search').fill('Giga, Source Adept')
        await self.page.locator('.single-tile .card-button').click()
        self.assertEqual(await self.page.locator('#edit-name').input_value(), 'Giga, Source Adept')
        self.assertIn('3 mastery', await self.page.locator('#edit-rules').input_value())
        await self.page.locator('#edit-name').fill('Giga alternate')
        await self.save_design()
        await self.page.locator('[data-view="proposals"]').click()
        await self.open_review()
        await self.page.locator('#customize-proposal').click()
        self.assertIn('2 mastery', await self.page.locator('#edit-rules').input_value())
        await self.page.locator('#edit-name').fill('Giga proposed alternative')
        await self.save_design()
        saved = await self.designs()
        self.assertEqual(len(saved['cards']), 2)
        self.assertEqual(saved['cards'][1]['source_proposal_id'], self.first)
        self.assertNotEqual(saved['cards'][0]['id'], saved['cards'][1]['id'])

    async def test_editor_network_failure_recovers_browser_draft(self):
        await self.load()
        async def fail_posts(route):
            if route.request.method == 'POST':
                await route.abort()
            else:
                await route.continue_()
        await self.page.route('**/api/card-drafts', fail_posts)
        await self.new_design('Recover my concept')
        await self.page.locator('#save-card').click()
        await self.page.wait_for_selector('#editor-error:not([hidden])')
        self.assertEqual((await self.designs())['cards'], [])
        await self.page.unroute('**/api/card-drafts', fail_posts)
        await self.load()
        await self.page.locator('#resume-editor').click()
        self.assertEqual(await self.page.locator('#edit-name').input_value(), 'Recover my concept')
        await self.save_design()
        self.assertEqual((await self.designs())['cards'][0]['name'], 'Recover my concept')

    async def test_editor_conflict_preserves_both_new_cards(self):
        await self.load()
        second_context = await self.browser.new_context()
        second = await second_context.new_page()
        second.on('dialog', lambda dialog: dialog.accept())
        await self.load(second)
        await self.new_design('First tab card')
        await self.save_design()
        await second.locator('#create-card').click()
        await second.locator('#edit-name').fill('Second tab card')
        await second.locator('#save-card').click()
        await second.wait_for_selector('#editor-error:not([hidden])')
        self.assertIn('Another tab', await second.locator('#editor-error').inner_text())
        self.assertEqual(await second.locator('#edit-name').input_value(), 'Second tab card')
        self.assertEqual(len((await self.designs())['cards']), 1)
        await self.save_design(second)
        self.assertEqual({c['name'] for c in (await self.designs())['cards']}, {'First tab card', 'Second tab card'})
        await second_context.close()

    async def test_stale_offline_editor_cannot_silently_replace_a_newer_saved_card(self):
        await self.load()
        await self.new_design('Original saved card')
        await self.save_design()
        second_context = await self.browser.new_context()
        second = await second_context.new_page()
        second.on('dialog', lambda dialog: dialog.accept())
        await self.load(second)
        await second.locator('[data-view="drafts"]').click()
        await second.locator('.single-tile .card-button').click()
        await self.page.locator('.single-tile .card-button').click()
        await self.page.locator('#edit-name').fill('Unfinished offline revision')
        await self.page.locator('#close-editor').click()
        await second.locator('#edit-name').fill('Newer saved revision')
        await self.save_design(second)
        await self.load()
        await self.page.locator('#resume-editor').click()
        self.assertEqual(await self.page.locator('#edit-name').input_value(), 'Unfinished offline revision')
        await self.page.locator('#save-card').click()
        await self.page.wait_for_selector('#editor-error:not([hidden])')
        self.assertEqual((await self.designs())['cards'][0]['name'], 'Newer saved revision')
        self.assertEqual(await self.page.locator('#edit-name').input_value(), 'Unfinished offline revision')
        await self.save_design()
        self.assertEqual((await self.designs())['cards'][0]['name'], 'Unfinished offline revision')
        await second_context.close()

    async def test_failed_card_collection_load_blocks_all_editor_entry_points(self):
        async def fail_get(route):
            await route.fulfill(status=503, content_type='application/json', body=json.dumps({'error': 'temporary draft failure'}))
        await self.page.route('**/api/card-drafts', fail_get)
        await self.load()
        self.assertTrue(await self.page.locator('#create-card').is_disabled())
        await self.open_review()
        await self.page.locator('#customize-proposal').click()
        self.assertFalse(await self.page.locator('#editor-dialog').evaluate('(d)=>d.open'))
        await self.page.locator('[data-view="library"]').click()
        await self.page.locator('.single-tile .card-button').first.click()
        self.assertFalse(await self.page.locator('#editor-dialog').evaluate('(d)=>d.open'))
        self.assertFalse((self.campaign / 'card-design-drafts.json').exists())

    async def test_editor_newer_typing_survives_inflight_save(self):
        await self.load()
        started, release = asyncio.Event(), asyncio.Event()
        async def slow_posts(route):
            if route.request.method == 'POST':
                started.set()
                await release.wait()
            await route.continue_()
        await self.page.route('**/api/card-drafts', slow_posts)
        await self.new_design('First snapshot')
        await self.page.locator('#save-card').click()
        await asyncio.wait_for(started.wait(), 5)
        # Either locking edits or preserving them is safe. If input remains editable,
        # the newer value must remain in the editor after the first save completes.
        editable = await self.page.locator('#edit-name').is_enabled()
        if editable:
            await self.page.locator('#edit-name').fill('Newer text while saving')
        release.set()
        await self.page.wait_for_function('() => !draftSaving')
        if editable:
            self.assertTrue(await self.page.locator('#editor-dialog').evaluate('(d)=>d.open'))
            self.assertEqual(await self.page.locator('#edit-name').input_value(), 'Newer text while saving')
            backup = await self.page.evaluate("JSON.parse(localStorage.getItem('shards-card-editor-v1'))")
            self.assertTrue(backup['dirty'])
            self.assertEqual(backup['card']['name'], 'Newer text while saving')
            await self.save_design()
        self.assertEqual(len((await self.designs())['cards']), 1)


@unittest.skipUnless(async_playwright, 'Playwright is not installed in this Python environment')
class CurrentBalanceProposalBrowserTests(BalanceWorkshopBrowserHarness, unittest.IsolatedAsyncioTestCase):
    """Smoke and persistence checks against the current published patch content."""

    manifest_fixture = 'balance_proposals.json'
    preview_fixture = 'balance_card_previews.json'

    async def test_current_gallery_has_one_changed_or_new_comparison_per_proposal(self):
        await self.load()
        self.assertEqual(self.previews['proposal_version'], self.manifest['version'])
        self.assertEqual(set(self.previews['proposals']), {p['id'] for p in self.manifest['proposals']})
        self.assertEqual(await self.page.locator('.proposal-tile').count(), len(self.manifest['proposals']))
        visible_fields = ('name', 'faction', 'type', 'cost', 'quantity', 'defense', 'shield', 'rules_text')
        rule_cards = 0
        for proposal in self.manifest['proposals']:
            with self.subTest(proposal=proposal['id']):
                self.assertNotEqual(proposal['kind'], 'keep')
                pairs = self.previews['proposals'][proposal['id']]['cards']
                self.assertEqual(len(pairs), 1, 'Current proposals must not bundle independent card reviews')
                pair = pairs[0]
                self.assertIsNotNone(pair['after'])
                self.assertTrue(pair['before'] is None or any(
                    pair['before'].get(key) != pair['after'].get(key) for key in visible_fields),
                    'Unchanged comparisons must not appear in the current patch')
                tile = self.page.locator(f'[data-proposal="{proposal["id"]}"]')
                self.assertEqual(await tile.count(), 1)
                self.assertNotIn(await tile.locator('.badge').inner_text(), ('Unchanged', 'Excluded'))
                for index, card in enumerate((pair['before'], pair['after'])):
                    face = tile.locator('.pair-slot').nth(index)
                    if card:
                        self.assertEqual(await face.locator('.card-name').inner_text(), card['name'])
                        rules = await face.locator('.card-rules').inner_text()
                        for line in card['rules_text'].splitlines():
                            self.assertIn(line, rules)
                    else:
                        self.assertEqual(await face.locator('.card-empty').count(), 1)
                await self.open_review(proposal['id'])
                self.assertTrue(await self.page.locator('#review-comment').is_enabled())
                self.assertTrue(await self.page.locator('#review-decision').is_enabled())
                self.assertEqual(await self.page.locator('#review-title').inner_text(), pair['after']['name'])
                modal_text = await self.page.locator('#review-dialog').text_content()
                for field in ('design_notes', 'implementation_notes'):
                    for note in proposal.get(field, []):
                        self.assertIn(note, modal_text)
                for source in proposal.get('sources', []):
                    source_link = self.page.locator('#review-dialog a').filter(has_text=source['title'])
                    self.assertEqual(await source_link.count(), 1)
                    self.assertEqual(await source_link.get_attribute('href'), source['url'])
                if pair['after']['type'] == 'Rule':
                    rule_cards += 1
                    self.assertTrue(await self.page.locator('#customize-proposal').is_disabled())
                    self.assertTrue(await self.page.locator('#review-cards .card-rules').last.is_visible())
                await self.close_review()
        self.assertGreater(rule_cards, 0, 'Current patch includes reviewable rule changes')
        await self.page.set_viewport_size({'width': 390, 'height': 844})
        self.assertTrue(await self.page.evaluate('document.documentElement.scrollWidth <= innerWidth'))
        self.assertTrue(await self.page.locator('.card-rules').evaluate_all(
            '(nodes) => nodes.every(n => n.scrollHeight <= n.clientHeight + 1)'),
            'Long card and rule text must remain fully visible')
        prefix, number = self.manifest['version'].rsplit('-v', 1)
        previous_archive = HERE.parent / f'BalanceReview/proposal_versions/{prefix}-v{int(number) - 1}.json'
        if previous_archive.exists():
            previous = json.loads(previous_archive.read_text())
            removed = {p['id'] for p in previous['proposals']} - {p['id'] for p in self.manifest['proposals']}
            for filter_name in ('all', 'changes', 'new', 'reviewed', 'unreviewed', 'keep'):
                await self.page.locator('#filter').select_option(filter_name)
                for key in removed:
                    self.assertEqual(await self.page.locator(f'.proposal-tile[data-proposal="{key}"]').count(), 0,
                                     f'Removed proposal {key} reappeared under filter {filter_name}')
        self.assertFalse(self.store.exists())

    async def test_current_cards_have_independent_reviews_filters_and_export(self):
        await self.load()
        expected = {}
        choices = ('accept', 'change', 'reject')
        for index, proposal in enumerate(self.manifest['proposals']):
            key = proposal['id']
            await self.open_review(key)
            self.assertEqual(await self.page.locator('#review-comment').input_value(), '')
            self.assertEqual(await self.page.locator('#review-decision').input_value(), 'unreviewed')
            value = {'comment': f'Independent current-patch feedback: {key}', 'decision': choices[index % 3]}
            await self.page.locator('#review-comment').fill(value['comment'])
            await self.page.locator('#review-decision').select_option(value['decision'])
            await self.close_review()
            expected[key] = value
            if index == 0:
                await self.page.locator('#filter').select_option('reviewed')
                self.assertEqual(await self.page.locator('.proposal-tile').count(), 1)
                self.assertEqual(await self.page.locator('.proposal-tile').get_attribute('data-proposal'), key)
                await self.page.locator('#filter').select_option('all')
        await self.settled()
        self.assertEqual(self.saved()['review']['entries'], expected)
        await self.load()
        for key, value in expected.items():
            await self.open_review(key)
            self.assertEqual(await self.page.locator('#review-comment').input_value(), value['comment'])
            self.assertEqual(await self.page.locator('#review-decision').input_value(), value['decision'])
            await self.close_review()
        await self.page.locator('#filter').select_option('reviewed')
        self.assertEqual(await self.page.locator('.proposal-tile').count(), len(expected))
        await self.page.locator('#filter').select_option('unreviewed')
        self.assertEqual(await self.page.locator('.proposal-tile').count(), 0)
        async with self.page.expect_download() as info:
            await self.page.locator('#export').click()
        exported = json.loads(Path(await (await info.value).path()).read_text())
        self.assertEqual(exported['review']['entries'], expected)

    async def test_current_patch_notes_link_every_proposal_and_open_its_review(self):
        await self.load()
        note_targets = await self.page.locator('#patch-notes a').evaluate_all(
            '(links) => links.map(link => link.getAttribute("href"))')
        self.assertCountEqual(note_targets, ['#' + p['id'] for p in self.manifest['proposals']],
                              'Patch notes must contain only the current proposals, without removed or duplicate entries')
        for proposal in self.manifest['proposals']:
            with self.subTest(proposal=proposal['id']):
                await self.page.locator('#package-button').click()
                notes = self.page.locator('#package-dialog details').filter(has=self.page.locator('#patch-notes'))
                if not await notes.evaluate('(node) => node.open'):
                    await notes.locator('summary').click()
                link = self.page.locator(f'#patch-notes a[href="#{proposal["id"]}"]')
                self.assertEqual(await link.count(), 1, 'Every change needs a patch-note entry')
                await link.click()
                await self.page.wait_for_selector('#review-dialog[open]')
                pair = self.previews['proposals'][proposal['id']]['cards'][0]
                self.assertEqual(await self.page.locator('#review-title').inner_text(), pair['after']['name'])
                self.assertTrue(await self.page.locator('#package-dialog').is_hidden())
                await self.close_review()
        self.assertFalse(self.store.exists())

    async def test_current_reviews_preserve_archived_feedback_and_submissions(self):
        previous_versions = {}
        for archive in sorted((HERE.parent / 'BalanceReview/proposal_versions').glob('*.json')):
            if archive.name.endswith('-card-previews.json'):
                continue
            previous_manifest = json.loads(archive.read_text())
            version = previous_manifest['version']
            if version == self.manifest['version']:
                continue
            preview_archive = archive.with_name(archive.stem + '-card-previews.json')
            previous_previews = json.loads(preview_archive.read_text()) if preview_archive.exists() else {'proposals': {}}
            card_entries = {
                key: {(pair['after'] or pair['before'])['id']: {
                    'decision': 'accept', 'comment': 'Preserve earlier independent card review'}
                    for pair in group['cards']}
                for key, group in previous_previews['proposals'].items() if len(group['cards']) > 1
            }
            previous_review = {
                'schema': 'shards-balance-proposal-review-v1', 'proposal_version': version,
                'revision': 7, 'entries': {p['id']: {'decision': 'change', 'comment': f'Preserve {version}: {p["id"]}'}
                                           for p in previous_manifest['proposals']},
                'card_entries': card_entries,
                'general_comment': 'Previous patch package feedback', 'submitted_at': '2026-10-08T12:00:00Z',
                'updated_at': '2026-10-08T12:00:00Z',
            }
            previous_versions[version] = {'proposal': previous_manifest, 'review': previous_review,
                                          'submissions': [copy.deepcopy(previous_review)]}
        self.assertTrue(previous_versions, 'Preservation test needs earlier published patch archives')
        prefix, number = self.manifest['version'].rsplit('-v', 1)
        self.assertIn(f'{prefix}-v{int(number) - 1}', previous_versions,
                      'Archive the immediately preceding patch before publishing a new version')
        self.store.write_text(json.dumps({'schema': 'shards-balance-proposal-reviews-v1',
                                         'versions': previous_versions}))
        await self.load()
        await self.open_review()
        self.assertEqual(await self.page.locator('#review-comment').input_value(), '')
        await self.page.locator('#review-comment').fill('Fresh review of the new patch')
        await self.close_review()
        await self.settled()
        versions = json.loads(self.store.read_text())['versions']
        for version, previous in previous_versions.items():
            self.assertEqual(versions[version], previous)
        self.assertEqual(versions[self.manifest['version']]['review']['entries'][self.first]['comment'],
                         'Fresh review of the new patch')


if __name__ == '__main__':
    unittest.main(verbosity=2)
