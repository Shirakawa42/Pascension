namespace Pascension.Game
{
    /// <summary>Which algorithmic agent occupies a Pascension bot seat. SoI uses its
    /// own ranked ladder instead (MatchSetup.SoiBotKind → ShardsBotRanks).</summary>
    public enum BotKind
    {
        Heuristic,
        Random
    }
}
