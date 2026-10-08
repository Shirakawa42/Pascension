"""Exact rolling eviction, seat credit, compact safe persistence and host mapping."""
import copy
from collections import Counter
import io
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import numpy as np
import torch
from game_stats import RollingGameStats
from host import BINARY, Host


CATALOG = {"observation_schema": "shards-zero-depth-observation-v2", "card_ids": ["crystal", "blaster"],
           "heroes": [{"id": "decima"}, {"id": "tetra"}],
           "histogram_descriptors": [{"name": "own_permanent_collection", "offset": 180, "length": 2, "scale": 10},
                                     {"name": "opponent_permanent_collection", "offset": 184, "length": 2, "scale": 10}]}


def cohort(winners, *, actors=None, done=None):
    size = len(winners)
    actors = np.array(actors if actors is not None else [0] * size, np.int32)
    host = SimpleNamespace(done=np.array(done if done is not None else [1] * size, np.int32),
                           actors=actors, rewards=np.zeros((size, 2), np.float32), obs=np.zeros((size, 200), np.float32))
    for lane, winner in enumerate(winners):
        if winner in (0, 1):
            host.rewards[lane, winner] = 1
            host.rewards[lane, 1 - winner] = -1
        host.obs[lane, 2] = (lane + 10) / 100
        for seat in (0, 1):
            start = 16 + (0 if actors[lane] == seat else 64)
            host.obs[lane, start + 6] = (seat + 1) / 5
            host.obs[lane, start + 1] = (seat * 10 + lane) / 30
            host.obs[lane, start] = (40 if winner == seat else -3) / 50
            offset = 180 if actors[lane] == seat else 184
            host.obs[lane, offset:offset + 2] = [.7, .3] if seat == 0 else [0, .2]
    return host


