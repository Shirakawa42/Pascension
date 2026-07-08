using System;

namespace Pascension.Game
{
    /// <summary>One bot opponent as configured in the menu.</summary>
    [Serializable]
    public sealed class OpponentSetup
    {
        public string HeroId = "wren";
        public BotKind Bot = BotKind.Heuristic;

        public OpponentSetup() { }

        public OpponentSetup(string heroId, BotKind bot)
        {
            HeroId = heroId;
            Bot = bot;
        }
    }
}
