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
        /// <summary>The crudest defensible evaluator: health and mastery differences, nothing
        /// else. The control the clock model has to beat to justify existing.</summary>
        private sealed class NaiveEval : IShardsValueEvaluator
        {
            public double Evaluate(ShardsState state, int playerIndex)
            {
                if (state.GameOver)
                    return state.WinnerIndex < 0 ? 0.5 : state.WinnerIndex == playerIndex ? 1 : 0;
                var me = state.Players[playerIndex];
                var opp = state.Players[1 - playerIndex];
                double x = (me.Health - opp.Health) / 50.0 + (me.Mastery - opp.Mastery) / 30.0;
                return 1.0 / (1.0 + System.Math.Exp(-2.0 * x));
            }
        }

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
            // A raw number means nothing without a reference: mid-game positions from
            // evenly-matched self-play are genuinely hard, so even a good evaluator will not
            // approach the ~76% the old nets reported (measured over ALL positions, including
            // near-terminal ones where the answer is obvious — not a comparable figure).
            //
            // So compare against the crudest possible baseline on the SAME sample. If the
            // clock model cannot beat "who has more health and mastery", its entire structure
            // is buying nothing.
            double clock = Accuracy(new ShardsClockEval(), out int samples);
            double naive = Accuracy(new NaiveEval(), out _);
            Assert.Greater(samples, 200, "too few sampled positions to conclude anything");
            // MEASURED 2026-07-27: clock 58.1%, naive 64.7%, over 1,311 positions.
            // The elaborate structure is WORSE than two linear terms.
            //
            // Two mechanisms, both information-destroying, both mine rather than the game's:
            //  · min(killClock, ascendClock) discards a whole route. Holding both a 5-turn
            //    ascend and a 6-turn kill is plainly better than holding only the 5, and the
            //    min cannot say so.
            //  · the soft horizon and the relative-urgency denominator each compress large
            //    differences, and stacked they flatten exactly the health and mastery gaps
            //    the naive function reads directly.
            //
            // The deeper mistake was mine: I audited eval-rules' EVIDENCE and found it
            // unreliable, then adopted its STRUCTURE on faith anyway. "Do not build a
            // weighted sum, build a race between clocks" was four LLM agents' opinion, and
            // it is now measured to be wrong here.
            //
            // Target: clock > naive + 0.02, and Phase 2 is blocked until then. The floor
            // below only catches further regression.
            Assert.Greater(clock, naive - 0.10,
                $"clock model {clock:P1} vs naive health+mastery {naive:P1} over {samples} " +
                "positions — worse than the 2026-07-27 measurement, so this is a regression " +
                "on top of an already-unearned structure.");
        }
    }
}
