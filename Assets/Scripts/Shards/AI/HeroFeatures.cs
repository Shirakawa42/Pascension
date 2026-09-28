using System;
using System.Linq;
using Shards.Engine;

namespace Shards.AI
{
    internal static class HeroFeatures
    {
        // Previously zero padding after the exact 189-card catalog in each
        // count channel. V3's seven Allegiance slots are explicitly excluded.
        internal static readonly int[] Slots = { 702, 703, 893, 894, 895, 1085, 1086, 1087,
            1277, 1278, 1279, 1469, 1470, 1471, 1661, 1662, 1663, 1853, 1854, 1855 };
        internal static readonly object Descriptor = new {
            schema = "shards-hero-knowledge-v1", slots = Slots,
            knownTopCapacity = 4, knownTopFields = new[] { "card_id_div192", "cost_div13", "faction_div6" },
            heroOneHotOrder = ShardsEngine.DraftableCharacters,
            tail = new[] { "sacrifice_preview", "hero_health_cost_div50", "hero_gem_cost_div20" },
            source = "own revealed Scry/reorder options plus public refill events; conservative invalidation",
            sacrifice = "choose before paying; decline costs zero and suppresses repeated preview until targets change or next turn",
        };

        internal static void Encode(Adapter game, Span<float> obs)
        {
            if (!game.HeroFixEnabled) return; // Explicit frozen-evaluation control only.
            var known = game.Knowledge.For(game.Actor);
            for (int i = 0; i < known.Count; i++)
            {
                var card = ShardsCardDatabase.Get(known[i]);
                obs[Slots[3 * i]] = (Encoder.CardIndex(known[i]) + 1) / 192f;
                obs[Slots[3 * i + 1]] = card.Cost / 13f;
                obs[Slots[3 * i + 2]] = (int)card.Faction / 6f;
            }
            var player = game.Engine.State.Players[game.Actor];
            int hero = Encoder.HeroIndex(player.CharacterId);
            if (hero >= 0) obs[Slots[12 + hero]] = 1;
            obs[Slots[17]] = game.SacrificePreview ? 1 : 0;
            var ability = ShardsEngine.HeroAbilityInfo(player.CharacterId);
            obs[Slots[18]] = ability.Health / 50f;
            obs[Slots[19]] = ability.Gems / 20f;
        }
    }
}
