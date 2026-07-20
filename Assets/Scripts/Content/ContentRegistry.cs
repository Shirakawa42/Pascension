using System.Collections.Generic;
using Pascension.Content.Heroes;
using Pascension.Content.Sets;
using Pascension.Engine.Core;

namespace Pascension.Content
{
    /// <summary>
    /// Registers all card and hero definitions, and builds the standard game configuration
    /// (pile compositions live here and are mirrored in .claude/skills/pascension-cards/SKILL.md).
    /// </summary>
    public static class ContentRegistry
    {
        private static bool _registered;

        public static void RegisterAll()
        {
            if (_registered) return;
            _registered = true;
            DefaultCards.Register();
            BasicCards.Register();
            AdvancedCards.Register();
            EliteCards.Register();
            BossCards.Register();
            HeroCatalog.Register();
        }

        /// <summary>Test hook: allows re-registering after CardDatabase.Clear().</summary>
        public static void ResetForTests()
        {
            _registered = false;
        }

        public static GameConfig StandardConfig(ulong seed, List<PlayerConfig> players)
        {
            RegisterAll();
            var config = new GameConfig
            {
                Seed = seed,
                Players = players,
                BossDefId = "the_gatekeeper",
                DefaultDeck =
                {
                    ("move", 7),
                    ("redbull", 1),
                    ("fire_bolt", 1),
                    ("pyroblast", 1)
                }
            };

            config.BasicPile
                .Add("run", 4)
                .Add("fireball", 4)
                .Add("clarity", 4)
                .Add("ban", 3)
                .Add("protective_barrier", 3)
                .Add("short_sword", 3)
                .Add("cloth_armor", 3)
                .Add("stone_totem", 3)
                .Add("goblin", 5)
                .Add("hobgoblin", 4);

            config.AdvancedPile
                .Add("counterspell", 3)
                .Add("random_bullshit_go", 3)
                .Add("sprint", 3)
                .Add("meteor", 3)
                .Add("adrenaline_shot", 3)
                .Add("fireworks", 3)
                .Add("reflexes", 3)
                .Add("sabotage", 3)
                .Add("longsword", 3)
                .Add("tower_shield", 3)
                .Add("lucky_charm", 3)
                .Add("merchant_stall", 3)
                .Add("war_banner", 3)
                .Add("loot_goblin", 3)
                .Add("mimic", 3)
                .Add("ogre", 3);

            config.ElitePile
                .Add("time_warp", 2)
                .Add("firestorm", 2)
                .Add("cataclysm", 2)
                .Add("divine_shield", 2)
                .Add("mind_steal", 2)
                .Add("blink", 2)
                .Add("excalibur", 2)
                .Add("dragonscale_armor", 2)
                .Add("philosophers_stone", 2)
                .Add("portal_stone", 2)
                .Add("throne_of_ambition", 2)
                .Add("travelers_map", 2)
                .Add("arcane_library", 2)
                .Add("dragon", 2)
                .Add("lich", 2)
                .Add("treasure_golem", 2);

            return config;
        }
    }
}
