using System.Collections.Generic;
using Pascension.Engine.Decisions;
using Pascension.Engine.Events;

namespace Pascension.Engine.Effects
{
    public enum ResolutionStatus
    {
        Idle,
        AwaitingDecision
    }

    /// <summary>
    /// Runs effect iterators. Only one effect resolves at a time (sub-effects compose by
    /// iterating child effects inside the parent iterator). When an effect yields
    /// AwaitDecision the whole engine pauses until that player answers.
    /// </summary>
    public sealed class ResolutionEngine
    {
        private IEnumerator<EngineStep> _current;
        private EffectContext _currentCtx;

        public DecisionRequest PendingDecision { get; private set; }
        public ResolutionStatus Status => PendingDecision != null ? ResolutionStatus.AwaitingDecision : ResolutionStatus.Idle;
        public bool IsBusy => _current != null;

        /// <summary>Begin resolving an effect. Must not already be busy.</summary>
        public void Begin(IEffect effect, EffectContext ctx)
        {
            _current = effect.Resolve(ctx).GetEnumerator();
            _currentCtx = ctx;
            Advance();
        }

        /// <summary>Deliver the pending decision's answer and continue resolving.</summary>
        public void Resume(DecisionAnswer answer)
        {
            _currentCtx.Answer = answer;
            var decisionPlayer = PendingDecision.PlayerIndex;
            var decisionId = PendingDecision.Id;
            PendingDecision = null;
            _currentCtx.Api.Emit(new DecisionMadeEvent { PlayerIndex = decisionPlayer, DecisionId = decisionId });
            Advance();
        }

        private void Advance()
        {
            while (_current != null)
            {
                if (!_current.MoveNext())
                {
                    _current = null;
                    _currentCtx = null;
                    return;
                }
                var step = _current.Current;
                if (step.IsAwait)
                {
                    PendingDecision = step.Decision;
                    _currentCtx.Api.Emit(new DecisionRequestedEvent
                    {
                        PlayerIndex = step.Decision.PlayerIndex,
                        DecisionId = step.Decision.Id,
                        Title = step.Decision.Title
                    });
                    return;
                }
            }
        }
    }
}
