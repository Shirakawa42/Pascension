using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace SoiSim.Tests
{
    /// <summary>Guards the buy-vs-play ablation instrument.
    ///
    /// The ablation measured that the acquisition axis carries 84-92% of SoI's strength
    /// (untuning buy costs 126-209 Elo, untuning play costs 11-39), which is the finding the
    /// whole planner design rests on. That conclusion is only valid if every action the
    /// engine can offer is actually attributed to one axis — an unclassified action is
    /// silently dropped by BOTH stages, which would bias the result toward whichever axis
    /// still owns its neighbours.</summary>
    [TestFixture]
    public sealed class SoiSimAblationTests
    {
        private static ShardsDlc AllWithDuel =>
            ShardsDlc.RelicsOfTheFuture | ShardsDlc.ShadowOfSalvation |
            ShardsDlc.IntoTheHorizon | ShardsDlc.Duel;

        private static ShardsEngineAdapter NewGame(ulong seed, IReadOnlyList<string> chars)
        {
            var specs = new List<PlayerSpec>
            {
                new() { Name = "S0", CharacterId = chars[(int)(seed % (ulong)chars.Count)] },
                new() { Name = "S1", CharacterId = chars[(int)((seed + 1) % (ulong)chars.Count)] }
            };
            return new ShardsEngineAdapter(
                ShardsContentRegistry.StandardConfig(seed, specs, AllWithDuel));
        }

        [Test]
        public void ActionClasses_PartitionEveryLegalAction()
        {
            // Behavioural rather than reflective: a new ShardsXxxAction type added to
            // LegalActions would compile fine and be invisible to a type-list assertion,
            // but it shows up here the moment a game offers it.
            ShardsContentRegistry.EnsureRegistered();
            var chars = ShardsContentRegistry.CharactersFor(AllWithDuel);
            var model = new ShardsValueModel(W.Pad(ShardsEvalWeights.V5));
            var unclassified = new SortedSet<string>();
            var seen = new SortedSet<string>();

            for (ulong seed = 900; seed < 930; seed++)
            {
                var adapter = NewGame(seed, chars);
                var seats = new IBotAgent[2];
                for (int i = 0; i < 2; i++)
                    seats[i] = new ShardsGreedyEvalBot(seed * 100 + (ulong)i, adapter.Inner, model);

                int guard = 0;
                while (!adapter.GameOver && guard++ < SimGameRunner.GuardLimit)
                {
                    var pending = adapter.PendingInput;
                    if (pending == null) break;
                    if (pending.Kind == PendingInputKind.Priority)
                        foreach (var action in adapter.Inner.LegalActions(pending.PlayerIndex))
                        {
                            string name = action.GetType().Name;
                            seen.Add(name);
                            bool play = PhaseHybridBot.IsPlay(action);
                            bool buy = PhaseHybridBot.IsAcquisition(action);
                            Assert.IsFalse(play && buy,
                                $"{name} is classified as BOTH play and acquisition");
                            // EndTurn is the shared baseline both stages score against;
                            // Concede is never a candidate.
                            if (!play && !buy &&
                                action is not ShardsEndTurnAction && action is not ConcedeAction)
                                unclassified.Add(name);
                        }
                    var chosen = seats[pending.PlayerIndex].Choose(pending, null)
                                 ?? adapter.DefaultActionFor(pending);
                    if (!adapter.Submit(chosen).Accepted &&
                        !adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted)
                        break;
                }
            }

            Assert.IsEmpty(unclassified,
                "these legal actions belong to neither ablation axis, so the attribution " +
                "silently ignores them: " + string.Join(", ", unclassified) +
                " — add each to PhaseHybridBot.IsPlay or .IsAcquisition");
            // Sanity: the sweep must actually have exercised a broad action set, or the
            // assertion above is vacuously true.
            Assert.Greater(seen.Count, 5, "too few distinct action types seen: " +
                                          string.Join(", ", seen));
        }

        [Test]
        public void PhaseHybridBot_PlaysCompleteGames()
        {
            // The two-stage delegation must never stall: if both stages decline, it has to
            // fall through to END TURN. A stalled arm would score 0.5 and quietly flatten
            // the measured Elo gap toward zero.
            ShardsContentRegistry.EnsureRegistered();
            var chars = ShardsContentRegistry.CharactersFor(AllWithDuel);
            var strong = new ShardsValueModel(W.Pad(ShardsEvalWeights.V5));
            var weak = new ShardsValueModel(W.Pad(ShardsEvalWeights.V1));
            int finished = 0;

            for (ulong seed = 700; seed < 712; seed++)
            {
                var adapter = NewGame(seed, chars);
                var seats = new IBotAgent[]
                {
                    new PhaseHybridBot(adapter.Inner, strong, weak),
                    new PhaseHybridBot(adapter.Inner, weak, strong)
                };
                int guard = 0;
                while (!adapter.GameOver && guard++ < SimGameRunner.GuardLimit)
                {
                    var pending = adapter.PendingInput;
                    if (pending == null) break;
                    var action = seats[pending.PlayerIndex].Choose(pending, null)
                                 ?? adapter.DefaultActionFor(pending);
                    if (!adapter.Submit(action).Accepted &&
                        !adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted)
                        break;
                }
                if (adapter.GameOver) finished++;
            }
            Assert.AreEqual(12, finished, "mismatched-axis hybrids failed to finish every game");
        }
    }
}
