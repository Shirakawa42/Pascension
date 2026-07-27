using System;
using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Serialization;
using Shards.Engine;

namespace Shards.Bots
{
    /// <summary>Search budget for <see cref="ShardsPlannerBot"/>.</summary>
    public sealed class ShardsPlannerConfig
    {
        /// <summary>Determinizations each candidate is scored against. The SAME worlds score
        /// every candidate (common random numbers), so the comparison is paired and the
        /// shuffle cancels — this is variance reduction, not extra strength. Kept small on
        /// purpose: an oracle that sees the opponent's real hand beats an identical honest
        /// agent by only ~38 Elo, so hidden information is nearly worthless here and budget
        /// spent on more worlds is budget not spent on depth.</summary>
        public int Worlds = 2;
        /// <summary>Safety cap on submits while completing one simulated turn.</summary>
        public int MaxTurnSubmits = 400;
    }

    /// <summary>Plans a whole TURN and scores where it ends, instead of scoring each action.
    ///
    /// Why the unit of search is a turn. A SoI turn is ~10-25 submits deep, so a
    /// micro-action tree spends its whole budget inside the first turn — which is why the
    /// existing ISMCTS needs about 1,200 iterations merely to break even with an instant
    /// greedy policy, and at 300 iterations scores 21.2% against it. Making the unit a
    /// complete turn means every unit of budget buys a whole strategic option.
    ///
    /// Each candidate first-action is played on a determinized fork, the rest of the turn is
    /// completed with the tuned greedy policy, and the resulting END-OF-TURN position is
    /// scored by <see cref="ShardsClockEval"/>. That leaf is the right place to evaluate:
    /// ResetTurn has zeroed gems and power, so what remains is exactly the durable state the
    /// clock model reads — deck composition, board, health, mastery.
    ///
    /// This is the shape every documented strong bot in this genre uses (Provincial for
    /// Dominion, Keldon Jones' Race for the Galaxy AI, the two-time Tales of Tribute
    /// champion): cheap wide enumeration of one turn, one good evaluator, averaged over a few
    /// sampled shuffles. Not deep search, not deep RL.
    ///
    /// ⚠ Determinization is not optional. Skipping it leaves the fork holding the opponent's
    /// real hand; the fairness invariance test exists to catch exactly that.
    ///
    /// ═══ STATUS 2026-07-27: FAILS ITS GATE. 4.0% [2.1-5.9] vs bench:greedy-v5, ~-550 Elo.
    /// Do not ship. Kept as a documented negative result. ═══
    ///
    /// The design flaw is in THIS class, not the evaluator. It scores a candidate FIRST
    /// ACTION by completing the rest of the turn with the greedy policy — but greedy
    /// completion dominates the outcome, so every candidate lands on a nearly identical
    /// end-of-turn position and the differences collapse into the evaluator's noise floor.
    /// The bot is then choosing on rounding error. It shows up unmistakably in coverage:
    /// 24.2 rerolls per game against the greedy policy's 0.49, with buys down from 36.3 to
    /// 23.8 — the fingerprint of a policy with no signal picking whatever differs last.
    ///
    /// What the plan actually specified, and what this skipped: enumerate COMPLETE TURN
    /// PLANS and compare those. The real decision is the purchase basket, which is where
    /// 84-92% of the strength was measured to live, and two baskets differ substantially at
    /// the leaf. Comparing first-actions-then-greedy measures almost nothing, because the
    /// greedy tail washes the choice out.
    ///
    /// A hard horizon clamp in the evaluator was suspected first and fixed (it made the
    /// score genuinely flat early, when both clocks exceed the horizon) — that was a real
    /// bug, now a smooth compression, but it moved the result 6.0% → 4.0%, so it was not the
    /// cause. Recording the ruled-out hypothesis matters as much as the surviving one.</summary>
    public sealed class ShardsPlannerBot : IBotAgent
    {
        private readonly ShardsEngine _engine;
        private readonly ShardsValueModel _model;
        private readonly IShardsValueEvaluator _eval;
        private readonly ShardsPlannerConfig _config;
        private readonly DeterministicRng _rng;
        private readonly ShardsCloneArena _arena = new();

