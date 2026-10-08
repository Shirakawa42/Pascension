using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Shards.AI;
using Shards.Engine;
using Shards.Content;
using Pascension.Core;

// Compare optimized copies and pruning against the frozen pre-optimization code.
// Includes real trajectories, private-memory observations and paused effect frames.
internal static class OptimizationAudit
{
    internal static void Run(FrozenPolicy policy,string output)
    {
        int transitions=0,scenarios=0;var contexts=new HashSet<string>();var rng=new Random(927194);
        void Equal(Adapter a,Adapter b)
        {
            if(TacticalSearch.Fingerprint(a)!=TacticalSearch.Fingerprint(b)||a.VisibleCount!=b.VisibleCount)
                throw new Exception("Copy state mismatch");
            var ao=new float[3328];var ac=new float[2048];var am=new float[64];
            var bo=new float[3328];var bc=new float[2048];var bm=new float[64];
            Encoder.Encode(a,ao,ac,am);Encoder.Encode(b,bo,bc,bm);
            if(!ao.SequenceEqual(bo)||!ac.SequenceEqual(bc)||!am.SequenceEqual(bm))throw new Exception("Copy observation/memory mismatch");
            for(int k=0;k<a.VisibleCount;k++)if(TacticalSearch.Key(a,k)!=TacticalSearch.Key(b,k))throw new Exception("Copy legal action mismatch");
        }
        void Transition(Adapter source,int choice)
        {
            var before=TacticalSearch.Fingerprint(source);
            var expected=BaselineTacticalSearch.Copy(source);var actual=TacticalSearch.Copy(source);var compiled=FastCopy.Copy(source);
            Equal(expected,actual);Equal(expected,compiled);expected.Step(choice);actual.Step(choice);compiled.Step(choice);Equal(expected,actual);Equal(expected,compiled);
            if(before!=TacticalSearch.Fingerprint(source))throw new Exception("Copy mutated source");
            source.Step(choice);Equal(source,expected);transitions++;
            contexts.Add(source.Decision?.Context??"actions");
        }
        foreach(var c in HeroTactics.Cases())foreach(int v in Enumerable.Range(0,4))
        {
            var a=c.Factory(v);var b=BaselineTacticalSearch.Copy(a);
            int x=BaselineTacticalSearch.Find(b,512,12,4,null,true);
            int y=TacticalSearch.Find(a,512,12,4,null,true);
            if(x!=y)throw new Exception("Search pruning mismatch: "+c.Id+" "+x+" "+y);
            // Exercise every currently available action from this tactical state.
            for(int k=0;k<a.VisibleCount;k++)Transition(BaselineTacticalSearch.Copy(a),k);
            scenarios++;
        }
        for(int i=0;i<40;i++)
        {
            var cfg=ShardsContentRegistry.StandardConfig((ulong)(881201+i),new List<PlayerSpec>{new(){Name="P0"},new(){Name="P1"}},ShardsDlc.Duel);
            var g=new Adapter(new ShardsEngine(cfg));g.SubmitThroughHost=act=>{var r=g.ApplyExternal(act);if(!r.Accepted)throw new Exception(r.Error);};
            int pair=i%20,h0=pair/4,h1=pair%4;if(h1>=h0)h1++;
            foreach(int h in new[]{h1,h0})Transition(g,Enumerable.Range(0,g.VisibleCount).Single(k=>g.Visible(k).Option?.DefId==ShardsEngine.DraftableCharacters[h]));
            while(!g.Engine.State.GameOver)
            {
                var o=new float[3328];var c=new float[2048];var m=new float[64];Encoder.Encode(g,o,c,m);
                var p=policy.Probabilities(o,c,m,out _);double r=rng.NextDouble();int a=0;
                for(int k=0;k<g.VisibleCount;k++){a=k;r-=p[k];if(r<0)break;}
                Transition(g,a);if(g.Truncated)throw new Exception("Truncated copy audit");
            }
        }
        File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=true,games=40,transitions,scenarios,contexts=contexts.OrderBy(x=>x)},Formatting.Indented));
    }
}
