using System.Collections.Generic;
using Pascension.Engine.Cards;
using Pascension.Engine.Core;
using Pascension.Engine.Targeting;

namespace Pascension.Engine.Actions
{
    /// <summary>
    /// Enumerates every action a player may legally take while holding priority.
    /// Drives bot choices, the LLM action menu, UI affordances, and auto-pass
    /// (a player whose only option is Pass can be fast-passed by the host).
    /// </summary>
    public static class LegalActionGenerator
    {
        public static List<PlayerAction> Generate(GameApi api, int playerIndex)
        {
            var state = api.State;
            var list = new List<PlayerAction>();
            var p = state.Players[playerIndex];
            if (p.Conceded || state.GameOver)
            {
                list.Add(new PassPriorityAction { PlayerIndex = playerIndex });
                return list;
            }

            bool isTurn = playerIndex == state.TurnPlayerIndex;
            bool stackEmpty = state.Stack.IsEmpty;
            bool sorceryTiming = isTurn && state.Phase == Phase.Main && stackEmpty;

            // --- Play cards from hand ---
            foreach (var card in p.Hand)
            {
                var def = card.Def;
                bool timingOk = def.Type == CardType.Instant ? true : sorceryTiming;
                if (!timingOk || def.IsMonster) continue;
                if (def.SpellTarget != null &&
                    TargetValidator.BuildOptions(state, playerIndex, def.SpellTarget).Count == 0)
                    continue;
                list.Add(new PlayCardAction { PlayerIndex = playerIndex, CardInstanceId = card.InstanceId });
            }

            if (sorceryTiming)
            {
                // --- Buy from the market ---
                for (int t = 0; t < Market.Tiers; t++)
                {
                    var tier = Market.TierFromIndex(t);
                    if (tier == CardTier.Advanced && p.Level < state.Rules.AdvancedLevelRequirement) continue;
                    if (tier == CardTier.Elite && p.Level < state.Rules.EliteLevelRequirement) continue;
                    var row = state.Market.Rows[t];
                    for (int s = 0; s < row.Length; s++)
                    {
                        var card = row[s];
                        if (card == null || card.Def.IsMonster) continue;
                        if (p.Ap >= api.GetBuyCost(p, card.Def))
                            list.Add(new BuyCardAction { PlayerIndex = playerIndex, TierIndex = t, SlotIndex = s });
                    }
                }

                // --- Move ---
                int maxSteps = p.Ap < state.Rules.BoardSteps - p.Position ? p.Ap : state.Rules.BoardSteps - p.Position;
                for (int steps = 1; steps <= maxSteps; steps++)
                    list.Add(new MoveStepsAction { PlayerIndex = playerIndex, Steps = steps });
            }

            // --- Assign damage (own turn, instant speed, lethal amounts only) ---
            if (isTurn && state.Phase != Phase.Untap && p.DamagePool > 0)
            {
                foreach (var (tier, slot, monster) in state.Market.Monsters())
                {
                    int remaining = api.EffectiveMonsterHp(monster) - monster.MarkedDamage;
                    if (remaining > 0 && p.DamagePool >= remaining)
                        list.Add(new AssignDamageAction { PlayerIndex = playerIndex, Target = TargetRef.Monster((int)tier, slot), Amount = remaining });
                }
                if (state.Boss != null && p.Position == state.Rules.BoardSteps)
                {
                    int remaining = state.Rules.BossHp - state.Boss.MarkedDamage;
                    if (remaining > 0 && p.DamagePool >= remaining)
                        list.Add(new AssignDamageAction { PlayerIndex = playerIndex, Target = TargetRef.TheBoss(), Amount = remaining });
                }
            }

            // --- Activated abilities on permanents (instant speed) ---
            foreach (var source in p.Permanents())
            {
                var abilities = source.Def.ActivatedAbilities;
                for (int i = 0; i < abilities.Count; i++)
                {
                    var ability = abilities[i];
                    if (ability.TapCost && source.Tapped) continue;
                    if (p.Ap < ability.ApCost) continue;
                    if (ability.Target != null &&
                        TargetValidator.BuildOptions(state, playerIndex, ability.Target).Count == 0)
                        continue;
                    list.Add(new ActivateAbilityAction { PlayerIndex = playerIndex, SourceInstanceId = source.InstanceId, AbilityIndex = i });
                }
            }

            // --- Hero active / ultimate (own turn only) ---
            if (isTurn && state.Phase != Phase.Untap)
            {
                var hero = p.Hero;
                if (hero.Active != null && p.Level >= hero.ActiveUnlockLevel && !p.HeroActiveUsedThisTurn &&
                    p.Ap >= hero.Active.ApCost && HeroAbilityUsable(api, p, hero.Active))
                    list.Add(new UseHeroAbilityAction { PlayerIndex = playerIndex, Ultimate = false });
                if (hero.Ultimate != null && p.Level >= hero.UltimateUnlockLevel && !p.HeroUltimateUsedThisTurn &&
                    p.Ap >= hero.Ultimate.ApCost && HeroAbilityUsable(api, p, hero.Ultimate))
                    list.Add(new UseHeroAbilityAction { PlayerIndex = playerIndex, Ultimate = true });
            }

            list.Add(new PassPriorityAction { PlayerIndex = playerIndex });
            return list;
        }

        private static bool HeroAbilityUsable(GameApi api, PlayerState p, Effects.ActivatedAbility ability)
        {
            if (ability.Target != null &&
                TargetValidator.BuildOptions(api.State, p.Index, ability.Target).Count == 0)
                return false;
            if (ability.UsableIf != null && !ability.UsableIf(api.State, p))
                return false;
            return true;
        }

        /// <summary>True when the player's only legal action is passing (candidate for fast-pass).</summary>
        public static bool OnlyPassAvailable(List<PlayerAction> legal) =>
            legal.Count == 1 && legal[0] is PassPriorityAction;
    }
}
