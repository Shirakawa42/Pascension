using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Core;
using Pascension.Engine.Core;
using Shards.Content;
using Shards.Engine;

namespace Pascension.Engine.Tests
{
    /// <summary>Count validation + per-ruling tests on the REAL Shards of Infinity card
    /// database (photo-verified quantities: 88 base center / 24 RotF / 12 SoS / 30 ItH).</summary>
    public sealed class ShardsContentTests
    {
        [SetUp]
        public void SetUp()
        {
            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();
        }

        private static int CenterCount(string set, ShardsFaction? faction = null)
        {
            int n = 0;
            foreach (var def in ShardsCardDatabase.All)
            {
                if (def.Set != set) continue;
                if (def.Type == ShardsCardType.Starter || def.Type == ShardsCardType.Relic ||
                    def.Type == ShardsCardType.Destiny)
                    continue;
                if (faction != null && def.Faction != faction) continue;
                n += def.Quantity;
            }
            return n;
        }

        [Test]
        public void Counts_MatchPublishedComponentLists()
        {
            // Base: 88 center cards, 22 per faction.
            Assert.AreEqual(88, CenterCount("base"), "base center deck");
            foreach (var f in new[] { ShardsFaction.Homodeus, ShardsFaction.Order, ShardsFaction.Undergrowth, ShardsFaction.Wraethe })
                Assert.AreEqual(22, CenterCount("base", f), $"base {f}");

            // Starters: 10 cards (7 Crystal / 1 Blaster / 1 Shard Reactor / 1 Infinity Shard).
            int starters = 0;
            foreach (var def in ShardsCardDatabase.All)
                if (def.Type == ShardsCardType.Starter)
                    starters += def.Quantity;
            Assert.AreEqual(10, starters, "starter deck size");

            // RotF: 24 center (6 per faction) + 8 relics (2 per base character).
            Assert.AreEqual(24, CenterCount("relics_of_the_future"), "RotF center cards");
            int relics = 0;
            foreach (var def in ShardsCardDatabase.All)
                if (def.Set == "relics_of_the_future" && def.Type == ShardsCardType.Relic)
                    relics += def.Quantity;
            Assert.AreEqual(8, relics, "RotF relics");

            // SoS competitive: 12 center cards + Rez's 2 relics.
            Assert.AreEqual(12, CenterCount("shadow_of_salvation"), "SoS center cards");
            int rezRelics = 0;
            foreach (var def in ShardsCardDatabase.All)
                if (def.Set == "shadow_of_salvation" && def.Type == ShardsCardType.Relic)
                    rezRelics += def.Quantity;
            Assert.AreEqual(2, rezRelics, "Rez relics");

            // ItH: 30 center cards (25 faction + 5 Ingeminex) + 30 destinies.
            Assert.AreEqual(30, CenterCount("into_the_horizon"), "ItH center cards");
            int monsters = 0, destinies = 0;
            foreach (var def in ShardsCardDatabase.All)
            {
                if (def.Set != "into_the_horizon") continue;
                if (def.Type == ShardsCardType.Monster) monsters += def.Quantity;
                if (def.Type == ShardsCardType.Destiny) destinies += def.Quantity;
            }
            Assert.AreEqual(5, monsters, "Ingeminex");
            Assert.AreEqual(30, destinies, "destinies");
        }

        private static ShardsEngineAdapter NewGame(ShardsDlc dlc = ShardsDlc.None, int players = 2, ulong seed = 42)
        {
            var specs = new List<PlayerSpec>();
            var characters = ShardsContentRegistry.CharactersFor(dlc);
            for (int i = 0; i < players; i++)
                specs.Add(new PlayerSpec { Name = "P" + i, CharacterId = characters[i % characters.Count] });
            return new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(seed, specs, dlc));
        }

        [Test]
        public void Setup_BaseOnly_88CardCenterDeck_NoExpansionCards()
        {
            var engine = NewGame().Inner;
            int total = engine.State.CenterDeck.Count + 6; // 6 already dealt to the row
            Assert.AreEqual(88, total, "center deck + row = full base set");
            Assert.AreEqual(0, engine.State.DestinyRow.Count, "no destinies without ItH");
            foreach (var p in engine.State.Players)
                Assert.AreEqual(0, p.SetAside.Count, "no relics without RotF");
        }

