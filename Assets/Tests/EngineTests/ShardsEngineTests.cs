using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Shards.Engine;

namespace Pascension.Engine.Tests
{
    /// <summary>
    /// Structural tests for the Shards of Infinity engine skeleton: setup invariants,
    /// the turn/action pump, focus, buying, mercenary fast-play, champion attacks,
    /// end-turn damage + shields, eliminations. Runs on a STUB card set — the real
    /// card database (M4 data) replaces it without changing these rules.
    /// </summary>
    [TestFixture]
    public class ShardsEngineTests
    {
        /// <summary>Minimal self-consistent card set: 10-card starter (7+1+1+1) and a
        /// small center pool, mirroring the real composition shape.</summary>
        private static void RegisterStubSet()
        {
            ShardsCardDatabase.Clear();
            void Def(string id, string name, ShardsCardType type, ShardsFaction faction,
                int cost, int qty, IShardsEffect play = null, int defense = 0, int shield = 0,
                string set = "base")
            {
                ShardsCardDatabase.Register(new ShardsCardDef
                {
                    Id = id, Name = name, Type = type, Faction = faction, Cost = cost,
                    Quantity = qty, PlayEffect = play, Defense = defense, Shield = shield, Set = set
                });
            }

            // Starters: 7 crystals (+1 gem), 1 blaster (+1 power), 1 reactor (+1 gem, M10: +1 more),
            // 1 infinity shard (power scaling by mastery).
            Def("crystal", "Crystal", ShardsCardType.Starter, ShardsFaction.None, 0, 7, new Gain { Gems = 1 });
            Def("blaster", "Blaster", ShardsCardType.Starter, ShardsFaction.None, 0, 1, new Gain { Power = 1 });
            Def("shard_reactor", "Shard Reactor", ShardsCardType.Starter, ShardsFaction.None, 0, 1,
                new ShardsComposite(new Gain { Gems = 1 }, new AtMastery(10, new Gain { Gems = 1 })));
            Def("infinity_shard", "Infinity Shard", ShardsCardType.Starter, ShardsFaction.None, 0, 1,
                new Custom(ctx =>
                {
                    int m = ctx.Controller.Mastery;
                    int power = m >= 30 ? 1000 : m >= 20 ? 5 : m >= 10 ? 3 : 2;
                    ctx.Engine.GainPower(ctx.ControllerIndex, power);
                    return System.Linq.Enumerable.Empty<ShardsStep>();
                }));

            // Center pool: enough distinct cards to fill a row + draws.
            Def("gem_ally", "Gem Ally", ShardsCardType.Ally, ShardsFaction.Homodeus, 2, 8, new Gain { Gems = 2 });
            Def("power_ally", "Power Ally", ShardsCardType.Ally, ShardsFaction.Order, 3, 8, new Gain { Power = 3 });
            Def("shield_ally", "Shield Ally", ShardsCardType.Ally, ShardsFaction.Undergrowth, 2, 8,
                new Gain { Gems = 1 }, shield: 3);
            Def("guard_champ", "Guard Champion", ShardsCardType.Champion, ShardsFaction.Order, 4, 6,
                new Gain { Power = 1 }, defense: 4, shield: 1);
            Def("hired_blade", "Hired Blade", ShardsCardType.Mercenary, ShardsFaction.Wraethe, 3, 6,
                new Gain { Power = 4 });
        }

        private static ShardsEngine NewGame(int players = 2, ulong seed = 42, ShardsDlc dlc = ShardsDlc.None)
        {
            RegisterStubSet();
            var config = new ShardsConfig { Seed = seed, Dlc = dlc };
            for (int i = 0; i < players; i++)
                config.Players.Add(new PlayerSpec { Name = "P" + i, CharacterId = "decima" });
            return new ShardsEngine(config);
        }

        private static void MustSubmit(ShardsEngine engine, PlayerAction action)
        {
            var result = engine.Submit(action);
            Assert.IsTrue(result.Accepted, $"'{action.Describe()}' rejected: {result.Error}");
        }

