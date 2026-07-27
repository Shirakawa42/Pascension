using System;
using Shards.Engine;

namespace Shards.Bots
{
    /// <summary>Flat, viewer-relative feature vector for the position evaluator.
    ///
    /// DESIGN RULE: every feature is ANTISYMMETRIC — f(state, p) == -f(state, 1-p). A linear
    /// model over antisymmetric features with no bias term is therefore antisymmetric too, so
    /// `eval(p) + eval(1-p) == 1` holds exactly, by construction rather than by test. That
    /// removes a whole class of bug (the previous campaign lost a generation to querying the
    /// net from the wrong seat) and halves what has to be learned.
    ///
    /// WHY FEATURES + A FITTED LINEAR MODEL, rather than a hand-built structure. Measured on
    /// 1,311 mid-game positions: a hand-designed "race between clocks" predicted the winner
    /// 58.1% of the time while a two-term health+mastery sigmoid managed 64.7%. Imposing a
    /// structure a priori destroyed signal. So the structure is not imposed: ratios that are
    /// known to matter are provided as INPUTS (a linear model cannot invent a division), and
    /// which of them matter, and how much, is left to the fit.
    ///
    /// This is also what lets the AI discover a playstyle instead of being told one. Nothing
    /// here says aggression beats economy or that the mastery race is good — those are just
    /// coordinates, and the weights come from what actually won.</summary>
    public static class ShardsEvalFeatures
    {
        public const int Count = 22;

        public static readonly string[] Names =
        {
            "health", "mastery", "gemsPerTurn", "powerPerTurn", "masteryPerTurn",
            "drawsPerTurn", "shieldPerTurn", "deckSize", "density", "champions",
            "boardPower", "boardGems", "distinctFactions", "allegiance4", "unifyLiveness",
            "toM30", "tempo", "killRate", "ascendRate", "killUrgency",
            "boardShare", "cycleSpeed"
        };

        public static void Extract(ShardsState state, int viewer, double[] dst)
        {
            var me = state.Players[viewer];
            var opp = state.Players[1 - viewer];
            var a = ShardsDeckStats.For(state, viewer);
            var b = ShardsDeckStats.For(state, 1 - viewer);

            int i = 0;
            dst[i++] = (me.Health - opp.Health) / 50.0;
            dst[i++] = (me.Mastery - opp.Mastery) / 30.0;
            dst[i++] = (a.GemsPerTurn - b.GemsPerTurn) / 10.0;
            dst[i++] = (a.PowerPerTurn - b.PowerPerTurn) / 10.0;
            dst[i++] = (a.MasteryPerTurn - b.MasteryPerTurn) / 3.0;
            dst[i++] = (a.DrawsPerTurn - b.DrawsPerTurn) / 3.0;
            dst[i++] = (a.ShieldPerTurn - b.ShieldPerTurn) / 5.0;
            dst[i++] = (a.N - b.N) / 20.0;
            // Density: output per card. Deck SIZE alone is ambiguous — big is good if the
            // cards are good — so give the fit both size and quality-per-card and let it
            // decide (eval-rules R7 argues density is the real quantity; this tests it).
            dst[i++] = (Density(a) - Density(b)) / 2.0;
            dst[i++] = (a.Champions - b.Champions) / 5.0;
            dst[i++] = (a.BoardPower - b.BoardPower) / 10.0;
            dst[i++] = (a.BoardGems - b.BoardGems) / 10.0;
            dst[i++] = (a.DistinctFactions - b.DistinctFactions) / 4.0;
            dst[i++] = (a.FactionsAtAllegiance4 - b.FactionsAtAllegiance4) / 2.0;
            dst[i++] = a.UnifyLiveness - b.UnifyLiveness;
            // Mastery remaining to M30, which the ascend route actually has to cover. The
            // Shard-banished flag was here first and fitted to EXACTLY 0.000 — it is almost
            // never true in real games, so it carried no information at all. Kept as a
            // correctness concern in the evaluator, dropped as a feature.
            dst[i++] = ((30 - opp.Mastery) - (30 - me.Mastery)) / 30.0;
            dst[i++] = state.TurnPlayerIndex == viewer ? 1 : -1;

            // Ratios, supplied because a linear model cannot form them. These are the clock
            // quantities INVERTED — a rate rather than a time — so that "no damage output" is
            // 0 instead of infinity, which keeps the feature bounded and the fit stable.
            double myKill = a.PowerPerTurn / Math.Max(1.0, opp.Health);
            double oppKill = b.PowerPerTurn / Math.Max(1.0, me.Health);
            dst[i++] = Math.Min(2.0, myKill) - Math.Min(2.0, oppKill);

            double myAscend = a.MasteryPerTurn / Math.Max(1.0, 30 - me.Mastery);
            double oppAscend = b.MasteryPerTurn / Math.Max(1.0, 30 - opp.Mastery);
            dst[i++] = Math.Min(2.0, myAscend) - Math.Min(2.0, oppAscend);

            // Urgency: how close the FASTER of the two kill clocks is to landing. killRate
            // above is a difference and cancels when both sides are equally fast; this says
            // whether the game is about to end at all, which is what decides if a one-turn
            // lead is decisive or noise.
            //
            // (The first version of this feature recomputed killRate exactly — the fit gave
            // both an identical 0.349711 weight, which is how the duplication was caught.
            // Perfectly collinear features are invisible to accuracy and obvious in the
            // weights.)
            double fastest = Math.Max(myKill, oppKill);
            dst[i++] = (myKill - oppKill) * Math.Min(2.0, fastest);

            // Share of output coming from the board rather than the deck. Board effects fire
            // every turn and occupy no deck slot, so a deck-heavy and a board-heavy engine of
            // equal total rate are not equally good.
            dst[i++] = Share(a) - Share(b);
            // Cycle speed: how much of the deck is seen per turn. Compounds twice — it lifts
            // every rate AND shortens the Infinity Shard wait.
            dst[i++] = (a.D / Math.Max(1, a.N)) - (b.D / Math.Max(1, b.N));

            if (i != Count) throw new InvalidOperationException($"feature count drift: {i} != {Count}");
        }

