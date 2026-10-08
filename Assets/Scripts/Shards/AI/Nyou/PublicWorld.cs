using System;
using System.Collections.Generic;
using System.Linq;
using Pascension.Engine.Core;
using Shards.Engine;

namespace Shards.Nyou
{
    // Sampling reads unordered public collections, visible encoder pool counts,
    // and the root observer's remembered constraints. It never reads hidden order
    // to decide a sample. All mutation belongs to the copied world.
    internal static class PublicWorld
    {
        // Exact card identities for inference and sampling. Player collections
        // are public as unordered multisets; opponent hand/draw allocation and
        // all shared-deck order remain unread. Generated copies are public supply.
        internal static Dictionary<string,int> RemainingCounts(Adapter g)
        {
            var s=g.Engine.State;var counts=g.Engine.InitialCardCounts();
            foreach(var pair in s.GeneratedCardCounts)
            {counts.TryGetValue(pair.Key,out int count);counts[pair.Key]=count+pair.Value;}
            var seen=new HashSet<int>();
            void Remove(ShardsCard card)
            {
                if(card==null||!seen.Add(card.InstanceId))return;
                counts.TryGetValue(card.DefId,out int count);counts[card.DefId]=count-1;
            }
            foreach(var player in s.Players)
            {
                foreach(var card in player.Hand)Remove(card);
                foreach(var card in player.Deck)Remove(card);
                foreach(var card in player.Discard)Remove(card);
                foreach(var card in player.PlayZone)Remove(card);
                foreach(var card in player.Champions)Remove(card);
                foreach(var card in player.Destinies)Remove(card);
                // Only the actor's set-aside is observable. Infer the opponent's
                // from the public hero catalog and public relic-recruit events.
                if(player.Index==g.Actor)foreach(var card in player.SetAside)Remove(card);
                else foreach(string id in Encoder.CatalogRelics(player.CharacterId,s.Dlc))
                    if(!g.Knowledge.RecruitedRelicDefs[player.Index].Contains(id))
                    {counts.TryGetValue(id,out int count);counts[id]=count-1;}
            }
            foreach(var card in s.CenterRow)Remove(card);
            foreach(var card in s.DestinyRow)Remove(card);
            foreach(var card in s.ActiveMonsters)Remove(card);
            foreach(var card in s.Banished)Remove(card);
            foreach(var card in g.Knowledge.Detached)Remove(card);
            return counts;
        }
        internal static Adapter Sample(Adapter source, ulong seed)
        {
            var g=DepthCopy.Copy(source);var s=g.Engine.State;int seat=g.Actor;
            var rng=new DeterministicRng(seed);s.Rng=rng;
            // Retire stale detached public identities before counting stock.
            Encoder.VisibleEntities(g);
            var remaining=RemainingCounts(g);
            Place(s.Players[seat].Deck,g.Knowledge.Personal[seat],rng);
            var enemy=s.Players[1-seat];int handCount=enemy.Hand.Count;
            var pool=enemy.Hand.Concat(enemy.Deck).OrderBy(c=>c.DefId,StringComparer.Ordinal).ThenBy(c=>c.InstanceId).ToList();rng.Shuffle(pool);
            var held=new List<ShardsCard>();var reserved=new List<ShardsCard>();
            ShardsCard Take(string id,int instance=-1)
            {
                int at=instance>=0?pool.FindIndex(c=>c.InstanceId==instance&&c.DefId==id):pool.FindIndex(c=>c.DefId==id);
                if(at<0)throw new InvalidOperationException("Public remembered card absent from collection: "+id);
                var c=pool[at];pool.RemoveAt(at);return c;
            }
            foreach(var fact in g.Knowledge.PublicHandIds[1-seat].OrderBy(x=>x.Key))held.Add(Take(fact.Value,fact.Key));
            foreach(var group in g.Knowledge.Hand[1-seat].GroupBy(x=>x).OrderBy(x=>x.Key,StringComparer.Ordinal))
                while(held.Count(c=>c.DefId==group.Key)<group.Count())held.Add(Take(group.Key));
            foreach(var fact in g.Knowledge.Personal[1-seat].Where(f=>!f.UncertainPresence).OrderBy(f=>f.InstanceId))
                reserved.Add(Take(fact.DefId,fact.InstanceId));
            if(held.Count>handCount||pool.Count<handCount-held.Count)throw new InvalidOperationException("Inconsistent public hand constraints");
            int missing=handCount-held.Count;held.AddRange(pool.Take(missing));pool.RemoveRange(0,missing);pool.AddRange(reserved);
            enemy.Hand.Clear();enemy.Hand.AddRange(held);enemy.Deck.Clear();enemy.Deck.AddRange(pool);
            foreach(var c in enemy.Hand)c.Zone=ShardsZone.Hand;
            foreach(var c in enemy.Deck)c.Zone=ShardsZone.Deck;
            Place(enemy.Deck,g.Knowledge.Personal[1-seat],rng);
            // The root cannot read another player's private Scry memory. Newly
            // simulated public reveals will rebuild that observer's knowledge.
            g.Knowledge.Center[1-seat].Clear();
            int syntheticId=s.NextInstanceId;
            Replace(s.CenterDeck,21,g.Knowledge.Center[seat],ShardsZone.CenterDeck);
            Replace(s.DestinyDeck,22,new List<KnownPosition>(),ShardsZone.DestinyRow);
            s.NextInstanceId=syntheticId;s.InvalidateCardIndex();g.ObservationAddress=IntPtr.Zero;
            return g;

            void Replace(List<ShardsCard> deck,int channel,List<KnownPosition> facts,ShardsZone zone)
            {
                var definitions=new List<string>();
                foreach(var pair in remaining.OrderBy(p=>p.Key,StringComparer.Ordinal))
                {
                    var def=ShardsCardDatabase.Get(pair.Key);
                    if(def.Type==ShardsCardType.Starter||def.Type==ShardsCardType.Relic)continue;
                    if((def.Type==ShardsCardType.Destiny)!=(channel==22))continue;
                    if(pair.Value<0)throw new InvalidOperationException("Negative inferred shared pool for "+pair.Key);
                    for(int k=0;k<pair.Value;k++)definitions.Add(pair.Key);
                }
                if(definitions.Count!=deck.Count)throw new InvalidOperationException($"Public pool differs from deck size: channel {channel}, {definitions.Count}/{deck.Count}");
                var sampled=new List<ShardsCard>();var retained=new List<KnownPosition>();
                var original=deck.ToDictionary(c=>c.InstanceId);
                // Public IDs remain stable even for an uncertain remembered card;
                // unknown IDs are synthetic and independent of the live deck.
                foreach(var fact in facts.OrderBy(f=>f.UncertainPresence).ThenBy(f=>f.Max-f.Min).ThenBy(f=>f.InstanceId))
                {
                    if(fact.UncertainPresence&&rng.Next(2)==0)continue;
                    int at=definitions.IndexOf(fact.DefId);
                    if(at<0){if(fact.UncertainPresence)continue;throw new InvalidOperationException("Known shared card absent from public pool");}
                    definitions.RemoveAt(at);retained.Add(fact);
                    if(!fact.UncertainPresence&&original.TryGetValue(fact.InstanceId,out var known))sampled.Add(known);
                    else sampled.Add(new ShardsCard{DefId=fact.DefId,InstanceId=fact.InstanceId,Owner=-1,Zone=zone});
                }
                rng.Shuffle(definitions);var ids=new HashSet<int>(sampled.Select(c=>c.InstanceId));
                foreach(string id in definitions)
                {while(ids.Contains(syntheticId))syntheticId++;sampled.Add(new ShardsCard{DefId=id,InstanceId=syntheticId++,Owner=-1,Zone=zone});}
                Place(sampled,retained,rng);deck.Clear();deck.AddRange(sampled);
            }
        }
        internal static void Place(List<ShardsCard> cards,IEnumerable<KnownPosition> facts,DeterministicRng rng)
        {
            cards.Sort((a,b)=>{int c=StringComparer.Ordinal.Compare(a.DefId,b.DefId);return c!=0?c:a.InstanceId.CompareTo(b.InstanceId);});rng.Shuffle(cards);
            var present=new List<(KnownPosition fact,ShardsCard card)>();
            foreach(var fact in facts)
            {
                var card=cards.Find(c=>c.InstanceId==fact.InstanceId&&c.DefId==fact.DefId);
                if(card==null){if(fact.UncertainPresence)continue;throw new InvalidOperationException("Required remembered personal card absent");}
                present.Add((fact,card));
            }
            var assignments=Enumerable.Repeat(-1,cards.Count).ToArray();
            bool Match(int fact,HashSet<int> visited)
            {
                var f=present[fact].fact;var slots=Enumerable.Range(Math.Max(0,f.Min),Math.Max(0,Math.Min(cards.Count-1,f.Max)-Math.Max(0,f.Min)+1)).ToList();rng.Shuffle(slots);
                foreach(int slot in slots)
                {if(!visited.Add(slot))continue;if(assignments[slot]<0||Match(assignments[slot],visited)){assignments[slot]=fact;return true;}}
                return false;
            }
            foreach(int i in Enumerable.Range(0,present.Count).OrderBy(i=>present[i].fact.Max-present[i].fact.Min))
                if(!Match(i,new HashSet<int>()))throw new InvalidOperationException("Unsatisfiable public position constraints");
            var assigned=new HashSet<int>(present.Select(x=>x.card.InstanceId));var rest=new Queue<ShardsCard>(cards.Where(c=>!assigned.Contains(c.InstanceId)));
            for(int top=0;top<cards.Count;top++)cards[cards.Count-1-top]=assignments[top]>=0?present[assignments[top]].card:rest.Dequeue();
        }
    }
}
