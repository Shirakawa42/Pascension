using NUnit.Framework;
using Pascension.Bots;
using Pascension.Engine.Actions;
using Pascension.Engine.Board;
using Pascension.Engine.Cards;
using Pascension.Engine.Core;
using Pascension.Engine.Targeting;

namespace Pascension.Engine.Tests
{
    [TestFixture]
    public class SystemsTests
    {
        [Test]
        public void MonsterKill_RewardsXpAndDraw_TotemTriggers_SlotRefills()
        {
            var config = TestGames.BareConfig();
            config.Players[0].DeckOverride = new() { ("fireball", 10) };
            config.BasicPile.Add("hobgoblin", 8);
            var engine = new GameEngine(config);
            var p0 = engine.State.Players[0];

            // White-box: equip a Stone Totem.
            var totem = new CardInstance { InstanceId = 90001, DefId = "stone_totem", Owner = 0, Zone = ZoneType.Exile };
            engine.Api.Equip(p0, totem);

            engine.PlayFromHand(0, "fireball");
            engine.PlayFromHand(0, "fireball");
            Assert.AreEqual(4, p0.DamagePool);

            var hobgoblin = engine.State.Market.SlotCard(CardTier.Basic, 0);
            int handBefore = p0.Hand.Count;
            engine.MustSubmit(new AssignDamageAction
            {
                PlayerIndex = 0,
                Target = TargetRef.Monster((int)CardTier.Basic, 0),
                Amount = 4
            });
            engine.PassUntilStackEmpty();

            Assert.AreEqual(ZoneType.Exile, hobgoblin.Zone, "Dead monster exiled");
            Assert.IsTrue(engine.State.MarketExile.Contains(hobgoblin));
            Assert.IsNotNull(engine.State.Market.SlotCard(CardTier.Basic, 0), "Slot refilled");
            Assert.AreEqual(handBefore + 1, p0.Hand.Count, "Hobgoblin reward drew a card");
            // 2 XP (reward) + 1 XP (totem): threshold 2 → level 2 with 1 XP left.
            Assert.AreEqual(2, p0.Level);
            Assert.AreEqual(1, p0.Xp);
        }

        [Test]
        public void Boss_RequiresStep50_DamageResets_BurstWins()
        {
            var engine = new GameEngine(TestGames.BareConfig());
            var p0 = engine.State.Players[0];
            var boss = engine.State.Boss;

            engine.Api.GainDamage(p0, 12, false);
            engine.MustReject(new AssignDamageAction { PlayerIndex = 0, Target = TargetRef.TheBoss(), Amount = 12 });

            p0.Position = 50;
            engine.MustSubmit(new AssignDamageAction { PlayerIndex = 0, Target = TargetRef.TheBoss(), Amount = 12 });
            engine.PassUntilStackEmpty();
            Assert.AreEqual(12, boss.MarkedDamage);

            engine.EndTurn();
            Assert.AreEqual(0, boss.MarkedDamage, "Boss damage resets at end of turn");

            engine.EndTurn(); // P1's turn passes
            Assert.AreEqual(0, engine.State.TurnPlayerIndex);

            engine.Api.GainDamage(p0, 20, false);
            engine.MustSubmit(new AssignDamageAction { PlayerIndex = 0, Target = TargetRef.TheBoss(), Amount = 20 });
            engine.PassUntilStackEmpty();

            Assert.IsTrue(engine.State.GameOver);
            Assert.AreEqual(0, engine.State.WinnerIndex);
        }

        [Test]
        public void Inn_FirstPassGivesChoice_CheckpointClampsMoveBack()
        {
            var engine = new GameEngine(TestGames.BareConfig());
            var p0 = engine.State.Players[0];
            engine.Api.GainAp(p0, 15);

            engine.MustSubmit(new MoveStepsAction { PlayerIndex = 0, Steps = 12 });
            // Crossed the inn at step 10 → inn choice.
            Assert.AreEqual(PendingInputKind.Decision, engine.PendingInput.Kind);
            engine.Answer(0); // +2 XP

            Assert.AreEqual(12, p0.Position);
            Assert.AreEqual(10, p0.LastInnCheckpoint);
            Assert.AreEqual(2, p0.Level, "2 XP → level 2");

            BoardSystem.MoveBack(engine.Api, p0, 5);
            Assert.AreEqual(10, p0.Position, "Cannot be pushed back past the last inn");

            // Crossing the same inn again gives nothing.
            engine.MustSubmit(new MoveStepsAction { PlayerIndex = 0, Steps = 1 });
            Assert.IsNull(engine.PendingInput?.Decision, "No second inn reward");
        }

        [Test]
        public void HeroPassives_Kindle_And_Pathfinder()
        {
            // Ignis: first damage card each turn gives +1.
            var config = TestGames.BareConfig();
            config.Players[0].HeroId = "ignis";
            config.Players[0].DeckOverride = new() { ("fireball", 10) };
            var engine = new GameEngine(config);
            var p0 = engine.State.Players[0];
            engine.PlayFromHand(0, "fireball");
            Assert.AreEqual(3, p0.DamagePool, "Kindle: 2 + 1");
            engine.PlayFromHand(0, "fireball");
            Assert.AreEqual(5, p0.DamagePool, "Second damage card gets no bonus");

            // Wren: first move each turn goes 1 extra step.
            var config2 = TestGames.BareConfig();
            config2.Players[0].HeroId = "wren";
            var engine2 = new GameEngine(config2);
            var w = engine2.State.Players[0];
            engine2.Api.GainAp(w, 8);
            engine2.MustSubmit(new MoveStepsAction { PlayerIndex = 0, Steps = 3 });
            Assert.AreEqual(4, w.Position, "Pathfinder: 3 paid + 1 free");
            Assert.AreEqual(5, w.Ap);
            engine2.MustSubmit(new MoveStepsAction { PlayerIndex = 0, Steps = 2 });
            Assert.AreEqual(6, w.Position, "Second move gets no bonus");
        }

        [Test]
        public void TimeWarp_GrantsExtraTurn_AndIsExiled()
        {
            var config = TestGames.BareConfig();
            config.Players[0].DeckOverride = new() { ("time_warp", 10) };
            var engine = new GameEngine(config);
            var p0 = engine.State.Players[0];

            engine.PlayFromHand(0, "time_warp");
            Assert.AreEqual(1, p0.PendingExtraTurns);
            Assert.AreEqual(1, p0.Exile.Count, "Time Warp exiles itself");

            engine.EndTurn();
            Assert.AreEqual(0, engine.State.TurnPlayerIndex, "P0 takes an extra turn");
            engine.EndTurn();
            Assert.AreEqual(1, engine.State.TurnPlayerIndex, "Then play continues normally");
        }

        [Test]
        public void Determinism_SameSeedsProduceIdenticalGames()
        {
            ulong ReplayHash(out int events)
            {
                var engine = new GameEngine(TestGames.StandardConfig(players: 4, seed: 42));
                var agents = new ISyncAgent[]
                {
                    new HeuristicBot(1), new HeuristicBot(2), new HeuristicBot(3), new HeuristicBot(4)
                };
                GameDriver.Run(engine, agents, maxRounds: 30);
                events = engine.Log.Count;
                return engine.State.ComputeHash();
            }

            ulong h1 = ReplayHash(out int e1);
            ulong h2 = ReplayHash(out int e2);
            Assert.AreEqual(h1, h2, "Same seeds must produce identical final state");
            Assert.AreEqual(e1, e2, "Same seeds must produce identical event logs");
        }
    }
}
