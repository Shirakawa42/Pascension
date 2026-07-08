using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Content;
using Pascension.Engine.Cards;
using Pascension.Engine.Core;
using Pascension.Engine.Effects.Common;
using Pascension.Engine.Events;

namespace Pascension.Engine.Tests
{
    /// <summary>
    /// Mana-ability rule: cards/taps whose only effect is gaining AP bypass the stack
    /// entirely and cannot be responded to (like MTG mana abilities).
    /// </summary>
    [TestFixture]
    public class ManaAbilityTests
    {
        [Test]
        public void ManaPlay_Run_ResolvesInstantly_NoStack_NoResponseWindow()
        {
            var config = TestGames.BareConfig();
            config.Players[0].DeckOverride = new() { ("run", 10) };
            // P1 holds counterspells AND full control — if Run ever hit the stack, P1 would get a window.
            config.Players[1].DeckOverride = new() { ("counterspell", 10) };
            config.Players[1].FullControl = true;
            var engine = new GameEngine(config);
            var p0 = engine.State.Players[0];

            int logStart = engine.Log.Count;
            engine.PlayFromHand(0, "run");

            Assert.AreEqual(2, p0.Ap, "Run resolved immediately");
            Assert.IsTrue(engine.State.Stack.IsEmpty);
            Assert.AreEqual(0, engine.PendingInput.PlayerIndex, "Priority stayed with P0 — no response window");
            Assert.AreEqual(1, p0.PlayedThisTurn.Count);

            bool sawCardPlayed = false, sawStackPushed = false;
            for (int i = logStart; i < engine.Log.Count; i++)
            {
                if (engine.Log[i] is CardPlayedEvent) sawCardPlayed = true;
                if (engine.Log[i] is StackPushedEvent) sawStackPushed = true;
            }
            Assert.IsTrue(sawCardPlayed, "CardPlayedEvent emitted for the off-stack play");
            Assert.IsFalse(sawStackPushed, "Mana plays never push onto the stack");
        }

        [Test]
        public void ManaPlay_Redbull_StillLevelScales()
        {
            var config = TestGames.BareConfig();
            config.Players[0].DeckOverride = new() { ("redbull", 10) };
            var engine = new GameEngine(config);
            var p0 = engine.State.Players[0];

            p0.Level = 5;
            engine.PlayFromHand(0, "redbull");
            Assert.AreEqual(3, p0.Ap, "Level 5-9 bracket applies through the bypass path");
        }

        [Test]
        public void EquipManaTap_ClothArmor_ResolvesInstantly()
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

            Assert.AreEqual(1, p0.Ap, "Tap resolved immediately");
            Assert.IsTrue(armor.Tapped);
            Assert.IsTrue(engine.State.Stack.IsEmpty);
            Assert.AreEqual(0, engine.PendingInput.PlayerIndex, "No response window for P1");
            for (int i = logStart; i < engine.Log.Count; i++)
                Assert.IsFalse(engine.Log[i] is StackPushedEvent, "Mana taps never stack");
        }

        [Test]
        public void AdrenalineShot_StillStacksAndIsCounterable()
        {
            var config = TestGames.BareConfig();
            config.Players[0].DeckOverride = new() { ("adrenaline_shot", 10) };
            config.Players[1].DeckOverride = new() { ("counterspell", 10) };
            var engine = new GameEngine(config);
            var p0 = engine.State.Players[0];
            var p1 = engine.State.Players[1];

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
        public void ManaFlag_RegistryInvariant_ExactlyTheKnownSetAndPureApOnly()
        {
            ContentRegistry.RegisterAll();

            var expectedCards = new HashSet<string> { "move", "redbull", "run", "sprint" };
            var expectedTaps = new HashSet<string> { "cloth_armor", "tower_shield", "dragonscale_armor" };

            var flaggedCards = new HashSet<string>();
            var flaggedTaps = new HashSet<string>();
            foreach (var def in CardDatabase.All)
            {
                if (def.IsManaAbility)
                {
                    flaggedCards.Add(def.Id);
                    Assert.IsInstanceOf<GainApEffect>(def.SpellEffect, $"{def.Id}: mana card must be a bare GainApEffect");
                    Assert.IsNull(def.SpellTarget, $"{def.Id}: mana card cannot have targets");
                    Assert.AreEqual(CardType.Action, def.Type, $"{def.Id}: mana cards are Actions");
                }
                foreach (var ability in def.ActivatedAbilities)
                {
                    if (!ability.ManaAbility) continue;
                    flaggedTaps.Add(def.Id);
                    Assert.IsInstanceOf<GainApEffect>(ability.Effect, $"{def.Id}: mana tap must be a bare GainApEffect");
                    Assert.IsNull(ability.Target, $"{def.Id}: mana tap cannot have targets");
                }
            }

            CollectionAssert.AreEquivalent(expectedCards, flaggedCards, "Flagged card set drifted — update the cards skill ruling");
            CollectionAssert.AreEquivalent(expectedTaps, flaggedTaps, "Flagged tap set drifted — update the cards skill ruling");
        }
    }
}
