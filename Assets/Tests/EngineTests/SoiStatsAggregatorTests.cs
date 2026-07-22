using System.Collections.Generic;
using NUnit.Framework;
using Shards.Stats;

namespace Pascension.Engine.Tests
{
    /// <summary>Pins the aggregation semantics: filter scope per aggregate, tie
    /// handling, concede classification, streaks, sentinels, Complete gating, stub
    /// lifetime contribution, pairs and head-to-head shapes.</summary>
    [TestFixture]
    public class SoiStatsAggregatorTests
    {
        /// <summary>2-player record: me (seat 0, "decima") vs one opponent.</summary>
        private static SoiGameRecord G(string guid, string endedAt, int winner,
            string mode = "ai", string myChar = "decima", string oppIdentity = "bot:greedy",
            string oppName = "Bot", string oppChar = "volos", bool complete = true)
        {
            var r = new SoiGameRecord
            {
                Guid = guid,
                EndedAtUtc = endedAt,
                Mode = mode,
                MyIndex = 0,
                WinnerIndex = winner,
                Termination = winner < 0 ? "tie" : "kill",
                Rounds = 10,
                DurationSeconds = 300,
                Complete = complete
            };
            r.Players.Add(new SoiSeatRecord { Identity = "me", Name = "Me", CharacterId = myChar });
            r.Players.Add(new SoiSeatRecord
            {
                Identity = oppIdentity,
                Name = oppName,
                IsBot = oppIdentity.StartsWith("bot:"),
                CharacterId = oppChar
            });
            return r;
        }

        private static SoiStatsAggregates Compute(List<SoiGameRecord> records,
            SoiStatsFilter f = null, List<SoiGameStub> stubs = null) =>
            SoiStatsAggregator.Compute(records, stubs, f);

        [Test]
        public void Winrate_CountsTiesSeparately()
        {
            var records = new List<SoiGameRecord>
            {
                G("g1", "t1", winner: 0),
                G("g2", "t2", winner: 0),
                G("g3", "t3", winner: 1),
                G("g4", "t4", winner: -1),
                G("g5", "t5", winner: -1)
            };
            var agg = Compute(records);
            Assert.AreEqual(5, agg.Games);
            Assert.AreEqual(2, agg.Wins);
            Assert.AreEqual(1, agg.Losses);
            Assert.AreEqual(2, agg.Ties);
            Assert.AreEqual(5, agg.Ai.Games);
            Assert.AreEqual(2, agg.Ai.Wins);
            Assert.AreEqual(2, agg.Ai.Ties);
            Assert.AreEqual(5, agg.LifetimeGames);
            Assert.AreEqual(2, agg.LifetimeWins);
            Assert.AreEqual(2, agg.LifetimeTies);
        }

        [Test]
        public void ModeFilter_ExcludesDisabledModes()
        {
            var records = new List<SoiGameRecord>
            {
                G("g1", "t1", 0, mode: "ai"),
                G("g2", "t2", 0, mode: "mp2"),
                G("g3", "t3", 1, mode: "mp3plus")
            };
            var agg = Compute(records, new SoiStatsFilter { IncludeMp2 = false });
            Assert.AreEqual(2, agg.Games);
            Assert.AreEqual(1, agg.Ai.Games);
            Assert.AreEqual(0, agg.Mp2.Games, "mp2 filtered out");
            Assert.AreEqual(1, agg.Mp3Plus.Games);
        }

        [Test]
        public void OpponentFilter_KeepsOnlyGamesAgainstThatIdentity()
        {
            var records = new List<SoiGameRecord>
            {
                G("g1", "t1", 0, oppIdentity: "alice", oppName: "Alice"),
                G("g2", "t2", 1, oppIdentity: "alice", oppName: "Alice"),
                G("g3", "t3", 0, oppIdentity: "bot:greedy")
            };
            var agg = Compute(records, new SoiStatsFilter { OpponentKey = "alice" });
            Assert.AreEqual(2, agg.Games);
            Assert.AreEqual(1, agg.Wins);
            Assert.AreEqual(1, agg.Losses);
        }

