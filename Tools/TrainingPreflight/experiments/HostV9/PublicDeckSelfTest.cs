using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Pascension.Engine.Actions;
using Shards.Engine;

namespace Shards.Preflight
{
    internal static class PublicDeckSelfTest
    {
        private static int _checks;
        private static void Check(bool good, string label)
        { _checks++; if (!good) throw new InvalidOperationException("Public deck: " + label); }
        private static Adapter Fixture(ulong seed=927912)
        {
            var g=new Adapter(seed); while(g.Decision?.Context=="soi.herodraft")g.Step(0);return g;
        }
        private static ShardsCard Add(Adapter g,ShardsPlayer p,string id,ShardsZone zone,List<ShardsCard> target)
        {
            var c=new ShardsCard{InstanceId=g.Engine.State.NextInstanceId++,Owner=p.Index,DefId=id,Zone=zone};target.Add(c);return c;
        }
        private static float[] Encode(Adapter g)
        {
            var x=new float[Encoder.ObsDim+Encoder.MaxActions*Encoder.ActionDim+Encoder.MaxActions];
            Encoder.Encode(g,x.AsSpan(0,Encoder.ObsDim),x.AsSpan(Encoder.ObsDim,2048),x.AsSpan(Encoder.ObsDim+2048,64));return x;
        }
        private static void ResetCollection(ShardsPlayer p)
        {p.Deck.Clear();p.Hand.Clear();p.Discard.Clear();p.PlayZone.Clear();p.Champions.Clear();p.Destinies.Clear();}
        internal static void Run()
        {
            _checks=0;
            var g=Fixture();var enemy=g.Engine.State.Players[1-g.Actor];ResetCollection(enemy);
            Add(g,enemy,"crystal",ShardsZone.Deck,enemy.Deck);
            Add(g,enemy,"blaster",ShardsZone.Hand,enemy.Hand);
            Add(g,enemy,"fungal_hermit",ShardsZone.Discard,enemy.Discard);
            Add(g,enemy,"ojas_genesis_druid",ShardsZone.PlayZone,enemy.PlayZone);
            var guard=Add(g,enemy,"ferrata_guard_duel",ShardsZone.Champions,enemy.Champions);
            var temporary=Add(g,enemy,"reactor_drone_duel",ShardsZone.PlayZone,enemy.PlayZone);temporary.FastPlayed=true;
            Add(g,enemy,"crystal_gate",ShardsZone.SetAside,enemy.SetAside);
            Add(g,enemy,"one_mind_one_army",ShardsZone.DestinyRow,enemy.Destinies);
            var a=Encode(g);
            foreach(string id in new[]{"crystal","blaster","fungal_hermit","ojas_genesis_druid","ferrata_guard_duel"})
                Check(Math.Abs(a[2560+Encoder.CardIndex(id)]-.1f)<1e-6,"Every permanent zone contributes: "+id);
            Check(a[2560+Encoder.CardIndex("reactor_drone_duel")]==0,"Temporary fast-play excluded from permanent composition");
            Check(a[2560+Encoder.CardIndex("crystal_gate")]==0 && a[2560+Encoder.CardIndex("one_mind_one_army")]==0,
                "Set-aside and destiny zones excluded from collection");
            Check(Math.Abs(a[2560+192+(int)ShardsFaction.Homodeus]-.1f)<1e-6,"Allegiance includes temporary Homodeus fast-play");
            Check(Math.Abs(a[2560+199+(int)ShardsFaction.Homodeus]-.05f)<1e-6,"Permanent faction count excludes temporary fast-play");
            var draw=enemy.Deck[0];var hand=enemy.Hand[0];enemy.Deck[0]=hand;enemy.Hand[0]=draw;
            hand.Zone=ShardsZone.Deck;draw.Zone=ShardsZone.Hand;
            enemy.Deck.Reverse();g.Engine.State.CenterDeck.Reverse();g.Engine.State.Players[g.Actor].Deck.Reverse();
            g.Engine.State.Rng.State^=0xD1B54A32D192ED03UL;
            Check(a.SequenceEqual(Encode(g)),"Hidden hand/draw redistribution and orders do not change any policy input");
            hand.DefId="infinity_shard";var changed=Encode(g);
            Check(changed[2560+Encoder.CardIndex("infinity_shard")]>0 && changed[2560+Encoder.CardIndex("blaster")]==0,
                "Authorized public composition changes must change inputs");
            temporary.FastPlayed=false;
            Check(Encode(g)[2560+Encoder.CardIndex("reactor_drone_duel")]>0,"Kept fast-play becomes permanent immediately");
            g.Engine.Banish(temporary,enemy.PlayZone);
            Check(Encode(g)[2560+Encoder.CardIndex("reactor_drone_duel")]==0,"Banishment removes card from current composition immediately");
            Ferrata();RandomRedistributions();
            Program.Print(new{passed=true,checks=_checks,observationSchema=Encoder.SchemaVersion,
                informationRule="Both full permanent collection multisets public; opponent hand/draw allocation and order private",
                fixtures="all-permanent-zones/temporary-and-kept-fastplays/banish/current-composition-change/Ferrata/public-composition-preserving-privacy"});
        }
        private static void Ferrata()
        {
            var g=Fixture(927913);var enemy=g.Engine.State.Players[1-g.Actor];ResetCollection(enemy);
            var guard=Add(g,enemy,"ferrata_guard_duel",ShardsZone.Champions,enemy.Champions);
            for(int n=0;n<3;n++)Add(g,enemy,"reactor_drone_duel",ShardsZone.Deck,enemy.Deck);
            var exact=typeof(Encoder).GetMethod("AuthorizedDefense",BindingFlags.NonPublic|BindingFlags.Static);
            Check(g.Engine.EffectiveDefense(enemy,guard)==6,"Public collection enables Ferrata aura");
            Check((int)exact.Invoke(null,new object[]{g,guard})==6,"Enemy exact public Ferrata defense represented outside split");
            var moved=enemy.Deck[0];enemy.Deck.RemoveAt(0);moved.Zone=ShardsZone.Hand;enemy.Hand.Add(moved);
            Check((int)exact.Invoke(null,new object[]{g,guard})==6,"Ferrata independent of private hand/draw allocation");
            moved.DefId="crystal";
            Check((int)exact.Invoke(null,new object[]{g,guard})==4,"Public composition threshold change updates defense");
        }
        private static void RandomRedistributions()
        {
            var rng=new Random(927914);
            for(int seed=0;seed<12;seed++)
            {
                var g=Fixture((ulong)(927914+seed));
                for(int step=0;step<250&&!g.Engine.State.GameOver&&!g.Truncated;step++)
                {
                    var enemy=g.Engine.State.Players[1-g.Actor];var own=g.Engine.State.Players[g.Actor];
                    var before=Encode(g);var hand=enemy.Hand.ToArray();var deck=enemy.Deck.ToArray();
                    var ownOrder=own.Deck.ToArray();var center=g.Engine.State.CenterDeck.ToArray();
                    var combined=hand.Concat(deck).ToArray();
                    // Same public multiset, same public hand/deck sizes. A revealed
                    // decision option stays intact; only actual private allocation changes.
                    for(int i=combined.Length-1;i>0;i--){int j=rng.Next(i+1);var t=combined[i];combined[i]=combined[j];combined[j]=t;}
                    enemy.Hand.Clear();enemy.Deck.Clear();
                    enemy.Hand.AddRange(combined.Take(hand.Length));enemy.Deck.AddRange(combined.Skip(hand.Length));
                    foreach(var c in enemy.Hand)c.Zone=ShardsZone.Hand;foreach(var c in enemy.Deck)c.Zone=ShardsZone.Deck;
                    own.Deck.Reverse();g.Engine.State.CenterDeck.Reverse();
                    var after=Encode(g);
                    enemy.Hand.Clear();enemy.Hand.AddRange(hand);enemy.Deck.Clear();enemy.Deck.AddRange(deck);
                    foreach(var c in enemy.Hand)c.Zone=ShardsZone.Hand;foreach(var c in enemy.Deck)c.Zone=ShardsZone.Deck;
                    own.Deck.Clear();own.Deck.AddRange(ownOrder);g.Engine.State.CenterDeck.Clear();g.Engine.State.CenterDeck.AddRange(center);
                    Check(before.SequenceEqual(after),"Exact composition-preserving hidden redistribution seed="+seed+" step="+step);
                    g.Step(g.ExerciseChoice(rng));
                }
            }
        }
    }
}
