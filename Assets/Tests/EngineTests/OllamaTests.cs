using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Bots.Ollama;
using Pascension.Engine.Actions;
using Pascension.Engine.Cards;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Pascension.Engine.Serialization;
using Pascension.Engine.Targeting;

namespace Pascension.Engine.Tests
{
    /// <summary>Ollama bot plumbing that must work offline: prompt construction and the
    /// snapshot-only fallback policy. No network calls here — ever.</summary>
    [TestFixture]
    public class OllamaTests
    {
        // ---------- PromptBuilder ----------

        [Test]
        public void PromptBuilder_NumbersEveryLegalAction()
        {
            var engine = new GameEngine(TestGames.StandardConfig(players: 2, seed: 21));
            var pending = engine.PendingInput;
            Assert.IsNotNull(pending);
            Assert.AreEqual(PendingInputKind.Priority, pending.Kind);

            var snap = SnapshotBuilder.Build(engine, pending.PlayerIndex);
            Assert.IsNotNull(snap.Pending.LegalActions, "Viewer is the pending player");
            string prompt = PromptBuilder.BuildUserPrompt(snap, snap.Pending);

            var legal = snap.Pending.LegalActions;
            Assert.Greater(legal.Count, 1, "Turn player should have more than just Pass");
            for (int i = 0; i < legal.Count; i++)
            {
                string entry = $"[{i}] {PromptBuilder.DescribeAction(legal[i], snap)}";
                StringAssert.Contains(entry, prompt, $"Action {i} missing from the menu");
            }
            StringAssert.Contains($"between 0 and {legal.Count - 1}", prompt);
        }

        [Test]
        public void PromptBuilder_ResolvesCardNamesAndStateSections()
        {
            var engine = new GameEngine(TestGames.StandardConfig(players: 2, seed: 21));
            var pending = engine.PendingInput;
            var snap = SnapshotBuilder.Build(engine, pending.PlayerIndex);
            string prompt = PromptBuilder.BuildUserPrompt(snap, snap.Pending);

            // Every card in the own hand appears by display name, not just instance id.
            foreach (var card in snap.Players[snap.ViewerIndex].Hand)
                StringAssert.Contains(CardDatabase.Get(card.DefId).Name, prompt);

            // Market cards appear with their names.
            foreach (var row in snap.MarketRows)
                foreach (var slot in row)
                    if (slot != null && slot.DefId != null)
                        StringAssert.Contains(CardDatabase.Get(slot.DefId).Name, prompt);

            StringAssert.Contains("== GAME ==", prompt);
            StringAssert.Contains("== PLAYERS ==", prompt);
            StringAssert.Contains("== MARKET ==", prompt);
            StringAssert.Contains("== YOUR HAND ==", prompt);
            StringAssert.Contains("== YOUR LEGAL ACTIONS ==", prompt);
        }

        [Test]
        public void PromptBuilder_ListsDecisionOptionsByIdWithBounds()
        {
            var engine = new GameEngine(TestGames.StandardConfig(players: 2, seed: 21));
            var snap = SnapshotBuilder.Build(engine, 0);

            var request = new DecisionRequest
            {
                Id = 9,
                PlayerIndex = 0,
                Kind = DecisionKind.InnChoice,
                Title = "Inn: choose a reward",
                Min = 1,
                Max = 2
            };
            request.Options.Add(new DecisionOption(0, "+2 XP"));
            request.Options.Add(new DecisionOption(1, "Draw 2 cards"));
            request.Options.Add(new DecisionOption(2, "Exile up to 2 cards from your discard"));
            var pendingSnap = new PendingSnap
            {
                Kind = PendingInputKind.Decision,
                PlayerIndex = 0,
                Decision = request
            };

            string prompt = PromptBuilder.BuildUserPrompt(snap, pendingSnap);
            StringAssert.Contains("== DECISION ==", prompt);
            StringAssert.Contains("Inn: choose a reward", prompt);
            StringAssert.Contains("[0] +2 XP", prompt);
            StringAssert.Contains("[1] Draw 2 cards", prompt);
            StringAssert.Contains("[2] Exile up to 2 cards from your discard", prompt);
            StringAssert.Contains("between 1 and 2", prompt);
        }

        // ---------- SnapshotFallbackPolicy: priority ----------

