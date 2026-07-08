using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Content;
using Pascension.Engine.Actions;
using Pascension.Engine.Cards;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;

namespace Pascension.Engine.Tests
{
    /// <summary>Builders and helpers for scripted, seeded test games.</summary>
    public static class TestGames
    {
        /// <summary>A minimal 2-player config: passive-less hero, all-"move" decks, empty piles.</summary>
        public static GameConfig BareConfig(int players = 2, ulong seed = 1)
        {
            ContentRegistry.RegisterAll();
            RegisterDummyHero();
            var config = new GameConfig { Seed = seed, BossDefId = "the_gatekeeper" };
            for (int i = 0; i < players; i++)
                config.Players.Add(new PlayerConfig { Name = $"P{i}", HeroId = "dummy" });
            config.DefaultDeck.Add(("move", 10));
            return config;
        }

        /// <summary>A hero with no passives/actives so tests observe raw rules.</summary>
        private static void RegisterDummyHero()
        {
            Engine.Heroes.HeroDatabase.Register(new Engine.Heroes.HeroDefinition
            {
                Id = "dummy",
                Name = "Test Dummy",
                Archetype = "None"
            });
        }

        public static GameConfig StandardConfig(int players = 2, ulong seed = 1)
        {
            var list = new List<PlayerConfig>();
            string[] heroes = { "ignis", "wren", "cornelius", "nyx" };
            for (int i = 0; i < players; i++)
                list.Add(new PlayerConfig { Name = $"P{i}", HeroId = heroes[i % heroes.Length] });
            return ContentRegistry.StandardConfig(seed, list);
        }

        // ---------- driving helpers ----------

        public static void MustSubmit(this GameEngine engine, PlayerAction action)
        {
            var result = engine.Submit(action);
            Assert.IsTrue(result.Accepted, $"Action '{action.Describe()}' rejected: {result.Error}");
        }

        public static void MustReject(this GameEngine engine, PlayerAction action)
        {
            var result = engine.Submit(action);
            Assert.IsFalse(result.Accepted, $"Action '{action.Describe()}' should have been rejected");
        }

        /// <summary>Play a card from the player's hand by definition id.</summary>
        public static void PlayFromHand(this GameEngine engine, int player, string defId)
        {
            var card = engine.State.Players[player].Hand.Find(c => c.DefId == defId);
            Assert.IsNotNull(card, $"{defId} not in P{player}'s hand");
            engine.MustSubmit(new PlayCardAction { PlayerIndex = player, CardInstanceId = card.InstanceId });
        }

        public static void Pass(this GameEngine engine, int player) =>
            engine.MustSubmit(new PassPriorityAction { PlayerIndex = player });

        /// <summary>Answer the pending decision by picking the first N valid option ids (or specific ids).</summary>
        public static void Answer(this GameEngine engine, params int[] optionIds)
        {
            var pending = engine.PendingInput;
            Assert.IsNotNull(pending, "No pending input");
            Assert.AreEqual(PendingInputKind.Decision, pending.Kind, "Expected a decision");
            var answer = new DecisionAnswer { DecisionId = pending.Decision.Id };
            answer.ChosenOptionIds.AddRange(optionIds);
            engine.MustSubmit(new SubmitDecisionAction { PlayerIndex = pending.PlayerIndex, Answer = answer });
        }

        /// <summary>Answer a pending ChooseTargets/ChooseCards decision by matching option labels or card ids.</summary>
        public static void AnswerWithCard(this GameEngine engine, int cardInstanceId)
        {
            var pending = engine.PendingInput;
            Assert.AreEqual(PendingInputKind.Decision, pending.Kind);
            var option = pending.Decision.Options.Find(o => o.CardInstanceId == cardInstanceId);
            Assert.IsNotNull(option, "No option for that card");
            engine.Answer(option.Id);
        }