        [Test]
        public void OpponentsList_IgnoresOpponentKey_AndTracksLatestName()
        {
            var records = new List<SoiGameRecord>
            {
                G("g1", "t1", 0, oppIdentity: "alice", oppName: "Alice"),
                G("g2", "t3", 1, oppIdentity: "alice", oppName: "AliceRenamed"),
                G("g3", "t2", -1, oppIdentity: "bot:greedy")
            };
            var agg = Compute(records, new SoiStatsFilter { OpponentKey = "alice" });

            Assert.AreEqual(2, agg.Opponents.Count, "opponent list ignores OpponentKey");
            var alice = agg.Opponents.Find(o => o.IdentityKey == "alice");
            Assert.IsNotNull(alice);
            Assert.AreEqual(2, alice.Games);
            Assert.AreEqual(1, alice.MyWins);
            Assert.AreEqual(1, alice.MyLosses);
            Assert.AreEqual("AliceRenamed", alice.DisplayName, "display name from the latest game");
            Assert.AreEqual("t3", alice.LastPlayedUtc);
            var bot = agg.Opponents.Find(o => o.IdentityKey == "bot:greedy");
            Assert.IsNotNull(bot);
            Assert.AreEqual(1, bot.Ties);
            Assert.IsTrue(bot.IsBot);
        }

        [Test]
        public void ConcedeClassification_AllCombos()
        {
            var lossConcede = G("g1", "t1", 1);
            lossConcede.Players[0].Conceded = true;
            var lossKill = G("g2", "t2", 1);
            var lossOverwhelm = G("g3", "t3", 1);
            lossOverwhelm.Termination = "overwhelm";
            var winConcede = G("g4", "t4", 0);
            winConcede.Players[1].Conceded = true;
            var winKill = G("g5", "t5", 0);
            var winOverwhelm = G("g6", "t6", 0);
            winOverwhelm.Termination = "overwhelm";
            // 3-player game where an opponent conceded but the game played on: my
            // eventual win is NOT "by concede".
            var winThreePlayer = G("g7", "t7", 0);
            winThreePlayer.Players.Add(new SoiSeatRecord
            {
                Identity = "carol", Name = "Carol", CharacterId = "tetra", Conceded = true
            });

            var agg = Compute(new List<SoiGameRecord>
            {
                lossConcede, lossKill, lossOverwhelm, winConcede, winKill, winOverwhelm, winThreePlayer
            });
            Assert.AreEqual(1, agg.LossesByConcede);
            Assert.AreEqual(1, agg.LossesByKill);
            Assert.AreEqual(1, agg.LossesByOverwhelm);
            Assert.AreEqual(1, agg.WinsByConcede);
            Assert.AreEqual(2, agg.WinsByKill, "the 3p concede win classifies by termination");
            Assert.AreEqual(1, agg.WinsByOverwhelm);
        }

        [Test]
        public void Streaks_TieResetsBoth_OrderIsByEndedAtUtc()
        {
            // Chronological W W T W L L — fed shuffled to prove the sort.
            var records = new List<SoiGameRecord>
            {
                G("g6", "t6", 1),
                G("g2", "t2", 0),
                G("g4", "t4", 0),
                G("g1", "t1", 0),
                G("g5", "t5", 1),
                G("g3", "t3", -1)
            };
            var agg = Compute(records);
            Assert.AreEqual(2, agg.BestWinStreak, "the tie broke the opening W W run");
            Assert.AreEqual(0, agg.CurrentWinStreak);
            Assert.AreEqual(2, agg.CurrentLossStreak);
        }

