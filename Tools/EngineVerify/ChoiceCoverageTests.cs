using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Shards.Content;
using Shards.Engine;

namespace Pascension.Engine.Tests
{
    // Independent headless scenarios: neither the live adapter nor learner is changed.
    public sealed class ChoiceCoverageTests
    {
        [SetUp]
        public void Register() { ShardsCardDatabase.Clear(); ShardsContentRegistry.EnsureRegistered(); }

        private static ShardsEngineAdapter Game()
        {
            var adapter = new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(93026,
                new List<PlayerSpec> { new PlayerSpec { Name = "A", CharacterId = "volos" },
                    new PlayerSpec { Name = "B", CharacterId = "tetra" } }, ShardsDlc.Duel));
            Drain(adapter);
            return adapter;
        }
        private static void Drain(ShardsEngineAdapter adapter)
        {
            int guard = 0;
            while (adapter.PendingInput?.Kind == PendingInputKind.Decision && guard++ < 50)
                Assert.IsTrue(adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted);
            Assert.Less(guard, 50);
        }
        private static ShardsCard Give(ShardsEngine engine, string id, ShardsZone zone = ShardsZone.Hand)
        {
            var card = new ShardsCard { InstanceId = engine.State.NextInstanceId++, DefId = id, Owner = 0, Zone = zone };
            var p = engine.State.Players[0];
            if (zone == ShardsZone.Champions) p.Champions.Add(card);
            else if (zone == ShardsZone.SetAside) p.SetAside.Add(card);
            else p.Hand.Add(card);
            engine.State.InvalidateCardIndex();
            return card;
        }
        private static void Play(ShardsEngine engine, ShardsCard card)
        { Assert.IsTrue(engine.Submit(new ShardsPlayCardAction { PlayerIndex = 0, CardInstanceId = card.InstanceId }).Accepted); }

        [TestCase(29, 5)]
        [TestCase(30, 9999)]
        public void InfinityShard_ActualPlayAtFinalThreshold(int mastery, int power)
        {
            var e = Game().Inner; var p = e.State.Players[0]; p.Mastery = mastery; p.Power = 0;
            Play(e, Give(e, "infinity_shard"));
            Assert.AreEqual(power, p.Power);
        }

        [TestCase(17, -1)]
        [TestCase(18, 0)]
        public void DuelSlipstream_OwnMasteryCrossesExtraTurnThreshold(int mastery, int expectedSeat)
        {
            var e = Game().Inner; var p = e.State.Players[0]; p.Mastery = mastery;
            Play(e, Give(e, "slipstream_shard_duel"));
            Assert.AreEqual(mastery + 2, p.Mastery);
            Assert.AreEqual(expectedSeat, e.State.ExtraTurnForPlayer);
            if (expectedSeat == 0)
            {
                Assert.IsTrue(p.ExtraTurnUsed);
                e.State.ExtraTurnForPlayer = -1;
                Play(e, Give(e, "slipstream_shard_duel"));
                Assert.AreEqual(-1, e.State.ExtraTurnForPlayer, "second copy cannot arm another extra turn");
            }
        }

        [TestCase(18, 10)]
        [TestCase(19, 15)]
        [TestCase(24, 20)]
        public void TerminalCrescents_ThresholdAfterOwnMasteryGain(int mastery, int power)
        {
            var e = Game().Inner; var p = e.State.Players[0]; p.Mastery = mastery; p.Power = 0;
            Play(e, Give(e, "terminal_crescents_duel"));
            Assert.AreEqual(mastery + 1, p.Mastery); Assert.AreEqual(power, p.Power);
        }

        [TestCase(19, 10, 2)]
        [TestCase(20, 20, 4)]
        public void UnknownGod_DoublesOwnAndOtherChampionEffects(int mastery, int healing, int power)
        {
            var e = Game().Inner; var p = e.State.Players[0]; p.Mastery = mastery; p.Health = 1; p.Power = 0;
            var god = Give(e, "unknown_god", ShardsZone.Champions);
            var other = Give(e, "li_hin_duel", ShardsZone.Champions);
            Assert.IsTrue(e.Submit(new ShardsExhaustAction { PlayerIndex = 0, CardInstanceId = god.InstanceId }).Accepted);
            Assert.AreEqual(1 + healing, p.Health);
            Assert.IsTrue(e.Submit(new ShardsExhaustAction { PlayerIndex = 0, CardInstanceId = other.InstanceId }).Accepted);
            Assert.AreEqual(power, p.Power);
        }

        [Test]
        public void Volos_ActualHealChoiceWithTalonsAtFullHealthGeneratesPower()
        {
            var e = Game().Inner; var p = e.State.Players[0]; p.CharacterId = "volos";
            p.Mastery = 5; p.Health = 50; p.Gems = 0;
            Play(e, Give(e, "entropic_talons")); int power = p.Power;
            Assert.IsTrue(e.Submit(new ShardsHeroAbilityAction { PlayerIndex = 0 }).Accepted);
            var request = e.PendingInput.Decision;
            Assert.AreEqual("soi.volos", request.Context);
            Assert.IsTrue(e.Submit(new SubmitDecisionAction { PlayerIndex = 0, Answer = new DecisionAnswer
                { DecisionId = request.Id, ChosenOptionIds = new List<int> { 0 } } }).Accepted);
            Assert.AreEqual(50, p.Health); Assert.AreEqual(power + 3, p.Power);
        }

        [TestCase(0, false)] [TestCase(1, false)] [TestCase(3, false)]
        [TestCase(0, true)] [TestCase(1, true)] [TestCase(2, true)]
        public void Corruption_ExtraRelicRewardPreservesNormalRecruitmentAllowance(int remaining, bool recruited)
        {
            var adapter = Game(); var e = adapter.Inner; var p = e.State.Players[0];
            p.Mastery = 0; p.RelicRecruited = recruited; p.SetAside.Clear();
            string[] ids = { "entropic_talons", "panconscious_crown_duel", "unknown_god" };
            for (int i = 0; i < remaining; i++) Give(e, ids[i], ShardsZone.SetAside);
            int hand = p.Hand.Count;
            var gate = Give(e, "doom_gate", ShardsZone.Champions);
            e.State.ActiveMonsters.Clear();
            e.State.ActiveMonsters.Add(new ShardsCard { InstanceId = e.State.NextInstanceId++,
                DefId = "ingeminex_corruption", Owner = -1, Zone = ShardsZone.CenterDeck });
            e.State.InvalidateCardIndex();
            Assert.IsTrue(e.Submit(new ShardsExhaustAction { PlayerIndex = 0, CardInstanceId = gate.InstanceId }).Accepted);
            Drain(adapter);
            Assert.AreEqual(0, e.State.ActiveMonsters.Count);
            Assert.AreEqual(hand + (remaining > 0 ? 1 : 0), p.Hand.Count, "bonus relic goes to hand even at mastery zero");
            Assert.AreEqual(System.Math.Max(0, remaining - 1), p.SetAside.Count);
            Assert.AreEqual(recruited, p.RelicRecruited, "bonus reward must neither consume nor reset normal allowance");
            p.Mastery = 10;
            Assert.AreEqual(!recruited && remaining > 1,
                e.LegalActions(0).Exists(a => a is ShardsRecruitRelicAction));
        }
    }
}
