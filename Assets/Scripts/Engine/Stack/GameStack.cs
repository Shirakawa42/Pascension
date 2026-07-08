using System.Collections.Generic;
using Pascension.Engine.Cards;
using Pascension.Engine.Effects;
using Pascension.Engine.Targeting;

namespace Pascension.Engine.Stack
{
    public enum StackItemKind
    {
        Spell,
        Ability,
        Trigger,
        DamageAssignment
    }

    /// <summary>
    /// Something waiting to resolve. Spells are counterable; abilities/triggers/damage
    /// assignments are not (MTG-compatible).
    /// </summary>
    public sealed class StackItem
    {
        public int Id;
        public StackItemKind Kind;
        public int ControllerIndex;
        /// <summary>The card being cast (spells) — physically in the Stack zone while here.</summary>
        public CardInstance SpellCard;
        /// <summary>The permanent whose ability this is (abilities/triggers).</summary>
        public CardInstance SourceCard;
        public IEffect Effect;
        public List<TargetRef> Targets = new();
        /// <summary>Spec the targets were chosen against — re-validated at resolution (fizzle check).</summary>
        public TargetSpec TargetSpec;
        /// <summary>Damage amount for DamageAssignment items.</summary>
        public int Amount;
        public bool Countered;
        /// <summary>Cast without paying costs (e.g. by Random Bullshit Go).</summary>
        public bool IsFree;
        /// <summary>Exile this spell's card after resolution regardless of its definition (spell copies).</summary>
        public bool ExileAfterResolve;
        public string Description = "";

        public bool IsCounterable => Kind == StackItemKind.Spell;
    }

    public sealed class GameStack
    {
        /// <summary>Bottom → top. Resolution pops from the end.</summary>
        public List<StackItem> Items = new();

        public bool IsEmpty => Items.Count == 0;
        public StackItem Top => Items.Count > 0 ? Items[^1] : null;

        public void Push(StackItem item) => Items.Add(item);

        public StackItem Pop()
        {
            var top = Items[^1];
            Items.RemoveAt(Items.Count - 1);
            return top;
        }

        public StackItem Find(int id) => Items.Find(i => i.Id == id);
    }
}
