namespace Pascension.Engine.Events
{
    /// <summary>A decision was requested from a player (game-neutral).</summary>
    public sealed class DecisionRequestedEvent : GameEvent
    {
        public int PlayerIndex;
        public int DecisionId;
        public string Title;
    }

    /// <summary>A pending decision was answered (game-neutral).</summary>
    public sealed class DecisionMadeEvent : GameEvent
    {
        public int PlayerIndex;
        public int DecisionId;
    }
}
