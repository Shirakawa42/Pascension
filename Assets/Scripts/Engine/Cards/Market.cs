using System.Collections.Generic;
using Pascension.Engine.Core;

namespace Pascension.Engine.Cards
{
    /// <summary>
    /// The shared market: three tiers (Basic/Advanced/Elite), each a face-down pile
    /// plus a row of 5 face-up slots. Monsters occupy slots until killed.
    /// </summary>
    public sealed class Market
    {
        public const int Tiers = 3;

        /// <summary>Piles by tier index 0..2 (Basic..Elite). Index 0 = top of pile.</summary>
        public List<CardInstance>[] Piles = { new(), new(), new() };

        /// <summary>Face-up rows by tier index 0..2; null = empty slot (pile exhausted).</summary>
        public CardInstance[][] Rows = { new CardInstance[5], new CardInstance[5], new CardInstance[5] };

        public static int TierIndex(CardTier tier) => (int)tier - 1;
        public static CardTier TierFromIndex(int index) => (CardTier)(index + 1);

        public List<CardInstance> PileFor(CardTier tier) => Piles[TierIndex(tier)];
        public CardInstance[] RowFor(CardTier tier) => Rows[TierIndex(tier)];

        public CardInstance SlotCard(CardTier tier, int slot) => Rows[TierIndex(tier)][slot];

        /// <summary>All monsters currently occupying row slots, with their coordinates.</summary>
        public IEnumerable<(CardTier tier, int slot, CardInstance card)> Monsters()
        {
            for (int t = 0; t < Tiers; t++)
            for (int s = 0; s < Rows[t].Length; s++)
            {
                var card = Rows[t][s];
                if (card != null && card.Def.IsMonster)
                    yield return (TierFromIndex(t), s, card);
            }
        }

        public bool TryLocate(int instanceId, out CardTier tier, out int slot)
        {
            for (int t = 0; t < Tiers; t++)
            for (int s = 0; s < Rows[t].Length; s++)
            {
                if (Rows[t][s]?.InstanceId == instanceId)
                {
                    tier = TierFromIndex(t);
                    slot = s;
                    return true;
                }
            }
            tier = default;
            slot = -1;
            return false;
        }
    }
}
