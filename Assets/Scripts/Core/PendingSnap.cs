using System.Collections.Generic;
using Pascension.Engine.Actions;
using Pascension.Engine.Core;
using Pascension.Engine.Decisions;

namespace Pascension.Engine.Serialization
{
    /// <summary>
    /// The pending-input surface a seat sees: what kind of input the engine awaits,
    /// whose it is, and (only for that player) the legal actions / decision payload.
    /// Game-agnostic — every game's engine produces these.
    /// </summary>
    public sealed class PendingSnap
    {
        public PendingInputKind Kind;
        public int PlayerIndex;
        /// <summary>Populated only when the viewer is the pending player.</summary>
        public List<PlayerAction> LegalActions;
        /// <summary>Populated only when the viewer is the pending player.</summary>
        public DecisionRequest Decision;
    }
}
