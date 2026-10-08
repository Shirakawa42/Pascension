using System;
using System.Collections.Generic;
using System.Linq;
using Shards.Engine;

namespace Shards.Preflight
{
    /// <summary>Versioned external game-setup intervention, never a PPO action.
    /// Does not inspect hidden cards or advance the engine's random generator.</summary>
    internal static class HeroCurriculum
    {
        internal const string Schema = "shards-hero-curriculum-75-25-v1";
        private const ulong Domain = 0x4852435552523031UL;
        private static readonly string[] Heroes = { "decima", "tetra", "volos", "kosynwu", "rez" };

        internal readonly struct Plan
        {
            public bool Forced { get; }
            public int PairIndex { get; }
            public string Seat0 { get; }
            public string Seat1 { get; }
            internal Plan(int pairIndex)
            {
                if (pairIndex < -1 || pairIndex >= 20) throw new ArgumentOutOfRangeException(nameof(pairIndex));
                Forced = pairIndex >= 0;
                PairIndex = pairIndex;
                if (!Forced) { Seat0 = Seat1 = null; return; }
                int first = pairIndex / 4, second = pairIndex % 4;
                if (second >= first) second++;
                Seat0 = Heroes[first]; Seat1 = Heroes[second];
            }
        }

        internal sealed class DraftStep
        {
            public int Seat { get; set; }
            public string Hero { get; set; }
            public int CandidateSlot { get; set; }
            public int OptionId { get; set; }
        }

        internal sealed class Setup
        {
            public string Schema { get; set; } = HeroCurriculum.Schema;
            public ulong EngineSeed { get; set; }
            public Plan Plan { get; set; }
            public List<DraftStep> Prefix { get; set; } = new();
            public long SetupWrapperSteps { get; set; }
            public long SetupEngineSubmissions { get; set; }
        }

        internal static Plan PlanForSeed(ulong seed)
        {
            // Separate stateless setup stream, not State.Rng. Rejection sampling
            // avoids modulo bias: each forced pair owns3 of80 buckets; the other
            //20 buckets preserve normal draft. Version/domain are replay inputs.
            ulong local = seed ^ Domain;
            int bucket = Bounded(ref local, 80);
            return new Plan(bucket < 60 ? bucket % 20 : -1);
        }

        private static int Bounded(ref ulong state, ulong bound)
        {
            ulong threshold = unchecked(0UL - bound) % bound;
            while (true)
            {
                ulong value = Next(ref state);
                if (value >= threshold) return (int)(value % bound);
            }
        }

        private static ulong Next(ref ulong state)
        {
            unchecked
            {
                state += 0x9E3779B97F4A7C15UL;
                ulong z = state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        internal static Setup ApplyAtGameStart(Adapter game, ulong engineSeed) =>
            ApplyPlanAtGameStart(game, engineSeed, PlanForSeed(engineSeed));

        // Explicit plan entry is for forced-match fixtures, not training policy
        // sampling. The future host must call ApplyAtGameStart with its seed.
        internal static Setup ApplyPlanAtGameStart(Adapter game, ulong engineSeed, Plan plan)
        {
            if (plan.Forced != (plan.PairIndex >= 0) || plan.PairIndex < -1 || plan.PairIndex >= 20 ||
                plan.Forced && (plan.Seat0 == null || plan.Seat1 == null || plan.Seat0 == plan.Seat1))
                throw new ArgumentException("Invalid explicit hero setup plan");
            if (!ShardsEngine.DraftableCharacters.SequenceEqual(Heroes))
                throw new InvalidOperationException("Hero catalog changed; curriculum mapping must be reviewed");
            var state = game.Engine.State;
            if (game.WrapperSteps != 0 || game.Submissions != 0 || state.GameOver || state.Round != 1 ||
                state.Players.Count != 2 || state.Players.Any(p => p.CharacterId != null) ||
                game.Selected.Count != 0 || game.SelectionTrace.Count != 0 || game.Actor != 1 ||
                game.Engine.PendingInput?.Decision?.Context != "soi.herodraft")
                throw new InvalidOperationException("Curriculum applies only to an untouched two-player initial draft");
            var result = new Setup { EngineSeed = engineSeed, Plan = plan };
            if (!plan.Forced) return result; // exactly untouched, including counters/RNG
            ulong rngState = state.Rng.State, rngIncrement = state.Rng.Inc;
            foreach (int seat in new[] { 1, 0 })
            {
                var request = game.Engine.PendingInput?.Decision;
                string hero = seat == 0 ? plan.Seat0 : plan.Seat1;
                if (game.Actor != seat || request?.Context != "soi.herodraft" || request.Min != 1 || request.Max != 1)
                    throw new InvalidOperationException("Unexpected initial draft protocol");
                int slot = -1, optionId = -1;
                for (int index = 0; index < game.VisibleCount; index++)
                {
                    var candidate = game.Visible(index);
                    if (candidate.Kind != 12 || candidate.Option == null || candidate.Option.Disabled || candidate.Option.DefId != hero)
                        continue;
                    if (slot >= 0) throw new InvalidOperationException("Duplicate legal hero candidate");
                    slot = index; optionId = candidate.Option.Id;
                }
                if (slot < 0) throw new InvalidOperationException("Requested forced hero is not legal");
                game.Step(slot); // normal validation, effects, relic setup, event flow
                result.Prefix.Add(new DraftStep { Seat = seat, Hero = hero, CandidateSlot = slot, OptionId = optionId });
            }
            if (state.Rng.State != rngState || state.Rng.Inc != rngIncrement)
                throw new InvalidOperationException("Hero setup unexpectedly consumed engine RNG; review required");
            if (state.Players[0].CharacterId != plan.Seat0 || state.Players[1].CharacterId != plan.Seat1 ||
                game.Actor != 0 || game.Engine.PendingInput.Decision != null || state.GameOver)
                throw new InvalidOperationException("Forced draft did not reach expected ordinary starting priority");
            result.SetupWrapperSteps = game.WrapperSteps;
            result.SetupEngineSubmissions = game.Submissions;
            if (result.SetupWrapperSteps != 2 || result.SetupEngineSubmissions != 2 ||
                game.SelectionTrace.Count != 0 || game.Selected.Count != 0)
                throw new InvalidOperationException("Unexpected draft setup counters or stale staging trace");
            return result;
        }
    }
}
