using System;
using Pascension.Engine.Core;
using Pascension.Engine.Targeting;

namespace Pascension.Engine.Effects
{
    /// <summary>
    /// "Cost: do Y." Paying the cost puts the ability on the stack (abilities are not
    /// counterable by Counterspell). Used by equipment taps and hero actives/ultimates.
    /// </summary>
    public sealed class ActivatedAbility
    {
        public string Description;
        public bool TapCost;
        public int ApCost;
        /// <summary>Hero actives/ultimates: usable once per turn.</summary>
        public bool OncePerTurn;
        /// <summary>Optional target chosen when the ability is activated.</summary>
        public TargetSpec Target;
        /// <summary>Extra legality condition (e.g. Trade needs a card in hand to discard).</summary>
        public Func<GameState, PlayerState, bool> UsableIf;
        /// <summary>Mana-ability rule: pure-AP ability — resolves immediately, never stacks,
        /// cannot be responded to (Cloth Armor / Tower Shield / Dragonscale taps).</summary>
        public bool ManaAbility;
        public IEffect Effect;

        public ActivatedAbility(string description, IEffect effect, TargetSpec target = null)
        {
            Description = description;
            Effect = effect;
            Target = target;
        }
    }
}
