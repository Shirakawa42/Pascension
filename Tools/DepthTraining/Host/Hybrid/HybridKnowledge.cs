using System;
using System.Collections.Generic;
using System.Linq;
using Shards.Engine;

namespace Shards.ZeroDepth
{
    internal sealed class Prediction
    {
        internal float[] P;
        internal float V;
    }

    internal static class HybridKnowledge
    {
        static string Positions(IEnumerable<KnownPosition> facts)=>string.Join(";",facts
            .OrderBy(f=>f.InstanceId).Select(f=>$"{f.InstanceId},{f.DefId},{f.Min},{f.Max},{f.UncertainPresence}"));

        // An observer never receives another seat's private center Scry memory.
        internal static string ObserverSignature(Knowledge k,int seat)=>Positions(k.Center[seat])+"/"+
            string.Join("/",Enumerable.Range(0,2).Select(p=>Positions(k.Personal[p])+":"+
                string.Join(",",k.Hand[p].OrderBy(x=>x,StringComparer.Ordinal))+":"+
                string.Join(",",k.PublicHandIds[p].OrderBy(x=>x.Key).Select(x=>$"{x.Key}={x.Value}"))+":"+
                string.Join(",",k.RecruitedRelicDefs[p].OrderBy(x=>x,StringComparer.Ordinal))))+"/"+
            string.Join(",",k.Detached.OrderBy(c=>c.InstanceId).Select(c=>$"{c.InstanceId}:{c.DefId}:{c.Owner}:{c.Zone}"));

        internal static Adapter DefensiveWorld(Adapter source,ulong seed)
        {
            var g=PublicWorld.Sample(source,seed);int seat=1-source.Actor;
            var enemy=g.Engine.State.Players[seat];int handCount=enemy.Hand.Count;
            // Keep every remembered personal-deck identity at its constrained
            // position, including uncertain facts present in this sampled world.
            var remembered=new HashSet<int>(g.Knowledge.Personal[seat].Select(f=>f.InstanceId));
            var reserved=enemy.Deck.Where(c=>remembered.Contains(c.InstanceId)).ToArray();
            var pool=enemy.Hand.Concat(enemy.Deck.Where(c=>!remembered.Contains(c.InstanceId))).ToList();
            var held=new List<ShardsCard>();
            void Take(string id,int instance=-1)
            {
                int at=pool.FindIndex(c=>c.DefId==id&&(instance<0||c.InstanceId==instance));
                if(at<0)throw new InvalidOperationException("Public hand fact absent in defensive world");
                held.Add(pool[at]);pool.RemoveAt(at);
            }
            foreach(var pair in g.Knowledge.PublicHandIds[seat].OrderBy(p=>p.Key))Take(pair.Value,pair.Key);
            foreach(var group in g.Knowledge.Hand[seat].GroupBy(x=>x).OrderBy(x=>x.Key,StringComparer.Ordinal))
                while(held.Count(c=>c.DefId==group.Key)<group.Count())Take(group.Key);
            if(held.Count>handCount)throw new InvalidOperationException("Too many public hand facts");
            var hand=held.Concat(pool.OrderByDescending(c=>c.Def.ShieldInPlay?0:g.Engine.ShieldValue(enemy,c))
                .ThenBy(c=>c.DefId,StringComparer.Ordinal).ThenBy(c=>c.InstanceId).Take(handCount-held.Count)).ToArray();
            var ids=new HashSet<int>(hand.Select(c=>c.InstanceId));
            enemy.Hand.Clear();enemy.Hand.AddRange(hand);
            enemy.Deck.Clear();enemy.Deck.AddRange(pool.Where(c=>!ids.Contains(c.InstanceId)));enemy.Deck.AddRange(reserved);
            foreach(var card in enemy.Hand)card.Zone=ShardsZone.Hand;
            foreach(var card in enemy.Deck)card.Zone=ShardsZone.Deck;
            PublicWorld.Place(enemy.Deck,g.Knowledge.Personal[seat],new Pascension.Engine.Core.DeterministicRng(seed));
            g.Engine.State.InvalidateCardIndex();g.ObservationAddress=IntPtr.Zero;
            return g;
        }
    }
}
