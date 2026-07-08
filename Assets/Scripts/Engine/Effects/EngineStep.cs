using Pascension.Engine.Decisions;

namespace Pascension.Engine.Effects
{
    /// <summary>Yield instruction returned by effect iterators.</summary>
    public readonly struct EngineStep
    {
        public readonly DecisionRequest Decision;

        private EngineStep(DecisionRequest decision) => Decision = decision;

        /// <summary>Continue resolving (useful as a checkpoint after mutations).</summary>
        public static EngineStep Continue => default;

        /// <summary>Pause resolution until the given player answers.</summary>
        public static EngineStep AwaitDecision(DecisionRequest request) => new(request);

        public bool IsAwait => Decision != null;
    }
}