class RollingGameStatsTests(unittest.TestCase):
    def test_rounds_separate_selfplay_archive_and_heroes_with_exact_eviction_and_resume(self):
        stats = RollingGameStats(CATALOG, window=3)
        stats.add_cohort(cohort([0, 1, -1, 0, 1, 0], done=[1, 1, 1, 1, 1, 2]),
                         [False, True, False, True, True, False], [0, 0, 0, 1, 1, 0])
        value = stats.snapshot()
        self.assertEqual(value['mean_rounds_by_mode'], {'selfplay': 12, 'archive': 13.5})
        self.assertEqual(value['rounds_game_coverage_by_mode'], {'selfplay': 1, 'archive': 2})
        self.assertEqual([hero['mean_rounds'] for hero in value['heroes']], [13, 13])
        self.assertEqual(next(row['mean_rounds'] for row in value['hero_matchups'] if row['games']), 13)
        restored = RollingGameStats.from_state(CATALOG, stats.state_dict())
        self.assertEqual(restored.state_dict(), stats.state_dict())
        self.assertEqual(restored.snapshot(), value)
        for ring in (stats, restored):
            ring.add_cohort(cohort([0]), [False], [0])
        self.assertEqual(restored.snapshot(), stats.snapshot())
        self.assertEqual(stats.snapshot()['mean_rounds_by_mode'], {'selfplay': 10, 'archive': 13.5})

    def test_exact_last_games_evict_old_outcomes_and_card_associations(self):
        stats = RollingGameStats(CATALOG, window=3)
        stats.add_cohort(cohort([0, 1, -1, 0, 1, 0], done=[1, 1, 1, 1, 1, 2]),
                         np.array([False, True, False, True, True, False]), np.array([0, 0, 0, 1, 1, 0]))
        value = stats.snapshot()
        self.assertEqual(value["games_in_window"], 3)
        self.assertEqual((value["total_natural_games"], value["total_censored_games"]), (5, 1))
        self.assertEqual((value["totals"]["seat0_wins"], value["totals"]["seat1_wins"], value["totals"]["draws"]), (1, 1, 1))
        self.assertEqual((value["totals"]["archive_wins"], value["totals"]["archive_losses"]), (1, 1))
        self.assertEqual(value["totals"]["selfplay_draws"], 1)
        self.assertEqual(value["mean_rounds"], 13)
        crystal, blaster = value["cards"]
        self.assertEqual((crystal["observations"], crystal["wins"], crystal["draws"], crystal["losses"]), (3, 1, 1, 1))
        self.assertEqual((blaster["observations"], blaster["wins"], blaster["draws"], blaster["losses"]), (6, 2, 2, 2))

    def test_terminal_relative_actor_maps_both_global_seats_and_owns_data(self):
        stats = RollingGameStats(CATALOG, window=4)
        host = cohort([1], actors=[1])
        stats.add_cohort(host, [True], [1])
        expected = stats.snapshot()
        self.assertEqual(expected["totals"]["archive_wins"], 1)
        self.assertEqual(expected["mean_mastery_by_seat"], [0, 10])
        self.assertEqual(expected["mean_collection_size_by_seat"], [10, 2])
        self.assertEqual(expected["heroes"][0]["losses"], 1)
        self.assertEqual(expected["heroes"][1]["wins"], 1)
        host.obs[:] = 71
        host.rewards[:] = 0
        self.assertEqual(stats.snapshot(), expected)

    def test_resume_after_ring_wrap_matches_uninterrupted_updates(self):
        stats = RollingGameStats(CATALOG, window=3)
        stats.add_cohort(cohort([0, 1, 0, -1]), [False] * 4, [0, 1, 0, 1])
        restored = RollingGameStats.from_state(CATALOG, stats.state_dict())
        self.assertEqual(restored.snapshot(), stats.snapshot())
        for ring in (stats, restored):
            ring.add_cohort(cohort([1, 1], actors=[1, 0]), [True, False], [0, 0])
        self.assertEqual(restored.snapshot(), stats.snapshot())
        self.assertEqual(restored.state_dict(), stats.state_dict())

    def test_empty_and_nonempty_state_roundtrip_through_safe_torch_loader(self):
        stats = RollingGameStats(CATALOG, window=5)
        for nonempty in (False, True):
            if nonempty:
                stats.add_cohort(cohort([0]), [False], [0])
            buffer = io.BytesIO()
            torch.save(stats.state_dict(), buffer)
            buffer.seek(0)
            restored = RollingGameStats.from_state(CATALOG, torch.load(buffer, weights_only=True))
            self.assertEqual(restored.snapshot(), stats.snapshot())

    def test_invalid_cohort_does_not_partially_mutate_ring(self):
        stats = RollingGameStats(CATALOG, window=3)
        original = stats.state_dict()
        invalid = cohort([0, 1])
        invalid.rewards[1] = [1, 1]
        with self.assertRaises(ValueError):
            stats.add_cohort(invalid, [False, False], [0, 1])
        self.assertEqual(stats.state_dict(), original)
        invalid.done[0] = 0
        with self.assertRaises(ValueError):
            stats.add_cohort(invalid, [False, False], [0, 1])
        self.assertEqual(stats.state_dict(), original)

    def test_checkpoint_catalog_drift_and_malformed_ring_rejected(self):
        stats = RollingGameStats(CATALOG, window=3)
        stats.add_cohort(cohort([0]), [False], [0])
        with self.assertRaises(ValueError):
            RollingGameStats.from_state({**CATALOG, "card_ids": ["changed", "blaster"]}, stats.state_dict())
        for changes in ({"head": 2}, {"records": None}, {"count": 4}, {"records": b"short"}):
            with self.subTest(changes=changes), self.assertRaises(ValueError):
                RollingGameStats.from_state(CATALOG, stats.state_dict() | changes)

    def test_censors_are_never_draws_and_minimal_catalog_omits_unavailable_features(self):
        stats = RollingGameStats({"card_ids": ["crystal"]}, window=2)
        host = SimpleNamespace(done=[2, 1], rewards=[[0, 0], [0, 0]])
        stats.add_cohort(host, [True, False], [0, 1])
        value = stats.snapshot()
        self.assertEqual((value["games_in_window"], value["total_censored_games"], value["totals"]["draws"]), (1, 1, 1))
        self.assertIsNone(value["mean_rounds"])
        self.assertIsNone(value["totals"]["archive_score"])
        self.assertEqual(value["cards"][0]["observations"], 0)