        [Test]
        public void RoundToM_SentinelExcludedFromAverages()
        {
            var reached5 = G("g1", "t1", 0);
            reached5.Players[0].RoundToM10 = 5;
            reached5.Players[0].RoundToM30 = 12;
            var never = G("g2", "t2", 0);
            var reached7 = G("g3", "t3", 0);
            reached7.Players[0].RoundToM10 = 7;

            var agg = Compute(new List<SoiGameRecord> { reached5, never, reached7 });
            Assert.AreEqual(6f, agg.AvgRoundToM10, 1e-5, "only the games that reached M10");
            Assert.AreEqual(-1f, agg.AvgRoundToM20, 1e-5, "no game reached M20");
            Assert.AreEqual(12f, agg.AvgRoundToM30, 1e-5);
            Assert.AreEqual(1f / 3f, agg.M30ReachRate, 1e-5);
        }

        [Test]
        public void BestHero_ThresholdAndFallback()
        {
            // decima: 6 games (5 decisive, 4W 1L 1T); volos: 3 games all won (< 5 decisive).
            var records = new List<SoiGameRecord>();
            for (int i = 0; i < 4; i++) records.Add(G("d" + i, "t0" + i, 0));
            records.Add(G("d4", "t04", 1));
            records.Add(G("d5", "t05", -1));
            for (int i = 0; i < 3; i++) records.Add(G("v" + i, "t1" + i, 0, myChar: "volos"));

            var agg = Compute(records);
            Assert.AreEqual("decima", agg.BestHeroCharacterId, "volos never met the 5-decisive bar");
            Assert.IsTrue(agg.BestHeroQualified);

            var few = Compute(new List<SoiGameRecord>
            {
                G("a1", "t1", 0),
                G("b1", "t2", 1, myChar: "volos"),
                G("b2", "t3", 1, myChar: "volos")
            });
            Assert.AreEqual("volos", few.BestHeroCharacterId, "fallback = most played");
            Assert.IsFalse(few.BestHeroQualified);

            var none = Compute(new List<SoiGameRecord>());
            Assert.IsNull(none.BestHeroCharacterId);
            Assert.IsFalse(none.BestHeroQualified);
        }

        [Test]
        public void IncompleteRecords_SkipCardsAndPairs_ButCountForWinrate()
        {
            var complete = G("g1", "t1", 0);
            complete.Players[0].Buys["ally_a"] = 1;
            complete.Players[0].Buys["ally_b"] = 1;
            complete.Players[0].Plays["ally_a"] = 2;
            var incomplete = G("g2", "t2", 0, complete: false);
            incomplete.Players[0].Buys["ally_c"] = 5;
            incomplete.Players[0].Plays["ally_c"] = 5;

            var records = new List<SoiGameRecord> { complete, incomplete };
            var agg = Compute(records);
            Assert.AreEqual(2, agg.Games, "winrate includes incomplete records");
            Assert.AreEqual(2, agg.Wins);
            Assert.AreEqual(2, agg.Cards.Count, "ally_a + ally_b only");
            Assert.IsNull(agg.Cards.Find(c => c.DefId == "ally_c"), "incomplete game gated out");
            var a = agg.Cards.Find(c => c.DefId == "ally_a");
            Assert.AreEqual(1, a.TimesBought);
            Assert.AreEqual(2, a.TimesPlayed);
            Assert.AreEqual(1, a.GamesBought);
            Assert.AreEqual(1, a.WinsWhenBought);

            var pairs = SoiStatsAggregator.ComputePairs(records, null);
            Assert.AreEqual(1, pairs.Count, "only the complete game pairs");
            Assert.AreEqual("ally_a", pairs[0].DefA);
            Assert.AreEqual("ally_b", pairs[0].DefB);
        }

