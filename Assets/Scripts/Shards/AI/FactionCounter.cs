using System;
using System.Collections.Generic;
using Shards.Engine;

namespace Shards.FactionCountProbe
{
    internal static class FactionCounter
    {
        // Standalone candidate only: neither the engine nor HostV3 calls this.
        internal static void Baseline(ShardsPlayer owner, Span<float> output)
        {
            for (int faction = 0; faction < 7; faction++)
                output[faction] = AllegianceEffect.OwnedCount(owner, (ShardsFaction)faction) / 20f;
        }

        internal static void OnePass(ShardsPlayer owner, Span<float> output)
        {
            Span<int> counts = stackalloc int[7];
            counts.Clear();
            bool yggdrasil = owner.Destinies.Exists(d => d.DefId == "project_yggdrasil");
            Count(owner.Deck, yggdrasil, counts);
            Count(owner.Hand, yggdrasil, counts);
            Count(owner.Discard, yggdrasil, counts);
            Count(owner.PlayZone, yggdrasil, counts);
            Count(owner.Champions, yggdrasil, counts);
            for (int faction = 0; faction < 7; faction++) output[faction] = counts[faction] / 20f;
        }

        internal static uint Membership(ShardsCardDef definition, bool yggdrasil)
        {
            int faction = (int)definition.Faction;
            if ((uint)faction >= 7) throw new ArgumentOutOfRangeException(nameof(definition));
            uint membership = 1u << faction;
            if (definition.CountsAsEveryFaction) membership |= 0b0111110u;
            else if (yggdrasil)
            {
                if (definition.Faction == ShardsFaction.Undergrowth) membership |= 1u << (int)ShardsFaction.Wraethe;
                if (definition.Faction == ShardsFaction.Wraethe) membership |= 1u << (int)ShardsFaction.Undergrowth;
            }
            return membership;
        }

        private static void Count(List<ShardsCard> cards, bool yggdrasil, Span<int> counts)
        {
            foreach (var card in cards)
            {
                uint mask = Membership(card.Def, yggdrasil);
                while (mask != 0)
                {
                    int bit = 0; uint remaining = mask;
                    while ((remaining & 1) == 0) { remaining >>= 1; bit++; }
                    counts[bit]++;
                    mask &= mask - 1;
                }
            }
        }
    }
}
