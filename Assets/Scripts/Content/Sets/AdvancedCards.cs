using Pascension.Content.Effects;
using Pascension.Engine.Core;
using Pascension.Engine.Effects.Common;
using Pascension.Engine.Targeting;
using static Pascension.Content.CardBuilder;

namespace Pascension.Content.Sets
{
    /// <summary>Advanced pile (48 cards, level ≥4): see the cards skill for copies per card.</summary>
    public static class AdvancedCards
    {
        public static void Register()
        {
            Card("counterspell", "Counterspell").Advanced().Cost(5).Instant()
                .Target(TargetSpec.Spell("Counter target spell"))
                .OnResolve(new CounterTargetSpellEffect())
                .Text("Counter target spell.")
                .Art("a swirling vortex of blue arcane energy dissolving an incoming fireball mid-air, two spell circles clashing, sparks of unraveling magic")
                .Register();

            Card("random_bullshit_go", "Random Bullshit Go").Advanced().Cost(5).Instant()
                .OnResolve(new RandomBullshitGoEffect())
                .Text("Exile cards from the top of the advanced pile until you exile 2 instants not named Random Bullshit Go. You may cast them for free. Put the other cards exiled this way on the bottom of the pile in any order.")
                .Art("a chaotic wizard hurling a handful of glowing random scrolls and cards into the air, confetti of sparks, wild grin, exploding magical slot machine behind")
                .Register();

            Card("sprint", "Sprint").Advanced().Cost(5).Action().ManaAbility()
                .OnResolve(new GainApEffect(4))
                .Text("+4 action points.")
                .Art("a blur of a runner breaking into superhuman speed, afterimages trailing, wind lines, cobblestone road cracking under the push-off")
                .Register();

            Card("meteor", "Meteor").Advanced().Cost(5).Action()
                .OnResolve(new GainDamageEffect(4))
                .Text("+4 damage.")
                .Art("a burning meteor screaming down from a night sky, molten core, debris trail, villagers pointing in awe below, impact imminent")
                .Register();

            Card("adrenaline_shot", "Adrenaline Shot").Advanced().Cost(6).Action()
                .OnResolve(new CompositeEffect(new GainApEffect(2), new DrawCardsEffect(1)))
                .Text("+2 action points.\nDraw a card.")
                .Art("a glowing green serum syringe injecting light into veins, energy coursing up an arm, heartbeat lines of light in the dark background")
                .Register();

            Card("fireworks", "Fireworks").Advanced().Cost(6).Action()
                .OnResolve(new CompositeEffect(new GainDamageEffect(3), new DrawCardsEffect(1)))
                .Text("+3 damage.\nDraw a card.")
                .Art("spectacular red and gold fireworks exploding into dragon shapes over a festival, sparks raining, crowd silhouettes, celebratory chaos")
                .Register();

            Card("reflexes", "Reflexes").Advanced().Cost(5).Instant()
                .OnResolve(new DrawCardsEffect(2))
                .Text("Draw 2 cards.")
                .Art("a duelist catching two thrown daggers between their fingers mid-flight, time-slow effect, motion frozen, sharp focus on the eyes")
                .Register();

            Card("sabotage", "Sabotage").Advanced().Cost(6).Instant()
                .Target(TargetSpec.Opponent("Target player discards a random card"))
                .OnResolve(new ForceDiscardRandomEffect())
                .Text("Target player discards a card at random.")
                .Art("a masked rogue cutting a rope bridge with a curved knife, cards falling into a chasm below, moonlit night, mischievous smirk")
                .Register();

            Card("longsword", "Longsword").Advanced().Cost(6).Equipment(EquipSlot.Weapon)
                .TapAbility("Tap: +2 damage", new GainDamageEffect(2))
                .Text("Tap: +2 damage.")
                .Art("an elegant steel longsword with blue gem pommel held aloft catching sunlight, engraved fuller, heroic presentation, castle armory")
                .Register();

            Card("tower_shield", "Tower Shield").Advanced().Cost(6).Equipment(EquipSlot.Armor)
                .TapAbility("Tap: +2 action points", new GainApEffect(2), manaAbility: true)
                .Text("Tap: +2 action points.")
                .Art("a massive rectangular tower shield planted in the ground, battle-worn steel with a golden lion emblem, arrows embedded, battlefield dawn")
                .Register();

            Card("lucky_charm", "Lucky Charm").Advanced().Cost(6).Equipment(EquipSlot.Trinket)
                .Triggered("At the start of your turn, gain 1 action point", When.YourTurnStarts, new GainApEffect(1))
                .Text("At the start of your turn, gain 1 action point.")
                .Art("a golden four-leaf clover charm on a delicate chain, softly glowing with green sparkles, resting on green velvet, macro detail")
                .Register();

            Card("merchant_stall", "Merchant Stall").Advanced().Cost(6).Relic()
                .Static(new FirstBuysDiscount(1))
                .Text("The first card you buy each turn costs 1 less.")
                .Art("a cozy wooden market stall overflowing with potions, scrolls and trinkets, striped awning, hanging lanterns, cheerful bazaar at golden hour")
                .Register();

            Card("war_banner", "War Banner").Advanced().Cost(7).Relic()
                .Triggered("At the start of your main phase, gain 1 damage", When.YourMainPhaseStarts, new GainDamageEffect(1))
                .Text("At the start of your main phase, gain 1 damage.")
                .Art("a tattered crimson war banner with a black wolf sigil planted on a hilltop, flapping in storm wind, army spears silhouetted behind")
                .Register();

            Card("loot_goblin", "Loot Goblin").Advanced()
                .Monster(6, new CompositeEffect(new GainXpEffect(2), new DrawCardsEffect(2)))
                .Text("HP 6.\nReward: 2 XP, draw 2 cards.")
                .Art("a plump goblin stuffing gold coins and gems into an overflowing sack, jewelry hanging off its ears, guilty look over its shoulder, treasure vault")
                .Register();

            Card("mimic", "Mimic").Advanced()
                .Monster(7, new CompositeEffect(new GainXpEffect(3), new MimicRewardEffect()))
                .Text("HP 7.\nReward: 3 XP, put the top card of the advanced pile into your discard pile for free.")
                .Art("a treasure chest revealing rows of sharp teeth and a long purple tongue, wooden lid as jaws, gold coins spilling like drool, dungeon torchlight")
                .Register();

            Card("ogre", "Ogre").Advanced()
                .Monster(8, new GainXpEffect(4))
                .Text("HP 8.\nReward: 4 XP.")
                .Art("a towering one-eyed ogre swinging a tree trunk club, grey-green mottled skin, crude bone jewelry, roaring, crushed wagon underfoot, mountain pass")
                .Register();
        }
    }
}
