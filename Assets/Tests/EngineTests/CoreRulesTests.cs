using NUnit.Framework;
using Pascension.Engine.Actions;
using Pascension.Engine.Cards;
using Pascension.Engine.Core;

namespace Pascension.Engine.Tests
{
    [TestFixture]
    public class CoreRulesTests
    {
        [Test]
        public void GameStart_FirstPlayerHoldsPriorityInMain()
        {
            var engine = new GameEngine(TestGames.BareConfig());
            Assert.AreEqual(Phase.Main, engine.State.Phase);
            Assert.AreEqual(0, engine.State.TurnPlayerIndex);
            Assert.AreEqual(PendingInputKind.Priority, engine.PendingInput.Kind);
            Assert.AreEqual(0, engine.PendingInput.PlayerIndex);
            Assert.AreEqual(5, engine.State.Players[0].Hand.Count);
        }

        [Test]
        public void StaggeredStart_CompensatesLaterPlayers()
        {
            var engine = new GameEngine(TestGames.BareConfig(players: 4));
            Assert.AreEqual(0, engine.State.Players[0].Ap);
            Assert.AreEqual(5, engine.State.Players[0].Hand.Count);
            Assert.AreEqual(1, engine.State.Players[1].Ap);
            Assert.AreEqual(5, engine.State.Players[1].Hand.Count);
            Assert.AreEqual(1, engine.State.Players[2].Ap);
            Assert.AreEqual(6, engine.State.Players[2].Hand.Count);
            Assert.AreEqual(1, engine.State.Players[3].Ap);
            Assert.AreEqual(6, engine.State.Players[3].Hand.Count);
        }

        [Test]
        public void PlayingMove_GainsApAndAutoResolves()
        {
            var engine = new GameEngine(TestGames.BareConfig());
            engine.PlayFromHand(0, "move");
            // Nobody can respond → the spell should have fully resolved in one submit.
            Assert.AreEqual(1, engine.State.Players[0].Ap);
            Assert.IsTrue(engine.State.Stack.IsEmpty);
            Assert.AreEqual(1, engine.State.Players[0].PlayedThisTurn.Count);
            Assert.AreEqual(4, engine.State.Players[0].Hand.Count);
        }

        [Test]
        public void Cleanup_DiscardsHandAndPlayedCards_DrawsFive_TurnPasses()
        {
            var engine = new GameEngine(TestGames.BareConfig());
            var p0 = engine.State.Players[0];
            engine.PlayFromHand(0, "move");
            engine.PlayFromHand(0, "move");
            engine.EndTurn();

            Assert.AreEqual(1, engine.State.TurnPlayerIndex, "Turn should have passed to P1");
            Assert.AreEqual(5, p0.Hand.Count, "P0 drew a fresh hand of 5");
            Assert.AreEqual(5, p0.Discard.Count, "3 unplayed + 2 played cards in discard");
            Assert.AreEqual(0, p0.PlayedThisTurn.Count);
            Assert.AreEqual(0, p0.Ap, "Unused AP lost at end of turn");
        }

        [Test]
        public void Draw_ReshufflesDiscardWhenDeckRunsOut()
        {
            var engine = new GameEngine(TestGames.BareConfig());
            var p0 = engine.State.Players[0];
            engine.EndTurn(); // P0 discards 5, draws the remaining 5 (deck empty now)
            Assert.AreEqual(0, p0.Deck.Count);
            engine.EndTurn(); // P1's turn ends
            Assert.AreEqual(0, engine.State.TurnPlayerIndex);
            engine.EndTurn(); // P0's turn 2 ends: draw must reshuffle the 10-card discard
            Assert.AreEqual(5, p0.Hand.Count);
            Assert.AreEqual(5, p0.Deck.Count);
            Assert.AreEqual(0, p0.Discard.Count);
        }

        [Test]
        public void Redbull_ScalesWithLevelBrackets()
        {
            var config = TestGames.BareConfig();
            config.Players[0].DeckOverride = new() { ("redbull", 10) };
            var engine = new GameEngine(config);
            var p0 = engine.State.Players[0];

            engine.PlayFromHand(0, "redbull");
            Assert.AreEqual(2, p0.Ap, "Level 1-4: +2");

            p0.Level = 5;
            engine.PlayFromHand(0, "redbull");
            Assert.AreEqual(5, p0.Ap, "Level 5-9: +3");

            p0.Level = 10;
            engine.PlayFromHand(0, "redbull");
            Assert.AreEqual(10, p0.Ap, "Level 10: +5");
        }