        [Test]
        public void Setup_AllDlc_AddsEverything_CloudOraclesErrataReplaces()
        {
            var all = ShardsDlc.RelicsOfTheFuture | ShardsDlc.ShadowOfSalvation | ShardsDlc.IntoTheHorizon;
            var engine = NewGame(all, players: 2, seed: 7).Inner;

            // 88 + 24 + 12 + 30 = 154, minus the 3 RotF Cloud Oracles the SoS errata
            // replaces = 151 total center cards (row + deck + any revealed Ingeminex).
            int total = engine.State.CenterDeck.Count + engine.State.ActiveMonsters.Count;
            foreach (var slot in engine.State.CenterRow)
                if (slot != null)
                    total++;
            Assert.AreEqual(151, total, "combined center deck respects the errata replacement");

            int rotfOracles = 0, sosOracles = 0;
            foreach (var card in engine.State.CenterDeck)
            {
                if (card.DefId == "cloud_oracles") rotfOracles++;
                if (card.DefId == "cloud_oracles_sos") sosOracles++;
            }
            Assert.AreEqual(0, rotfOracles, "RotF Cloud Oracles replaced by the errata copies");

            Assert.AreEqual(6, engine.State.DestinyRow.Count, "6 destinies dealt face up");
            Assert.AreEqual(24, engine.State.DestinyDeck.Count, "24 destinies remain in the deck");
            foreach (var p in engine.State.Players)
                Assert.AreEqual(2, p.SetAside.Count, "each character sets aside their 2 relics");
        }

        [Test]
        public void Setup_ItHWithoutRotF_RemovesCorruption()
        {
            var engine = NewGame(ShardsDlc.IntoTheHorizon, seed: 11).Inner;
            foreach (var card in engine.State.CenterDeck)
                Assert.AreNotEqual("ingeminex_corruption", card.DefId,
                    "Corruption needs relics — removed without RotF");
            foreach (var card in engine.State.ActiveMonsters)
                Assert.AreNotEqual("ingeminex_corruption", card.DefId);
        }

        [Test]
        public void FullGame_HeuristiclessBots_AlwaysTerminates()
        {
            // Random-legal-action players must finish a real-cards game (the Infinity
            // Shard guarantees lethal at M30; last survivor or tie ends it).
            for (ulong seed = 1; seed <= 3; seed++)
            {
                var adapter = NewGame(ShardsDlc.RelicsOfTheFuture | ShardsDlc.ShadowOfSalvation | ShardsDlc.IntoTheHorizon,
                    players: 3, seed: seed);
                var engine = adapter.Inner;
                var rng = new System.Random((int)seed);
                int guard = 0;
                while (!engine.State.GameOver && guard++ < 20000)
                {
                    var pending = adapter.PendingInput;
                    Assert.IsNotNull(pending, $"seed {seed}: no pending input but game not over (step {guard})");
                    Pascension.Engine.Actions.PlayerAction action;
                    if (pending.Kind == PendingInputKind.Decision)
                    {
                        action = adapter.DefaultActionFor(pending);
                    }
                    else
                    {
                        var legal = engine.LegalActions(pending.PlayerIndex);
                        // Bias: play everything, then buy/focus, end turn only when forced.
                        var plays = legal.FindAll(a => a is ShardsPlayCardAction);
                        var mid = legal.FindAll(a => a is ShardsBuyCardAction || a is ShardsFocusAction ||
                                                     a is ShardsExhaustAction || a is ShardsAttackChampionAction ||
                                                     a is ShardsAttackMonsterAction || a is ShardsTakeDestinyAction ||
                                                     a is ShardsRecruitRelicAction);
                        if (plays.Count > 0) action = plays[rng.Next(plays.Count)];
                        else if (mid.Count > 0 && rng.Next(3) > 0) action = mid[rng.Next(mid.Count)];
                        else action = new ShardsEndTurnAction { PlayerIndex = pending.PlayerIndex };
                    }
                    var result = adapter.Submit(action);
                    Assert.IsTrue(result.Accepted, $"seed {seed}: rejected {action.Describe()}: {result.Error}");
                }
                Assert.IsTrue(engine.State.GameOver, $"seed {seed}: game did not terminate in 20000 steps (round {engine.State.Round})");
            }
        }

        [Test]
        public void CardConservation_AfterManyTurns()
        {
            var adapter = NewGame(ShardsDlc.IntoTheHorizon, players: 2, seed: 99);
            var engine = adapter.Inner;
            int CountAll()
            {
                int n = engine.State.CenterDeck.Count + engine.State.ActiveMonsters.Count + engine.State.Banished.Count;
                foreach (var slot in engine.State.CenterRow)
                    if (slot != null)
                        n++;
                foreach (var p in engine.State.Players)
                    n += p.Deck.Count + p.Hand.Count + p.Discard.Count + p.PlayZone.Count + p.Champions.Count;
                return n;
            }
            int before = CountAll();
            var rng = new System.Random(5);
            for (int step = 0; step < 600 && !engine.State.GameOver; step++)
            {
                var pending = adapter.PendingInput;
                if (pending.Kind == PendingInputKind.Decision)
                {
                    adapter.Submit(adapter.DefaultActionFor(pending));
                    continue;
                }
                var legal = engine.LegalActions(pending.PlayerIndex);
                legal.RemoveAll(a => a is Pascension.Engine.Actions.ConcedeAction);
                adapter.Submit(legal[rng.Next(legal.Count)]);
            }
            Assert.AreEqual(before, CountAll(), "no card created or lost across play (banish pile included)");
        }
    }
}