        /// <summary>Keep passing whoever holds priority until the given player holds priority on an empty stack (or a cap).</summary>
        public static void PassUntilMainOf(this GameEngine engine, int player)
        {
            for (int i = 0; i < 200; i++)
            {
                var pending = engine.PendingInput;
                if (pending == null) return;
                if (pending.Kind == PendingInputKind.Priority &&
                    pending.PlayerIndex == player &&
                    engine.State.Stack.IsEmpty &&
                    engine.State.Phase == Phase.Main &&
                    engine.State.TurnPlayerIndex == player)
                    return;
                if (pending.Kind == PendingInputKind.Priority)
                    engine.Pass(pending.PlayerIndex);
                else
                    engine.AnswerDefault();
            }
            Assert.Fail($"Never reached P{player}'s main phase");
        }

        /// <summary>Answer a pending ChooseTargets decision by picking the option with the given target.</summary>
        public static void AnswerWithTarget(this GameEngine engine, Targeting.TargetRef target)
        {
            var pending = engine.PendingInput;
            Assert.AreEqual(PendingInputKind.Decision, pending.Kind);
            var option = pending.Decision.Options.Find(o => o.Target.HasValue && o.Target.Value.Equals(target));
            Assert.IsNotNull(option, $"No option targeting {target}");
            engine.Answer(option.Id);
        }

        /// <summary>Pass every priority window (answering decisions with defaults) until the stack is empty.</summary>
        public static void PassUntilStackEmpty(this GameEngine engine)
        {
            for (int i = 0; i < 200; i++)
            {
                if (engine.State.Stack.IsEmpty && engine.PendingInput?.Kind != PendingInputKind.Decision)
                    return;
                var pending = engine.PendingInput;
                if (pending == null) return;
                if (pending.Kind == PendingInputKind.Priority)
                    engine.Pass(pending.PlayerIndex);
                else
                    engine.AnswerDefault();
            }
            Assert.Fail("Stack never emptied");
        }

        /// <summary>Answer the pending decision with its defaults (respecting Min).</summary>
        public static void AnswerDefault(this GameEngine engine)
        {
            var req = engine.PendingInput.Decision;
            var answer = new DecisionAnswer { DecisionId = req.Id };
            answer.ChosenOptionIds.AddRange(req.DefaultOptionIds);
            for (int i = 0; answer.ChosenOptionIds.Count < req.Min && i < req.Options.Count; i++)
                if (!answer.ChosenOptionIds.Contains(req.Options[i].Id))
                    answer.ChosenOptionIds.Add(req.Options[i].Id);
            engine.MustSubmit(new SubmitDecisionAction { PlayerIndex = req.PlayerIndex, Answer = answer });
        }

        /// <summary>End the current player's turn: pass main, then pass all end-phase windows/decisions.
        /// Returns as soon as a NEW turn starts (handles extra turns for the same player).</summary>
        public static void EndTurn(this GameEngine engine)
        {
            int logStart = engine.Log.Count;
            for (int i = 0; i < 200; i++)
            {
                for (int s = logStart; s < engine.Log.Count; s++)
                    if (engine.Log[s] is Events.TurnStartedEvent)
                        return;
                var pending = engine.PendingInput;
                if (pending == null) return;
                if (pending.Kind == PendingInputKind.Priority)
                    engine.Pass(pending.PlayerIndex);
                else
                    engine.AnswerDefault();
            }
            Assert.Fail("Turn never ended");
        }

        /// <summary>Count every card instance in the game (conservation checks).</summary>
        public static int TotalCards(this GameState state)
        {
            int n = 0;
            foreach (var p in state.Players)
            {
                n += p.Deck.Count + p.Hand.Count + p.Discard.Count + p.Exile.Count +
                     p.PlayedThisTurn.Count + p.Relics.Count;
                foreach (var e in p.Equipment)
                    if (e != null)
                        n++;
            }
            for (int t = 0; t < Market.Tiers; t++)
            {
                n += state.Market.Piles[t].Count;
                foreach (var c in state.Market.Rows[t])
                    if (c != null)
                        n++;
            }
            n += state.MarketExile.Count;
            foreach (var item in state.Stack.Items)
                if (item.SpellCard != null)
                    n++;
            if (state.Boss != null) n++;
            return n;
        }
    }
}
