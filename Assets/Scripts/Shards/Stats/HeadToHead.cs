using System.Collections.Generic;

namespace Shards.Stats
{
    /// <summary>The named opponent's side of the filtered games: what they played and
    /// how it went for ME (Wins on these aggregates = MY wins).</summary>
    public sealed class HeadToHead
    {
        public List<HeroAgg> TheirHeroes = new();
        public List<CardAgg> TheirCards = new();
    }
}
