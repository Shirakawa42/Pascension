using System.Collections.Generic;

namespace Shards.Engine
{
    /// <summary>Recycles the object graph a search clone needs across Fork calls: one
    /// reusable ShardsState (lists keep their backing arrays through Clear), a pooled
    /// card store and the instance-id identity map. One arena per SEARCH — strictly
    /// single-threaded use, and the state it hands out is overwritten by the next
    /// Fork(arena) call, so a caller must be done with the previous clone by then
    /// (ISMCTS iterations are exactly that shape). Fresh forks (no arena) are
    /// unaffected. Profiling: allocating ~150 objects per iteration-fork was ~20% of
    /// self-play CPU before pooling.</summary>
    public sealed class ShardsCloneArena
    {
        internal ShardsState State;
        internal ShardsCard[] Map = System.Array.Empty<ShardsCard>();
        internal readonly List<ShardsCard> CardPool = new(256);
        internal int CardCursor;

        internal void BeginCopy(int nextInstanceId)
        {
            if (Map.Length < nextInstanceId)
                Map = new ShardsCard[System.Math.Max(nextInstanceId * 2, 256)];
            else
                System.Array.Clear(Map, 0, nextInstanceId);
            CardCursor = 0;
        }

        internal ShardsCard Rent()
        {
            if (CardCursor < CardPool.Count)
                return CardPool[CardCursor++];
            var card = new ShardsCard();
            CardPool.Add(card);
            CardCursor++;
            return card;
        }
    }
}
