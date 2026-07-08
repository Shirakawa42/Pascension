using System;
using Pascension.Engine.Cards;
using Pascension.Engine.Core;
using Pascension.Engine.Effects;
using Pascension.Engine.Events;
using Pascension.Engine.Targeting;

namespace Pascension.Content
{
    /// <summary>
    /// Fluent card authoring. Every card is one builder chain ending in Register().
    /// See .claude/skills/cards/SKILL.md for the registry and the add-a-card checklist.
    /// </summary>
    public sealed class CardBuilder
    {
        private readonly CardDefinition _def = new();

        private CardBuilder(string id, string name)
        {
            _def.Id = id;
            _def.Name = name;
        }

        public static CardBuilder Card(string id, string name) => new(id, name);

        public CardBuilder DefaultTier() { _def.Tier = CardTier.Default; return this; }
        public CardBuilder Basic() { _def.Tier = CardTier.Basic; return this; }
        public CardBuilder Advanced() { _def.Tier = CardTier.Advanced; return this; }
        public CardBuilder Elite() { _def.Tier = CardTier.Elite; return this; }
        public CardBuilder BossTier() { _def.Tier = CardTier.Boss; return this; }

        public CardBuilder Cost(int cost) { _def.Cost = cost; return this; }

        public CardBuilder Action() { _def.Type = CardType.Action; return this; }
        public CardBuilder Instant() { _def.Type = CardType.Instant; return this; }
        public CardBuilder Relic() { _def.Type = CardType.Relic; return this; }

        public CardBuilder Equipment(EquipSlot slot)
        {
            _def.Type = CardType.Equipment;
            _def.Slot = slot;
            return this;
        }

        public CardBuilder Monster(int hp, IEffect reward)
        {
            _def.Type = CardType.Monster;
            _def.MonsterHp = hp;
            _def.MonsterReward = reward;
            return this;
        }

        public CardBuilder OnResolve(IEffect effect) { _def.SpellEffect = effect; return this; }
        public CardBuilder Target(TargetSpec spec) { _def.SpellTarget = spec; return this; }
        public CardBuilder Keyword(Keyword keyword) { _def.Keywords.Add(keyword); return this; }
        public CardBuilder ExilesAfterResolve() { _def.ExileAfterResolve = true; return this; }

        /// <summary>Mana-ability rule: the card's ONLY effect is gaining AP — it bypasses the
        /// stack and cannot be responded to. See the cards skill for the qualifying list.</summary>
        public CardBuilder ManaAbility() { _def.IsManaAbility = true; return this; }

        public CardBuilder TapAbility(string description, IEffect effect, TargetSpec target = null, bool manaAbility = false)
        {
            _def.ActivatedAbilities.Add(new ActivatedAbility(description, effect)
            {
                TapCost = true,
                Target = target,
                ManaAbility = manaAbility
            });
            return this;
        }

        public CardBuilder Triggered(string description, Func<GameEvent, TriggerSource, bool> filter, IEffect effect)
        {
            _def.TriggeredAbilities.Add(new TriggeredAbility(description, filter, effect));
            return this;
        }

        public CardBuilder Static(IStaticAbility ability)
        {
            _def.StaticAbilities.Add(ability);
            return this;
        }

        public CardBuilder Text(string rulesText) { _def.RulesText = rulesText; return this; }
        public CardBuilder Art(string artPrompt) { _def.ArtPrompt = artPrompt; return this; }

        public CardDefinition Register()
        {
            CardDatabase.Register(_def);
            return _def;
        }
    }

    /// <summary>Reusable trigger filters for card/hero authoring.</summary>
    public static class When
    {
        public static Func<GameEvent, TriggerSource, bool> YourTurnStarts =>
            (e, src) => e is TurnStartedEvent t && t.PlayerIndex == src.ControllerIndex;

        public static Func<GameEvent, TriggerSource, bool> YourMainPhaseStarts =>
            (e, src) => e is PhaseChangedEvent p && p.Phase == Phase.Main && p.PlayerIndex == src.ControllerIndex;

        public static Func<GameEvent, TriggerSource, bool> YouKillAMonster =>
            (e, src) => e is MonsterDiedEvent m && m.KillerIndex == src.ControllerIndex;
    }
}