        [Test]
        public void Setup_StaggeredMastery_Hands_Row_Health()
        {
            var engine = NewGame(players: 4);
            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(i, engine.State.Players[i].Mastery, $"P{i} staggered mastery");
                Assert.AreEqual(5, engine.State.Players[i].Hand.Count);
                Assert.AreEqual(5, engine.State.Players[i].Deck.Count);
                Assert.AreEqual(50, engine.State.Players[i].Health);
            }
            foreach (var slot in engine.State.CenterRow)
                Assert.IsNotNull(slot, "center row starts full");
            Assert.AreEqual(0, engine.State.TurnPlayerIndex);
            Assert.AreEqual(PendingInputKind.Priority, engine.PendingInput.Kind);
        }

        [Test]
        public void PlayAllStarters_GainsResources_Immediately()
        {
            var engine = NewGame(seed: 7);
            var p0 = engine.State.Players[0];
            int gems = 0, power = 0;
            foreach (var card in new List<ShardsCard>(p0.Hand))
            {
                MustSubmit(engine, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = card.InstanceId });
                if (card.DefId == "crystal") gems++;
                if (card.DefId == "shard_reactor") gems++;
                if (card.DefId == "blaster") power++;
                if (card.DefId == "infinity_shard") power += 2;
            }
            Assert.AreEqual(gems, p0.Gems, "gems match played crystals/reactors");
            Assert.AreEqual(power, p0.Power, "power matches blasters/shard");
            Assert.AreEqual(5, p0.PlayZone.Count, "played cards visible in the play zone");
        }

        [Test]
        public void Focus_OncePerTurn_ExhaustsCharacter_GainsMastery()
        {
            var engine = NewGame(seed: 11);
            var p0 = engine.State.Players[0];
            // Play crystals until we have a gem.
            foreach (var card in new List<ShardsCard>(p0.Hand))
                if (card.DefId == "crystal")
                {
                    MustSubmit(engine, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = card.InstanceId });
                    break;
                }
            Assert.GreaterOrEqual(p0.Gems, 1);

            int masteryBefore = p0.Mastery;
            MustSubmit(engine, new ShardsFocusAction { PlayerIndex = 0 });
            Assert.AreEqual(masteryBefore + 1, p0.Mastery);
            Assert.IsTrue(p0.CharacterExhausted);

            var again = engine.Submit(new ShardsFocusAction { PlayerIndex = 0 });
            Assert.IsFalse(again.Accepted, "focus is once per turn");
        }

        [Test]
        public void Buy_RefillsRowImmediately_CardGoesToDiscard()
        {
            var engine = NewGame(seed: 13);
            var p0 = engine.State.Players[0];
            p0.Gems = 10; // white-box: grant gems

            int slot = -1;
            for (int s = 0; s < engine.State.CenterRow.Length; s++)
                if (engine.State.CenterRow[s] != null && !engine.State.CenterRow[s].Def.IsMonster)
                {
                    slot = s;
                    break;
                }
            Assert.GreaterOrEqual(slot, 0);
            string boughtDef = engine.State.CenterRow[slot].DefId;

            MustSubmit(engine, new ShardsBuyCardAction { PlayerIndex = 0, SlotIndex = slot });
            Assert.IsNotNull(engine.State.CenterRow[slot], "slot refilled immediately");
            Assert.AreEqual(1, p0.Discard.Count);
            Assert.AreEqual(boughtDef, p0.Discard[0].DefId);
        }

        [Test]
        public void Mercenary_FastPlay_EffectNow_ReturnsToCenterDeckBottom()
        {
            var engine = NewGame(seed: 17);
            var p0 = engine.State.Players[0];
            p0.Gems = 10;

            // Force a mercenary into slot 0 (white-box).
            var merc = engine.State.CenterDeck.Find(c => c.Def.Type == ShardsCardType.Mercenary);
            Assert.IsNotNull(merc, "stub set has mercenaries");
            engine.State.CenterDeck.Remove(merc);
            engine.State.CenterRow[0] = merc;

            int powerBefore = p0.Power;
            MustSubmit(engine, new ShardsBuyCardAction { PlayerIndex = 0, SlotIndex = 0, FastPlay = true });
            Assert.Greater(p0.Power, powerBefore, "fast-play effect applied immediately");
            Assert.AreEqual(1, p0.PlayZone.Count);
            Assert.IsTrue(p0.PlayZone[0].FastPlayed);
            Assert.Greater(p0.FactionPlays(merc.Def.Faction), 0, "fast-play counts as playing the faction");

            MustSubmit(engine, new ShardsEndTurnAction { PlayerIndex = 0 });
            // (2p: end-turn may need the split/shield flow — power was spent? No: fast-play
            // gave power which flows into the end-turn assignment. Resolve any decisions.)
            ResolveAllDecisionsWithDefaults(engine);

            Assert.AreEqual(0, p0.PlayZone.Count);
            Assert.AreEqual(merc.InstanceId, engine.State.CenterDeck[0].InstanceId,
                "fast-played mercenary went to the BOTTOM of the center deck");
        }

        [Test]
        public void EndTurn_DamageAutoTargetsSingleOpponent_ShieldsPrevent()
        {
            var engine = NewGame(seed: 19);
            var p0 = engine.State.Players[0];
            var p1 = engine.State.Players[1];
            p0.Power = 6; // white-box damage pool

            // Give the defender one shield card in hand (white-box swap).
            var shieldCard = new ShardsCard { InstanceId = 9001, DefId = "shield_ally", Owner = 1, Zone = ShardsZone.Hand };
            p1.Hand.Add(shieldCard);

            MustSubmit(engine, new ShardsEndTurnAction { PlayerIndex = 0 });

            // Single opponent → no split decision; the defender's shield decision pends.
            Assert.AreEqual(PendingInputKind.Decision, engine.PendingInput.Kind);
            Assert.AreEqual(1, engine.PendingInput.PlayerIndex, "defender decides shields");
            Assert.AreEqual("soi.shields", engine.PendingInput.Decision.Context);

            // Reveal the shield: prevents 3 of the 6.
            var answer = new Pascension.Engine.Decisions.DecisionAnswer { DecisionId = engine.PendingInput.Decision.Id };
            answer.ChosenOptionIds.Add(shieldCard.InstanceId);
            MustSubmit(engine, new SubmitDecisionAction { PlayerIndex = 1, Answer = answer });

            Assert.AreEqual(50 - 3, p1.Health, "6 damage - 3 shield = 3 taken");
            Assert.Contains(shieldCard, p1.Hand, "revealed shield STAYS in hand");
            Assert.AreEqual(1, engine.State.TurnPlayerIndex, "turn advanced after cleanup");
            Assert.AreEqual(5, p0.Hand.Count, "attacker redrew a full hand");
        }

        [Test]
        public void ChampionAttack_MidTurnIsIllegal_ChampionsDieInTheEndTurnSplit()
        {
            var engine = NewGame(seed: 23);
            var p0 = engine.State.Players[0];
            var p1 = engine.State.Players[1];
            p1.Hand.RemoveAll(_ => true); // no shields — deterministic split resolution

            var champion = new ShardsCard { InstanceId = 9100, DefId = "guard_champ", Owner = 1, Zone = ShardsZone.Champions };
            p1.Champions.Add(champion);

            // Mid-turn power attacks on champions are illegal (user decision 2026-07-20:
            // champions die ONLY in the end-of-turn damage assignment).
            p0.Power = 10;
            var rejected = engine.Submit(new ShardsAttackChampionAction
            {
                PlayerIndex = 0, TargetPlayerIndex = 1, CardInstanceId = champion.InstanceId, Amount = 3
            });
            Assert.IsFalse(rejected.Accepted, "champions can't be attacked mid-turn");
            Assert.AreEqual(10, p0.Power, "no power spent on the rejected attack");
            Assert.IsEmpty(engine.LegalActions(0).FindAll(a => a is ShardsAttackChampionAction),
                "champion attacks are never advertised");

            // The end-turn split CAN kill it: 4 on the champion (defense 4) + 6 face.
            MustSubmit(engine, new ShardsEndTurnAction { PlayerIndex = 0 });
            Assert.AreEqual("soi.split", engine.PendingInput.Decision.Context);
            var request = engine.PendingInput.Decision;
            var champOption = request.Options.Find(o => o.CardInstanceId == champion.InstanceId);
            Assert.IsNotNull(champOption, "the champion is a split target");
            Assert.AreEqual(4, champOption.Amount, "option carries the remaining defense");

            var answer = new Pascension.Engine.Decisions.DecisionAnswer { DecisionId = request.Id };
            answer.ChosenOptionIds.AddRange(new[]
            {
                champOption.Id, champOption.Id, champOption.Id, champOption.Id,
                p1.Index, p1.Index, p1.Index, p1.Index, p1.Index, p1.Index
            });
            MustSubmit(engine, new SubmitDecisionAction { PlayerIndex = 0, Answer = answer });
            ResolveAllDecisionsWithDefaults(engine);

            Assert.AreEqual(0, p1.Champions.Count, "the split killed the champion");
            Assert.IsTrue(p1.Discard.Contains(champion), "destroyed champion goes to owner's discard");
            Assert.AreEqual(50 - 6, p1.Health, "the remainder reached the owner");
        }

        [Test]
        public void SnapshotBuyableSlots_OnlyWhileTheViewerHoldsPriority()
        {
            var engine = NewGame(seed: 41);
            var p0 = engine.State.Players[0];
            var p1 = engine.State.Players[1];
            p0.Gems = 99;
            p1.Gems = 99;

            Assert.IsNotEmpty(ShardsSnapshotBuilder.Build(engine, 0).BuyableSlots,
                "the turn player with priority sees affordable slots");
            Assert.IsEmpty(ShardsSnapshotBuilder.Build(engine, 1).BuyableSlots,
                "gems or not, it isn't P1's turn — nothing is buyable");

            // Submitting END TURN parks an end-phase decision (split): the affordable
            // halos must die instantly even though gems only reset at cleanup.
            p1.Hand.RemoveAll(_ => true);
            p1.Champions.Add(new ShardsCard { InstanceId = 9300, DefId = "guard_champ", Owner = 1, Zone = ShardsZone.Champions });
            p0.Power = 2;
            MustSubmit(engine, new ShardsEndTurnAction { PlayerIndex = 0 });
            Assert.AreEqual(PendingInputKind.Decision, engine.PendingInput.Kind, "split parked");
            Assert.IsEmpty(ShardsSnapshotBuilder.Build(engine, 0).BuyableSlots,
                "end phase — buying is over, gems notwithstanding");
        }

        [Test]
        public void Elimination_LastSurvivorWins()
        {
            var engine = NewGame(players: 3, seed: 29);
            engine.State.Players[1].Health = 2;
            engine.State.Players[2].Health = 2;
            engine.State.Players[0].Power = 4;

            MustSubmit(engine, new ShardsEndTurnAction { PlayerIndex = 0 });
            // Split decision pends (2 living opponents): 2 damage to each.
            Assert.AreEqual("soi.split", engine.PendingInput.Decision.Context);
            var answer = new Pascension.Engine.Decisions.DecisionAnswer { DecisionId = engine.PendingInput.Decision.Id };
            answer.ChosenOptionIds.AddRange(new[] { 1, 1, 2, 2 });
            MustSubmit(engine, new SubmitDecisionAction { PlayerIndex = 0, Answer = answer });
            ResolveAllDecisionsWithDefaults(engine); // shield decisions (none — no shields in hand)

            Assert.IsTrue(engine.State.Players[1].Eliminated);
            Assert.IsTrue(engine.State.Players[2].Eliminated);
            Assert.IsTrue(engine.State.GameOver);
            Assert.AreEqual(0, engine.State.WinnerIndex);
        }

        [Test]
        public void DeckOut_ReshufflesDiscardMidDraw()
        {
            var engine = NewGame(seed: 31);
            var p0 = engine.State.Players[0];
            // End the turn: 5 in hand discarded, 5 in deck drawn... second end-turn forces reshuffle.
            MustSubmit(engine, new ShardsEndTurnAction { PlayerIndex = 0 });
            ResolveAllDecisionsWithDefaults(engine);
            MustSubmit(engine, new ShardsEndTurnAction { PlayerIndex = 1 });
            ResolveAllDecisionsWithDefaults(engine);
            MustSubmit(engine, new ShardsEndTurnAction { PlayerIndex = 0 });
            ResolveAllDecisionsWithDefaults(engine);

            Assert.AreEqual(5, p0.Hand.Count, "hand refilled through the reshuffle");
            Assert.AreEqual(10, p0.Hand.Count + p0.Deck.Count + p0.Discard.Count, "no cards lost");
        }

        [Test]
        public void MasteryCap_And_ThresholdAtPlayTime()
        {
            var engine = NewGame(seed: 37);
            var p0 = engine.State.Players[0];
            p0.Mastery = 29;
            engine.GainMastery(0, 5);
            Assert.AreEqual(30, p0.Mastery, "mastery hard-capped at 30");

            // Threshold checks at play time: reactor at M10+ gives 2 gems total.
            var reactor = new ShardsCard { InstanceId = 9200, DefId = "shard_reactor", Owner = 0, Zone = ShardsZone.Hand };
            p0.Hand.Add(reactor);
            int gems = p0.Gems;
            MustSubmit(engine, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = reactor.InstanceId });
            Assert.AreEqual(gems + 2, p0.Gems, "M10 threshold bonus applied at play time");
        }

        [Test]
        public void WireFormat_ShardsActions_RoundTrip()
        {
            var actions = new PlayerAction[]
            {
                new ShardsPlayCardAction { PlayerIndex = 1, CardInstanceId = 5 },
                new ShardsBuyCardAction { PlayerIndex = 0, SlotIndex = 3, FastPlay = true },
                new ShardsFocusAction { PlayerIndex = 2 },
                new ShardsEndTurnAction { PlayerIndex = 3 },
                new ShardsAttackChampionAction { PlayerIndex = 0, TargetPlayerIndex = 1, CardInstanceId = 9 }
            };
            foreach (var action in actions)
            {
                var json = ShardsJson.Wire.SerializeAction(action);
                var back = ShardsJson.Wire.DeserializeAction(json);
                Assert.AreEqual(action.GetType(), back.GetType(), json);
            }
        }

        [Test]
        public void TurnCycle_SafeDefaults_NeverStall()
        {
            // Safe-default seats only end turns (nobody ever deals damage), so the game
            // cannot END — this proves the pump cycles turns indefinitely without stalls,
            // rejected actions, or exceptions. Real termination is covered by the
            // heuristic-bot sims once the full card set lands (M4/M5 data).
            var adapter = new ShardsEngineAdapter(NewConfig(seed: 41));

            for (int i = 0; i < 400; i++)
            {
                var pending = adapter.PendingInput;
                Assert.IsNotNull(pending, "pump always awaits input while the game runs");
                var action = adapter.DefaultActionFor(pending);
                Assert.IsNotNull(action, "safe default always exists");
                var result = adapter.Submit(action);
                Assert.IsTrue(result.Accepted, result.Error);
            }
            Assert.Greater(adapter.Inner.State.Round, 50, "turns cycled freely");
            Assert.IsFalse(adapter.GameOver);
        }

        private static ShardsConfig NewConfig(int players = 2, ulong seed = 43)
        {
            RegisterStubSet();
            var config = new ShardsConfig { Seed = seed };
            for (int i = 0; i < players; i++)
                config.Players.Add(new PlayerSpec { Name = "P" + i, CharacterId = "decima" });
            return config;
        }

        private static void ResolveAllDecisionsWithDefaults(ShardsEngine engine)
        {
            for (int guard = 0; guard < 50; guard++)
            {
                var pending = engine.PendingInput;
                if (pending == null || pending.Kind != PendingInputKind.Decision) return;
                var req = pending.Decision;
                var answer = new Pascension.Engine.Decisions.DecisionAnswer { DecisionId = req.Id };
                answer.ChosenOptionIds.AddRange(req.DefaultOptionIds);
                for (int i = 0; answer.ChosenOptionIds.Count < req.Min && i < req.Options.Count; i++)
                    answer.ChosenOptionIds.Add(req.Options[i].Id);
                var result = engine.Submit(new SubmitDecisionAction { PlayerIndex = pending.PlayerIndex, Answer = answer });
                Assert.IsTrue(result.Accepted, result.Error);
            }
        }
    }
}
