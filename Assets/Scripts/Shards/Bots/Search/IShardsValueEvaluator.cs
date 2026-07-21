using Shards.Engine;

namespace Shards.Bots
{
    /// <summary>Win-probability estimate for truncated rollouts. Implementations must
    /// be pure reads (called on cloned states inside the search) and cheap (µs-scale).
    /// The neural net (Part C) implements this; ShardsBaselineEvaluator is the interim.</summary>
    public interface IShardsValueEvaluator
    {
        /// <summary>P(playerIndex wins) in [0,1] for a 2-player state.</summary>
        double Evaluate(ShardsState state, int playerIndex);
    }

    /// <summary>Interim hand-coefficient logistic over cheap state aggregates: health,
    /// mastery race, deck quality (tuned card values), champion walls. Good enough to
    /// stop rollouts 2 end-turns out; superseded by the trained net.</summary>
    public sealed class ShardsBaselineEvaluator : IShardsValueEvaluator
    {
        private readonly ShardsValueModel _model;

        public ShardsBaselineEvaluator(ShardsValueModel model) => _model = model;

        public double Evaluate(ShardsState state, int playerIndex)
        {
            if (state.GameOver)
                return state.WinnerIndex < 0 ? 0.5 : state.WinnerIndex == playerIndex ? 1 : 0;
            var me = state.Players[playerIndex];
            var opp = state.Players[1 - playerIndex];
            if (me.Eliminated) return 0;
            if (opp.Eliminated) return 1;

            double x =
                2.8 * (me.Health - opp.Health) / 50.0 +
                1.6 * (me.Mastery - opp.Mastery) / 30.0 +
                1.0 * (DeckQuality(me) - DeckQuality(opp)) / 40.0 +
                0.5 * (ChampionWall(me) - ChampionWall(opp)) / 12.0 +
                0.15 * (state.TurnPlayerIndex == playerIndex ? 1 : -1);
            return 1.0 / (1.0 + System.Math.Exp(-x));
        }

        private double DeckQuality(ShardsPlayer p)
        {
            double sum = 0;
            foreach (var c in p.Deck) sum += _model.CardValue(c.Def, p.Mastery);
            foreach (var c in p.Hand) sum += _model.CardValue(c.Def, p.Mastery);
            foreach (var c in p.Discard) sum += _model.CardValue(c.Def, p.Mastery);
            foreach (var c in p.PlayZone) sum += _model.CardValue(c.Def, p.Mastery);
            foreach (var c in p.Destinies) sum += _model.CardValue(c.Def, p.Mastery);
            return sum;
        }

        private static double ChampionWall(ShardsPlayer p)
        {
            double sum = 0;
            foreach (var c in p.Champions)
                sum += System.Math.Max(0, c.Def.Defense - c.DamageThisTurn) + (c.Def.Taunt ? 2 : 0);
            return sum;
        }
    }
}
