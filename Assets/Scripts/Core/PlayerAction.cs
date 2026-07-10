using Pascension.Engine.Decisions;

namespace Pascension.Engine.Actions
{
    /// <summary>An intent submitted by a player (human UI, bot, or network client).</summary>
    public abstract class PlayerAction
    {
        public int PlayerIndex;

        public abstract string Describe();
    }

    /// <summary>
    /// Marker for the action that is always safe to take on a seat's behalf (pass /
    /// end turn) — used by auto-clients and disconnect timeouts, game-agnostically.
    /// </summary>
    public interface ISafeDefaultAction { }

    public sealed class PassPriorityAction : PlayerAction, ISafeDefaultAction
    {
        public override string Describe() => "Pass";
    }

    public sealed class SubmitDecisionAction : PlayerAction
    {
        public DecisionAnswer Answer;

        public override string Describe() => $"Answer decision #{Answer?.DecisionId}";
    }

    public sealed class ConcedeAction : PlayerAction
    {
        public override string Describe() => "Concede";
    }
}
