using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Pascension.Core;
using Pascension.Engine.Actions;
using Shards.AI;
using Shards.Engine;
using Shards.Content;

// Offline truth oracle only. None of these hidden-zone inspections feeds a
// policy, repairs memory or chooses a gameplay action.
internal static class KnowledgeFuzzAudit
{
    internal static void Run(string output,int count)
    {
        ShardsContentRegistry.EnsureRegistered();Encoder.Initialize();
        int transitions=0,completed=0,capped=0,handFacts=0,deckFacts=0,centerFacts=0,sampled=0;
        var actions=new List<string>();int gameIndex=-1,step=0;ulong seed=0;
        var contexts=new Dictionary<string,int>();
        try
        {
            for(gameIndex=0;gameIndex<count;gameIndex++)
            {
                seed=9106000000000000000UL+(ulong)gameIndex;actions.Clear();
                var specs=new List<PlayerSpec>{new(){Name="P0",CharacterId="decima"},new(){Name="P1",CharacterId="tetra"}};
                var g=new Adapter(new ShardsEngine(ShardsContentRegistry.StandardConfig(seed,specs,ShardsDlc.Duel)));
                g.SubmitThroughHost=a=>{var r=g.ApplyExternal(a);if(!r.Accepted)throw new Exception(r.Error);};
                var rng=new Random(712901+gameIndex*7919);
                for(step=0;step<2000&&!g.Engine.State.GameOver;step++)
                {
                    var state=g.Engine.State;
                    for(int seat=0;seat<2;seat++)
                    {
                        foreach(var fact in g.Supplement.Hand(seat).GroupBy(id=>id))
                        {
                            if(fact.Count()>state.Players[seat].Hand.Count(c=>c.DefId==fact.Key))throw new Exception($"False hand fact: seat {seat}, card {fact.Key}");
                            handFacts+=fact.Count();
                        }
                        var top=g.Supplement.Top(seat);
                        for(int n=0;n<top.Count;n++)
                        {
                            if(n>=state.Players[seat].Deck.Count||state.Players[seat].Deck[state.Players[seat].Deck.Count-1-n].DefId!=top[n])throw new Exception($"False personal top: seat {seat}, position {n}, card {top[n]}");
                            deckFacts++;
                        }
                        var center=g.Knowledge.For(seat);
                        for(int n=0;n<center.Count;n++)
                        {
                            if(n>=state.CenterDeck.Count||state.CenterDeck[state.CenterDeck.Count-1-n].DefId!=center[n])throw new Exception($"False center top: seat {seat}, position {n}, card {center[n]}");
                            centerFacts++;
                        }
                    }
                    if(g.Supplement.HandInvalidations!=0)throw new Exception("Unreported hand movement");
                    if(step%31==0)
                    {
                        ulong before=TacticalSearch.Fingerprint(g);
                        TacticalSearch.PublicWorld(g,991+step,FastCopy.Copy);sampled++;
                        if(before!=TacticalSearch.Fingerprint(g))throw new Exception("Sampler mutated source");
                    }
                    string context=g.Decision?.Context??"main";contexts.TryGetValue(context,out int old);contexts[context]=old+1;
                    var legal=Enumerable.Range(0,g.VisibleCount).Where(a=>!(g.Visible(a).Action is ConcedeAction)).ToArray();
                    // Favor playing a varied turn over immediately passing, but
                    // never use any private truth inspected by the assertions.
                    var weights=legal.Select(a=>g.Visible(a).Action is ShardsEndTurnAction?.08:1.0).ToArray();
                    double draw=rng.NextDouble()*weights.Sum();int chosen=legal.Last();
                    for(int n=0;n<legal.Length;n++)if((draw-=weights[n])<=0){chosen=legal[n];break;}
                    actions.Add(TacticalSearch.Key(g,chosen));g.Step(chosen);transitions++;
                }
                if(g.Engine.State.GameOver)completed++;else capped++;
            }
            File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=true,games=count,completed,capped,transitions,handFacts,deckFacts,centerFacts,sampled,contexts},Formatting.Indented));
        }
        catch(Exception e)
        {
            File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=false,gameIndex,seed,step,error=e.ToString(),transitions,handFacts,deckFacts,centerFacts,sampled,contexts,actions},Formatting.Indented));throw;
        }
    }
}
