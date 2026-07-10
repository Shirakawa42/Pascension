using System.Collections.Generic;
using Pascension.Engine.Decisions;

namespace Shards.Engine
{
    /// <summary>
    /// Shards' effect-as-iterator (same pattern as Pascension's, typed on this game's
    /// context): yield steps; AwaitDecision pauses the engine for the given player.
    /// </summary>
    public interface IShardsEffect
    {
        IEnumerable<ShardsStep> Resolve(ShardsContext ctx);
    }

    public sealed class ShardsStep
    {
        public DecisionRequest Decision;
        public static readonly ShardsStep Done = new();
        public static ShardsStep AwaitDecision(DecisionRequest request) => new() { Decision = request };
    }

    /// <summary>Per-resolution context: the engine API, controller, and the answer to the
    /// most recent AwaitDecision.</summary>
    public sealed class ShardsContext
    {
        public ShardsEngine Engine;
        public int ControllerIndex;
        public ShardsCard Source;
        public DecisionAnswer Answer;

        public ShardsPlayer Controller => Engine.State.Players[ControllerIndex];
    }

    public sealed class ShardsNullEffect : IShardsEffect
    {
        public static readonly ShardsNullEffect Instance = new();
        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx) { yield break; }
    }

    /// <summary>Run parts in order. Sequencing is rules-critical: a card's own mastery
    /// gain resolves BEFORE later threshold checks (the Fungal Hermit ruling).</summary>
    public sealed class ShardsComposite : IShardsEffect
    {
        private readonly IShardsEffect[] _parts;
        public ShardsComposite(params IShardsEffect[] parts) => _parts = parts;

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            foreach (var part in _parts)
                foreach (var step in part.Resolve(ctx))
                    yield return step;
        }
    }

    /// <summary>Mastery Threshold wrapper: the inner effect fires only if the controller's
    /// mastery is at least N AT THIS MOMENT of resolution (never retro-checked).</summary>
    public sealed class AtMastery : IShardsEffect
    {
        private readonly int _threshold;
        private readonly IShardsEffect _inner;

        public AtMastery(int threshold, IShardsEffect inner)
        {
            _threshold = threshold;
            _inner = inner;
        }

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            if (ctx.Controller.Mastery < _threshold) yield break;
            foreach (var step in _inner.Resolve(ctx))
                yield return step;
        }
    }

    /// <summary>Faction trigger wrapper: fires only if the controller played another card
    /// of the faction this turn (fast-played mercenaries count). TODO-VERIFY exact
    /// Unify/Dominion conditions from rules-notes (M4) — condition kind is data.</summary>
    public sealed class FactionTrigger : IShardsEffect
    {
        private readonly ShardsFaction _faction;
        private readonly int _required; // cards of the faction played this turn (excluding this one)
        private readonly IShardsEffect _inner;

        public FactionTrigger(ShardsFaction faction, int required, IShardsEffect inner)
        {
            _faction = faction;
            _required = required;
            _inner = inner;
        }

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            if (ctx.Controller.FactionPlays(_faction) - 1 < _required) yield break; // -1: itself
            foreach (var step in _inner.Resolve(ctx))
                yield return step;
        }
    }

    // ---------------- primitive effects (built from lambdas for content brevity) ----------------

    public sealed class Gain : IShardsEffect
    {
        public int Gems, Power, Mastery, Health, Draw;

        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx)
        {
            var e = ctx.Engine;
            int p = ctx.ControllerIndex;
            if (Gems != 0) e.GainGems(p, Gems);
            if (Power != 0) e.GainPower(p, Power);
            if (Mastery != 0) e.GainMastery(p, Mastery);
            if (Health != 0) e.GainHealth(p, Health);
            if (Draw != 0) e.DrawCards(p, Draw);
            yield break;
        }
    }

    /// <summary>Escape hatch for bespoke card logic without a dedicated class.</summary>
    public sealed class Custom : IShardsEffect
    {
        private readonly System.Func<ShardsContext, IEnumerable<ShardsStep>> _body;
        public Custom(System.Func<ShardsContext, IEnumerable<ShardsStep>> body) => _body = body;
        public IEnumerable<ShardsStep> Resolve(ShardsContext ctx) => _body(ctx);
    }
}
