using Pascension.Core;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Pascension.Engine.Serialization;
using Shards.Engine;

namespace Shards.Bots
{
    /// <summary>The strong SoI seat: SO-ISMCTS at priority points; follow-up decisions
    /// in the chosen action's chain (damage splits, warps, reveals) are served from the
    /// searched subtree via the plan cursor, anything unplanned falls back to the tuned
    /// model. Fair by construction — every state the search inspects passed through the
    /// determinizer; the live engine is only touched to Fork.
    /// Deterministic given (seed, engine state, config) in Iterations mode.</summary>
    public sealed class ShardsSearchBot : IBotAgent
    {
        private readonly ShardsEngine _engine;
        private readonly ShardsValueModel _model;
        private readonly ShardsSearchConfig _config;
        private readonly IShardsValueEvaluator _evaluator;
        private readonly ulong _seed;
        private ulong _searches;
        private ShardsIsmcts.PlanCursor _plan;

        public string Descriptor =>
            $"ismcts-{WeightsName()}-" +
            (_config.Mode == ShardsSearchConfig.BudgetMode.Iterations
                ? _config.Iterations + "it"
                : _config.WallClockSeconds + "s");

        public int LastIterations { get; private set; }

        public ShardsSearchBot(ulong seed, ShardsEngine engine,
            ShardsSearchConfig config = null, ShardsValueModel model = null,
            IShardsValueEvaluator evaluator = null)
        {
            _seed = seed;
            _engine = engine;
            _config = config ?? ShardsSearchConfig.ForSims(200);
            _model = model ?? new ShardsValueModel();
            _evaluator = evaluator ?? (_config.RolloutEndTurns >= 0
                ? ShardsNetWeights.Available
                    ? (IShardsValueEvaluator)ShardsNeuralEval.LoadCurrent()
                    : new ShardsBaselineEvaluator(_model)
                : null);
        }

        public PlayerAction Choose(PendingSnap pending, SnapshotBase view)
        {
            if (pending == null) return null;
            if (pending.Kind == PendingInputKind.Decision)
            {
                var answer = new DecisionAnswer { DecisionId = pending.Decision.Id };
                if (ShardsIsmcts.TryPlannedAnswer(ref _plan, pending.Decision, out var planned))
                    answer.ChosenOptionIds.AddRange(planned);
                else
                    answer = _model.ChooseAnswer(_engine, pending.Decision);
                return new SubmitDecisionAction { PlayerIndex = pending.PlayerIndex, Answer = answer };
            }

            ulong searchSeed = _seed ^ (++_searches * 0x9E3779B97F4A7C15UL);
            if (_config.RootWorkers > 1)
            {
                var parallelAction = ShardsRootParallelSearch.Search(_engine, pending.PlayerIndex,
                    _model, _config, searchSeed, _evaluator, out _plan, out int iterations);
                LastIterations = iterations;
                return parallelAction;
            }

            var search = new ShardsIsmcts(_engine, pending.PlayerIndex, _model, _config,
                searchSeed, _evaluator);
            var action = search.Search();
            LastIterations = search.IterationsRun;
            _plan = search.LastChosenPlan;
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
