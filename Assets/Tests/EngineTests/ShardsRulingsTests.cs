using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace Pascension.Engine.Tests
{
    /// <summary>One test per verified official ruling / FAQ clarification, exercised on
    /// the REAL card database (white-box card placement like ShardsEngineTests).</summary>
    public sealed class ShardsRulingsTests
    {
        [SetUp]
        public void SetUp()
        {
            ShardsCardDatabase.Clear();
            ShardsContentRegistry.EnsureRegistered();
        }

        private static ShardsEngine NewGame(ShardsDlc dlc = ShardsDlc.None, int players = 2, ulong seed = 42,
            string character = "decima")
        {
            var specs = new List<PlayerSpec>();
            for (int i = 0; i < players; i++)
                specs.Add(new PlayerSpec { Name = "P" + i, CharacterId = character });
            return new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(seed, specs, dlc)).Inner;
        }

        private static ShardsCard Give(ShardsEngine engine, ShardsPlayer player, string defId, ShardsZone zone)
        {
            var card = new ShardsCard
            {
                InstanceId = engine.State.NextInstanceId++,
                DefId = defId,
                Owner = player.Index,
                Zone = zone
            };
            switch (zone)
            {
                case ShardsZone.Hand: player.Hand.Add(card); break;
                case ShardsZone.Discard: player.Discard.Add(card); break;
                case ShardsZone.Champions: player.Champions.Add(card); break;
                default: throw new System.ArgumentException("zone");
            }
            return card;
        }

        private static void MustSubmit(ShardsEngine engine, PlayerAction action)
        {
            var result = engine.Submit(action);
            Assert.IsTrue(result.Accepted, action.Describe() + " → " + result.Error);
        }

        private static void Answer(ShardsEngine engine, params int[] optionIds)
        {
            Assert.AreEqual(PendingInputKind.Decision, engine.PendingInput.Kind, "a decision should be pending");
            var answer = new DecisionAnswer { DecisionId = engine.PendingInput.Decision.Id };
            answer.ChosenOptionIds.AddRange(optionIds);
            MustSubmit(engine, new SubmitDecisionAction
            {
                PlayerIndex = engine.PendingInput.PlayerIndex,
                Answer = answer
            });
        }

        private static void DrainDecisionsWithDefaults(ShardsEngine engine)
        {
            int guard = 0;
            while (!engine.State.GameOver && engine.PendingInput != null &&
                   engine.PendingInput.Kind == PendingInputKind.Decision && guard++ < 50)
            {
                DrainOne(engine);
            }
        }

        /// <summary>Answer one pending decision like DefaultActions does: declared
        /// defaults first (the split pre-fills a full assignment there), then pad with
        /// distinct options up to Min, clamped to Max.</summary>
        private static void DrainOne(ShardsEngine engine)
        {
            var request = engine.PendingInput.Decision;
            var answer = new DecisionAnswer { DecisionId = request.Id };
            answer.ChosenOptionIds.AddRange(request.DefaultOptionIds);
            for (int i = 0; answer.ChosenOptionIds.Count < request.Min && i < request.Options.Count; i++)
                if (!answer.ChosenOptionIds.Contains(request.Options[i].Id))
                    answer.ChosenOptionIds.Add(request.Options[i].Id);
            while (answer.ChosenOptionIds.Count > request.Max)
                answer.ChosenOptionIds.RemoveAt(answer.ChosenOptionIds.Count - 1);
            MustSubmit(engine, new SubmitDecisionAction { PlayerIndex = request.PlayerIndex, Answer = answer });
        }

        // ------------------------------------------------------------- faction triggers

        [Test]
        public void Unify_RevealFromHandAlternative_SelfNeverCounts()
        {
            var engine = NewGame(seed: 3);
            var p0 = engine.State.Players[0];

            // No other Undergrowth ally played; NO candidate in hand → no bonus, no decision.
            p0.Hand.RemoveAll(_ => true);
            var aspirant = Give(engine, p0, "undergrowth_aspirant", ShardsZone.Hand);
            MustSubmit(engine, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = aspirant.InstanceId });
            Assert.AreEqual(0, p0.Power, "Unify unsatisfied: the card never counts itself");

            // Candidate in hand → reveal decision; revealing grants the bonus, card stays in hand.
            var spore = Give(engine, p0, "spore_cleric", ShardsZone.Hand);
            var second = Give(engine, p0, "undergrowth_aspirant", ShardsZone.Hand);
            p0.ResetTurn();
            MustSubmit(engine, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = second.InstanceId });
            Assert.AreEqual("soi.reveal", engine.PendingInput.Decision.Context);
            Answer(engine, spore.InstanceId);
            Assert.AreEqual(5, p0.Power, "Unify satisfied by hand reveal");
            Assert.Contains(spore, p0.Hand, "revealed card stays in hand");
        }

        [Test]
        public void Unify_SatisfiedByFastPlayedMercenary_NoDecision()
        {
            var engine = NewGame(seed: 5);
            var p0 = engine.State.Players[0];
            // Fast-play counts as playing an ally of its faction (FAQ).
            var merc = new ShardsCard { InstanceId = engine.State.NextInstanceId++, DefId = "spore_cleric", Owner = -1, Zone = ShardsZone.CenterRow };
            engine.State.CenterRow[0] = merc;
            p0.Gems = 2;
            MustSubmit(engine, new ShardsBuyCardAction { PlayerIndex = 0, SlotIndex = 0, FastPlay = true });

            var aspirant = Give(engine, p0, "undergrowth_aspirant", ShardsZone.Hand);
            MustSubmit(engine, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = aspirant.InstanceId });
            Assert.AreEqual(PendingInputKind.Priority, engine.PendingInput.Kind, "already satisfied — no reveal decision");
            Assert.AreEqual(5, p0.Power, "Unify satisfied by the fast-played mercenary");
        }

        // ------------------------------------------------------------- shields & statics

        [Test]
        public void Praetorian02_ShieldsInPlay_NotFromHand()
        {
            var engine = NewGame(ShardsDlc.RelicsOfTheFuture, seed: 7);
            var p0 = engine.State.Players[0];
            var p1 = engine.State.Players[1];
            p1.Hand.RemoveAll(_ => true);

            Give(engine, p1, "praetorian_02", ShardsZone.Champions);
            p0.Power = 5;
            MustSubmit(engine, new ShardsEndTurnAction { PlayerIndex = 0 });
            DrainDecisionsWithDefaults(engine);
            Assert.AreEqual(50 - (5 - 3), p1.Health, "in-play relic champion shields 3 passively");

            // From HAND it is never offered as a shield (its shield only works in play).
            var engine2 = NewGame(ShardsDlc.RelicsOfTheFuture, seed: 8);
            var q0 = engine2.State.Players[0];
            var q1 = engine2.State.Players[1];
            q1.Hand.RemoveAll(_ => true);
            Give(engine2, q1, "praetorian_02", ShardsZone.Hand);
            q0.Power = 4;
            MustSubmit(engine2, new ShardsEndTurnAction { PlayerIndex = 0 });
            Assert.AreEqual(PendingInputKind.Priority, engine2.PendingInput.Kind, "no shield decision offered");
            Assert.AreEqual(46, q1.Health);
        }

        [Test]
        public void RuBoVai_M10_AttackerIgnoresAllShields()
        {
            var engine = NewGame(seed: 9);
            var p0 = engine.State.Players[0];
            var p1 = engine.State.Players[1];
            p0.Mastery = 10;
            p1.Hand.RemoveAll(_ => true);
            Give(engine, p1, "cryptofist_monk", ShardsZone.Hand); // shield 8 in hand

            var champion = Give(engine, p0, "ru_bo_vai", ShardsZone.Champions);
            MustSubmit(engine, new ShardsExhaustAction { PlayerIndex = 0, CardInstanceId = champion.InstanceId });
            Assert.AreEqual(4, p0.Power);
            Assert.IsTrue(p0.IgnoreShieldsThisTurn);

            MustSubmit(engine, new ShardsEndTurnAction { PlayerIndex = 0 });
            Assert.AreEqual(PendingInputKind.Priority, engine.PendingInput.Kind, "defender never gets a shield decision");
            Assert.AreEqual(46, p1.Health, "full 4 damage through the shield");
        }

        [Test]
        public void Zetta_Taunt_ProtectsOwnerAndOtherChampions()
        {
            var engine = NewGame(seed: 11);
            var p0 = engine.State.Players[0];
            var p1 = engine.State.Players[1];

            Give(engine, p1, "zetta_encryptor", ShardsZone.Champions);
            var other = Give(engine, p1, "li_hin", ShardsZone.Champions);

            p0.Power = 20;
            var blocked = engine.Submit(new ShardsAttackChampionAction
            {
                PlayerIndex = 0, TargetPlayerIndex = 1, CardInstanceId = other.InstanceId
            });
            Assert.IsFalse(blocked.Accepted, "other champions can't be attacked while Zetta is in play");

            MustSubmit(engine, new ShardsEndTurnAction { PlayerIndex = 0 });
            DrainDecisionsWithDefaults(engine);
            Assert.AreEqual(50, p1.Health, "end-turn damage can't be assigned to Zetta's owner");
        }

        [Test]
        public void Split_LethalOnZetta_UnlocksOwnerInSameAnswer()
        {
            var engine = NewGame(seed: 71);
            var p0 = engine.State.Players[0];
            var p1 = engine.State.Players[1];
            p1.Hand.RemoveAll(_ => true); // no shields
            var zetta = Give(engine, p1, "zetta_encryptor", ShardsZone.Champions); // defense 5, Taunt

            p0.Power = 8;
            MustSubmit(engine, new ShardsEndTurnAction { PlayerIndex = 0 });
            Assert.AreEqual(PendingInputKind.Decision, engine.PendingInput.Kind, "split decision offered");
            var request = engine.PendingInput.Decision;
            Assert.AreEqual(0, request.Min, "everyone protected — waste allowed");
            var zettaOption = request.Options.Find(o => o.CardInstanceId == zetta.InstanceId);
            Assert.IsTrue(zettaOption.Required, "taunt champion is marked Required");
            Assert.AreEqual(5, zettaOption.Amount, "remaining effective HP rides the option");

            // 5 on Zetta (lethal) + 3 on the owner — legal in one answer.
            Answer(engine, zettaOption.Id, zettaOption.Id, zettaOption.Id, zettaOption.Id, zettaOption.Id,
                p1.Index, p1.Index, p1.Index);
            Assert.AreEqual(0, p1.Champions.Count, "Zetta died to the split");
            Assert.AreEqual(50 - 3, p1.Health, "remainder reached the owner");
        }

        [Test]
        public void Split_NoLethalOnZetta_OwnerAssignmentsDropped()
        {
            var engine = NewGame(seed: 73);
            var p0 = engine.State.Players[0];
            var p1 = engine.State.Players[1];
            p1.Hand.RemoveAll(_ => true);
            var zetta = Give(engine, p1, "zetta_encryptor", ShardsZone.Champions);

            p0.Power = 3; // not enough to kill Zetta (5)
            MustSubmit(engine, new ShardsEndTurnAction { PlayerIndex = 0 });
            Assert.AreEqual(PendingInputKind.Decision, engine.PendingInput.Kind);

            // Try to hit the protected owner directly — the rule guard wastes it.
            Answer(engine, p1.Index, p1.Index, p1.Index);
            Assert.AreEqual(50, p1.Health, "taunt still protects the owner");
            Assert.Contains(zetta, p1.Champions);
            Assert.AreEqual(1, engine.State.TurnPlayerIndex, "turn still ends cleanly");
        }

        [Test]
        public void Split_OverwhelmingPower_KillsAllOpponentsInstantly()
        {
            var engine = NewGame(seed: 75, players: 3);
            var p0 = engine.State.Players[0];
            engine.State.Players[1].Hand.Clear();
            engine.State.Players[2].Hand.Clear();
            Give(engine, engine.State.Players[1], "zetta_encryptor", ShardsZone.Champions); // even taunt can't help

            p0.Power = 5000; // infinite Infinity Shard
            MustSubmit(engine, new ShardsEndTurnAction { PlayerIndex = 0 });

            Assert.IsTrue(engine.State.Players[1].Eliminated, "no split window — instant kill");
            Assert.IsTrue(engine.State.Players[2].Eliminated);
            Assert.IsTrue(engine.State.GameOver);
            Assert.AreEqual(0, engine.State.WinnerIndex);
        }

        [Test]
        public void LiHin_UnattackableWithPower_ButDestroyEffectsKillIt()
        {
            var engine = NewGame(seed: 13);
            var p0 = engine.State.Players[0];
            var p1 = engine.State.Players[1];
            var liHin = Give(engine, p1, "li_hin", ShardsZone.Champions);

            p0.Power = 10;
            var rejected = engine.Submit(new ShardsAttackChampionAction
            {
                PlayerIndex = 0, TargetPlayerIndex = 1, CardInstanceId = liHin.InstanceId
            });
            Assert.IsFalse(rejected.Accepted, "Li Hin can't be attacked with power");

            // Thorn Zealot's Unify destroy-effect bypasses defense AND unattackability (FAQ).
            Give(engine, p0, "spore_cleric", ShardsZone.Hand);
            var sporeInHand = p0.Hand.Find(c => c.DefId == "spore_cleric");
            MustSubmit(engine, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = sporeInHand.InstanceId });
            var zealot = Give(engine, p0, "thorn_zealot", ShardsZone.Hand);
            MustSubmit(engine, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = zealot.InstanceId });
            Assert.AreEqual(0, p1.Champions.Count, "Thorn Zealot destroyed Li Hin");
            Assert.Contains(liHin, p1.Discard, "destroyed champion → owner's discard");
        }

        [Test]
        public void Axia_CostsLessPerHomodeusChampion()
        {
            var engine = NewGame(seed: 15);
            var p0 = engine.State.Players[0];
            Give(engine, p0, "evokatus", ShardsZone.Champions);
            Give(engine, p0, "optio_crusher", ShardsZone.Champions);
            var axia = ShardsCardDatabase.Get("axia");
            Assert.AreEqual(5, engine.EffectiveCost(p0, axia), "7 − 2 Homodeus champions = 5");
        }

        // ------------------------------------------------------------- discard-pile triggers

        [Test]
        public void Praetorian01_ReturnsFromDiscardWhenChampionPlayed()
        {
            var engine = NewGame(ShardsDlc.RelicsOfTheFuture, seed: 17);
            var p0 = engine.State.Players[0];
            var relic = Give(engine, p0, "praetorian_01", ShardsZone.Discard);
            var champion = Give(engine, p0, "evokatus", ShardsZone.Hand);
            MustSubmit(engine, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = champion.InstanceId });
            Assert.Contains(relic, p0.Hand, "Praetorian-01 bounced from discard to hand");
        }

        [Test]
        public void CinderScars_SecondCopyGetsThePairBonus()
        {
            var engine = NewGame(ShardsDlc.IntoTheHorizon, seed: 19);
            var p0 = engine.State.Players[0];
            var first = Give(engine, p0, "cinder_scars", ShardsZone.Hand);
            var second = Give(engine, p0, "cinder_scars", ShardsZone.Hand);

            MustSubmit(engine, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = first.InstanceId });
            Assert.AreEqual(0, p0.Power, "first copy: no pair yet");
            MustSubmit(engine, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = second.InstanceId });
            Assert.AreEqual(3, p0.Power, "second copy sees the first");
        }

        [Test]
        public void FungalHermit_OwnMasteryGainCountsForItsThreshold()
        {
            var engine = NewGame(seed: 21);
            var p0 = engine.State.Players[0];
            p0.Mastery = 9;
            var hermit = Give(engine, p0, "fungal_hermit", ShardsZone.Hand);
            p0.Health = 40;
            MustSubmit(engine, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = hermit.InstanceId });
            Assert.AreEqual(10, p0.Mastery);
            Assert.AreEqual(45, p0.Health, "M10 line fired thanks to the card's own +1 mastery");
        }

        // ------------------------------------------------------------- relic conversions

        [Test]
        public void EntropicTalons_HealthGainsConvertToPower_EvenAtTheCap()
        {
            var engine = NewGame(ShardsDlc.RelicsOfTheFuture, seed: 23, character: "volos");
            var p0 = engine.State.Players[0];
            p0.Health = 50; // at cap — "would gain" still counts (FAQ)
            var talons = Give(engine, p0, "entropic_talons", ShardsZone.Hand);
            MustSubmit(engine, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = talons.InstanceId });
            Assert.IsTrue(p0.HealthToPowerThisTurn);
            int before = p0.Power;
            engine.GainHealth(0, 6);
            Assert.AreEqual(50, p0.Health, "capped");
            Assert.AreEqual(before + 6, p0.Power, "fizzled heal still converts");
        }

        [Test]
        public void HeartOfNothing_TenPlusDamageOnOneOpponent_DrawsThreeExtra()
        {
            var engine = NewGame(ShardsDlc.RelicsOfTheFuture, seed: 25, character: "kosynwu");
            var p0 = engine.State.Players[0];
            var p1 = engine.State.Players[1];
            p1.Hand.RemoveAll(_ => true); // no shields
            var heart = Give(engine, p0, "heart_of_nothing", ShardsZone.Hand);
            MustSubmit(engine, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = heart.InstanceId });
            p0.Power = 12;
            MustSubmit(engine, new ShardsEndTurnAction { PlayerIndex = 0 });
            DrainDecisionsWithDefaults(engine);
            Assert.AreEqual(38, p1.Health);
            Assert.AreEqual(5 + 3, p0.Hand.Count, "redrew 5 + 3 bonus cards");
        }

        // ------------------------------------------------------------- ItH: monsters & destinies

        [Test]
        public void Ingeminex_BypassesRow_AttacksAllOnceAtEndOfRevealTurn()
        {
            var engine = NewGame(ShardsDlc.IntoTheHorizon, seed: 27, players: 3);
            var p0 = engine.State.Players[0];

            // Force the next center-deck card to be Brutality, then buy to trigger a refill.
            var brutality = new ShardsCard { InstanceId = engine.State.NextInstanceId++, DefId = "ingeminex_brutality", Owner = -1, Zone = ShardsZone.CenterDeck };
            engine.State.CenterDeck.Add(brutality); // list end = top
            p0.Gems = 20;
            int slot = 0;
            MustSubmit(engine, new ShardsBuyCardAction { PlayerIndex = 0, SlotIndex = slot });

            Assert.Contains(brutality, engine.State.ActiveMonsters, "Ingeminex went to its own space");
            Assert.IsNotNull(engine.State.CenterRow[slot], "the NEXT card refilled the row");
            Assert.AreNotEqual(brutality.InstanceId, engine.State.CenterRow[slot].InstanceId);

            MustSubmit(engine, new ShardsEndTurnAction { PlayerIndex = 0 });
            DrainDecisionsWithDefaults(engine);
            foreach (var player in engine.State.Players)
                Assert.AreEqual(45, player.Health, "Brutality hit ALL players for 5 (shield-proof loss)");

            // It does not attack again on later turns.
            MustSubmit(engine, new ShardsEndTurnAction { PlayerIndex = 1 });
            DrainDecisionsWithDefaults(engine);
            foreach (var player in engine.State.Players)
                Assert.AreEqual(45, player.Health, "one-time attack only");
        }

        [Test]
        public void Ingeminex_DefeatSameTurn_CancelsAttack_RewardsDefeaterOnly()
        {
            var engine = NewGame(ShardsDlc.IntoTheHorizon, seed: 29);
            var p0 = engine.State.Players[0];
            var brutality = new ShardsCard { InstanceId = engine.State.NextInstanceId++, DefId = "ingeminex_brutality", Owner = -1, Zone = ShardsZone.CenterDeck };
            engine.State.CenterDeck.Add(brutality);
            p0.Gems = 20;
            MustSubmit(engine, new ShardsBuyCardAction { PlayerIndex = 0, SlotIndex = 0 });
            Assert.Contains(brutality, engine.State.ActiveMonsters);

            p0.Health = 30;
            p0.Power = 10;
            MustSubmit(engine, new ShardsAttackMonsterAction { PlayerIndex = 0, CardInstanceId = brutality.InstanceId });
            Assert.AreEqual(0, engine.State.ActiveMonsters.Count);
            Assert.AreEqual(brutality.InstanceId, engine.State.CenterDeck[0].InstanceId, "defeated Ingeminex → BOTTOM of center deck");
            Assert.AreEqual(50, p0.Health, "reward: +20 health to the defeater only");

            MustSubmit(engine, new ShardsEndTurnAction { PlayerIndex = 0 });
            DrainDecisionsWithDefaults(engine);
            Assert.AreEqual(50, engine.State.Players[1].Health, "cancelled attack never fired");
        }

        [Test]
        public void Malice_AttackDestroysHighestCostChampion_EvenOnTies()
        {
            var engine = NewGame(ShardsDlc.IntoTheHorizon, seed: 61);
            var p0 = engine.State.Players[0];
            var p1 = engine.State.Players[1];

            // Reveal Malice via a refill on p0's turn; its attack fires at p0's end turn.
            var malice = new ShardsCard { InstanceId = engine.State.NextInstanceId++, DefId = "ingeminex_malice", Owner = -1, Zone = ShardsZone.CenterDeck };
            engine.State.CenterDeck.Add(malice);
            p0.Gems = 20;
            MustSubmit(engine, new ShardsBuyCardAction { PlayerIndex = 0, SlotIndex = 0 });
            Assert.Contains(malice, engine.State.ActiveMonsters);

            // p1 owns TWO champions with the SAME cost (the playtest tie report) and one
            // cheaper one; p0 owns one champion.
            var tieA = Give(engine, p1, "general_decurion", ShardsZone.Champions); // cost 7
            var tieB = Give(engine, p1, "general_decurion", ShardsZone.Champions); // cost 7
            var small = Give(engine, p1, "korvus_legionnaire", ShardsZone.Champions);
            var mine = Give(engine, p0, "korvus_legionnaire", ShardsZone.Champions);

            MustSubmit(engine, new ShardsEndTurnAction { PlayerIndex = 0 });
            DrainDecisionsWithDefaults(engine);

            Assert.AreEqual(2, p1.Champions.Count, "exactly ONE of p1's champions died");
            Assert.IsFalse(p1.Champions.Contains(tieA), "tie breaks to the lowest instance id");
            Assert.Contains(tieB, p1.Champions);
            Assert.Contains(small, p1.Champions);
            Assert.AreEqual(0, p0.Champions.Count, "EVERY player destroys their biggest");
            Assert.AreEqual(ShardsZone.Discard, tieA.Zone);
            Assert.Contains(tieA, p1.Discard);
            Assert.Contains(mine, p0.Discard);
        }

        [Test]
        public void Malice_DefeatGrantsReturnAndBonusDestiny()
        {
            var engine = NewGame(ShardsDlc.IntoTheHorizon, seed: 63);
            var p0 = engine.State.Players[0];
            var malice = new ShardsCard { InstanceId = engine.State.NextInstanceId++, DefId = "ingeminex_malice", Owner = -1, Zone = ShardsZone.CenterDeck };
            engine.State.CenterDeck.Add(malice);
            p0.Gems = 20;
            MustSubmit(engine, new ShardsBuyCardAction { PlayerIndex = 0, SlotIndex = 0 });

            var dead = Give(engine, p0, "general_decurion", ShardsZone.Discard);
            p0.Power = 10;
            MustSubmit(engine, new ShardsAttackMonsterAction { PlayerIndex = 0, CardInstanceId = malice.InstanceId });

            // Reward part 1: optional champion return from discard.
            Assert.AreEqual(PendingInputKind.Decision, engine.PendingInput.Kind, "return decision pending");
            Assert.AreEqual("soi.return", engine.PendingInput.Decision.Context);
            Answer(engine, dead.InstanceId);
            Assert.Contains(dead, p0.Hand, "champion returned to hand");

            // Reward part 2: bonus destiny from the row (bypasses M5 + once-per-game).
            Assert.AreEqual(PendingInputKind.Decision, engine.PendingInput.Kind, "destiny decision pending");
            Assert.AreEqual("soi.destiny", engine.PendingInput.Decision.Context);
            int before = engine.State.DestinyRow.Count;
            Answer(engine, engine.State.DestinyRow[0].InstanceId);
            Assert.AreEqual(1, p0.Destinies.Count, "bonus destiny granted");
            Assert.AreEqual(before - 1, engine.State.DestinyRow.Count);
        }

        [Test]
        public void Setup_NoIngeminexInOpeningRow_MonstersReshuffledBack()
        {
            // A monster revealed during the INITIAL row fill would attack on turn 1
            // before anyone acts. Setup must hold any drawn Ingeminex out of the row and
            // shuffle them back into the center deck: none active, none in the row, none
            // lost. (RotF keeps Corruption in the deck so the count is exact.)
            var dlc = ShardsDlc.IntoTheHorizon | ShardsDlc.RelicsOfTheFuture;
            int expectedMonsters = 0;
            foreach (var def in ShardsCardDatabase.All)
                if (def.IsMonster) expectedMonsters += def.Quantity;
            Assert.Greater(expectedMonsters, 0, "ItH must contribute Ingeminex");

            for (ulong seed = 1; seed <= 40; seed++)
            {
                var st = NewGame(dlc, seed: seed).State;
                Assert.AreEqual(0, st.ActiveMonsters.Count, "seed " + seed + ": no active Ingeminex at setup");
                Assert.AreEqual(0, st.PendingMonsterAttacks.Count, "seed " + seed + ": no pending monster attacks at setup");
                foreach (var slot in st.CenterRow)
                    Assert.IsFalse(slot != null && slot.Def.IsMonster, "seed " + seed + ": no Ingeminex in the opening row");

                int inDeck = 0;
                foreach (var c in st.CenterDeck) if (c.Def.IsMonster) inDeck++;
                Assert.AreEqual(expectedMonsters, inDeck,
                    "seed " + seed + ": every Ingeminex conserved in the center deck");
            }
        }

        [Test]
        public void BloodForBlood_TriggersOnFivePlusUnblockedDamage_BanishesPlayedCard()
        {
            var engine = NewGame(ShardsDlc.IntoTheHorizon, seed: 51);
            var p0 = engine.State.Players[0];
            var p1 = engine.State.Players[1];
            p1.Hand.RemoveAll(_ => true); // no shields — every point lands unblocked

            // Own the destiny (white-box; acquisition rules are covered elsewhere).
            var destiny = new ShardsCard
            {
                InstanceId = engine.State.NextInstanceId++,
                DefId = "blood_for_blood",
                Owner = 0,
                Zone = ShardsZone.DestinyRow
            };
            p0.Destinies.Add(destiny);

            // Play a card legitimately so PlayedThisTurn holds a banish candidate.
            var played = p0.Hand[0];
            MustSubmit(engine, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = played.InstanceId });

            p0.Power = 6;
            MustSubmit(engine, new ShardsEndTurnAction { PlayerIndex = 0 });

            Assert.AreEqual(PendingInputKind.Decision, engine.PendingInput.Kind,
                "5+ unblocked damage must offer the Blood for Blood banish");
            var request = engine.PendingInput.Decision;
            Assert.AreEqual("soi.banish", request.Context);
            Assert.AreEqual(0, engine.PendingInput.PlayerIndex, "the ATTACKER chooses");
            Assert.AreEqual(50 - 6, p1.Health, "damage lands before the trigger resolves");

            Answer(engine, played.InstanceId);
            Assert.AreEqual(ShardsZone.Banished, played.Zone, "chosen card banished");
            Assert.Contains(played, engine.State.Banished);
            DrainDecisionsWithDefaults(engine);
            Assert.AreEqual(1, engine.State.TurnPlayerIndex, "turn passes cleanly afterwards");
        }

        [Test]
        public void Destiny_TakeAtMastery5_OncePerGame_RowNeverRefills()
        {
            var engine = NewGame(ShardsDlc.IntoTheHorizon, seed: 31);
            var p0 = engine.State.Players[0];
            Assert.AreEqual(6, engine.State.DestinyRow.Count);

            p0.Mastery = 4;
            var early = engine.Submit(new ShardsTakeDestinyAction
            {
                PlayerIndex = 0, CardInstanceId = engine.State.DestinyRow[0].InstanceId
            });
            Assert.IsFalse(early.Accepted, "requires Mastery 5");

            p0.Mastery = 5;
            var taken = engine.State.DestinyRow[0];
            MustSubmit(engine, new ShardsTakeDestinyAction { PlayerIndex = 0, CardInstanceId = taken.InstanceId });
            DrainDecisionsWithDefaults(engine);
            Assert.AreEqual(5, engine.State.DestinyRow.Count, "the row only shrinks");
            Assert.Contains(taken, p0.Destinies);

            var again = engine.Submit(new ShardsTakeDestinyAction
            {
                PlayerIndex = 0, CardInstanceId = engine.State.DestinyRow[0].InstanceId
            });
            Assert.IsFalse(again.Accepted, "once per game");
        }

        [Test]
        public void LoseHealth_IsNotDamage_SimultaneousDrop_IsATie()
        {
            var engine = NewGame(ShardsDlc.IntoTheHorizon, seed: 33);
            engine.State.Players[0].Health = 3;
            engine.State.Players[1].Health = 3;

            // Bound for Life: ALL players lose 4 — unpreventable, both die at once ⇒ tie.
            var p0 = engine.State.Players[0];
            var destiny = new ShardsCard { InstanceId = engine.State.NextInstanceId++, DefId = "bound_for_life", Owner = 0, Zone = ShardsZone.SetAside };
            p0.Destinies.Add(destiny);
            MustSubmit(engine, new ShardsExhaustAction { PlayerIndex = 0, CardInstanceId = destiny.InstanceId });

            Assert.IsTrue(engine.State.GameOver);
            Assert.AreEqual(-1, engine.State.WinnerIndex, "simultaneous elimination is a TIE");
        }

        [Test]
        public void SlipstreamShard_M20_ExtraTurn_OncePerGame()
        {
            var engine = NewGame(ShardsDlc.RelicsOfTheFuture | ShardsDlc.ShadowOfSalvation, seed: 35, character: "rez");
            var p0 = engine.State.Players[0];
            p0.Mastery = 20;

            var shard = Give(engine, p0, "slipstream_shard", ShardsZone.Hand);
            MustSubmit(engine, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = shard.InstanceId });
            Assert.AreEqual(0, engine.State.ExtraTurnForPlayer >= 0 ? engine.State.ExtraTurnForPlayer : -99, "extra turn armed for P0");

            MustSubmit(engine, new ShardsEndTurnAction { PlayerIndex = 0 });
            DrainDecisionsWithDefaults(engine);
            Assert.AreEqual(0, engine.State.TurnPlayerIndex, "P0 takes another turn");
            Assert.IsTrue(p0.ExtraTurnUsed);

            // Second use never fires again.
            var shard2 = Give(engine, p0, "slipstream_shard", ShardsZone.Hand);
            MustSubmit(engine, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = shard2.InstanceId });
            Assert.AreEqual(-1, engine.State.ExtraTurnForPlayer, "once per game");
        }

        [Test]
        public void Swyft_WithRez_MayKeepFastPlayedCards()
        {
            var engine = NewGame(ShardsDlc.ShadowOfSalvation, seed: 37, character: "rez");
            var p0 = engine.State.Players[0];
            Give(engine, p0, "swyft", ShardsZone.Champions);

            var merc = new ShardsCard { InstanceId = engine.State.NextInstanceId++, DefId = "nil_assassin", Owner = -1, Zone = ShardsZone.CenterRow };
            engine.State.CenterRow[0] = merc;
            p0.Gems = 2;
            p0.Power = 0;
            MustSubmit(engine, new ShardsBuyCardAction { PlayerIndex = 0, SlotIndex = 0, FastPlay = true });
            Assert.AreEqual(5, p0.Power);

            MustSubmit(engine, new ShardsEndTurnAction { PlayerIndex = 0 });
            // Damage flows first (5 power → defender), then the keep decision.
            int guard = 0;
            while (engine.PendingInput.Kind == PendingInputKind.Decision &&
                   engine.PendingInput.Decision.Context != "soi.keepfast" && guard++ < 10)
                DrainOne(engine);
            Assert.AreEqual("soi.keepfast", engine.PendingInput.Decision.Context);
            Answer(engine, merc.InstanceId);
            DrainDecisionsWithDefaults(engine);

            Assert.Contains(merc, p0.Discard, "kept fast-play joins the discard (recruited)");
            Assert.AreNotEqual(merc.InstanceId, engine.State.CenterDeck.Count > 0 ? engine.State.CenterDeck[0].InstanceId : -1);
        }

        // ------------------------------------------------------------- verification-pass fixes

        [Test]
        public void MandatoryReturn_KorvusCannotDecline()
        {
            var engine = NewGame(seed: 41);
            var p0 = engine.State.Players[0];
            Give(engine, p0, "evokatus", ShardsZone.Discard);
            Give(engine, p0, "li_hin", ShardsZone.Discard);
            var korvus = Give(engine, p0, "korvus_legionnaire", ShardsZone.Hand);
            MustSubmit(engine, new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = korvus.InstanceId });

            Assert.AreEqual(PendingInputKind.Decision, engine.PendingInput.Kind);
            Assert.AreEqual(1, engine.PendingInput.Decision.Min, "no 'may' printed — the return is mandatory");
            var empty = new DecisionAnswer { DecisionId = engine.PendingInput.Decision.Id };
            Assert.IsFalse(engine.Submit(new SubmitDecisionAction { PlayerIndex = 0, Answer = empty }).Accepted,
                "declining a mandatory return is rejected");
            Answer(engine, engine.PendingInput.Decision.Options[0].Id);
            Assert.AreEqual(1, p0.Discard.FindAll(c => c.Def.IsChampion).Count, "one champion returned");
        }

        [Test]
        public void SplitDecision_FullPowerAssignmentIsMandatory()
        {
            var engine = NewGame(seed: 43, players: 3);
            var p0 = engine.State.Players[0];
            p0.Power = 4;
            MustSubmit(engine, new ShardsEndTurnAction { PlayerIndex = 0 });
            Assert.AreEqual("soi.split", engine.PendingInput.Decision.Context);
            Assert.AreEqual(4, engine.PendingInput.Decision.Min, "assign ALL remaining power");

            var partial = new DecisionAnswer { DecisionId = engine.PendingInput.Decision.Id };
            partial.ChosenOptionIds.AddRange(new[] { 1, 1 });
            Assert.IsFalse(engine.Submit(new SubmitDecisionAction { PlayerIndex = 0, Answer = partial }).Accepted,
                "letting power evaporate is illegal");
            Answer(engine, 1, 1, 2, 2);
            DrainDecisionsWithDefaults(engine);
            Assert.AreEqual(48, engine.State.Players[1].Health);
            Assert.AreEqual(48, engine.State.Players[2].Health);
        }

        [Test]
        public void SelfElimination_MidTurn_PassesTheTurnInsteadOfDeadlocking()
        {
            var engine = NewGame(ShardsDlc.IntoTheHorizon, seed: 45, players: 3);
            var p0 = engine.State.Players[0];
            p0.Health = 2; // Bound for Life kills only its own exhauster

            var destiny = new ShardsCard { InstanceId = engine.State.NextInstanceId++, DefId = "bound_for_life", Owner = 0, Zone = ShardsZone.SetAside };
            p0.Destinies.Add(destiny);
            MustSubmit(engine, new ShardsExhaustAction { PlayerIndex = 0, CardInstanceId = destiny.InstanceId });

            Assert.IsTrue(p0.Eliminated);
            Assert.IsFalse(engine.State.GameOver, "two players remain");
            Assert.AreEqual(1, engine.State.TurnPlayerIndex, "turn passed to the next living player");
            Assert.AreEqual(PendingInputKind.Priority, engine.PendingInput.Kind);
        }

        [Test]
        public void RezRelics_AvailableWithShadowOfSalvationAlone()
        {
            var engine = NewGame(ShardsDlc.ShadowOfSalvation, seed: 47, character: "rez");
            var p0 = engine.State.Players[0];
            Assert.AreEqual(2, p0.SetAside.Count, "Rez's relics ship in the SoS box — no RotF needed");

            p0.Mastery = 10;
            var relic = p0.SetAside[0];
            MustSubmit(engine, new ShardsRecruitRelicAction { PlayerIndex = 0, CardInstanceId = relic.InstanceId });
            Assert.Contains(relic, p0.Discard);
            Assert.AreEqual(1, p0.SetAside.Count, "the other relic stays set aside");
        }

        [Test]
        public void DecurionM20_AlsoDoublesFastPlaysAfterTheExhaust()
        {
            var engine = NewGame(seed: 49);
            var p0 = engine.State.Players[0];
            p0.CopyHomodeusAlliesThisTurn = true; // armed by General Decurion's M20 exhaust

            var merc = new ShardsCard { InstanceId = engine.State.NextInstanceId++, DefId = "venator_of_the_wastes", Owner = -1, Zone = ShardsZone.CenterRow };
            engine.State.CenterRow[0] = merc;
            p0.Gems = 4;
            MustSubmit(engine, new ShardsBuyCardAction { PlayerIndex = 0, SlotIndex = 0, FastPlay = true });
            Assert.AreEqual(8, p0.Power, "a fast-played Homodeus ally resolves twice too");
        }

        // ------------------------------------------------------------- bot sims

        [Test]
        public void HeuristicBots_FullGame_TerminatesWithAWinner()
        {
            for (ulong seed = 1; seed <= 3; seed++)
            {
                ShardsCardDatabase.Clear();
                ShardsContentRegistry.EnsureRegistered();
                var specs = new List<PlayerSpec>();
                var characters = new[] { "decima", "tetra", "volos" };
                for (int i = 0; i < 3; i++)
                    specs.Add(new PlayerSpec { Name = "Bot" + i, CharacterId = characters[i] });
                var adapter = new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(
                    seed, specs, ShardsDlc.RelicsOfTheFuture | ShardsDlc.ShadowOfSalvation | ShardsDlc.IntoTheHorizon));
                var bots = new IBotAgent[3];
                for (int i = 0; i < 3; i++)
                    bots[i] = new ShardsHeuristicBot(seed * 100 + (ulong)i, adapter.Inner);

                int guard = 0;
                while (!adapter.GameOver && guard++ < 30000)
                {
                    var pending = adapter.PendingInput;
                    Assert.IsNotNull(pending, $"seed {seed}: stall at step {guard}");
                    var action = bots[pending.PlayerIndex].Choose(pending, null)
                                 ?? adapter.DefaultActionFor(pending);
                    var result = adapter.Submit(action);
                    if (!result.Accepted)
                    {
                        // A heuristic misfire must never stall the game — fall back.
                        var fallback = adapter.DefaultActionFor(adapter.PendingInput);
                        Assert.IsTrue(adapter.Submit(fallback).Accepted,
                            $"seed {seed}: bot AND default rejected: {result.Error}");
                    }
                }
                Assert.IsTrue(adapter.GameOver, $"seed {seed}: no result in 30000 submits (round {adapter.Inner.State.Round})");
                Assert.GreaterOrEqual(adapter.WinnerIndex, -1);
            }
        }
    }
}