        private static double Density(ShardsDeckStats s) =>
            s.N == 0 ? 0 : (s.GemsPerTurn + s.PowerPerTurn + s.MasteryPerTurn) / Math.Max(1, s.D);

        private static double Share(ShardsDeckStats s)
        {
            double board = s.BoardPower + s.BoardGems + s.BoardMastery;
            double total = s.PowerPerTurn + s.GemsPerTurn + s.MasteryPerTurn;
            return total <= 0.01 ? 0 : board / total;
        }
    }

    /// <summary>Position evaluator: sigmoid of a weighted sum over
    /// <see cref="ShardsEvalFeatures"/>. No bias term — the features are antisymmetric, so
    /// omitting it makes `eval(p) + eval(1-p) == 1` exact.
    ///
    /// Weights are FITTED to game outcomes by `soisim fit` (logistic regression), never
    /// hand-set. The measured lesson behind that: a hand-designed structure scored 58.1%
    /// where two fitted-looking linear terms scored 64.7%.</summary>
    public sealed class ShardsLinearEval : IShardsValueEvaluator
    {
        private readonly double[] _w;
        [ThreadStatic] private static double[] _scratch;

        public ShardsLinearEval(double[] weights = null)
        {
            _w = weights ?? ShardsEvalLinearWeights.Current;
            if (_w.Length != ShardsEvalFeatures.Count)
                throw new ArgumentException(
                    $"expected {ShardsEvalFeatures.Count} weights, got {_w.Length}");
        }

        public double Evaluate(ShardsState state, int playerIndex)
        {
            if (state.GameOver)
                return state.WinnerIndex < 0 ? 0.5 : state.WinnerIndex == playerIndex ? 1 : 0;
            var me = state.Players[playerIndex];
            if (me.Eliminated) return 0;
            if (state.Players[1 - playerIndex].Eliminated) return 1;

            _scratch ??= new double[ShardsEvalFeatures.Count];
            ShardsEvalFeatures.Extract(state, playerIndex, _scratch);
            double z = 0;
            for (int i = 0; i < _w.Length; i++) z += _w[i] * _scratch[i];
            return 1.0 / (1.0 + Math.Exp(-Math.Max(-30, Math.Min(30, z))));
        }
    }
}
