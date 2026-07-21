using System;
using System.Collections.Generic;
using Pascension.Engine.Core;
using Shards.Engine;

namespace Shards.Bots
{
    /// <summary>Resamples a forked ShardsState's hidden information from the viewer's
    /// legitimate knowledge, and nothing more:
    ///  - own deck: composition known, ORDER forgotten (canonical-sort then shuffle);
    ///  - each opponent: hand ∪ deck pooled (composition is public card-counting — every
    ///    acquisition/departure is a public event), hand membership and both orders
    ///    forgotten; hand COUNT preserved;
    ///  - center deck and destiny deck: order forgotten;
    ///  - everything else (rows, discards, play zones, champions, set-asides) is public
    ///    and untouched.
    /// Canonical sorting first makes the output a pure function of the information set —
    /// any ordering fingerprint of the true hidden state is erased (this is what the
    /// fairness-invariance test pins).</summary>
    public static class ShardsDeterminizer
    {
        public static void Sample(ShardsState state, int viewerIndex, DeterministicRng rng)
        {
            foreach (var player in state.Players)
            {
                if (player.Index == viewerIndex)
                {
                    // Own deck: composition is the viewer's to know, order is not.
                    Canonicalize(player.Deck);
                    rng.Shuffle(player.Deck);
                    continue;
                }

                // Opponent: pool hand into deck, forget the partition, re-deal.
                int handCount = player.Hand.Count;
                foreach (var card in player.Hand)
                {
                    card.Zone = ShardsZone.Deck;
                    player.Deck.Add(card);
                }
                player.Hand.Clear();
                Canonicalize(player.Deck);
                rng.Shuffle(player.Deck);
                for (int i = 0; i < handCount; i++)
                {
                    var card = player.Deck[player.Deck.Count - 1];
                    player.Deck.RemoveAt(player.Deck.Count - 1);
                    card.Zone = ShardsZone.Hand;
                    player.Hand.Add(card);
                }
            }

            Canonicalize(state.CenterDeck);
            rng.Shuffle(state.CenterDeck);
            Canonicalize(state.DestinyDeck);
            rng.Shuffle(state.DestinyDeck);
        }

        private static void Canonicalize(List<ShardsCard> cards) =>
            cards.Sort((a, b) =>
            {
                int byDef = string.CompareOrdinal(a.DefId, b.DefId);
                return byDef != 0 ? byDef : a.InstanceId.CompareTo(b.InstanceId);
            });
    }
}
