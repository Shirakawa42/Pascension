using Pascension.Engine.Core;
using Pascension.Engine.Effects;
using Pascension.Engine.Effects.Common;
using Pascension.Engine.Targeting;
using static Pascension.Content.CardBuilder;

namespace Pascension.Content.Sets
{
    /// <summary>Basic pile (36 cards): see the cards skill for copies per card.</summary>
    public static class BasicCards
    {
        public static void Register()
        {
            Card("run", "Run").Basic().Cost(2).Action().ManaAbility()
                .OnResolve(new GainApEffect(2))
                .Text("+2 action points.")
                .Art("a young adventurer sprinting down a forest trail, cloak streaming behind, dust kicked up, dynamic low angle, midday sun through the trees")
                .Register();

            Card("fireball", "Fireball").Basic().Cost(2).Action()
                .OnResolve(new GainDamageEffect(2))
                .Text("+2 damage.")
                .Art("a blazing sphere of fire hurtling forward, trailing embers and smoke, orange and crimson flames, dark cavern background, dynamic composition")
                .Register();

            Card("clarity", "Clarity").Basic().Cost(3).Action()
                .OnResolve(new DrawCardsEffect(2))
                .Text("Draw 2 cards.")
                .Art("a serene mage meditating cross-legged, third eye glowing softly, floating spell cards orbiting their head, calm blue tones, library backdrop")
                .Register();

            Card("ban", "Ban").Basic().Cost(3).Action()
                .Keyword(Keyword.Ethereal)
                .OnResolve(new ExileFromHandEffect(1, 1))
                .Text("Ethereal. (Exile this card at end of turn if you didn't play it.)\nExile a card from your hand.")
                .Art("a stern judge's gavel made of glowing runes striking down, a playing card shattering into light fragments, dramatic red banner background")
                .Register();

            Card("protective_barrier", "Protective Barrier").Basic().Cost(3).Instant()
                .Target(TargetSpec.Monster("Protect target monster"))
                .OnResolve(new CompositeEffect(
                    new ModifyMonsterHpEffect(3, ModifierDuration.EndOfTurn),
                    new DrawCardsEffect(1)))
                .Text("Target monster gets +3 HP until end of turn.\nDraw a card.")
                .Art("a shimmering hexagonal dome of golden light enclosing a snarling goblin, magical energy ripples, protective sigils floating on the barrier surface")
                .Register();

            Card("short_sword", "Short Sword").Basic().Cost(4).Equipment(EquipSlot.Weapon)
                .TapAbility("Tap: +1 damage", new GainDamageEffect(1))
                .Text("Tap: +1 damage.")
                .Art("a simple iron short sword with a leather-wrapped hilt resting on a wooden table, soft forge light, still life, detailed metal texture")
                .Register();

            Card("cloth_armor", "Cloth Armor").Basic().Cost(4).Equipment(EquipSlot.Armor)
                .TapAbility("Tap: +1 action point", new GainApEffect(1), manaAbility: true)
                .Text("Tap: +1 action point.")
                .Art("a padded cloth gambeson armor on a wooden stand, humble but well-stitched, warm candlelight in a tailor's shop")
                .Register();

            Card("stone_totem", "Stone Totem").Basic().Cost(3).Equipment(EquipSlot.Trinket)
                .Triggered("Killing a monster grants +1 XP", When.YouKillAMonster, new GainXpEffect(1))
                .Text("Whenever you kill a monster, gain 1 additional XP.")
                .Art("a small carved stone totem idol with glowing amber eyes, tribal engravings, moss growing on its base, mystical aura, forest shrine")
                .Register();

            Card("goblin", "Goblin").Basic()
                .Monster(2, new GainXpEffect(1))
                .Text("HP 2.\nReward: 1 XP.")
                .Art("a mischievous green goblin brandishing a rusty dagger, big ears, sharp teeth grin, tattered leather rags, cave entrance at dusk")
                .Register();

            Card("hobgoblin", "Hobgoblin").Basic()
                .Monster(4, new CompositeEffect(new GainXpEffect(2), new DrawCardsEffect(1)))
                .Text("HP 4.\nReward: 2 XP, draw a card.")
                .Art("a burly orange-skinned hobgoblin warrior in scavenged plate armor, wielding a spiked club, battle scars, war paint, torchlit war camp")
                .Register();
        }
    }
}
