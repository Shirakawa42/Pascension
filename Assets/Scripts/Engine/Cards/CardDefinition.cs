using System.Collections.Generic;
using Pascension.Engine.Core;
using Pascension.Engine.Effects;
using Pascension.Engine.Targeting;

namespace Pascension.Engine.Cards
{
    /// <summary>
    /// Immutable description of a card. One instance per card id, registered in
    /// <see cref="CardDatabase"/>; runtime copies are <see cref="CardInstance"/>s.
    /// Authored via the Content assembly's CardBuilder.
    /// </summary>
    public sealed class CardDefinition
    {
        public string Id;
        public string Name;
        public CardTier Tier;
        public CardType Type;
        /// <summary>AP cost to buy from the market. Monsters have no cost.</summary>
        public int Cost;
        public EquipSlot Slot = EquipSlot.None;

        /// <summary>Monster base HP (before continuous modifiers). 0 for non-monsters.</summary>
        public int MonsterHp;
        /// <summary>Resolved for the killer when the monster dies.</summary>
        public IEffect MonsterReward;

        /// <summary>Resolved when the card is played as a spell (Action/Instant cards).</summary>
        public IEffect SpellEffect;
        /// <summary>Target required to play this card as a spell.</summary>
        public TargetSpec SpellTarget;
        /// <summary>Exile instead of going to PlayedThisTurn after resolving (Time Warp).</summary>
        public bool ExileAfterResolve;
        /// <summary>Mana-ability rule: this card's only effect is gaining AP — playing it
        /// bypasses the stack entirely and cannot be responded to (like MTG mana abilities).</summary>
        public bool IsManaAbility;

        public List<Keyword> Keywords = new();
        public List<ActivatedAbility> ActivatedAbilities = new();
        public List<TriggeredAbility> TriggeredAbilities = new();
        public List<IStaticAbility> StaticAbilities = new();

        public string RulesText = "";
        /// <summary>Subject part of the Anima prompt — see the card-art skill.</summary>
        public string ArtPrompt = "";

        public bool IsMonster => Type == CardType.Monster;
        public bool IsPermanent => Type == CardType.Equipment || Type == CardType.Relic;
        public bool HasKeyword(Keyword k) => Keywords.Contains(k);

        /// <summary>Display type line, honoring the GDD's "nothing" name for Action cards.</summary>
        public string TypeLine => Type switch
        {
            CardType.Action => "Nothing",
            CardType.Equipment => $"Equipment — {Slot}",
            CardType.Monster => "Monster",
            CardType.Relic => "Relic",
            CardType.Instant => "Instant",
            _ => Type.ToString()
        };
    }
}
