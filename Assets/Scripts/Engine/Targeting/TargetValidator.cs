using System.Collections.Generic;
using Pascension.Engine.Core;
using Pascension.Engine.Stack;

namespace Pascension.Engine.Targeting
{
    /// <summary>Enumerates legal targets when playing, and re-validates them at resolution.</summary>
    public static class TargetValidator
    {
        /// <summary>All currently legal targets for a spec. Excludes the given stack item (a spell can't target itself).</summary>
        public static List<TargetRef> BuildOptions(GameState state, int controllerIndex, TargetSpec spec, int excludeStackItemId = -1)
        {
            var options = new List<TargetRef>();
            switch (spec.Kind)
            {
                case TargetSpecKind.Monster:
                    foreach (var (tier, slot, _) in state.Market.Monsters())
                        options.Add(TargetRef.Monster((int)tier, slot));
                    break;

                case TargetSpecKind.SpellOnStack:
                    foreach (var item in state.Stack.Items)
                        if (item.Kind == StackItemKind.Spell && !item.Countered && item.Id != excludeStackItemId)
                            options.Add(TargetRef.Spell(item.Id));
                    break;

                case TargetSpecKind.Opponent:
                    foreach (var p in state.Players)
                        if (p.Index != controllerIndex && !p.Conceded)
                            options.Add(TargetRef.PlayerAt(p.Index));
                    break;

                case TargetSpecKind.RelicOrEquipment:
                    foreach (var p in state.Players)
                        foreach (var card in p.Permanents())
                            options.Add(TargetRef.CardById(card.InstanceId));
                    break;
            }
            return options;
        }

        /// <summary>Is this target still legal at resolution time?</summary>
        public static bool IsStillValid(GameState state, TargetRef target, TargetSpec spec)
        {
            switch (spec.Kind)
            {
                case TargetSpecKind.Monster:
                    if (target.Kind != TargetKind.MonsterSlot) return false;
                    var card = state.Market.SlotCard((CardTier)target.A, target.B);
                    return card != null && card.Def.IsMonster;

                case TargetSpecKind.SpellOnStack:
                    if (target.Kind != TargetKind.StackItem) return false;
                    var item = state.Stack.Find(target.A);
                    return item != null && item.Kind == StackItemKind.Spell && !item.Countered;

                case TargetSpecKind.Opponent:
                    return target.Kind == TargetKind.Player && target.A >= 0 && target.A < state.Players.Count && !state.Players[target.A].Conceded;

                case TargetSpecKind.RelicOrEquipment:
                    if (target.Kind != TargetKind.Card) return false;
                    var c = state.FindCard(target.A);
                    return c != null && (c.Zone == ZoneType.Equipment || c.Zone == ZoneType.Relics);

                default:
                    return false;
            }
        }
    }
}
