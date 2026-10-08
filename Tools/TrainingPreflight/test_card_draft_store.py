"""Card design edits are durable, isolated, recoverable, and safe across tabs."""
from copy import deepcopy
import http.client
import json
import multiprocessing
from pathlib import Path
import tempfile
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import card_draft_store as d
from balance_review_store import MAX_REQUEST_BYTES, ReviewError
import monitor_training as m


def concurrent_save(campaign, payload, barrier, queue):
    barrier.wait(timeout=5)
    try:
        queue.put((200, d.CardDraftStore(campaign).save(payload)))
    except ReviewError as error:
        queue.put((error.status, error.payload))


class DraftFixture(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.campaign = self.root / "campaign"
        self.campaign.mkdir()
        self.store = d.CardDraftStore(self.campaign)

    def card(self, **changes):
        return {"id": "draft-847c31df-01ca-4a1b-a48a-3ce30ec1babb", "name": "Aegis Surveyor",
                "faction": "Order", "type": "Ally", "cost": 3, "quantity": 2,
                "defense": 0, "shield": 2, "rules_text": "Gain 1 mastery.\nIf you have less mastery, draw a card.",
                "notes": "Review later — not implemented.", "art_id": "shard_seer", "source_proposal_id": "new-catchup-mastery", **changes}

    def payload(self, revision=0, cards=None):
        return {"revision": revision, "cards": [self.card()] if cards is None else cards}


class StoreTests(DraftFixture):
    def test_get_is_read_only_and_drafts_survive_restart_without_touching_feedback(self):
        self.assertEqual(self.store.get(), {"schema": d.SCHEMA, "revision": 0, "cards": [], "updated_at": None})
        self.assertEqual(list(self.campaign.iterdir()), [])
        feedback = self.campaign / "balance-proposal-reviews.json"
        feedback.write_text("existing balance feedback left byte-for-byte intact")
        saved = self.store.save(self.payload())
        self.assertEqual(saved["schema"], d.SCHEMA)
        self.assertEqual(saved["revision"], 1)
        self.assertEqual(saved, d.CardDraftStore(self.campaign).get())
        self.assertEqual(saved["cards"][0]["created_at"], saved["updated_at"])
        self.assertEqual(feedback.read_text(), "existing balance feedback left byte-for-byte intact")

    def test_edits_and_deletes_keep_exact_recoverable_snapshots_and_server_timestamps(self):
        first = self.store.save(self.payload())
        card = {**first["cards"][0], "name": "Surveyor v2", "created_at": "2000-01-01T00:00:00Z"}
        second = self.store.save(self.payload(1, [card]))
        self.assertEqual(second["cards"][0]["created_at"], first["cards"][0]["created_at"])
        self.assertNotEqual(second["cards"][0]["updated_at"], first["cards"][0]["updated_at"])
        unchanged = self.store.save(self.payload(2, second["cards"]))
        self.assertEqual(unchanged["cards"], second["cards"])
        deleted = self.store.save(self.payload(3, []))
        self.assertEqual(deleted["cards"], [])
        history = self.campaign / "card-design-draft-history"
        self.assertEqual(sorted(p.name for p in history.iterdir()), [f"revision-{i:08d}.json" for i in (1, 2, 3)])
        for snapshot in (first, second, unchanged):
            self.assertEqual(json.loads((history / f"revision-{snapshot['revision']:08d}.json").read_text()), snapshot)

    def test_separate_processes_cannot_overwrite_same_revision(self):
        context = multiprocessing.get_context("fork")
        queue, barrier = context.Queue(), context.Barrier(2)
        processes = [context.Process(target=concurrent_save,
                        args=(self.campaign, self.payload(cards=[self.card(name=name)]), barrier, queue))
                     for name in ("First tab", "Second tab")]
        for process in processes:
            process.start()
        try:
            results = [queue.get(timeout=8) for _ in processes]
        finally:
            for process in processes:
                process.join(8)
                if process.is_alive():
                    process.kill()
                    process.join()
        self.assertEqual(sorted(status for status, _ in results), [200, 409])
        winner = next(value for status, value in results if status == 200)
        conflict = next(value for status, value in results if status == 409)
        self.assertEqual(conflict["code"], "revision_conflict")
        self.assertEqual(conflict["draft"], winner)
        self.assertEqual(self.store.get(), winner)

    def test_strict_validation_rejects_lossy_invalid_or_oversized_editor_fields(self):
        invalid = [None, [], {"revision": True, "cards": []}, {"revision": 0, "cards": [], "other": 1},
                   self.payload(cards=[self.card()] * 2), self.payload(cards=[self.card(id=f"card-{i}") for i in range(101)]),
                   self.payload(cards=[{"id": "only-id"}])]
        for changes in ({"name": " "}, {"name": "x" * 161}, {"name": "\ud800"}, {"rules_text": "x" * 10001},
                        {"notes": "x" * 10001}, {"cost": True}, {"cost": 1.2}, {"cost": -1}, {"cost": 1000},
                        {"defense": -1}, {"shield": "3"}, {"quantity": 0}, {"quantity": 100}, {"faction": "Custom"},
                        {"faction": []}, {"type": "Spell"}, {"art_id": "../outside"}, {"source_proposal_id": 3},
                        {"id": "../escape"}, {"created_at": None}, {"created_at": "yesterday"}, {"unknown": 4}):
            invalid.append(self.payload(cards=[self.card(**changes)]))
        for payload in invalid:
            with self.subTest(payload=repr(payload)[:180]):
                with self.assertRaises(ReviewError) as error:
                    self.store.save(payload)
                self.assertEqual(error.exception.status, 400)
                self.assertFalse((self.campaign / "card-design-drafts.json").exists())

    def test_catalog_types_and_factions_are_accepted_and_text_remains_inert(self):
        cards = [self.card(id=f"catalog-{i}", faction=faction, type=kind, art_id="", source_proposal_id="",
                           rules_text="<script>window.bad=1</script>")
                 for i, (faction, kind) in enumerate(zip(sorted(d.FACTIONS), sorted(d.CARD_TYPES)))]
        saved = self.store.save(self.payload(cards=cards))
        self.assertEqual(saved["cards"][0]["rules_text"], "<script>window.bad=1</script>")
        self.assertEqual(len(saved["cards"]), len(cards))

    def test_corrupt_saved_drafts_are_never_overwritten(self):
        path = self.campaign / "card-design-drafts.json"
        for raw in ("broken", '{"schema":"unrecognized"}', '{"revision":1,"revision":2}'):
            path.write_text(raw)
            for operation in (self.store.get, lambda: self.store.save(self.payload())):
                with self.assertRaises(ReviewError) as error:
                    operation()
                self.assertEqual(error.exception.status, 503)
                self.assertEqual(path.read_text(), raw)

    def test_failed_publish_preserves_old_draft_and_retry_preserves_existing_snapshot(self):
        first = self.store.save(self.payload())
        replace = d.os.replace
        def failure(source, target, **kwargs):
            if target == "card-design-drafts.json":
                raise OSError("simulated publication failure")
            return replace(source, target, **kwargs)
        with patch.object(d.os, "replace", failure):
            with self.assertRaises(ReviewError):
                self.store.save(self.payload(1, []))
        self.assertEqual(self.store.get(), first)
        self.assertFalse(list(self.campaign.rglob("*.tmp")))
        snapshot = self.campaign / "card-design-draft-history/revision-00000001.json"
        original = snapshot.read_bytes()
        self.assertEqual(self.store.save(self.payload(1, []))["cards"], [])
        self.assertEqual(snapshot.read_bytes(), original)

    def test_symlink_targets_locks_campaign_and_history_are_rejected(self):
        outside = self.root / "outside.json"
        outside.write_text("untouched")
        for name in ("card-design-drafts.json", "card-design-drafts.lock"):
            link = self.campaign / name
            link.unlink(missing_ok=True)
            link.symlink_to(outside)
            with self.assertRaises(ReviewError):
                self.store.save(self.payload())
            self.assertEqual(outside.read_text(), "untouched")
            link.unlink()
        campaign_link = self.root / "campaign-link"
        campaign_link.symlink_to(self.campaign, target_is_directory=True)
        with self.assertRaises(ReviewError):
            d.CardDraftStore(campaign_link).get()
        first = self.store.save(self.payload())
        (self.campaign / "card-design-draft-history").symlink_to(self.root, target_is_directory=True)
        with self.assertRaises(ReviewError):
            self.store.save(self.payload(1, []))
        self.assertEqual(self.store.get(), first)


class HttpTests(DraftFixture):
    def setUp(self):
        super().setUp()
        assets = self.root / "Tools/TrainingPreflight"
        assets.mkdir(parents=True)
        for name, data in (("balance_proposals.html", "<h1>Card proposals</h1>"),
                           ("balance_proposals.css", "body { color: white; }"),
                           ("balance_proposals.js", "'use strict';")):
            (assets / name).write_text(data)
        previews = assets.parent / "BalanceReview"
        previews.mkdir()
        (previews / "balance_card_previews.json").write_text('{"version":"previews-v1"}')
        (previews / "balance_proposals.json").write_text('{"version":"proposal-v1","proposals":[{"id":"giga"}]}')
        here = patch.object(m, "HERE", assets)
        here.start()
        self.addCleanup(here.stop)
        self.catalog = {"cards": [{"id": "frozen_card", "name": "Frozen card"}], "heroes": []}
        self.fallback = {"cards": [{"id": "fallback_card"}], "heroes": []}
        self.monitor = SimpleNamespace(campaign=self.campaign, card_artwork=SimpleNamespace(presentation=lambda: self.fallback))
        self.server = m.make_server(self.monitor, port=0)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.addCleanup(self.stop_server)
        self.origin = "http://127.0.0.1:" + str(self.server.server_port)

    def stop_server(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(3)

    def request(self, method="GET", path="/api/card-drafts", data=None, headers=None, raw=None):
        connection = http.client.HTTPConnection(*self.server.server_address, timeout=3)
        body = raw if raw is not None else json.dumps(data).encode() if data is not None else None
        request_headers = {"Content-Type": "application/json", "Origin": self.origin} if method == "POST" else {}
        request_headers.update(headers or {})
        connection.request(method, path, body=body, headers=request_headers)
        response = connection.getresponse()
        result = response.status, response.read(), dict(response.getheaders())
        connection.close()
        return result

    def test_http_save_reload_conflict_delete_and_feedback_isolation(self):
        status, body, headers = self.request()
        self.assertEqual(status, 200)
        self.assertEqual(headers["Cache-Control"], "no-store")
        self.assertEqual(json.loads(body)["revision"], 0)
        self.assertEqual(list(self.campaign.iterdir()), [])
        status, body, _ = self.request("POST", data=self.payload())
        self.assertEqual(status, 200)
        saved = json.loads(body)
        self.assertEqual(json.loads(self.request()[1]), saved)
        status, body, _ = self.request("POST", data=self.payload())
        self.assertEqual(status, 409)
        self.assertEqual(json.loads(body)["draft"], saved)
        self.assertEqual(self.request("POST", data=self.payload(1, []))[0], 200)
        self.assertEqual(json.loads(self.request()[1])["cards"], [])
        self.assertEqual(json.loads(self.request(path="/api/balance-proposals")[1])["review"]["revision"], 0)
        self.assertFalse((self.campaign / "balance-proposal-reviews.json").exists())

    def test_new_read_routes_only_serve_fixed_assets_and_catalog_fallback(self):
        for route, kind in (("/balance_proposals.css", "text/css"), ("/balance_proposals.js", "text/javascript")):
            status, _, headers = self.request(path=route)
            self.assertEqual(status, 200)
            self.assertIn(kind, headers["Content-Type"])
        self.assertEqual(json.loads(self.request(path="/api/card-catalog")[1]), self.fallback)
        (self.campaign / "card-catalog.json").write_text(json.dumps(self.catalog))
        self.assertEqual(json.loads(self.request(path="/api/card-catalog?path=/etc/passwd")[1]), self.catalog)
        self.assertEqual(json.loads(self.request(path="/api/balance-card-previews?path=/etc/passwd")[1]), {"version": "previews-v1"})
        self.assertEqual(self.request(path="/../balance_proposals.css")[0], 404)
        (m.HERE / "balance_proposals.css").unlink()
        (m.HERE / "balance_proposals.css").symlink_to(self.campaign / "card-catalog.json")
        self.assertEqual(self.request(path="/balance_proposals.css")[0], 404)

    def test_cross_origin_non_json_oversized_and_invalid_requests_never_write(self):
        cases = [({"Origin": "https://evil.example"}, 403), ({"Origin": "null"}, 403),
                 ({"Host": "evil.example", "Origin": "http://evil.example"}, 403),
                 ({"Sec-Fetch-Site": "cross-site"}, 403), ({"Content-Type": "text/plain"}, 415),
                 ({"Content-Length": str(MAX_REQUEST_BYTES + 1)}, 413), ({"Transfer-Encoding": "chunked"}, 400)]
        for headers, expected in cases:
            with self.subTest(headers=headers):
                self.assertEqual(self.request("POST", data=self.payload(), headers=headers)[0], expected)
        for raw in (b'{"revision":0,"revision":1}', b'{"revision":NaN}', b'{', b'[]'):
            self.assertEqual(self.request("POST", raw=raw)[0], 400)
        self.assertEqual(self.request(headers={"Host": "evil.example"})[0], 403)
        self.assertFalse((self.campaign / "card-design-drafts.json").exists())


if __name__ == "__main__":
    unittest.main()
