"""Feedback must survive refreshes, stale tabs, new proposals, and failed writes."""
from concurrent.futures import ThreadPoolExecutor
import http.client
import json
from pathlib import Path
import tempfile
import threading
import unittest
from unittest.mock import patch

import balance_review_store as b
import monitor_training as m


class ReviewFixture(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.campaign = self.root / "campaign"
        self.campaign.mkdir()
        self.manifest = self.root / "balance_proposals.json"
        self.proposal = {"version": "balance-v1", "proposals": [{"id": "giga", "title": "Giga"}, {"id": "brain", "title": "Brain"}]}
        self.manifest.write_text(json.dumps(self.proposal))
        self.store = b.BalanceReviewStore(self.campaign, self.manifest)

    def payload(self, revision=0, *, submit=False, comment="Keep the draw; reduce mastery."):
        return {"proposal_version": self.proposal["version"], "revision": revision,
                "entries": {"giga": {"decision": "change", "comment": comment}},
                "general_comment": "Check the Crown / Talons interaction — ensemble.", "submit": submit}

    def saved(self):
        return json.loads((self.campaign / "balance-proposal-reviews.json").read_text())


class StoreTests(ReviewFixture):
    def test_get_is_read_only_and_save_survives_restart(self):
        result = self.store.get()
        self.assertEqual(result["proposal"], self.proposal)
        self.assertEqual(result["review"]["revision"], 0)
        self.assertEqual(result["review"]["entries"]["brain"], {"decision": "unreviewed", "comment": ""})
        self.assertEqual(list(self.campaign.iterdir()), [])
        review = self.store.save(self.payload())["review"]
        self.assertEqual(review["revision"], 1)
        self.assertIsNotNone(review["updated_at"])
        self.assertIsNone(review["submitted_at"])
        restarted = b.BalanceReviewStore(self.campaign, self.manifest)
        self.assertEqual(restarted.get()["review"], review)

    def test_submissions_are_preserved_when_draft_is_edited_and_resubmitted(self):
        submitted = self.store.save(self.payload(submit=True))["review"]
        self.assertIsNotNone(submitted["submitted_at"])
        draft = self.store.save(self.payload(1, comment="Actually, test price instead."))["review"]
        self.assertIsNone(draft["submitted_at"])
        second = self.store.save(self.payload(2, submit=True, comment="Final comment"))["review"]
        version = self.saved()["versions"]["balance-v1"]
        self.assertEqual(version["submissions"], [submitted, second])
        self.assertEqual(version["review"], second)

    def test_new_proposal_version_keeps_prior_reviews_and_rejects_old_tabs(self):
        old = self.store.save(self.payload(submit=True))["review"]
        before = (self.campaign / "balance-proposal-reviews.json").read_bytes()
        old_payload = self.payload(1)
        self.proposal = {"version": "balance-v2", "proposals": [{"id": "crown", "title": "Crown"}]}
        self.manifest.write_text(json.dumps(self.proposal))
        current = self.store.get()
        self.assertEqual(current["review"]["revision"], 0)
        self.assertEqual((self.campaign / "balance-proposal-reviews.json").read_bytes(), before)
        with self.assertRaises(b.ReviewError) as conflict:
            self.store.save(old_payload)
        self.assertEqual(conflict.exception.status, 409)
        self.assertEqual(conflict.exception.payload["code"], "proposal_version_conflict")
        self.assertEqual(conflict.exception.payload["proposal"], self.proposal)
        payload = {"proposal_version": "balance-v2", "revision": 0, "entries": {}, "general_comment": "New version", "submit": False}
        self.store.save(payload)
        versions = self.saved()["versions"]
        self.assertEqual(set(versions), {"balance-v1", "balance-v2"})
        self.assertEqual(versions["balance-v1"]["review"], old)
        self.assertEqual(versions["balance-v1"]["submissions"], [old])

    def test_same_version_cannot_reinterpret_saved_feedback(self):
        self.store.save(self.payload())
        self.proposal["proposals"][0]["title"] = "Changed rule"
        self.manifest.write_text(json.dumps(self.proposal))
        for operation in (self.store.get, lambda: self.store.save(self.payload(1))):
            with self.assertRaises(b.ReviewError) as conflict:
                operation()
            self.assertEqual(conflict.exception.status, 409)
            self.assertEqual(conflict.exception.payload["code"], "proposal_content_conflict")
        self.assertEqual(self.saved()["versions"]["balance-v1"]["proposal"]["proposals"][0]["title"], "Giga")

    def test_concurrent_tabs_only_one_writer_wins_even_with_separate_stores(self):
        barrier = threading.Barrier(2)
        def write(comment):
            store = b.BalanceReviewStore(self.campaign, self.manifest)
            barrier.wait()
            try:
                return 200, store.save(self.payload(comment=comment))
            except b.ReviewError as error:
                return error.status, error.payload
        with ThreadPoolExecutor(max_workers=2) as pool:
            results = list(pool.map(write, ("First tab", "Second tab")))
        self.assertEqual(sorted(status for status, _ in results), [200, 409])
        winner = next(value["review"] for status, value in results if status == 200)
        conflict = next(value for status, value in results if status == 409)
        self.assertEqual(conflict["code"], "revision_conflict")
        self.assertEqual(conflict["review"], winner)
        self.assertEqual(self.store.get()["review"], winner)

    def test_invalid_payloads_never_create_feedback(self):
        invalid = [None, [], {**self.payload(), "revision": True}, {**self.payload(), "submit": 1},
                   {**self.payload(), "unknown": "ignored?"}, {**self.payload(), "general_comment": "x" * (b.MAX_GENERAL_CHARS + 1)},
                   {**self.payload(), "entries": {"../outside": {"decision": "accept", "comment": ""}}},
                   {**self.payload(), "entries": {"giga": {"decision": ["accept"], "comment": ""}}},
                   {**self.payload(), "entries": {"giga": {"decision": "accept", "comment": "x" * (b.MAX_COMMENT_CHARS + 1)}}},
                   {**self.payload(), "entries": {"giga": {"decision": "accept", "comment": "\ud800"}}}]
        for payload in invalid:
            with self.subTest(payload_type=type(payload)):
                with self.assertRaises(b.ReviewError) as invalid_review:
                    self.store.save(payload)
                self.assertEqual(invalid_review.exception.status, 400)
                self.assertFalse((self.campaign / "balance-proposal-reviews.json").exists())

    def test_corrupt_store_is_never_overwritten(self):
        path = self.campaign / "balance-proposal-reviews.json"
        path.write_text("broken existing feedback")
        for operation in (self.store.get, lambda: self.store.save(self.payload())):
            with self.assertRaises(b.ReviewError) as error:
                operation()
            self.assertEqual(error.exception.status, 503)
        self.assertEqual(path.read_text(), "broken existing feedback")

    def test_failed_atomic_publish_preserves_previous_feedback_and_removes_temp(self):
        old = self.store.save(self.payload())["review"]
        with patch.object(b.os, "replace", side_effect=OSError("simulated publication failure")):
            with self.assertRaises(b.ReviewError):
                self.store.save(self.payload(1, comment="Lost connection"))
        self.assertEqual(self.store.get()["review"], old)
        self.assertFalse(list(self.campaign.glob("*.tmp")))

    def test_symlink_review_lock_manifest_and_campaign_are_rejected(self):
        outside = self.root / "outside.json"
        outside.write_text("must remain untouched")
        for name in ("balance-proposal-reviews.json", "balance-proposal-reviews.lock"):
            link = self.campaign / name
            link.unlink(missing_ok=True)
            link.symlink_to(outside)
            with self.assertRaises(b.ReviewError):
                self.store.save(self.payload())
            self.assertEqual(outside.read_text(), "must remain untouched")
            link.unlink()
        manifest_link = self.root / "manifest-link.json"
        manifest_link.symlink_to(self.manifest)
        with self.assertRaises(b.ReviewError):
            b.BalanceReviewStore(self.campaign, manifest_link).get()
        campaign_link = self.root / "campaign-link"
        campaign_link.symlink_to(self.campaign, target_is_directory=True)
        with self.assertRaises(b.ReviewError):
            b.BalanceReviewStore(campaign_link, self.manifest).get()


class CardReviewTests(ReviewFixture):
    def setUp(self):
        super().setUp()
        self.proposal = {"version": "balance-v1", "proposals": [
            {"id": "crown-draw", "title": "Crown / Talons"},
            {"id": "early-order-hold", "title": "Early Order"},
            {"id": "giga", "title": "Giga"}]}
        self.manifest.write_text(json.dumps(self.proposal))
        self.previews = {"proposal_version": "balance-v1", "proposals": {
            "crown-draw": {"cards": [
                {"after": {"id": "panconscious_crown_duel"}}, {"after": {"id": "entropic_talons"}}]},
            "early-order-hold": {"cards": [
                {"after": {"id": "order_initiate_duel"}}, {"after": None, "before": {"id": "shard_seer"}}]},
            "giga": {"cards": [{"after": {"id": "giga_source_adept"}}]}}}
        self.preview_path = self.manifest.with_name("balance_card_previews.json")
        self.preview_path.write_text(json.dumps(self.previews))

    def card_payload(self, revision=0, *, submit=False):
        return {**self.payload(revision, submit=submit), "card_entries": {
            "crown-draw": {
                "panconscious_crown_duel": {"decision": "accept", "comment": "Keep Crown's new draw."},
                "entropic_talons": {"decision": "change", "comment": "Reduce Talons separately."}},
            "early-order-hold": {"shard_seer": {"decision": "reject", "comment": "Seer only."}}}}

    def test_card_reviews_are_independent_and_scopes_match_preview_cards(self):
        initial = self.store.get()
        self.assertEqual(initial["card_review_scopes"], {
            "crown-draw": ["panconscious_crown_duel", "entropic_talons"],
            "early-order-hold": ["order_initiate_duel", "shard_seer"]})
        self.assertEqual(initial["review"]["card_entries"], {})
        self.assertEqual(list(self.campaign.iterdir()), [])
        saved = self.store.save(self.card_payload(submit=True))["review"]
        edited = self.card_payload(1)
        edited["card_entries"]["crown-draw"]["panconscious_crown_duel"] = {
            "decision": "reject", "comment": "Crown needs another design."}
        current = self.store.save(edited)["review"]
        self.assertEqual(current["card_entries"]["crown-draw"]["entropic_talons"],
                         saved["card_entries"]["crown-draw"]["entropic_talons"])
        self.assertEqual(current["card_entries"]["early-order-hold"], saved["card_entries"]["early-order-hold"])
        self.assertEqual(current["entries"], saved["entries"])
        self.assertEqual(b.BalanceReviewStore(self.campaign, self.manifest).get()["review"], current)
        self.assertEqual(self.saved()["versions"]["balance-v1"]["submissions"], [saved])

    def test_old_client_omitting_cards_preserves_cards_and_group_history(self):
        first = self.store.save(self.card_payload(submit=True))["review"]
        second = self.store.save(self.payload(1, submit=True, comment="Updated Giga in an older tab"))["review"]
        self.assertEqual(second["card_entries"], first["card_entries"])
        self.assertEqual(second["entries"]["giga"]["comment"], "Updated Giga in an older tab")
        self.assertEqual(self.saved()["versions"]["balance-v1"]["submissions"], [first, second])

    def test_legacy_review_get_normalizes_only_response_and_preserves_snapshots(self):
        self.store.save(self.payload(submit=True))
        path = self.campaign / "balance-proposal-reviews.json"
        data = self.saved()
        version = data["versions"]["balance-v1"]
        version["review"].pop("card_entries")
        version["submissions"][0].pop("card_entries")
        legacy_entry = {"decision": "change", "comment": "Original shared Crown and Talons feedback."}
        version["review"]["entries"]["crown-draw"] = legacy_entry
        version["submissions"][0]["entries"]["crown-draw"] = legacy_entry
        path.write_text(json.dumps(data))
        before = path.read_bytes()
        loaded = self.store.get()["review"]
        self.assertEqual(loaded["card_entries"], {})
        self.assertEqual(loaded["entries"]["crown-draw"], legacy_entry)
        self.assertEqual(path.read_bytes(), before)
        payload = self.card_payload(1)
        payload["entries"] = loaded["entries"]
        self.store.save(payload)
        self.assertEqual(self.saved()["versions"]["balance-v1"]["review"]["entries"]["crown-draw"], legacy_entry)
        self.assertEqual(self.saved()["versions"]["balance-v1"]["submissions"], version["submissions"])

    def test_invalid_card_scopes_and_text_preserve_existing_feedback(self):
        self.store.save(self.card_payload())
        path = self.campaign / "balance-proposal-reviews.json"
        before = path.read_bytes()
        item = {"decision": "accept", "comment": ""}
        invalid = [None, [], {"missing": {}}, {"giga": {"giga_source_adept": item}},
                   {"crown-draw": []}, {"crown-draw": {"shard_seer": item}},
                   {"crown-draw": {"../entropic_talons": item}},
                   {"crown-draw": {"entropic_talons": {"decision": "maybe", "comment": ""}}},
                   {"crown-draw": {"entropic_talons": {"decision": "accept", "comment": 5}}},
                   {"crown-draw": {"entropic_talons": {"decision": "accept", "comment": "\ud800"}}},
                   {"crown-draw": {"entropic_talons": {"decision": "accept", "comment": "x" * (b.MAX_COMMENT_CHARS + 1)}}},
                   {"crown-draw": {"entropic_talons": {**item, "extra": "discard?"}}}]
        for cards in invalid:
            with self.subTest(cards=repr(cards)[:120]):
                with self.assertRaises(b.ReviewError) as error:
                    self.store.save({**self.card_payload(1), "card_entries": cards})
                self.assertEqual(error.exception.status, 400)
                self.assertEqual(path.read_bytes(), before)

    def test_preview_version_missing_and_invalid_sources_fail_closed(self):
        self.previews["proposal_version"] = "another-version"
        self.preview_path.write_text(json.dumps(self.previews))
        self.assertEqual(self.store.get()["card_review_scopes"], {})
        with self.assertRaises(b.ReviewError) as error:
            self.store.save(self.card_payload())
        self.assertEqual(error.exception.status, 400)
        self.preview_path.unlink()
        self.assertEqual(self.store.get()["card_review_scopes"], {})
        with self.assertRaises(b.ReviewError):
            self.store.save(self.card_payload())
        self.preview_path.symlink_to(self.manifest)
        with self.assertRaises(b.ReviewError) as error:
            self.store.get()
        self.assertEqual(error.exception.status, 503)
        self.assertFalse((self.campaign / "balance-proposal-reviews.json").exists())

    def test_same_card_id_twice_in_one_group_is_rejected(self):
        pairs = self.previews["proposals"]["crown-draw"]["cards"]
        pairs[1] = pairs[0]
        self.preview_path.write_text(json.dumps(self.previews))
        with self.assertRaises(b.ReviewError) as error:
            self.store.get()
        self.assertEqual(error.exception.status, 503)

    def test_card_revisions_and_failed_atomic_publication_preserve_all_scopes(self):
        first = self.store.save(self.card_payload())["review"]
        with self.assertRaises(b.ReviewError) as conflict:
            self.store.save(self.card_payload())
        self.assertEqual(conflict.exception.status, 409)
        self.assertEqual(conflict.exception.payload["review"]["card_entries"], first["card_entries"])
        with patch.object(b.os, "replace", side_effect=OSError("simulated publication failure")):
            with self.assertRaises(b.ReviewError):
                self.store.save({**self.card_payload(1), "card_entries": {}})
        self.assertEqual(self.store.get()["review"], first)

    def test_every_real_multi_card_group_accepts_distinct_card_feedback(self):
        source = Path(__file__).resolve().parent.parent / "BalanceReview"
        self.proposal = json.loads((source / "balance_proposals.json").read_text())
        self.manifest.write_text(json.dumps(self.proposal))
        self.preview_path.write_bytes((source / "balance_card_previews.json").read_bytes())
        scopes = self.store.get()["card_review_scopes"]
        self.assertIn("entropic_talons", scopes["crown-draw"])
        self.assertIn("early-order-hold", scopes)
        self.assertIn("rare-relics-hold", scopes)
        cards = {group: {card: {"decision": "change", "comment": f"Only {group}/{card}"} for card in ids}
                 for group, ids in scopes.items()}
        payload = {"proposal_version": self.proposal["version"], "revision": 0, "entries": {},
                   "card_entries": cards, "general_comment": "", "submit": True}
        saved = self.store.save(payload)["review"]
        self.assertEqual(saved["card_entries"], cards)
        self.assertEqual(self.store.get()["review"]["card_entries"], cards)


class HttpTests(ReviewFixture):
    def setUp(self):
        super().setUp()
        assets = self.root / "Tools/TrainingPreflight"
        assets.mkdir(parents=True)
        (assets / "balance_proposals.html").write_text("<h1>Balance proposals</h1>")
        manifest = assets.parent / "BalanceReview/balance_proposals.json"
        manifest.parent.mkdir()
        manifest.write_bytes(self.manifest.read_bytes())
        here = patch.object(m, "HERE", assets)
        here.start()
        self.addCleanup(here.stop)
        self.server = m.make_server(m.Monitor(self.campaign), port=0)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.addCleanup(self.stop_server)
        self.origin = "http://127.0.0.1:" + str(self.server.server_port)

    def stop_server(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(3)

    def request(self, method="GET", path="/api/balance-proposals", data=None, headers=None, raw=None):
        connection = http.client.HTTPConnection(*self.server.server_address, timeout=3)
        body = raw if raw is not None else json.dumps(data).encode() if data is not None else None
        request_headers = {"Content-Type": "application/json", "Origin": self.origin} if method == "POST" else {}
        request_headers.update(headers or {})
        connection.request(method, path, body=body, headers=request_headers)
        response = connection.getresponse()
        result = response.status, response.read(), dict(response.getheaders())
        connection.close()
        return result

    def test_http_page_default_save_reload_and_stale_revision(self):
        for route in ("/balance-proposals", "/balance-proposals/"):
            status, body, _ = self.request(path=route)
            self.assertEqual(status, 200)
            self.assertIn(b"Balance proposals", body)
        status, body, headers = self.request()
        self.assertEqual(status, 200)
        self.assertEqual(headers["Cache-Control"], "no-store")
        self.assertEqual(json.loads(body)["review"]["revision"], 0)
        self.assertFalse(list(self.campaign.iterdir()))
        status, body, _ = self.request("POST", "/api/balance-reviews", self.payload(submit=True))
        self.assertEqual(status, 200)
        review = json.loads(body)["review"]
        self.assertIsNotNone(review["submitted_at"])
        self.assertEqual(json.loads(self.request()[1])["review"], review)
        status, body, _ = self.request("POST", "/api/balance-reviews", self.payload())
        self.assertEqual(status, 409)
        self.assertEqual(json.loads(body)["review"], review)

    def test_cross_site_invalid_host_and_non_json_writes_are_rejected(self):
        cases = [({"Origin": "https://evil.example"}, 403), ({"Origin": "null"}, 403),
                 ({"Origin": ""}, 403), ({"Origin": self.origin + "/"}, 403),
                 ({"Host": "evil.example", "Origin": "http://evil.example"}, 403),
                 ({"Host": "localhost:123:456", "Origin": "http://localhost:123:456"}, 403),
                 ({"Sec-Fetch-Site": "cross-site"}, 403), ({"Content-Type": "text/plain"}, 415),
                 ({"Content-Length": str(b.MAX_REQUEST_BYTES + 1)}, 413),
                 ({"Transfer-Encoding": "chunked"}, 400)]
        for headers, expected in cases:
            with self.subTest(headers=headers):
                self.assertEqual(self.request("POST", "/api/balance-reviews", self.payload(), headers)[0], expected)
        self.assertFalse(list(self.campaign.iterdir()))

    def test_bad_json_and_wrong_route_do_not_modify_feedback(self):
        for raw in (b'{"revision":0,"revision":1}', b'{"revision":NaN}', b'{', b'[]'):
            self.assertEqual(self.request("POST", "/api/balance-reviews", raw=raw)[0], 400)
        self.assertEqual(self.request("POST", "/api/arbitrary-file", self.payload())[0], 404)
        self.assertFalse(list(self.campaign.iterdir()))


if __name__ == "__main__":
    unittest.main()