        [Test]
        public void Stubs_ContributeOnlyToLifetime_AndAreFiltered()
        {
            var records = new List<SoiGameRecord> { G("g1", "t1", 0) };
            var stubs = new List<SoiGameStub>
            {
                new()
                {
                    Guid = "s1", EndedAtUtc = "t0", Mode = "ai", Won = true,
                    Opponents = { new SoiStubOpponent { Identity = "bot:greedy", CharacterId = "volos" } }
                },
                new()
                {
                    Guid = "s2", EndedAtUtc = "t0", Mode = "mp2", Tie = true,
                    Opponents = { new SoiStubOpponent { Identity = "alice", CharacterId = "volos" } }
                }
            };

            var agg = SoiStatsAggregator.Compute(records, stubs, null);
            Assert.AreEqual(1, agg.Games, "stubs never feed detailed aggregates");
            Assert.AreEqual(3, agg.LifetimeGames);
            Assert.AreEqual(2, agg.LifetimeWins);
            Assert.AreEqual(1, agg.LifetimeTies);

            var noMp2 = SoiStatsAggregator.Compute(records, stubs,
                new SoiStatsFilter { IncludeMp2 = false });
            Assert.AreEqual(2, noMp2.LifetimeGames, "the mp2 stub is mode-filtered");

            var vsAlice = SoiStatsAggregator.Compute(records, stubs,
                new SoiStatsFilter { OpponentKey = "alice" });
            Assert.AreEqual(0, vsAlice.Games);
            Assert.AreEqual(1, vsAlice.LifetimeGames, "only the alice stub passes");
            Assert.AreEqual(1, vsAlice.LifetimeTies);
        }

        [Test]
        public void Pairs_CountUnorderedBuyPairsPerGame()
        {
            var g1 = G("g1", "t1", 0);
            g1.Players[0].Buys["a"] = 1;
            g1.Players[0].Buys["b"] = 2;
            g1.Players[0].Buys["c"] = 1;
            var g2 = G("g2", "t2", 1);
            g2.Players[0].Buys["a"] = 1;
            g2.Players[0].Buys["b"] = 1;

            var pairs = SoiStatsAggregator.ComputePairs(new List<SoiGameRecord> { g1, g2 }, null);
            Assert.AreEqual(3, pairs.Count);
            Assert.AreEqual("a", pairs[0].DefA);
            Assert.AreEqual("b", pairs[0].DefB);
            Assert.AreEqual(2, pairs[0].GamesTogether);
            Assert.AreEqual(1, pairs[0].WinsTogether);
            Assert.AreEqual("a", pairs[1].DefA);
            Assert.AreEqual("c", pairs[1].DefB);
            Assert.AreEqual(1, pairs[1].GamesTogether);
            Assert.AreEqual("b", pairs[2].DefA);
            Assert.AreEqual("c", pairs[2].DefB);
        }

        [Test]
        public void HeadToHead_BuildsTheirHeroesAndCards()
        {
            var win = G("g1", "t1", 0, oppIdentity: "alice", oppName: "Alice");
            win.Players[1].Buys["x"] = 1;
            win.Players[1].Plays["x"] = 1;
            win.Players[1].MaxSingleHit = 9;
            var loss = G("g2", "t2", 1, oppIdentity: "alice", oppName: "Alice");
            loss.Players[1].Buys["x"] = 2;
            var other = G("g3", "t3", 0, oppIdentity: "bot:greedy");

            var records = new List<SoiGameRecord> { win, loss, other };
            var agg = Compute(records, new SoiStatsFilter { OpponentKey = "alice" });

            Assert.IsNotNull(agg.H2H);
            Assert.AreEqual(1, agg.H2H.TheirHeroes.Count);
            var volos = agg.H2H.TheirHeroes[0];
            Assert.AreEqual("volos", volos.CharacterId);
            Assert.AreEqual(2, volos.Games, "games where alice played volos");
            Assert.AreEqual(1, volos.Wins, "Wins on H2H heroes = MY wins");
            Assert.AreEqual(9, volos.MaxSingleHit, "their biggest hit");

            Assert.AreEqual(1, agg.H2H.TheirCards.Count);
            var x = agg.H2H.TheirCards[0];
            Assert.AreEqual("x", x.DefId);
            Assert.AreEqual(3, x.TimesBought);
            Assert.AreEqual(2, x.GamesBought);
            Assert.AreEqual(1, x.WinsWhenBought, "WinsWhenBought = MY wins in those games");
            Assert.AreEqual(1, x.TimesPlayed);

            Assert.IsNull(Compute(records).H2H, "no opponent key, no head-to-head");
        }
    }
}
