using System;
using Shards.Engine;

namespace Shards.Bots
{
    /// <summary>Tunable coefficients for <see cref="ShardsClockEval"/>. Separate from
    /// <see cref="W"/>, which holds ACTION-policy weights: nothing in W scores a position.
    /// Non-zero defaults throughout, so sep-CMA-ES can move every one of them (it scales each
    /// dimension by max(|start|, 0.05), which makes a 0.0 default untunable forever).</summary>
    public sealed class ShardsClockParams
    {
        /// <summary>Sigmoid steepness on the RELATIVE clock lead. Deliberately modest: a turn
        /// of clock is genuinely uncertain — ~40% of games are comebacks, and one card can
        /// compress a 3-turn clock to 1 (Fao Cu'tul doubles power at M20).</summary>
        public double Steepness = 1.6;

        /// <summary>Clocks are clamped here, because a clock longer than the remaining game
        /// is not a clock at all. Measured game length is p10/p50/p90 = 11/14/17 rounds, so
        /// past ~12 more turns a position is decided by how it DEVELOPS, not by today's rates.
        ///
        /// Without this the evaluator saturates on differences that can never be realised:
        /// 50 health against 1.5 damage a turn is a 33-turn clock and 25 health is a 17-turn
        /// clock, and scoring that gap as decisive says losing half your health matters
        /// enormously against an opponent who cannot actually kill you.</summary>
        public double HorizonTurns = 12;
        /// <summary>Tempo edge for being the player to move.</summary>
        public double TempoTurns = 0.5;
        /// <summary>Floor on net damage per turn, so a stalled damage clock is very long
        /// rather than infinite or divide-by-zero.</summary>
        public double MinDamagePerTurn = 0.25;
        /// <summary>Floor on mastery per turn, same reason for the ascend clock.</summary>
        public double MinMasteryPerTurn = 0.05;
        /// <summary>Clock assigned when a route is dead (no damage output, or the Infinity
        /// Shard is banished). Large but finite, so comparisons stay well-defined.</summary>
        public double DeadClock = 99;
        /// <summary>Champion defence counts toward survival at this rate. Defence is not a
        /// damage sponge — it is a price tag on removing your engine — so it is deliberately
        /// weak, not additive with health.</summary>
        public double ChampionWallPerPoint = 0.15;

        /// <summary>Mastery per turn from FOCUS — exhaust your character plus 1 gem for +1
        /// mastery, once per turn.
        ///
        /// This is not a card, so it appears nowhere in the deck's own rates, and leaving it
        /// out makes the ascend clock read as DEAD for every deck: no starter gains mastery,
        /// so masteryPerTurn is exactly 0 and M29 scores identically to M0. Focus is in fact
        /// the PRIMARY mastery engine — both human players in the recorded match history use
        /// it 5-10 times a game, and the tuned bot reaches M30 in half its wins.
        ///
        /// Slightly under 1.0 because a turn's gems occasionally go to a purchase instead.</summary>
        public double FocusMasteryPerTurn = 0.85;
        /// <summary>Gems per turn needed before Focus is assumed affordable.</summary>
        public double FocusGemThreshold = 1.0;
    }

    /// <summary>Position evaluator: a RACE BETWEEN CLOCKS, not a weighted sum.
    ///
    /// Each side runs two clocks — how many turns to kill, and how many to reach M30 and draw
    /// the Infinity Shard. Whoever's shorter clock finishes first wins, so the position is
    /// scored on the DIFFERENCE of times-to-win. Health therefore enters only through
    /// `health / damagePerTurn`, never as a linear term: 40 health against a 3-damage deck is
    /// safe, and against a 20-damage deck it is nearly lost, which no linear coefficient can
    /// say. The previous hand-written evaluator's largest coefficient was linear health, and
    /// four independent reviews each named that its single biggest error.
    ///
    /// Why the ascend clock is co-equal rather than a corner case: a TUNED policy wins 51.1%
    /// of its games through M30 + Infinity Shard, while the hand-written bot wins 5.7% that
    /// way under identical rules. Both human players in the recorded match history race
    /// mastery too (Focus 5-10x a game, M10 by round 7-9). Treating it as a side-line would
    /// misprice half of all games that good play produces.
    ///
    /// Every ratio here is computed EXACTLY by <see cref="ShardsDeckStats"/> rather than left
    /// to be learned. The retired neural evaluator was fed summed bags of card vectors and
    /// asked to learn a target built from `D x Sum/N` and `health/damage`; a net that never
    /// sees N multiplicatively against the sum cannot express a division, and it measured
    /// 40.6% against having no evaluator at all.
    ///
    /// ⚠ NOT YET SHIPPED ANYWHERE. Per the standing bar, an evaluator only enters a bot after
    /// it beats full-rollout ISMCTS head-to-head at equal WALL-CLOCK, paired, SPRT, n≥2000.
    /// ⚠ No burst/lethal detection yet. At an end-of-turn leaf gems and power are both zero
    /// (ResetTurn), so a leaf evaluator does not need it — but a mid-turn caller would, and
    /// burst is multiplicative (an additive estimate of 26 power was actually 53).</summary>
    public sealed class ShardsClockEval : IShardsValueEvaluator
    {
        private readonly ShardsClockParams _p;

