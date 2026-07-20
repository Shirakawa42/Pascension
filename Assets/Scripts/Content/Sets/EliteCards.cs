using Pascension.Content.Effects;
using Pascension.Engine.Core;
using Pascension.Engine.Effects;
using Pascension.Engine.Effects.Common;
using Pascension.Engine.Targeting;
using static Pascension.Content.CardBuilder;

namespace Pascension.Content.Sets
{
    /// <summary>Elite pile (32 cards, level ≥8): see the pascension-cards skill for copies per card.</summary>
    public static class EliteCards
    {
        public static void Register()
        {
            Card("time_warp", "Time Warp").Elite().Cost(12).Action()
                .OnResolve(new ExtraTurnEffect())
                .ExilesAfterResolve()
                .Text("Take an extra turn after this one.\nExile Time Warp.")
                .Art("a colossal cracked hourglass floating in a starry void, sand flowing upward in defiance of gravity, clock faces warping around it, deep blues and violet")
                .Register();

            Card("firestorm", "Firestorm").Elite().Cost(9).Action()
                .OnResolve(new GainDamageEffect(7))
                .Text("+7 damage.")
                .Art("an apocalyptic rain of fire pillars scorching a battlefield, tornado of flame at the center, ash-black sky, silhouettes fleeing, epic scale destruction")
                .Register();

            Card("cataclysm", "Cataclysm").Elite().Cost(8).Action()
                .OnResolve(new CataclysmEffect())
                .Text("Exile all face-up market cards and refill each slot.\nGain 1 XP for each monster exiled this way.")
                .Art("the earth splitting open beneath a marketplace, stalls and cards tumbling into a glowing rift, reality shattering like glass, cataclysmic purple light")
                .Register();

            Card("divine_shield", "Divine Shield").Elite().Cost(8).Instant()
                .Target(TargetSpec.Monster("Protect target monster"))
                .OnResolve(new CompositeEffect(
                    new ModifyMonsterHpEffect(8, ModifierDuration.EndOfTurn),
                    new DrawCardsEffect(2)))
                .Text("Target monster gets +8 HP until end of turn.\nDraw 2 cards.")
                .Art("a radiant angelic barrier of white-gold feathers and light wrapping around a dragon, divine rays from heaven, sacred geometry circles")
                .Register();

            Card("mind_steal", "Mind Steal").Elite().Cost(9).Instant()
                .Target(TargetSpec.Spell("Counter target spell"))
                .OnResolve(new MindStealEffect())
                .Text("Counter target spell. If it was an instant, you may cast a copy of it for free.")
                .Art("a psychic sorceress pulling a glowing thought-thread from a rival mage's head, spell energy transferring between minds, purple and teal magic")
                .Register();

            Card("blink", "Blink").Elite().Cost(8).Instant()
                .Target(TargetSpec.RelicOrEquipment("Return target relic or equipment"))
                .OnResolve(new ReturnPermanentToDiscardEffect())
                .Text("Return target relic or equipment to its owner's discard pile.")
                .Art("a suit of armor mid-teleport, dissolving into cyan particles and vanishing, empty space where it stood, motes of light drifting away")
                .Register();

            Card("excalibur", "Excalibur").Elite().Cost(10).Equipment(EquipSlot.Weapon)
                .TapAbility("Tap: +4 damage", new GainDamageEffect(4))
                .Text("Tap: +4 damage.")
                .Art("a legendary golden sword embedded in a stone anvil radiating holy light, intricate celtic engravings on the blade, mist and cherry petals swirling")
                .Register();

            Card("dragonscale_armor", "Dragonscale Armor").Elite().Cost(10).Equipment(EquipSlot.Armor)
                .TapAbility("Tap: +3 action points", new GainApEffect(3))
                .Text("Tap: +3 action points.")
                .Art("a masterwork cuirass forged from overlapping crimson dragon scales, each scale shimmering with inner fire, displayed on an obsidian stand")
                .Register();

            Card("philosophers_stone", "Philosopher's Stone").Elite().Cost(9).Equipment(EquipSlot.Trinket)
                .Static(new BonusXpPerGain(1))
                .Text("Whenever you gain XP, gain 1 more.")
                .Art("a deep red translucent crystal stone hovering above an alchemist's transmutation circle, golden equations orbiting it, lead turning to gold beneath")
                .Register();

            Card("portal_stone", "Portal Stone").Elite().Cost(10).Equipment(EquipSlot.Trinket)
                .TapAbility("Tap: move 2 steps", new FreeMoveEffect(2))
                .Text("Tap: move 2 steps.")
                .Art("a runic waystone with a swirling blue portal at its center, stone archway crackling with teleportation energy, footprints leading in")
                .Register();

            Card("throne_of_ambition", "Throne of Ambition").Elite().Cost(10).Relic()
                .Triggered("At the start of your turn, gain 1 XP", When.YourTurnStarts, new GainXpEffect(1))
                .Text("At the start of your turn, gain 1 XP.")
                .Art("an imposing obsidian throne with gold veins and floating crown above it, steps littered with fallen banners of rivals, dramatic god rays")
                .Register();

            Card("travelers_map", "Traveler's Map").Elite().Cost(9).Relic()
                .Triggered("At the start of your turn, move 1 step", When.YourTurnStarts, new FreeMoveEffect(1))
                .Text("At the start of your turn, move 1 step.")
                .Art("an ancient parchment map unrolled on a table, a glowing golden path drawing itself across mountains and forests toward a castle, compass and candle")
                .Register();

            Card("arcane_library", "Arcane Library").Elite().Cost(10).Relic()
                .Static(new DrawCountOverride(6))
                .Text("At the end of your turn, draw 6 cards instead of 5.")
                .Art("an infinite spiral library with floating books and glowing tomes flying between shelves, spiral staircases into the void, warm magical light")
                .Register();

            Card("dragon", "Dragon").Elite()
                .Monster(15, new CompositeEffect(new GainXpEffect(8), new DrawCardsEffect(2)))
                .Text("HP 15.\nReward: 8 XP, draw 2 cards.")
                .Art("a magnificent crimson dragon rearing up with wings spread wide, breathing a column of fire into a stormy sky, perched on a mountain of gold")
                .Register();

            Card("lich", "Lich").Elite()
                .Monster(12, new CompositeEffect(new GainXpEffect(5), new ExileFromDiscardEffect(3)))
                .Text("HP 12.\nReward: 5 XP, exile up to 3 cards from your discard pile.")
                .Art("a skeletal lich king in tattered royal robes holding a soul-phylactery, green necrotic flames in its eye sockets, crumbling crypt throne room")
                .Register();

            Card("treasure_golem", "Treasure Golem").Elite()
                .Monster(10, new CompositeEffect(new GainXpEffect(4), new GainApEffect(4)))
                .Text("HP 10.\nReward: 4 XP, gain 4 action points.")
                .Art("a hulking golem made entirely of gold coins, gem eyes, treasure chest torso spilling jewels as it lumbers forward, vault interior")
                .Register();
        }
    }
}
