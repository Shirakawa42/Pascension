namespace Pascension.Engine.Core
{
    /// <summary>Tunable rule constants. Balance knobs live here first — see the playtesting skill.</summary>
    public sealed class GameRules
    {
        public int HandSize = 5;
        public int MaxLevel = 10;

        /// <summary>XP required to advance FROM level (index+1). Cumulative 34 to reach level 10.</summary>
        public int[] XpToNextLevel = { 2, 2, 3, 3, 4, 4, 5, 5, 6 };

        public int AdvancedLevelRequirement = 4;
        public int EliteLevelRequirement = 8;

        public int BoardSteps = 50;
        public int[] InnSteps = { 10, 20, 30, 40 };

        public int BossHp = 20;
        public int MarketRowSize = 5;

        /// <summary>Response-window timer, seconds. Enforced by the host (GameHost), not the engine.</summary>
        public int ResponseTimerSeconds = 25;

        /// <summary>Round-1 compensation: [playerIndex] → (bonus AP, bonus opening cards).</summary>
        public (int ap, int cards)[] StaggeredStart = { (0, 0), (1, 0), (1, 1), (1, 1) };

        public int XpFromLevel(int level) => level >= 1 && level <= XpToNextLevel.Length ? XpToNextLevel[level - 1] : int.MaxValue;
    }
}
