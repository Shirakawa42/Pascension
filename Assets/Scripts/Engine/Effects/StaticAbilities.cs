using Pascension.Engine.Cards;
using Pascension.Engine.Core;

namespace Pascension.Engine.Effects
{
    /// <summary>
    /// Static abilities are always-on rule modifiers carried by permanents in play
    /// (equipment/relics) or by hero passives. The engine queries them at the moment
    /// the relevant value is computed — they never write state themselves.
    /// Implementations must be stateless; per-turn bookkeeping lives on PlayerState.
    /// </summary>
    public interface IStaticAbility
    {
        string Description { get; }
    }

    /// <summary>Modifies the AP cost of buying a card (e.g. Merchant Stall, Haggle).</summary>
    public interface IBuyCostModifier : IStaticAbility
    {
        int CostDelta(GameState state, PlayerState buyer, CardDefinition card);
    }

    /// <summary>Adds to XP gains (e.g. Philosopher's Stone "whenever you gain XP, gain 1 more" —
    /// implemented as a gain-time bonus so it cannot re-trigger itself).</summary>
    public interface IXpGainModifier : IStaticAbility
    {
        int Bonus(GameState state, PlayerState player, int baseAmount);
    }

    /// <summary>Adds to damage granted by cards (e.g. Ignis's Kindle: first damage card each turn).</summary>
    public interface IDamageGainModifier : IStaticAbility
    {
        int Bonus(GameState state, PlayerState player, int baseAmount, bool fromCardSpell);
    }

    /// <summary>Changes how many cards are drawn at end of turn (e.g. Arcane Library → 6).</summary>
    public interface IDrawCountModifier : IStaticAbility
    {
        int ModifyDrawCount(GameState state, PlayerState player, int current);
    }

    /// <summary>Grants bonus steps on movement (e.g. Wren's Pathfinder: first move each turn +1).</summary>
    public interface IMoveBonusModifier : IStaticAbility
    {
        int BonusSteps(GameState state, PlayerState player, int paidSteps);
    }

    /// <summary>Changes how many inn reward options may be picked (e.g. Wren's Trailblazer → 2).</summary>
    public interface IInnChoiceCountModifier : IStaticAbility
    {
        int ModifyChoiceCount(GameState state, PlayerState player, int current);
    }

    /// <summary>Allows keeping cards at cleanup instead of discarding (Nyx's Up the Sleeve).</summary>
    public interface ICleanupKeepModifier : IStaticAbility
    {
        int MaxKeepCount(GameState state, PlayerState player, int current);
    }
}
