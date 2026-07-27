using System.Collections.Generic;
using NUnit.Framework;
using Pascension.Core;
using Pascension.Engine.Core;
using Shards.Bots;
using Shards.Content;
using Shards.Engine;

namespace SoiSim.Tests
{
    /// <summary>Does the evaluator actually predict who wins?
    ///
    /// This is the measurement that should have come BEFORE any planner was built. The shape
    /// tests prove the function responds in the right directions; they cannot prove it carries
    /// enough signal to steer a search. An evaluator can pass every structural property and
    /// still be worthless if it sits near 0.5 everywhere.
    ///
    /// Method: play real games, sample end-of-turn positions, and ask whether the side the
    /// evaluator prefers is the side that eventually wins. Positions are sampled from the
    /// middle of the game — the opening is genuinely near-even and the endgame is trivially
    /// predictable, so both would flatter the result.</summary>
    [TestFixture]
    public sealed class SoiSimEvalAccuracyTests
    {
        private static ShardsDlc AllWithDuel =>
            ShardsDlc.RelicsOfTheFuture | ShardsDlc.ShadowOfSalvation |
            ShardsDlc.IntoTheHorizon | ShardsDlc.Duel;

        private static double Accuracy(IShardsValueEvaluator eval, out int samples)
        {
            ShardsContentRegistry.EnsureRegistered();
            var chars = ShardsContentRegistry.CharactersFor(AllWithDuel);
            var model = new ShardsValueModel(W.Pad(ShardsEvalWeights.Current));
            int correct = 0, total = 0;

            for (ulong seed = 5000; seed < 5060; seed++)
            {
                var rng = new DeterministicRng(seed * 31 + 7, 91);
                int a = rng.Next(chars.Count);
                int b = rng.Next(chars.Count - 1);
                if (b >= a) b++;
                var adapter = new ShardsEngineAdapter(ShardsContentRegistry.StandardConfig(
                    seed, new List<PlayerSpec>
                    {
                        new() { Name = "S0", CharacterId = chars[a] },
                        new() { Name = "S1", CharacterId = chars[b] }
                    }, AllWithDuel));
                var seats = new IBotAgent[]
                {
                    new ShardsGreedyEvalBot(seed * 100, adapter.Inner, model),
                    new ShardsGreedyEvalBot(seed * 100 + 1, adapter.Inner, model)
                };

                var sampled = new List<double>();
                int guard = 0;
                while (!adapter.GameOver && guard++ < SimGameRunner.GuardLimit)
                {
                    var pending = adapter.PendingInput;
                    if (pending == null) break;
                    // End-of-turn leaves from round 5 on: past the near-even opening, and
                    // exactly the position kind a leaf evaluator is asked to score.
                    if (pending.Kind == PendingInputKind.Priority &&
                        adapter.Inner.State.Round >= 5 &&
                        adapter.Inner.State.Players[0].PlayZone.Count == 0 &&
                        adapter.Inner.State.Players[1].PlayZone.Count == 0)
                        sampled.Add(eval.Evaluate(adapter.Inner.State, 0));

                    var action = seats[pending.PlayerIndex].Choose(pending, null)
                                 ?? adapter.DefaultActionFor(pending);
                    if (!adapter.Submit(action).Accepted &&
                        !adapter.Submit(adapter.DefaultActionFor(adapter.PendingInput)).Accepted)
                        break;
                }
                if (!adapter.GameOver || adapter.WinnerIndex < 0) continue;
                bool zeroWon = adapter.WinnerIndex == 0;
                foreach (double v in sampled)
                {
                    if (v == 0.5) continue; // no opinion
                    if (v > 0.5 == zeroWon) correct++;
                    total++;
                }
            }
            samples = total;
            return total == 0 ? 0 : (double)correct / total;
        }

        [Test]
        public void ClockEval_PredictsTheWinner_BetterThanChance()
        {
            // The bar is deliberately low. A leaf evaluator does not need to be an oracle —
            // it needs to carry enough signal that a search steering by it beats one that
            // does not. But below ~60% it is close to a coin flip, and a search steering by a
            // coin flip is worse than no search at all: that is precisely why 300-iteration
            // ISMCTS scores 21.2% against instant greedy.
            // MEASURED 2026-07-27: 58.2% over 1,311 positions. Barely above chance, and it
            // explains the planner directly — 11.7% against instant greedy, because a search
            // steering by a near-coin-flip is worse than no search at all. That is the same
            // mechanism behind 300-iteration ISMCTS scoring 21.2% against the same opponent.
            //
            // Known gap, and it is specific: the clock model has NO ECONOMY TERM. GemsPerTurn
            // is computed by ShardsDeckStats and then used only as a Focus affordability gate,
            // so a deck producing 12 gems a turn scores identically to one producing 3 when
            // their damage and mastery rates match. In a deck-builder that is most of the
            // position. eval-rules prices it as gems converting into row power ("8 gems buy
            // 9 power out of the shop") plus faster deck growth; neither is implemented.
            //
            // The floor below guards against REGRESSION while that is fixed. The target is
            // 0.60+, and the planner should not be revisited until it is met — measuring the
            // evaluator is far cheaper than measuring a bot that depends on it.
            double accuracy = Accuracy(new ShardsClockEval(), out int samples);
            Assert.Greater(samples, 200, "too few sampled positions to conclude anything");
            Assert.Greater(accuracy, 0.55,
                $"ShardsClockEval predicted the winner on {accuracy:P1} of {samples} mid-game " +
                "end-of-turn positions — BELOW the 58.2% measured on 2026-07-27, so this is a " +
                "regression, not merely an unfinished evaluator.");
        }
    }
}
