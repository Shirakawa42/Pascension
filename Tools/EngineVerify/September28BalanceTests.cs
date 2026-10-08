using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;
using Shards.Content;
using Shards.Engine;

namespace Pascension.Engine.Tests
{
    public sealed class September28BalanceTests
    {
        private static ShardsEngine Game()
        {
            ShardsCardDatabase.Clear(); ShardsContentRegistry.EnsureRegistered();
            var adapter = new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(92828,
                new List<PlayerSpec> { new PlayerSpec { Name = "A", CharacterId = "decima" },
                    new PlayerSpec { Name = "B", CharacterId = "tetra" } }, ShardsDlc.Duel));
            while (adapter.PendingInput.Kind == Pascension.Engine.Core.PendingInputKind.Decision)
                Assert.IsTrue(adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted);
            return adapter.Inner;
        }
        private static ShardsCard Give(ShardsEngine e, string id, ShardsZone zone = ShardsZone.Hand, int seat = 0)
        {
            var c = new ShardsCard { InstanceId = e.State.NextInstanceId++, DefId = id, Owner = seat, Zone = zone };
            var p = e.State.Players[seat];
            if (zone == ShardsZone.Champions) p.Champions.Add(c);
            else if (zone == ShardsZone.DestinyRow) p.Destinies.Add(c);
            else p.Hand.Add(c);
            e.State.InvalidateCardIndex(); return c;
        }
        [TestCase(14, 0)] [TestCase(15, 8)] [TestCase(20, 10)]
        public void DaticRobesDiscardShieldStartsAtFifteen(int mastery, int shield)
        {
            var e = Game(); var p = e.State.Players[0]; p.Mastery = mastery;
            var robes = ShardsCardDatabase.Get("datic_robes_duel");
            Assert.AreEqual(shield, robes.DiscardPassiveShield(p));
            Assert.AreEqual(mastery, robes.DynamicShield(p));
        }