        public string Descriptor => $"planner-w{_config.Worlds}";

        public ShardsPlannerBot(ulong seed, ShardsEngine engine, ShardsValueModel model = null,
            IShardsValueEvaluator evaluator = null, ShardsPlannerConfig config = null)
        {
            _engine = engine;
            _model = model ?? new ShardsValueModel();
            _eval = evaluator ?? new ShardsClockEval();
            _config = config ?? new ShardsPlannerConfig();
            _rng = new DeterministicRng(seed, 37);
        }

        public PlayerAction Choose(PendingSnap pending, SnapshotBase view)
        {
            if (pending == null) return null;
            // Decisions resolve inside a turn the planner has already committed to, and the
            // engine cannot fork mid-decision (effects are iterators with no snapshot), so
            // the tuned model answers them.
            if (pending.Kind == PendingInputKind.Decision)
                return new SubmitDecisionAction
                {
                    PlayerIndex = pending.PlayerIndex,
                    Answer = _model.ChooseAnswer(_engine, pending.Decision)
                };

            int me = pending.PlayerIndex;
            var legal = _engine.LegalActions(me);
            if (legal.Count == 0) return new ShardsEndTurnAction { PlayerIndex = me };
            if (legal.Count == 1) return legal[0];

            // One seed per world, drawn once and reused for every candidate: common random
            // numbers, so candidates differ by their own merit rather than by luck.
            var worldSeeds = new ulong[Math.Max(1, _config.Worlds)];
            for (int w = 0; w < worldSeeds.Length; w++)
                worldSeeds[w] = (((ulong)_rng.NextUInt() << 32) | _rng.NextUInt()) | 1UL;

            PlayerAction best = null;
            double bestScore = double.NegativeInfinity;
            foreach (var candidate in legal)
            {
                if (candidate is ConcedeAction) continue; // never resign by policy
                double total = 0;
                for (int w = 0; w < worldSeeds.Length; w++)
                    total += ScoreCandidate(candidate, me, worldSeeds[w]);
                double score = total / worldSeeds.Length;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }
            return best ?? _model.ChooseAction(_engine, me);
        }

        /// <summary>Plays <paramref name="candidate"/> on a determinized fork, finishes the
        /// turn greedily, and scores the end-of-turn position.</summary>
        private double ScoreCandidate(PlayerAction candidate, int me, ulong worldSeed)
        {
            var fork = _engine.Fork(rngReseed: worldSeed, quiet: true, arena: _arena);
            ShardsDeterminizer.Sample(fork.State, me, fork.State.Rng);

            if (!fork.Submit(candidate).Accepted)
                return double.NegativeInfinity; // rejected on the fork: not a real option

            // Finish the turn with the tuned policy. Stop when the turn leaves us — that is
            // the end-of-turn leaf, after ResetTurn has zeroed gems and power.
            int submits = 0;
            while (!fork.State.GameOver &&
                   fork.State.TurnPlayerIndex == me &&
                   submits++ < _config.MaxTurnSubmits)
            {
                var next = fork.PendingInput;
                if (next == null) break;
                PlayerAction action = next.Kind == PendingInputKind.Decision
                    ? new SubmitDecisionAction
                    {
                        PlayerIndex = next.PlayerIndex,
                        Answer = _model.ChooseAnswer(fork, next.Decision)
                    }
                    : _model.ChooseAction(fork, next.PlayerIndex);
                if (!fork.Submit(action).Accepted &&
                    !fork.Submit(DefaultActions.For(ToSnap(fork.PendingInput))).Accepted)
                    break;
            }
            return _eval.Evaluate(fork.State, me);
        }

        private static PendingSnap ToSnap(PendingInput pending) => pending == null ? null : new PendingSnap
        {
            Kind = pending.Kind,
            PlayerIndex = pending.PlayerIndex,
            LegalActions = pending.LegalActions,
            Decision = pending.Decision
        };
    }
}
