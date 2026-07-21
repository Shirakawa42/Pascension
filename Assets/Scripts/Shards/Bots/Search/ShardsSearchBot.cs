using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Serialization;
using Shards.Engine;

namespace Shards.Bots
{
    /// <summary>The strong SoI seat: SO-ISMCTS at priority points, tuned-model answers
    /// at decision points (a fresh tree per priority decision; decision-point search
    /// lands with the shadow/suffix-replay milestone). Fair by construction — every
    /// state the search inspects passed through the determinizer; the live engine is
    /// only touched to Fork.

    /// Deterministic given (seed, engine state, config) in Iterations mode.</summary>
    public sealed class ShardsSearchBot : IBotAgent
    {
        private readonly ShardsEngine _engine;
        private readonly ShardsValueModel _model;
        private readonly ShardsSearchConfig _config;
        private readonly ulong _seed;
        private ulong _searches;

        public string Descriptor =>
            $"ismcts-{WeightsName()}-" +
            (_config.Mode == ShardsSearchConfig.BudgetMode.Iterations
                ? _config.Iterations + "it"
                : _config.WallClockSeconds + "s");

        public int LastIterations { get; private set; }

        public ShardsSearchBot(ulong seed, ShardsEngine engine,
            ShardsSearchConfig config = null, ShardsValueModel model = null)
        {
            _seed = seed;
            _engine = engine;
            _config = config ?? ShardsSearchConfig.ForSims(200);
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

            var search = new ShardsIsmcts(_engine, pending.PlayerIndex, _model, _config,
                _seed ^ (++_searches * 0x9E3779B97F4A7C15UL));
            var action = search.Search();
            LastIterations = search.IterationsRun;
            return action;
        }

        private static string WeightsName()
        {
            foreach (var field in typeof(ShardsEvalWeights).GetFields())
                if (field.FieldType == typeof(double[]) &&
                    ReferenceEquals(field.GetValue(null), ShardsEvalWeights.Current))
                    return field.Name;
            return "custom";
        }
    }
}
