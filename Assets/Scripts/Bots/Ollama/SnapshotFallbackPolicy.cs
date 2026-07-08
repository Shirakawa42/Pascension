using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;
using Pascension.Engine.Serialization;

namespace Pascension.Bots.Ollama
{
    /// <summary>
    /// Deterministic fallback used when the LLM fails (timeout, HTTP error, malformed or
    /// out-of-range answer). Works from the masked PendingSnap ALONE — async agents never
    /// see the engine. Priority: take the first useful proactive action (buy / assign
    /// damage / play), else pass. Decisions: mirror GameHost.DefaultActionFor (defaults,
    /// padded to Min, trimmed to Max).
    /// </summary>
    public static class SnapshotFallbackPolicy
    {
        /// <summary>A guaranteed-legal action for whatever input is pending.</summary>
        public static PlayerAction Choose(PendingSnap pending)
        {
            if (pending.Kind == PendingInputKind.Decision && pending.Decision != null)
                return ChooseDecision(pending.PlayerIndex, pending.Decision);
            return ChoosePriority(pending);
        }

        /// <summary>First legal BuyCard / AssignDamage / PlayCard, else Pass.</summary>
        public static PlayerAction ChoosePriority(PendingSnap pending)
        {
            var legal = pending.LegalActions;
            if (legal != null)
                foreach (var action in legal)
                    if (action is BuyCardAction || action is AssignDamageAction || action is PlayCardAction)
                    {
                        action.PlayerIndex = pending.PlayerIndex;
                        return action;
                    }
            return new PassPriorityAction { PlayerIndex = pending.PlayerIndex };
        }

        /// <summary>Default answer: DefaultOptionIds, padded up to Min with the first
        /// unchosen options, trimmed down to Max. Mirrors GameHost.DefaultActionFor.</summary>
        public static SubmitDecisionAction ChooseDecision(int playerIndex, DecisionRequest request)
        {
            var answer = new DecisionAnswer { DecisionId = request.Id };
            answer.ChosenOptionIds.AddRange(request.DefaultOptionIds);
            for (int i = 0; answer.ChosenOptionIds.Count < request.Min && i < request.Options.Count; i++)
                if (!answer.ChosenOptionIds.Contains(request.Options[i].Id))
                    answer.ChosenOptionIds.Add(request.Options[i].Id);
            while (answer.ChosenOptionIds.Count > request.Max)
                answer.ChosenOptionIds.RemoveAt(answer.ChosenOptionIds.Count - 1);
            return new SubmitDecisionAction { PlayerIndex = playerIndex, Answer = answer };
        }
    }
}
