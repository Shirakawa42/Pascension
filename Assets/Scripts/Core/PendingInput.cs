using System.Collections.Generic;
using Pascension.Engine.Actions;
using Pascension.Engine.Decisions;

namespace Pascension.Engine.Core
{
    public enum PendingInputKind
    {
        Priority,
        Decision
    }

    /// <summary>
    /// The single input the engine is waiting for. Priority inputs carry the legal
    /// actions (drives UI affordances, bots, and the LLM prompt); decision inputs
    /// carry the pending DecisionRequest.
    /// </summary>
    public sealed class PendingInput
    {
        public PendingInputKind Kind;
        public int PlayerIndex;
        public List<PlayerAction> LegalActions = new();
        public DecisionRequest Decision;

        public static PendingInput Priority(int player, List<PlayerAction> legal) =>
            new() { Kind = PendingInputKind.Priority, PlayerIndex = player, LegalActions = legal };

        public static PendingInput ForDecision(DecisionRequest request) =>
            new() { Kind = PendingInputKind.Decision, PlayerIndex = request.PlayerIndex, Decision = request };
    }
}
