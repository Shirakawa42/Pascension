namespace Pascension.Game
{
    /// <summary>Which algorithmic agent occupies a bot seat.</summary>
    public enum BotKind
    {
        Heuristic,
        Random,
        /// <summary>SoI: tuned value-model argmax (instant, strong).</summary>
        Greedy,
        /// <summary>SoI: ISMCTS search, ~1s of thinking per decision (strongest).</summary>
        Strong
    }
}
