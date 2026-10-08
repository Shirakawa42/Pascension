using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shards.AI;
using Shards.Engine;

// Exercise candidate coverage and contingent information-set search with a
// deliberately hostile policy. This is not a claim of whole-game strength.
internal static class FutureScryAudit
{
    internal static void Run(string output)
    {
        var rows=new List<object>();
        for(int seat=0;seat<2;seat++)foreach(int workers in new[]{1,8})foreach(int depth in new[]{0,1,2,3,4})
        {
            var root=RezAudit.Game(seat:seat);
            RezAudit.Add(root,"crystal",seat);RezAudit.Add(root,"longshot",seat);
            Prediction Predict(Adapter g)
            {
                var p=new float[64];
                for(int a=0;a<g.VisibleCount;a++)
                    p[a]=g.Visible(a).Action is ShardsHeroAbilityAction?0:
                        g.Visible(a).Action is ShardsEndTurnAction?2:g.Visible(a).Kind==13?1:.1f;
                float total=p.Sum();for(int a=0;a<64;a++)p[a]/=total;
                return new Prediction{P=p,V=0};
            }
            var prediction=Predict(root);int fallback=Array.IndexOf(prediction.P,prediction.P.Max());
            var config=new PolicySearchSettings{Candidates=1,Depth=12,Worlds=2,Workers=workers,TerminalNodes=0,FutureScryPlans=true,FutureScryDepth=depth};
            var planner=new HybridLookahead(config,gs=>gs.Select(Predict).ToArray(),FastCopy.Copy){CaptureLeaves=true};
            var options=planner.BuildOptions(root,prediction,fallback);
            if(!options.Any(o=>root.Visible(o.First).Action is ShardsHeroAbilityAction))throw new Exception("Zero-prior information action was omitted");
            var off=config.ValidatedCopy();off.FutureScryPlans=false;
            var baseline=new HybridLookahead(off,gs=>gs.Select(Predict).ToArray(),FastCopy.Copy);
            if(baseline.BuildOptions(root,prediction,fallback).Any(o=>root.Visible(o.First).Action is ShardsHeroAbilityAction))throw new Exception("Coverage fixture is not red without the feature");
            ulong before=TacticalSearch.Fingerprint(root);
            int action=planner.Choose(new[]{root},new[]{prediction},new[]{fallback},new[]{true})[0];
            long branches=JObject.FromObject(planner.Diagnostics)["menuBranches"].Value<long>();
            if(depth>0&&branches<=0)throw new Exception("Future Scry choices were not searched");
            if(before!=TacticalSearch.Fingerprint(root)||root.Knowledge.For(seat).Count!=0)throw new Exception("Planning mutated root or revealed its actual center deck");
            var altered=FastCopy.Copy(root);altered.Engine.State.CenterDeck.Reverse();altered.Engine.State.Players[seat].Deck.Reverse();
            var enemy=altered.Engine.State.Players[1-seat];
            if(enemy.Hand.Count>0&&enemy.Deck.Count>0)
            {var card=enemy.Hand[0];enemy.Hand[0]=enemy.Deck[0];enemy.Deck[0]=card;enemy.Hand[0].Zone=ShardsZone.Hand;enemy.Deck[0].Zone=ShardsZone.Deck;}
            var other=new HybridLookahead(config,gs=>gs.Select(Predict).ToArray(),FastCopy.Copy){CaptureLeaves=true};
            int changed=other.Choose(new[]{altered},new[]{prediction},new[]{fallback},new[]{true})[0];
            if(action!=changed||JsonConvert.SerializeObject(planner.DebugLeaves)!=JsonConvert.SerializeObject(other.DebugLeaves))throw new Exception("Search used actual hidden allocation");
            rows.Add(new{seat,workers,depth,branches,coverage=true,redWithoutFeature=true,sourcePreserved=true,hiddenInvariant=true});
        }
        File.WriteAllText(output,JsonConvert.SerializeObject(new{passed=true,rows},Formatting.Indented));
    }
}