        public ShardsClockEval(ShardsClockParams parameters = null) => _p = parameters ?? new ShardsClockParams();

        public double Evaluate(ShardsState state, int playerIndex)
        {
            if (state.GameOver)
                return state.WinnerIndex < 0 ? 0.5 : state.WinnerIndex == playerIndex ? 1 : 0;
            var me = state.Players[playerIndex];
            var opp = state.Players[1 - playerIndex];
            if (me.Eliminated) return 0;
            if (opp.Eliminated) return 1;

            var myStats = ShardsDeckStats.For(state, playerIndex);
            var oppStats = ShardsDeckStats.For(state, 1 - playerIndex);

            double myTtk = TimeToWin(myStats, oppStats, opp);
            double oppTtk = TimeToWin(oppStats, myStats, me);

            // Positive when the opponent needs longer than we do.
            double edge = oppTtk - myTtk;
            edge += state.TurnPlayerIndex == playerIndex ? _p.TempoTurns : -_p.TempoTurns;

            // RELATIVE, not absolute. One turn of lead is decisive when both clocks are 2 and
            // is noise when both are 12, so the lead is measured against the shorter clock —
            // the one that actually decides when the game ends. This also keeps the function
            // scale-free, so it behaves the same in round 3 and round 15.
            //
            // min() is symmetric between the seats and the numerator negates exactly, so the
            // two seats' scores still sum to 1.
            double urgency = Math.Max(1.0, Math.Min(myTtk, oppTtk));
            return 1.0 / (1.0 + Math.Exp(-_p.Steepness * edge / urgency));
        }

        /// <summary>Turns until <paramref name="attacker"/> wins, by whichever route is faster.</summary>
        private double TimeToWin(ShardsDeckStats attacker, ShardsDeckStats defender, ShardsPlayer defenderPlayer)
        {
            // --- kill clock ---
            // Shields sit in the DENOMINATOR as prevented damage. When prevention meets the
            // incoming rate the clock goes to infinity, which is exactly right and is what a
            // flat "+N effective HP" bonus cannot express.
            double net = attacker.PowerPerTurn - defender.ShieldPerTurn;
            // Champion defence buys turns, weakly: it is a price on removing the engine, not
            // a damage sponge. In one reviewed position 33 points of champion defence
            // absorbed exactly zero damage all game.
            double wall = 0;
            foreach (var champion in defenderPlayer.Champions)
                wall += Math.Max(0, champion.Def.Defense) * _p.ChampionWallPerPoint;
            net -= wall;

            double killClock = net <= _p.MinDamagePerTurn
                ? _p.DeadClock
                : defenderPlayer.Health / net;

            // --- ascend clock ---
            // Reaching M30 does not win: you must then DRAW the Infinity Shard, of which
            // there is exactly one in a deck of N. So deck thinning is a win-condition
            // accelerator, not a nicety — and if the Shard has been banished the route is
            // dead discontinuously, which several self-banish effects can cause.
            double ascendClock;
            if (attacker.ShardBanished)
            {
                ascendClock = _p.DeadClock;
            }
            else
            {
                // Focus is an ACTION, not a card, so it is absent from the deck's own rates —
                // and without it every deck's ascend clock reads as dead.
                double masteryRate = attacker.MasteryPerTurn;
                if (attacker.GemsPerTurn >= _p.FocusGemThreshold)
                    masteryRate += _p.FocusMasteryPerTurn;
                double toM30 = Math.Max(0, 30 - attacker.Mastery) /
                               Math.Max(_p.MinMasteryPerTurn, masteryRate);
                // Half a cycle on average to draw a specific card once mastery is there.
                ascendClock = Math.Min(_p.DeadClock, toM30 + attacker.CycleTurns * 0.5);
            }

            // Compress toward the horizon — SMOOTHLY, never with a hard min().
            //
            // A hard clamp was the first attempt and it was badly wrong: early on both
            // clocks exceed the horizon, so every candidate turn clamped to the same value,
            // the evaluator went FLAT, and the planner chose almost at random. It showed up
            // as reroll spam — 24 rerolls a game against the greedy policy's 0.49 — because
            // with no signal it picks whichever option happens to differ in the last decimal.
            //
            // h*c/(c+h) keeps the anti-saturation property (a 33-turn clock and a 17-turn
            // clock stop being treated as decisively different) while remaining strictly
            // monotonic, so shorter is always better and no region is ever flat.
            double clock = Math.Min(killClock, ascendClock);
            return _p.HorizonTurns * clock / (clock + _p.HorizonTurns);
        }
    }
}
