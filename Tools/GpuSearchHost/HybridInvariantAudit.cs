using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shards.AI;
using Shards.Content;
using Shards.Engine;

internal static class HybridInvariantAudit
{
    static string Observation(Adapter g)
    {
        var o=new float[3328];var c=new float[2048];var m=new float[64];Encoder.Encode(g,o,c,m);
        // Candidate order and physical IDs are not strategic differences between
        // interchangeable copies. Compare the observation and candidate multiset.
        return JsonConvert.SerializeObject(new{o,choices=Enumerable.Range(0,g.VisibleCount)
            .Select(a=>JsonConvert.SerializeObject(c.Skip(a*32).Take(32))).OrderBy(x=>x,StringComparer.Ordinal)});
    }
    internal static void Run(string output)
    {
        ShardsContentRegistry.EnsureRegistered();Encoder.Initialize();int positions=0,comparisons=0,macroLengths=0;
        foreach(var test in HeroTactics.Cases())for(int variant=0;variant<4;variant++)
        {
            var g=test.Factory(variant+95000);if(g.Decision!=null)continue;
            var cards=Enumerable.Range(0,3).Select(_=>RezAudit.Add(g,"crystal",g.Actor)).ToArray();
            ulong before=TacticalSearch.Fingerprint(g);SearchDiagnostics.AssertPrivacy(g);
            int[] group=HybridLookahead.Groups(g).Single(xs=>xs.Any(a=>g.Visible(a).Action is ShardsPlayCardAction p&&p.CardInstanceId==cards[0].InstanceId));
            if(group.Length<3)throw new Exception("Identical Crystals were not grouped");
            var prediction=new Prediction{P=Enumerable.Repeat(1f/g.VisibleCount,64).ToArray()};
            var options=HybridLookahead.Options(g,prediction,group[0],64).Where(o=>o.First==group[0]).ToArray();
            if(!options.Select(o=>o.Keys.Length).OrderBy(x=>x).SequenceEqual(Enumerable.Range(1,group.Length)))throw new Exception("Resource macro lost a partial stopping point");
            macroLengths+=options.Length;
            for(int count=1;count<=3;count++)
            {
                var a=FastCopy.Copy(g);var b=FastCopy.Copy(g);
                foreach(int id in cards.Take(count).Select(c=>c.InstanceId))Play(a,id);
                foreach(int id in cards.Reverse().Take(count).Select(c=>c.InstanceId))Play(b,id);
                if(Observation(a)!=Observation(b))throw new Exception("Resource-copy symmetry failed: "+test.Id);
                if(g.Engine.State.Players[g.Actor].Hand.Count-a.Engine.State.Players[g.Actor].Hand.Count!=count||a.Engine.State.Players[g.Actor].Gems-g.Engine.State.Players[g.Actor].Gems!=count)throw new Exception("Unexpected resource macro side effect");
                comparisons++;
            }
            // Different mutable flags must not accidentally enter the same class.
            var changed=FastCopy.Copy(g);changed.Engine.State.FindCard(cards[1].InstanceId).BanishAtCleanup=true;
            if(HybridLookahead.Groups(changed).Any(xs=>xs.Count(a=>changed.Visible(a).Action is ShardsPlayCardAction p&&(p.CardInstanceId==cards[0].InstanceId||p.CardInstanceId==cards[1].InstanceId))==2))throw new Exception("Macro merged differently flagged cards");
            if(before!=TacticalSearch.Fingerprint(g))throw new Exception("Macro audit mutated source");
            positions++;
        }
        int forcedCollapseComparisons=0;
        var paged=HeroTactics.Cases().First().Factory(95000);
        for(int i=0;i<70;i++)RezAudit.Add(paged,"crystal",paged.Actor);
        if(paged.Candidates.Count<=Encoder.MaxActions||Enumerable.Range(0,paged.VisibleCount).Any(a=>HybridLookahead.ResourceCard(paged,a)))throw new Exception("Paged hands must fall back to primitive choices");
        Prediction[] Infer(Adapter[] games)=>games.Select(g=>
        {
            var p=new float[64];
            for(int a=0;a<g.VisibleCount;a++)p[a]=g.Visible(a).Action is ShardsPlayCardAction?4:g.Visible(a).Action is ShardsHeroAbilityAction?3:g.Visible(a).Action is ShardsFocusAction?2:1;
            float sum=p.Sum();for(int a=0;a<p.Length;a++)p[a]/=sum;
            var own=g.Engine.State.Players[g.Actor];var enemy=g.Engine.State.Players[1-g.Actor];
            return new Prediction{P=p,V=(float)Math.Tanh((own.Mastery-enemy.Mastery)/30.0+(own.Health-enemy.Health)/50.0+own.Power/80.0)};
        }).ToArray();
        foreach(var test in HeroTactics.Cases())for(int variant=0;variant<2;variant++)
        {
            var original=test.Factory(variant+95000);var a=FastCopy.Copy(original);var b=FastCopy.Copy(original);
            var settings=new PolicySearchSettings{Hybrid=true,HybridSkipForced=false,Candidates=4,Depth=24,Workers=1,TerminalNodes=0};
            var reference=new HybridLookahead(settings,Infer,FastCopy.Copy){CaptureLeaves=true};
            var optimizedSettings=settings.ValidatedCopy();optimizedSettings.HybridSkipForced=true;
            var optimized=new HybridLookahead(optimizedSettings,Infer,FastCopy.Copy){CaptureLeaves=true};
            var first=reference.Choose(new[]{a},Infer(new[]{a}),new[]{0},new[]{true});
            var second=optimized.Choose(new[]{b},Infer(new[]{b}),new[]{0},new[]{true});
            if(!first.SequenceEqual(second)||JsonConvert.SerializeObject(reference.DebugLeaves)!=JsonConvert.SerializeObject(optimized.DebugLeaves))
                throw new Exception("Forced-step collapse changed a path, horizon, leaf state/value or decision: "+test.Id);
            forcedCollapseComparisons++;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
        File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=true,positions,comparisons,macroLengths,forcedCollapseComparisons},Formatting.Indented));
    }
    static void Play(Adapter g,int id)
    {g.Step(Enumerable.Range(0,g.VisibleCount).Single(a=>g.Visible(a).Action is ShardsPlayCardAction p&&p.CardInstanceId==id));}
    internal static void CompareForced(Func<Adapter[],Prediction[]> infer,string output)
    {
        int positions=0,leaves=0;double maximumValueError=0;
        foreach(var test in HeroTactics.Cases())for(int variant=0;variant<2;variant++)
        {
            var source=test.Factory(variant+95000);var a=FastCopy.Copy(source);var b=FastCopy.Copy(source);
            var settings=new PolicySearchSettings{Hybrid=true,HybridSkipForced=false,Candidates=4,Depth=24,Workers=8,TerminalNodes=0};
            var reference=new HybridLookahead(settings,infer,FastCopy.Copy){CaptureLeaves=true};
            var other=settings.ValidatedCopy();other.HybridSkipForced=true;
            var optimized=new HybridLookahead(other,infer,FastCopy.Copy){CaptureLeaves=true};
            var p=infer(new[]{source});int fallback=Array.IndexOf(p[0].P,p[0].P.Max());
            int first=reference.Choose(new[]{a},p,new[]{fallback},new[]{true})[0];
            int second=optimized.Choose(new[]{b},p,new[]{fallback},new[]{true})[0];
            if(first!=second)throw new Exception("Forced-step GPU action mismatch: "+test.Id);
            if(reference.DebugLeaves!=null)
            {
                var x=JArray.FromObject(reference.DebugLeaves);var y=JArray.FromObject(optimized.DebugLeaves);
                if(x.Count!=y.Count)throw new Exception("Forced-step leaf count mismatch");
                for(int i=0;i<x.Count;i++)
                {
                    double error=Math.Abs((double)x[i]["Value"]-(double)y[i]["Value"]);maximumValueError=Math.Max(maximumValueError,error);
                    ((JObject)x[i]).Remove("Value");((JObject)y[i]).Remove("Value");
                    if(error>1e-5||!JToken.DeepEquals(x[i],y[i]))throw new Exception("Forced-step GPU path/state mismatch: "+test.Id);
                    leaves++;
                }
            }
            if(TacticalSearch.Fingerprint(source)!=TacticalSearch.Fingerprint(a)||TacticalSearch.Fingerprint(source)!=TacticalSearch.Fingerprint(b))throw new Exception("Forced-step GPU audit changed live source");
            positions++;
        }
        File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=true,positions,leaves,maximumValueError},Formatting.Indented));
    }
}