        [Test]
        public void WarpquartzNewlyDrawnCardIsAvailableToBanish()
        {
            var e = Game(); var p = e.State.Players[0];
            p.Hand.Clear(); p.Deck.Clear(); p.Discard.Clear(); p.Gems = 0; p.Power = 0;
            var target = Give(e, "crystal"); p.Hand.Remove(target); target.Zone = ShardsZone.Deck; p.Deck.Add(target);
            var warp = Give(e, "warpquartz_duel");
            Assert.IsTrue(e.Submit(new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = warp.InstanceId }).Accepted);
            Assert.IsTrue(p.Hand.Contains(target));
            Assert.IsTrue(e.PendingInput.Decision.Options.Exists(o => o.CardInstanceId == target.InstanceId));
            var answer = new DecisionAnswer { DecisionId = e.PendingInput.Decision.Id };
            answer.ChosenOptionIds.Add(target.InstanceId);
            Assert.IsTrue(e.Submit(new SubmitDecisionAction { PlayerIndex = 0, Answer = answer }).Accepted);
            Assert.AreEqual(5, p.Gems); Assert.AreEqual(3, p.Power);
            Assert.IsFalse(p.Hand.Contains(target));
        }

        [TestCase(false)] [TestCase(true)]
        public void PowerStrugglePaysSixOnlyAfterSacrificingChampion(bool sacrifice)
        {
            var e = Game(); var p = e.State.Players[0]; p.Power = 0;
            var champion = Give(e, "ferrata_guard_duel", ShardsZone.Champions);
            var destiny = Give(e, "power_struggle", ShardsZone.DestinyRow);
            Assert.IsTrue(e.Submit(new ShardsExhaustAction { PlayerIndex = 0, CardInstanceId = destiny.InstanceId }).Accepted);
            var answer = new DecisionAnswer { DecisionId = e.PendingInput.Decision.Id };
            if (sacrifice) answer.ChosenOptionIds.Add(champion.InstanceId);
            Assert.IsTrue(e.Submit(new SubmitDecisionAction { PlayerIndex = 0, Answer = answer }).Accepted);
            Assert.AreEqual(sacrifice ? 6 : 0, p.Power);
            Assert.AreEqual(!sacrifice, p.Champions.Contains(champion));
            Assert.AreEqual(sacrifice, p.Discard.Contains(champion));
        }

        [TestCase(false, 4)] [TestCase(true, 6)]
        public void KnightUnifyRetainsSixPowerCeiling(bool reveal, int expected)
        {
            var e = Game(); var p = e.State.Players[0]; p.Hand.Clear(); p.Power = 0;
            if (reveal) Give(e, "thorn_zealot");
            var c = Give(e, "leshai_knight");
            Assert.IsTrue(e.Submit(new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = c.InstanceId }).Accepted);
            Assert.AreEqual(expected, p.Power);
        }
        [TestCase(false, 2)] [TestCase(true, 3)]
        public void FerrataGetsBaseGemAndCountsItself(bool anotherHomodeus, int expected)
        {
            var e = Game(); var p = e.State.Players[0]; p.Gems = 0;
            var c = Give(e, "ferrata_guard_duel", ShardsZone.Champions);
            if (anotherHomodeus) Give(e, "primus_pilus_duel", ShardsZone.Champions);
            Assert.IsTrue(e.Submit(new ShardsExhaustAction { PlayerIndex = 0, CardInstanceId = c.InstanceId }).Accepted);
            Assert.AreEqual(expected, p.Gems);
        }
        [TestCase(4, true)] [TestCase(5, false)]
        public void SwyftDefenseAppliesToMidTurnAttacks(int power, bool survives)
        {
            var e = Game(); var p = e.State.Players[0]; p.Power = power;
            var c = Give(e, "swyft_duel", ShardsZone.Champions, 1);
            Assert.AreEqual(5, e.EffectiveDefense(e.State.Players[1], c));
            bool offered = e.LegalActions(0).Exists(action =>
                action is ShardsAttackChampionAction attack && attack.CardInstanceId == c.InstanceId);
            Assert.AreEqual(!survives, offered, "Four power cannot pay Swyft's full defense; five can");
            var result = e.Submit(new ShardsAttackChampionAction
            {
                PlayerIndex = 0, TargetPlayerIndex = 1, CardInstanceId = c.InstanceId, Amount = 5
            });
            Assert.AreEqual(!survives, result.Accepted);
            Assert.AreEqual(survives, e.State.Players[1].Champions.Contains(c));
            Assert.AreEqual(survives ? ShardsZone.Champions : ShardsZone.Discard, c.Zone);
            Assert.AreEqual(survives ? power : 0, p.Power, "Rejected attacks spend nothing; lethal attacks pay exactly five");
            Assert.AreEqual(0, c.DamageThisTurn, "A rejected attack cannot leave partial damage");
            Assert.AreEqual(0, e.State.TurnPlayerIndex, "Champion combat resolves during the attacking player's turn");
        }
        [Test]
        public void CommandosGainThreeGemsAndKeepCombatValues()
        {
            var e = Game(); var p = e.State.Players[0]; p.Gems = p.Power = 0;
            var c = Give(e, "torian_commandos");
            Assert.AreEqual(4, e.ShieldValue(p, c));
            Assert.IsTrue(e.Submit(new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = c.InstanceId }).Accepted);
            Assert.AreEqual(3, p.Gems); Assert.AreEqual(2, p.Power);
        }
        [TestCase(1, 0)] [TestCase(2, 6)]
        public void MedicineRequiresTwoPositiveEvenCosts(int evenCards, int healing)
        {
            var e = Game(); var p = e.State.Players[0]; p.Health = 30;
            for (int i = 0; i < evenCards; i++) p.PlayedThisTurn.Add(Give(e, "mining_drones"));
            p.PlayedThisTurn.Add(Give(e, "crystal")); // Cost zero must not satisfy the gate.
            var c = Give(e, "advanced_medicine", ShardsZone.DestinyRow);
            Assert.IsTrue(e.Submit(new ShardsExhaustAction { PlayerIndex = 0, CardInstanceId = c.InstanceId }).Accepted);
            Assert.AreEqual(30 + healing, p.Health);
        }
        [Test]
        public void DecimaDiscountGatesAndFloorsAtZero()
        {
            var e = Game(); var p = e.State.Players[0]; p.CharacterId = "decima";
            var def = ShardsCardDatabase.Get("torian_commandos");
            p.Mastery = 4; Assert.AreEqual(3, e.EffectiveCost(p, def));
            p.Mastery = 5; Assert.AreEqual(1, e.EffectiveCost(p, def));
            Assert.AreEqual(0, e.EffectiveCost(p, ShardsCardDatabase.Get("kiln_drone")));
            p.FirstBuyUsedThisTurn = true; Assert.AreEqual(3, e.EffectiveCost(p, def));
        }
    }
}