class MatchupCoverageTests(unittest.TestCase):
    def host(self, records):
        size=len(records);host=SimpleNamespace(done=np.ones(size,np.int32),actors=np.arange(size,dtype=np.int32)%2,rewards=np.zeros((size,2),np.float32),obs=np.zeros((size,100),np.float32))
        for lane,(hero0,hero1,winner,status) in enumerate(records):
            host.done[lane]=status
            if winner in (0,1):host.rewards[lane,winner]=1;host.rewards[lane,1-winner]=-1
            for seat,hero in enumerate((hero0,hero1)):
                start=16+(0 if host.actors[lane]==seat else 64);host.obs[lane,start+6]=(hero+1)/5
        return host
    def test_matchups_eviction_and_restore_match_independent_last_games_oracle(self):
        ids=['decima','tetra','volos','kosynwu','rez'];catalog={'observation_schema':'shards-zero-depth-observation-v2','heroes':[{'id':hero} for hero in ids]};stats=RollingGameStats(catalog,window=23);history=[]
        records=[(a,b,[-1,0,1][(a*5+b)%3],1) for a in range(5) for b in range(5) if a!=b];records.extend([(0,0,1,1),(4,1,0,2),(2,3,0,1),(3,4,-1,1),(4,0,1,1),(0,2,0,1),(1,4,1,1)])
        for start in (0,11,20):
            end={0:11,11:20,20:len(records)}[start];batch=records[start:end];stats.add_cohort(self.host(batch),[False]*len(batch),[0]*len(batch));history.extend(row for row in batch if row[3]==1);window=history[-23:];expected=Counter((ids[a],ids[b],w) for a,b,w,_ in window);value=stats.snapshot();self.assertEqual(value['hero_matchup_game_coverage'],len(window));self.assertEqual(len(value['hero_matchups']),25)
            for row in value['hero_matchups']:
                pair=(row['seat0_hero'],row['seat1_hero']);self.assertEqual((row['games'],row['seat0_wins'],row['seat1_wins'],row['draws']),(sum(expected[(*pair,w)] for w in (-1,0,1)),expected[(*pair,0)],expected[(*pair,1)],expected[(*pair,-1)]))
            for hero in value['heroes']:
                index=ids.index(hero['id']);self.assertEqual(hero['seat0_games'],sum(a==index for a,b,w,status in window));self.assertEqual(hero['seat1_games'],sum(b==index for a,b,w,status in window))
            restored=RollingGameStats.from_state(catalog,stats.state_dict());self.assertEqual(restored.snapshot(),value);self.assertEqual(restored.state_dict(),stats.state_dict())
        self.assertEqual(stats.total_censored_games,1)
    def test_existing_ring_wire_and_schema_stay_unchanged(self):
        # The existing fixed record size/schema and state keys are the wire
        # contract; derived matchup counters must reconstruct from those bytes.
        stats=RollingGameStats(CATALOG,window=3);stats.add_cohort(cohort([0,1,-1,0]),[False]*4,[0,1,0,1]);state=stats.state_dict();self.assertEqual(state['schema'],'shards-zero-depth-rolling-games-v1');self.assertEqual(len(state['records']),3*(34+2));self.assertEqual(set(state),{'schema','catalog_signature','window','head','count','total_natural_games','total_censored_games','records'});restored=RollingGameStats.from_state(CATALOG,state);self.assertEqual(restored.state_dict(),state);self.assertEqual(restored.snapshot(),stats.snapshot())
    def test_unavailable_hero_features_do_not_fabricate_matchup_coverage(self):
        stats=RollingGameStats({},window=2);stats.add_cohort(SimpleNamespace(done=[1,2],rewards=[[0,0],[0,0]]),[False,False],[0,1]);value=stats.snapshot();self.assertEqual(value['hero_matchups'],[]);self.assertEqual(value['hero_matchup_game_coverage'],0);self.assertEqual(value['total_censored_games'],1)


@unittest.skipUnless(BINARY.exists(), "Build the frozen headless host before terminal-feature integration")
class RealTerminalFeaturesTests(unittest.TestCase):
    def test_real_host_public_terminal_scalars_and_permanent_collections(self):
        with Host(batch=2, workers=1, seed=0x2200000000007710) as host:
            # Select the first legal hero options, then concede at the first
            # priority. These actual terminals only validate extraction; no
            # optimizer or training session is constructed.
            for _ in range(30):
                if host.done.all():
                    break
                actions = np.full(2, -1, np.int32)
                for lane in np.flatnonzero(host.done == 0):
                    concede = np.flatnonzero((host.candidates[lane, :, 11] == 1) & (host.mask[lane] == 1))
                    desired_hero = .2 if host.actors[lane] == 1 else .4
                    hero = np.flatnonzero(np.isclose(host.candidates[lane, :, 36], desired_hero) & (host.mask[lane] == 1))
                    actions[lane] = concede[0] if concede.size else hero[0] if hero.size else np.flatnonzero(host.mask[lane] == 1)[0]
                host.advance(actions)
            self.assertTrue((host.done == 1).all())
            self.assertEqual([hero["id"] for hero in host.catalog["heroes"]], ["decima", "tetra", "volos", "kosynwu", "rez"])
            stats = RollingGameStats(host.catalog)
            stats.add_cohort(host, [True, False], [1, 0])
            value = stats.snapshot()
            self.assertEqual(value["scalar_game_coverage"], 2)
            self.assertEqual(value["collection_game_coverage"], 2)
            self.assertEqual(value["mean_collection_size_by_seat"], [10, 10])
            self.assertEqual(value["mean_mastery_by_seat"], [0, 1])
            observed = {hero["id"]: hero["observations"] for hero in value["heroes"]}
            # Duel drafts in reverse seat order; explicitly chosen Decima for
            # seat one and Tetra for seat zero above.
            self.assertEqual(observed["decima"], 2)
            self.assertEqual(observed["tetra"], 2)
            by_card = {card["id"]: card for card in value["cards"]}
            self.assertEqual(by_card["crystal"]["observations"], 4)
            self.assertEqual(by_card["blaster"]["observations"], 4)


if __name__ == "__main__":
    unittest.main()