        [Test]
        public void Fallback_PicksFirstProactiveAction()
        {
            var pending = new PendingSnap
            {
                Kind = PendingInputKind.Priority,
                PlayerIndex = 1,
                LegalActions = new List<PlayerAction>
                {
                    new MoveStepsAction { Steps = 2 },
                    new UseHeroAbilityAction(),
                    new PlayCardAction { CardInstanceId = 42 },
                    new BuyCardAction { TierIndex = 0, SlotIndex = 1 },
                    new PassPriorityAction()
                }
            };

            var chosen = SnapshotFallbackPolicy.Choose(pending);
            Assert.IsInstanceOf<PlayCardAction>(chosen, "Play/Buy/AssignDamage beat Move/Hero in the scan");
            Assert.AreEqual(1, chosen.PlayerIndex, "PlayerIndex is stamped from the pending input");
        }

        [Test]
        public void Fallback_PassesWhenNoProactiveActionExists()
        {
            var pending = new PendingSnap
            {
                Kind = PendingInputKind.Priority,
                PlayerIndex = 2,
                LegalActions = new List<PlayerAction>
                {
                    new MoveStepsAction { Steps = 1 },
                    new UseHeroAbilityAction { Ultimate = true },
                    new PassPriorityAction()
                }
            };

            var chosen = SnapshotFallbackPolicy.Choose(pending);
            Assert.IsInstanceOf<PassPriorityAction>(chosen);
            Assert.AreEqual(2, chosen.PlayerIndex);

            // Null / empty legal lists degrade to Pass too (defensive).
            var bare = new PendingSnap { Kind = PendingInputKind.Priority, PlayerIndex = 0 };
            Assert.IsInstanceOf<PassPriorityAction>(SnapshotFallbackPolicy.Choose(bare));
        }

        [Test]
        public void Fallback_PriorityChoice_IsAcceptedByTheEngine()
        {
            var engine = new GameEngine(TestGames.StandardConfig(players: 2, seed: 33));
            var pending = engine.PendingInput;
            var snap = SnapshotBuilder.Build(engine, pending.PlayerIndex);

            var action = SnapshotFallbackPolicy.Choose(snap.Pending);
            var result = engine.Submit(action);
            Assert.IsTrue(result.Accepted, $"Fallback action '{action.Describe()}' rejected: {result.Error}");
        }

        // ---------- SnapshotFallbackPolicy: decisions ----------

        [Test]
        public void Fallback_Decision_UsesDefaultsAndPadsToMin()
        {
            var request = new DecisionRequest { Id = 5, PlayerIndex = 1, Min = 2, Max = 2 };
            request.Options.Add(new DecisionOption(5, "A"));
            request.Options.Add(new DecisionOption(6, "B"));
            request.Options.Add(new DecisionOption(7, "C"));
            request.DefaultOptionIds.Add(6);

            var action = SnapshotFallbackPolicy.ChooseDecision(1, request);
            Assert.AreEqual(5, action.Answer.DecisionId);
            CollectionAssert.AreEqual(new[] { 6, 5 }, action.Answer.ChosenOptionIds,
                "Default first, then the first unchosen option to reach Min");
        }

        [Test]
        public void Fallback_Decision_TrimsDefaultsAboveMax()
        {
            var request = new DecisionRequest { Id = 8, PlayerIndex = 0, Min = 0, Max = 1 };
            request.Options.Add(new DecisionOption(0, "A"));
            request.Options.Add(new DecisionOption(1, "B"));
            request.DefaultOptionIds.Add(0);
            request.DefaultOptionIds.Add(1);

            var action = SnapshotFallbackPolicy.ChooseDecision(0, request);
            CollectionAssert.AreEqual(new[] { 0 }, action.Answer.ChosenOptionIds);
        }

        [Test]
        public void Fallback_Decision_YesNoDefaultsToNo()
        {
            var request = DecisionRequest.YesNo(0, "Sacrifice a card?");
            var pending = new PendingSnap
            {
                Kind = PendingInputKind.Decision,
                PlayerIndex = 0,
                Decision = request
            };

            var action = (SubmitDecisionAction)SnapshotFallbackPolicy.Choose(pending);
            CollectionAssert.AreEqual(new[] { 1 }, action.Answer.ChosenOptionIds, "Option 1 = No");
            Assert.IsFalse(action.Answer.IsYes);
        }

        [Test]
        public void Fallback_AssignDamage_BeatsPass()
        {
            var pending = new PendingSnap
            {
                Kind = PendingInputKind.Priority,
                PlayerIndex = 0,
                LegalActions = new List<PlayerAction>
                {
                    new PassPriorityAction(),
                    new AssignDamageAction { Target = TargetRef.Monster(1, 0), Amount = 3 }
                }
            };

            var chosen = SnapshotFallbackPolicy.Choose(pending);
            Assert.IsInstanceOf<AssignDamageAction>(chosen);
        }
    }
}
