using Pascension.Engine.Cards;
using Pascension.Engine.Core;
using Pascension.Engine.Effects;

namespace Pascension.Content
{
    /// <summary>"The first N cards you buy each turn cost X less" (Merchant Stall, Haggle).</summary>
    public sealed class FirstBuysDiscount : IBuyCostModifier
    {
        private readonly int _amount;
        private readonly int _buysCovered;

        public FirstBuysDiscount(int amount, int buysCovered = 1)
        {
            _amount = amount;
            _buysCovered = buysCovered;
        }

        public string Description => _buysCovered == 1
            ? $"The first card you buy each turn costs {_amount} less"
            : $"The first {_buysCovered} cards you buy each turn cost {_amount} less";

        public int CostDelta(GameState state, PlayerState buyer, CardDefinition card) =>
            buyer.BuysThisTurn < _buysCovered ? -_amount : 0;
    }

    /// <summary>"Whenever you gain XP, gain 1 more" (Philosopher's Stone) — gain-time bonus, cannot loop.</summary>
    public sealed class BonusXpPerGain : IXpGainModifier
    {
        private readonly int _bonus;

        public BonusXpPerGain(int bonus) => _bonus = bonus;

        public string Description => $"Whenever you gain XP, gain {_bonus} more";

        public int Bonus(GameState state, PlayerState player, int baseAmount) => _bonus;
    }

    /// <summary>Arcane Library: draw up to N at end of turn instead of the normal hand size.</summary>
    public sealed class DrawCountOverride : IDrawCountModifier
    {
        private readonly int _count;

        public DrawCountOverride(int count) => _count = count;

        public string Description => $"At the end of your turn, draw {_count} cards instead";

        public int ModifyDrawCount(GameState state, PlayerState player, int current) =>
            _count > current ? _count : current;
    }
}
