using NUnit.Framework;
using Pascension.Engine.Actions;
using Pascension.Engine.Cards;
using Pascension.Engine.Core;
using Pascension.Engine.Targeting;

namespace Pascension.Engine.Tests
{
    [TestFixture]
    public class StackTests
    {
        [Test]
        public void Counterspell_CountersSpell_NoEffectHappens()
        {
            var config = TestGames.BareConfig();
            config.Players[0].DeckOverride = new() { ("fireball", 10) };
            config.Players[1].DeckOverride = new() { ("counterspell", 10) };
            var engine = new GameEngine(config);
            var p0 = engine.State.Players[0];
            var p1 = engine.State.Players[1];

            engine.PlayFromHand(0, "fireball");
            // P0 fast-passes (nothing to respond with); P1 holds counterspells → gets priority.
            Assert.AreEqual(1, engine.PendingInput.PlayerIndex);
            Assert.IsFalse(engine.State.Stack.IsEmpty, "Fireball still on the stack");

            engine.PlayFromHand(1, "counterspell");
            engine.Answer(0); // target the only spell — fireball
            engine.PassUntilStackEmpty();

            Assert.AreEqual(0, p0.DamagePool, "Countered fireball gave no damage");
            Assert.AreEqual(1, p0.PlayedThisTurn.Count, "Fireball went to PlayedThisTurn");
            Assert.AreEqual(1, p1.PlayedThisTurn.Count, "Counterspell resolved to PlayedThisTurn");
        }

        [Test]
        public void ProtectiveBarrier_DeniesAKill_BuffAndDamageExpireAtEndOfTurn()
        {
            var config = TestGames.BareConfig();
            config.Players[0].DeckOverride = new() { ("fireball", 10) };
            config.Players[1].DeckOverride = new() { ("protective_barrier", 10) };
            config.BasicPile.Add("goblin", 8);
            var engine = new GameEngine(config);
            var p0 = engine.State.Players[0];
            var p1 = engine.State.Players[1];

            engine.PlayFromHand(0, "fireball");
            engine.PassUntilStackEmpty(); // P1 declines to counter anything (passes via helper)
            Assert.AreEqual(2, p0.DamagePool);

            var goblin = engine.State.Market.SlotCard(CardTier.Basic, 0);
            Assert.AreEqual("goblin", goblin.DefId);

            engine.MustSubmit(new AssignDamageAction
            {
                PlayerIndex = 0,
                Target = TargetRef.Monster((int)CardTier.Basic, 0),
                Amount = 2
            });

            // P1 responds with Protective Barrier on the attacked goblin.
            Assert.AreEqual(1, engine.PendingInput.PlayerIndex);
            engine.PlayFromHand(1, "protective_barrier");
            engine.AnswerWithTarget(TargetRef.Monster((int)CardTier.Basic, 0));
            int p1HandBefore = p1.Hand.Count;
            engine.PassUntilStackEmpty();

            Assert.AreEqual(ZoneType.MarketRow, goblin.Zone, "Goblin survived: 2 damage < 2+3 HP");
            Assert.AreEqual(2, goblin.MarkedDamage);
            Assert.AreEqual(0, p0.Level == 1 ? p0.Xp : -1, "No kill reward");
            Assert.GreaterOrEqual(p1.Hand.Count, p1HandBefore, "Barrier drew a card");

            engine.EndTurn();
            Assert.AreEqual(0, goblin.MarkedDamage, "Marked damage clears at end of turn");
            Assert.AreEqual(0, engine.State.Continuous.Modifiers.Count, "Until-EOT buff expired");
        }

        [Test]
        public void FastPass_SkipsPlayersWithoutResponses_FullControlSurfaces()
        {
            var config = TestGames.BareConfig();
            config.Players[0].DeckOverride = new() { ("fireball", 10) };
            // P1 has only Action cards → can never respond.
            var engine = new GameEngine(config);

            engine.PlayFromHand(0, "fireball");
            Assert.IsTrue(engine.State.Stack.IsEmpty, "Resolved without surfacing P1");
            Assert.AreEqual(2, engine.State.Players[0].DamagePool);

            // Same setup with full control: P1 is surfaced even with no playable responses.
            var config2 = TestGames.BareConfig();
            config2.Players[0].DeckOverride = new() { ("fireball", 10) };
            config2.Players[1].FullControl = true;
            var engine2 = new GameEngine(config2);

            engine2.PlayFromHand(0, "fireball");
            Assert.IsFalse(engine2.State.Stack.IsEmpty);
            Assert.AreEqual(1, engine2.PendingInput.PlayerIndex, "Full-control player gets the window");
        }

        [Test]
        public void RandomBullshitGo_CastsTwoFreeInstants_KeepsThem()
        {
            var config = TestGames.BareConfig();
            config.Players[0].DeckOverride = new() { ("random_bullshit_go", 10) };
            config.AdvancedPile.Add("reflexes", 12);
            var engine = new GameEngine(config);
            var p0 = engine.State.Players[0];
            int pileBefore = engine.State.Market.PileFor(CardTier.Advanced).Count; // 7 after row fill

            engine.PlayFromHand(0, "random_bullshit_go");
            engine.Pass(0); // caster holds more instants, so priority surfaces before resolving
            // RBG resolves: exiles reflexes until 2 found; each offers a free cast (YesNo).
            engine.Answer(0); // yes to first
            engine.Answer(0); // yes to second
            engine.PassUntilStackEmpty();

            Assert.AreEqual(pileBefore - 2, engine.State.Market.PileFor(CardTier.Advanced).Count);
            // Hand: 5 - RBG + 2 draws + 2 draws = 8.
            Assert.AreEqual(8, p0.Hand.Count, "Both free Reflexes drew 2 cards each");
            int playedReflexes = p0.PlayedThisTurn.FindAll(c => c.DefId == "reflexes").Count;
            Assert.AreEqual(2, playedReflexes, "Cast pile cards are kept by the caster");
        }

        [Test]
        public void Sabotage_ForcesRandomDiscard()
        {
            var config = TestGames.BareConfig();
            config.Players[0].DeckOverride = new() { ("sabotage", 10) };
            var engine = new GameEngine(config);
            var p1 = engine.State.Players[1];

            engine.PlayFromHand(0, "sabotage");
            engine.AnswerWithTarget(TargetRef.PlayerAt(1));
            engine.PassUntilStackEmpty();

            Assert.AreEqual(4, p1.Hand.Count);
            Assert.AreEqual(1, p1.Discard.Count);
        }
    }
}
