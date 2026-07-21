using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Serialization;
using Shards.Engine;

namespace Shards.Bots
{
    /// <summary>Argmax player over the tuned ShardsValueModel — the CMA-ES tuning
    /// workhorse and the cheap strong-ish seat for mass sims. Deterministic given the
    /// engine state and weights (the seed only feeds future stochastic variants).</summary>
    public sealed class ShardsGreedyEvalBot : IBotAgent
    {
        private readonly ShardsEngine _engine;
        private readonly ShardsValueModel _model;

        public ShardsGreedyEvalBot(ulong seed, ShardsEngine engine, ShardsValueModel model = null)
        {
            _ = seed;
            _engine = engine;
            _model = model ?? new ShardsValueModel();
        }

        public PlayerAction Choose(PendingSnap pending, SnapshotBase view)
        {
            if (pending == null) return null;
            if (pending.Kind == PendingInputKind.Decision)
                return new SubmitDecisionAction
                {
                    PlayerIndex = pending.PlayerIndex,
                    Answer = _model.ChooseAnswer(_engine, pending.Decision)
                };
            return _model.ChooseAction(_engine, pending.PlayerIndex);
        }
    }
}
