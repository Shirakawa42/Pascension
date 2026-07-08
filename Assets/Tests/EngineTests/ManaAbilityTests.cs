using NUnit.Framework;
using Pascension.Content;
using Pascension.Engine.Cards;
using Pascension.Engine.Core;
using Pascension.Engine.Events;

namespace Pascension.Engine.Tests
{
    /// <summary>
    /// Respondability rule (2026-07-08, reverses the earlier mana-ability rule):
    /// EVERY play and tap uses the stack and can be responded to. The IsManaAbility /
    /// ManaAbility opt-out flags stay in the engine for future cards, but such a card
    /// MUST say so in its rules text — pinned by the registry invariant below.
    /// </summary>
    [TestFixture]
    public class ManaAbilityTests
    {
        [Test]
        public void Run_GoesOnStack_AndIsCounterable()
        {
            var config = TestGames.BareConfig();
            config.Players[0].DeckOverride = new() { ("run", 10) };
            config.Players[1].DeckOverride = new() { ("counterspell", 10) };
            config.Players[1].FullControl = true;
            var engine = new GameEngine(config);
            var p0 = engine.State.Players[0];

            int logStart = engine.Log.Count;
            engine.PlayFromHand(0, "run");

            Assert.IsFalse(engine.State.Stack.IsEmpty, "Run uses the stack like any card");
            Assert.AreEqual(1, engine.PendingInput.PlayerIndex, "P1 gets a response window");
            bool sawStackPushed = false;
            for (int i = logStart; i < engine.Log.Count; i++)
                if (engine.Log[i] is StackPushedEvent) sawStackPushed = true;
            Assert.IsTrue(sawStackPushed, "AP cards announce via StackPushed like everything else");

            engine.PlayFromHand(1, "counterspell");
            engine.Answer(0);
            engine.PassUntilStackEmpty();

            Assert.AreEqual(0, p0.Ap, "Countered: no AP");
        }

        [Test]
        public void Run_ResolvesViaStack_WhenNobodyResponds()
        {
            var config = TestGames.BareConfig();
            config.Players[0].DeckOverride = new() { ("run", 10) };
            var engine = new GameEngine(config);
            var p0 = engine.State.Players[0];

            engine.PlayFromHand(0, "run");
            engine.PassUntilStackEmpty();

            Assert.AreEqual(2, p0.Ap);
            Assert.AreEqual(1, p0.PlayedThisTurn.Count);
            Assert.IsTrue(engine.State.Stack.IsEmpty);
        }

        [Test]
        public void Redbull_LevelScales_ViaStack()
        {
            var config = TestGames.BareConfig();
            config.Players[0].DeckOverride = new() { ("redbull", 10) };
            var engine = new GameEngine(config);
            var p0 = engine.State.Players[0];

            p0.Level = 5;
            engine.PlayFromHand(0, "redbull");
            engine.PassUntilStackEmpty();
            Assert.AreEqual(3, p0.Ap, "Level 5-9 bracket applies on stack resolution");
        }

        [Test]
        public void EquipTap_ClothArmor_GoesOnStack_AndIsRespondable()
        {
            var config = TestGames.BareConfig();
            config.Players[1].DeckOverride = new() { ("counterspell", 10) };
            config.Players[1].FullControl = true;
            var engine = new GameEngine(config);
            var p0 = engine.State.Players[0];

            var armor = new CardInstance { InstanceId = 95001, DefId = "cloth_armor", Owner = 0, Zone = ZoneType.Exile };
            engine.Api.Equip(p0, armor);

            int logStart = engine.Log.Count;
            engine.MustSubmit(new Engine.Actions.ActivateAbilityAction
            {
                PlayerIndex = 0,
                SourceInstanceId = armor.InstanceId,
                AbilityIndex = 0
            });

            Assert.IsFalse(engine.State.Stack.IsEmpty, "Tap ability uses the stack");
            Assert.AreEqual(1, engine.PendingInput.PlayerIndex, "P1 gets a response window");
            bool sawStackPushed = false;
            for (int i = logStart; i < engine.Log.Count; i++)
                if (engine.Log[i] is StackPushedEvent) sawStackPushed = true;
            Assert.IsTrue(sawStackPushed, "Taps announce via StackPushed");

            engine.PassUntilStackEmpty();
            Assert.AreEqual(1, p0.Ap, "Resolved once P1 declined to respond");
            Assert.IsTrue(armor.Tapped);
        }

        [Test]
        public void AdrenalineShot_StacksAndIsCounterable()
        {
            var config = TestGames.BareConfig();
            config.Players[0].DeckOverride = new() { ("adrenaline_shot", 10) };
            config.Players[1].DeckOverride = new() { ("counterspell", 10) };
            var engine = new GameEngine(config);
            var p0 = engine.State.Players[0];

            engine.PlayFromHand(0, "adrenaline_shot");
            Assert.IsFalse(engine.State.Stack.IsEmpty, "AP + draw card uses the stack");
            Assert.AreEqual(1, engine.PendingInput.PlayerIndex, "P1 gets a response window");

            int handBefore = p0.Hand.Count;
            engine.PlayFromHand(1, "counterspell");
            engine.Answer(0);
            engine.PassUntilStackEmpty();

            Assert.AreEqual(0, p0.Ap, "Countered: no AP");
            Assert.AreEqual(handBefore, p0.Hand.Count, "Countered: no draw");
        }

        [Test]
        public void RespondableInvariant_NoCardOptsOut_WithoutSayingSoInItsText()
        {
            ContentRegistry.RegisterAll();

            foreach (var def in CardDatabase.All)
            {
                if (def.IsManaAbility)
                    StringAssert.Contains("can't be responded to", def.RulesText.ToLowerInvariant(),
                        $"{def.Id}: a card that bypasses the stack MUST say so in its rules text");
                foreach (var ability in def.ActivatedAbilities)
                    if (ability.ManaAbility)
                        StringAssert.Contains("can't be responded to", ability.Description.ToLowerInvariant(),
                            $"{def.Id}: a tap that bypasses the stack MUST say so in its description");
            }
        }
    }
}