        [Test]
        public void Ethereal_UnplayedBanIsExiledAtCleanup()
        {
            var config = TestGames.BareConfig();
            config.Players[0].DeckOverride = new() { ("ban", 10) };
            var engine = new GameEngine(config);
            var p0 = engine.State.Players[0];

            engine.EndTurn();
            Assert.AreEqual(5, p0.Exile.Count, "All 5 unplayed Bans exiled (ethereal)");
            Assert.AreEqual(0, p0.Discard.Count);
        }

        [Test]
        public void Ban_WhenPlayed_ExilesChosenCardAndBanReachesDiscard()
        {
            var config = TestGames.BareConfig();
            config.Players[0].DeckOverride = new() { ("ban", 5), ("move", 5) };
            var engine = new GameEngine(config);
            var p0 = engine.State.Players[0];
            var ban = p0.Hand.Find(c => c.DefId == "ban");
            if (ban == null) Assert.Inconclusive("Seeded hand has no ban");

            engine.MustSubmit(new PlayCardAction { PlayerIndex = 0, CardInstanceId = ban.InstanceId });
            // Ban resolves → decision: exile a card from hand.
            var victim = p0.Hand[0];
            engine.AnswerWithCard(victim.InstanceId);
            Assert.AreEqual(ZoneType.Exile, victim.Zone);
            Assert.AreEqual(ZoneType.PlayedThisTurn, ban.Zone, "Played Ban is not exiled by ethereal");

            engine.EndTurn();
            Assert.IsTrue(p0.Discard.Contains(ban), "Played Ban ends in discard, not exile");
        }

        [Test]
        public void Buy_PaysCost_GoesToDiscard_RefillsSlot_EnforcesLevelGates()
        {
            var config = TestGames.BareConfig();
            config.BasicPile.Add("run", 8);
            config.AdvancedPile.Add("sprint", 8);
            var engine = new GameEngine(config);
            var p0 = engine.State.Players[0];
            engine.Api.GainAp(p0, 10);

            int pileBefore = engine.State.Market.PileFor(CardTier.Basic).Count;
            engine.MustSubmit(new BuyCardAction { PlayerIndex = 0, TierIndex = 0, SlotIndex = 0 });
            Assert.AreEqual(8, p0.Ap, "Run costs 2");
            Assert.AreEqual(1, p0.Discard.Count);
            Assert.AreEqual("run", p0.Discard[0].DefId);
            Assert.IsNotNull(engine.State.Market.Rows[0][0], "Slot refilled");
            Assert.AreEqual(pileBefore - 1, engine.State.Market.PileFor(CardTier.Basic).Count);

            // Advanced gated behind level 4.
            engine.MustReject(new BuyCardAction { PlayerIndex = 0, TierIndex = 1, SlotIndex = 0 });
            p0.Level = 4;
            engine.MustSubmit(new BuyCardAction { PlayerIndex = 0, TierIndex = 1, SlotIndex = 0 });
            Assert.AreEqual(3, p0.Ap, "Sprint costs 5");
        }

        [Test]
        public void Equipment_ReplacementIsExiled_TapUntapsNextTurn()
        {
            var config = TestGames.BareConfig();
            config.Players[0].DeckOverride = new() { ("short_sword", 10) };
            var engine = new GameEngine(config);
            var p0 = engine.State.Players[0];

            engine.PlayFromHand(0, "short_sword");
            var first = p0.EquipmentIn(EquipSlot.Weapon);
            Assert.IsNotNull(first);

            engine.PlayFromHand(0, "short_sword");
            var second = p0.EquipmentIn(EquipSlot.Weapon);
            Assert.AreNotEqual(first.InstanceId, second.InstanceId);
            Assert.AreEqual(ZoneType.Exile, first.Zone, "Replaced equipment is exiled");

            // Tap for +1 damage.
            engine.MustSubmit(new ActivateAbilityAction { PlayerIndex = 0, SourceInstanceId = second.InstanceId, AbilityIndex = 0 });
            Assert.AreEqual(1, p0.DamagePool);
            Assert.IsTrue(second.Tapped);
            engine.MustReject(new ActivateAbilityAction { PlayerIndex = 0, SourceInstanceId = second.InstanceId, AbilityIndex = 0 });

            engine.EndTurn(); // P1's turn
            engine.EndTurn(); // back to P0: untap
            Assert.IsFalse(second.Tapped, "Equipment untaps at the start of its controller's turn");
        }
    }
}
