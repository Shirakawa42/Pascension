using System.Collections.Generic;
using Pascension.Engine.Heroes;

namespace Pascension.Game.View
{
    /// <summary>Hero text helpers shared by the player sheet and the opponent detail modal.</summary>
    public static class HeroTextUtil
    {
        /// <summary>All hero passives; not-yet-unlocked ones are prefixed with their level.</summary>
        public static string PassiveSummary(HeroDefinition hero, int level)
        {
            if (hero == null) return "";
            var parts = new List<string>();
            foreach (var (minLevel, ability) in hero.PassiveStatics)
                parts.Add(Describe(ability.Description, minLevel, level));
            foreach (var (minLevel, ability) in hero.PassiveTriggers)
                parts.Add(Describe(ability.Description, minLevel, level));
            return parts.Count == 0 ? "" : "Passive — " + string.Join("  ·  ", parts);
        }

        private static string Describe(string description, int minLevel, int level) =>
            level >= minLevel ? description : $"(unlocks L{minLevel}) {description}";
    }
}
